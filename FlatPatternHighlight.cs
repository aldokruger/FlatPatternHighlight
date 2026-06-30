using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using NXOpen;
using NXOpen.Features;

// ReSharper disable InconsistentNaming

// Aliases for JSON serialisation
using JObject = System.Collections.Generic.Dictionary<string, object>;

namespace FlatPatternHighlight
{
    /// <summary>
    /// Plugin NXOpen (C# .NET) para análise de flat patterns de Sheet Metal no NX 2512.
    ///
    /// O plugin executa em 3 etapas sequenciais que se complementam:
    ///
    ///   1. FILTRAGEM DO PERÍMETRO EXTERNO
    ///            Reduz as curvas externas brutas (que incluem contornos de entalhes/recortes) apenas
    ///            às verdadeiras bordas externas. Estratégia: usa UF_MODL_ask_face_loops (P/Invoke
    ///            na libufun.dll) para consultar cada face do sólido aplainado por seus loops, mantendo apenas
    ///            loops externos (type == 1), então intersecciona com as arestas do corpo FlatSolid para filtrar
    ///            quaisquer furos internos. Faz fallback para uma aproximação geométrica de convex-hull
    ///            quando o caminho P/Invoke não está disponível.
    ///
    ///   2. LINHAS DE CENTRO DE DOBRA
    ///            Enumera linhas de centro up e down via FlatPattern.GetBendUpCenterLines /
    ///            GetBendDownCenterLines. Destaca cada linha no NX com uma cor dedicada e
    ///            anexa um atributo de usuário "FlatPatternHighlight" para identificação posterior.
    ///            Pula curvas artefato cujo ponto médio está sobre (ou a menos de 0.5 mm) do
    ///            perímetro externo — algumas configurações do NX retornam bordas do perímetro por estas APIs.
    ///
    ///   3. COTAS PMI EM CADEIA (AnalyzeBendToPerimeter → CreateChainDimensions)
    ///            Para cada linha de dobra, varre todas as curvas do perímetro externo e coleta
    ///            candidatos paralelos em ambos os lados da dobra (Lado A = nml+, Lado B = nml-). Seis índices
    ///            são rastreados por lado (mais próximo, 2º mais próximo, mais distante, mais distante-Line, mais próximo-Line,
    ///            mais próximo-qualquer). Aplica correção SmallEdgeRatio para substituir candidatos de entalhe de canto
    ///            pelo segmento mais longo (verdadeiro contorno). Agrupa dobras paralelas em grupos
    ///            de direção, então divide cada grupo em lanes (flanges) independentes via clusterização
    ///            por sobreposição de range. Para cada lane, particiona as dobras em "lado baixo" e "lado alto"
    ///            relativo ao centro do bbox, e cria uma cadeia PMI:
    ///          contorno externo → 1ª dobra → 2ª dobra → ... → última dobra.
    ///            O contorno é sempre a Line paralela mais DISTANTE no lado externo correto
    ///            (isLowSide ⊕ flipped determina qual dos Lados A / B aponta para o exterior).
    ///            Usa PmiRapidDimensionBuilder com MeasurementMethod.Perpendicular para que o NX
    ///            calcule as distâncias geométricas verdadeiras sem correção de coordenadas.
    /// </summary>
    public class HighlightFlatPattern
    {
        // =====================================================================
        // P/Invoke: funções UF_MODL nativas necessárias porque a API gerenciada do NXOpen
        // não expõe topologia de nível de loop (loops externos vs internos). Chamamos
        // a libufun.dll para consultar cada face plana por seus loops e filtrar
        // apenas loops externos (type == 1).
        // =====================================================================

        /// <summary>Obtém a lista de loops de uma face (cada loop = uma cadeia fechada de arestas).</summary>
        [DllImport("libufun.dll", CallingConvention = CallingConvention.Cdecl)]
        static extern int UF_MODL_ask_face_loops(Tag face, out IntPtr loopList);

        /// <summary>Conta quantos loops existem na lista de loops.</summary>
        [DllImport("libufun.dll", CallingConvention = CallingConvention.Cdecl)]
        static extern int UF_MODL_ask_loop_list_count(IntPtr loopList, out int count);

        /// <summary>
        /// Recupera um loop específico da lista.
        /// Loop type: 1 = loop externo, 2 = loop interno (furo/recorte).
        /// edgeList é uma lista de tags de aresta pertencentes àquele loop.
        /// </summary>
        [DllImport("libufun.dll", CallingConvention = CallingConvention.Cdecl)]
        static extern int UF_MODL_ask_loop_list_item(IntPtr loopList, int index, out int type, out IntPtr edgeList);

        /// <summary>Conta itens em uma lista de objetos UF genérica.</summary>
        [DllImport("libufun.dll", CallingConvention = CallingConvention.Cdecl)]
        static extern int UF_MODL_ask_list_count(IntPtr list, out int count);

        /// <summary>Obtém a tag de um item por índice de uma lista de objetos UF genérica.</summary>
        [DllImport("libufun.dll", CallingConvention = CallingConvention.Cdecl)]
        static extern int UF_MODL_ask_list_item(IntPtr list, int index, out Tag tag);

        /// <summary>Libera a memória alocada por UF_MODL_ask_face_loops.</summary>
        [DllImport("libufun.dll", CallingConvention = CallingConvention.Cdecl)]
        static extern int UF_MODL_delete_loop_list(ref IntPtr loopList);

        // =====================================================================
        // CONFIGURAÇÃO DO USUÁRIO — ver Settings.cs
        // =====================================================================

        // A classe Settings foi movida para o arquivo Settings.cs (mesmo namespace).
        // Permanece internal sealed; as propriedades resolvidas abaixo continuam
        // referenciando Settings.Load() / Settings.Save() pela mesma interface.

        // =====================================================================
        // VALORES RESOLVIDOS — carregados do Settings do usuário no startup
        // =====================================================================

        private static Settings Config = Settings.Load();

        private static double ParallelismThreshold => Config.ParallelismThreshold;
        private static double DiagonalBendThreshold => Config.DiagonalBendThreshold;
        private static double ArtefactSkipDistanceSq => Config.ArtefactSkipDistanceSq;
        private static double SmallEdgeRatio => Config.SmallEdgeRatio;
        private static double SmallEdgeGuardFactor => Config.SmallEdgeGuardFactor;
        private static double LaneLengthRatioThreshold => Config.LaneLengthRatioThreshold;
        private static double MaxChainGap => Config.MaxChainGap;
        private static int DimensionDecimalPlaces => Config.DimensionDecimalPlaces;

        // Guardas numéricas — não expostas ao usuário (risco de instabilidade)
        private const double MinSegmentLength = 1e-6;
        private const double OverlapEpsilon = 1e-6;

        private static Session theSession;
        private static UI theUI;

        // =====================================================================
        // PONTO DE ENTRADA — execução Ctrl+U
        // =====================================================================

        /// <summary>
        /// Entrada principal — chamada quando o usuário executa a DLL via File → Execute → NX Open
        /// (Ctrl+U), ou pelo callback do menu.
        /// </summary>
        public static int Main(string[] args)
        {
            List<Curve> outerPerim = null;
            List<Curve> bendLines = null;

            try
            {
                theSession = Session.GetSession();
                theUI = UI.GetUI();

                // Recarrega configurações do disco (pode ter sido alterado pelo FlatPatternHighlightConfig.dll)
                Config = Settings.Load();

                // Exige um work part ativo (não apenas um displayed part).
                Part workPart = theSession.Parts.Work;
                if (workPart == null)
                {
                    theUI.NXMessageBox.Show(
                        "Flat Pattern Highlight",
                        NXMessageBox.DialogType.Error,
                        "No active work part. Open or create a part first.");
                    return 1;
                }

                // Escreve tudo no LogFile do NX (disponível em Help → Log File).
                LogFile lw = theSession.LogFile;

                // Garante que o settings.json do usuário existe em %APPDATA%
                lw.WriteLine("[config] Arquivo de configuração do usuário: " + Settings.ConfigPath);
                lw.WriteLine($"[config] ParallelismThreshold     = {ParallelismThreshold:F3}");
                lw.WriteLine($"[config] DiagonalBendThreshold    = {DiagonalBendThreshold:F3}");
                lw.WriteLine($"[config] ArtefactSkipDistanceSq   = {ArtefactSkipDistanceSq:F6}");
                lw.WriteLine($"[config] SmallEdgeRatio           = {SmallEdgeRatio:F3}");
                lw.WriteLine($"[config] SmallEdgeGuardFactor     = {SmallEdgeGuardFactor:F3}");
                lw.WriteLine($"[config] LaneLengthRatioThreshold = {LaneLengthRatioThreshold:F3}");
                lw.WriteLine($"[config] MaxChainGap               = {MaxChainGap:F1}  (offset gap for lane merge)");
                lw.WriteLine($"[config] DimensionDecimalPlaces    = {DimensionDecimalPlaces}  (PMI dimension precision)");
                lw.WriteLine("");

                // Localiza a feature FlatPattern — sem ela não há o que analisar.
                FlatPattern flatPattern = FindFlatPattern(workPart, lw);
                if (flatPattern == null)
                {
                    theUI.NXMessageBox.Show(
                        "Flat Pattern Highlight",
                        NXMessageBox.DialogType.Error,
                        "No Flat Pattern feature found.\n\n" +
                        "The active part must contain a Sheet Metal flat pattern.\n" +
                        "Use Insert > Sheet Metal > Flat Pattern to create one first.");
                    return 1;
                }

                lw.WriteLine("=== FlatPatternHighlight Diagnostic Log ===");
                lw.WriteLine($"Part: {workPart.Name}");
                lw.WriteLine("");

                // Etapa 1 — identifica o verdadeiro contorno externo.
                HighlightOuterPerimeter(flatPattern, workPart, lw, out outerPerim);

                // Etapa 2 — coleta e destaca as linhas de centro de dobra.
                if (outerPerim != null && outerPerim.Count > 0)
                    HighlightBendCenterLines(flatPattern, lw, outerPerim, out bendLines);

                // Etapa 3 — analisa a proximidade de cada linha de dobra ao perímetro externo.
                if (bendLines != null && bendLines.Count > 0 && outerPerim != null && outerPerim.Count > 0)
                    AnalyzeBendToPerimeter(bendLines, outerPerim, lw, workPart);

                lw.WriteLine("=== End of Diagnostic Log ===");
                return 0;
            }
            catch (NXException ex)
            {
                if (theUI != null)
                    try { theUI.NXMessageBox.Show("Flat Pattern Highlight - Error", NXMessageBox.DialogType.Error, $"NX Error: {ex.Message}"); } catch { }
                return 1;
            }
            catch (Exception ex)
            {
                if (theUI != null)
                    try { theUI.NXMessageBox.Show("Flat Pattern Highlight - Error", NXMessageBox.DialogType.Error, $"Error: {ex.Message}"); } catch { }
                return 1;
            }
            finally
            {
                // Highlights são auxílios transitórios de análise; remove antes de sair.
                // Executa mesmo se uma exceção ocorrer durante a análise.
                try { if (outerPerim != null) UnhighlightObjects(outerPerim); } catch { }
                try { if (bendLines != null) UnhighlightObjects(bendLines); } catch { }
            }
        }

        // =====================================================================
        // ETAPA 1 — Identificação do Perímetro Externo
        // =====================================================================

        /// <summary>
        /// Etapa 1: Filtra as curvas externas brutas de GetExteriorCurves() para obter
        /// apenas o verdadeiro contorno externo, excluindo loops internos de entalhes/recortes.
        ///
        /// Abordagem:
        ///   a) Localiza o corpo sólido plano via 3 estratégias (ver FindFlatSolidBody).
        ///   b) Se encontrado, chama UF_MODL_ask_face_loops em cada face plana e coleta
        ///      tags de arestas pertencentes a loops externos (type == 1).
        ///   c) Cruza essas tags com o FlatSolidObject.Tag de cada
        ///      curva exterior.
        ///   d) Se nenhum corpo for encontrado, TODAS as curvas externas são retornadas (fallback).
        /// </summary>
        private static void HighlightOuterPerimeter(FlatPattern flatPattern, Part workPart, LogFile lw, out List<Curve> outerPerim)
        {
            outerPerim = null;
            const int OuterLoopType = 1; // tipo de loop UF_MODL para contorno externo
            lw.WriteLine("--- Outer Perimeter (True External Boundary) ---");

            // Busca TODAS as curvas externas — inclui contorno externo + toda aresta de entalhe/recorte.
            FlatPattern.ObjectDataEdge[] exteriorCurves;
            flatPattern.GetExteriorCurves(out exteriorCurves);

            if (exteriorCurves == null || exteriorCurves.Length == 0)
            {
                lw.WriteLine("  (no exterior curves found)");
                return;
            }

            lw.WriteLine($"  Total exterior curves (raw): {exteriorCurves.Length}");
            lw.WriteLine("");

            // Tenta localizar o corpo sólido plano para distinguir loops externos de internos.
            Body flatBody = FindFlatSolidBody(workPart, flatPattern, lw);
            if (flatBody == null)
            {
                // Fallback: usa TODAS as curvas externas (sem filtragem possível).
                // Isso ocorre em peças onde o sólido plano é interno ou não consultável.
                lw.WriteLine("  ERROR: Could not locate flat solid body — using all exterior curves as perimeter.");
                var allDisp = new List<DisplayableObject>();
                outerPerim = new List<Curve>();
                foreach (var ed in exteriorCurves)
                {
                    if (ed.FlatPatternObject != null)
                    {
                        allDisp.Add(ed.FlatPatternObject);
                        outerPerim.Add(ed.FlatPatternObject);
                    }
                }
                HighlightObjects(allDisp);
                return;
            }

            // Coleta tags dos loops externos de todas as faces planas.
            var outerEdgeTags = new HashSet<Tag>();

            Face[] faces = flatBody.GetFaces();
            lw.WriteLine($"  Flat body faces: {faces.Length}");

            foreach (var face in faces)
            {
                // Apenas faces planas têm a geometria 2D — faces cilíndricas (dobra) não.
                if (face.SolidFaceType != Face.FaceType.Planar) continue;

                IntPtr loopList = IntPtr.Zero;
                try
                {
                    NXOpen.Utilities.JAM.StartUFCall("FlatPatternHighlight");
                    int err = UF_MODL_ask_face_loops(face.Tag, out loopList);
                    NXOpen.Utilities.JAM.EndUFCall();

                    if (err != 0 || loopList == IntPtr.Zero)
                    {
                        lw.WriteLine($"  [diag] UF_MODL_ask_face_loops returned {err} for face Tag={face.Tag}");
                        continue;
                    }

                    int loopCount;
                    int rc = UF_MODL_ask_loop_list_count(loopList, out loopCount);
                    if (rc != 0) { lw.WriteLine($"  [diag] UF_MODL_ask_loop_list_count returned {rc}"); continue; }

                    for (int li = 0; li < loopCount; li++)
                    {
                        int loopType;
                        IntPtr edgeList;
                        rc = UF_MODL_ask_loop_list_item(loopList, li, out loopType, out edgeList);
                        if (rc != 0) { lw.WriteLine($"  [diag] UF_MODL_ask_loop_list_item({li}) returned {rc}"); continue; }
                        if (loopType != OuterLoopType) continue; // Pula loops internos (type 2).

                        int edgeCount;
                        rc = UF_MODL_ask_list_count(edgeList, out edgeCount);
                        if (rc != 0) { lw.WriteLine($"  [diag] UF_MODL_ask_list_count returned {rc}"); continue; }
                        for (int ei = 0; ei < edgeCount; ei++)
                        {
                            Tag et;
                            rc = UF_MODL_ask_list_item(edgeList, ei, out et);
                            if (rc == 0) outerEdgeTags.Add(et);
                        }
                    }
                }
                finally
                {
                    if (loopList != IntPtr.Zero)
                        UF_MODL_delete_loop_list(ref loopList);
                }
            }

            lw.WriteLine($"  Outer loop edge tags: {outerEdgeTags.Count}");

            // Mantém apenas as curvas externas cuja tag de aresta sólida correspondente
            // está no conjunto de tags de aresta de loop externo.
            outerPerim = new List<Curve>();
            foreach (var ed in exteriorCurves)
            {
                if (ed.FlatPatternObject == null || ed.FlatSolidObject == null) continue;
                if (outerEdgeTags.Contains(ed.FlatSolidObject.Tag))
                    outerPerim.Add(ed.FlatPatternObject);
            }

            lw.WriteLine($"  Outer perimeter curves: {outerPerim.Count}");
            lw.WriteLine($"  Inner (excluded): {exteriorCurves.Length - outerPerim.Count}");
            lw.WriteLine("");

            var displayList = new List<DisplayableObject>();
            foreach (var c in outerPerim) displayList.Add(c);

            HighlightObjects(displayList);
        }

        // =====================================================================
        // ETAPA 2 — Linhas de Centro de Dobra
        // =====================================================================

        /// <summary>
        /// Etapa 2: Recupera e destaca as linhas de centro de dobra (faces up e down).
        /// Estas são as linhas axiais nos centros das regiões cilíndricas de dobra.
        ///
        /// Bend Up   = linha de centro da dobra quando a flange dobra para cima.
        /// Bend Down = linha de centro da dobra quando a flange dobra para baixo.
        ///
        /// Em muitos flat patterns, dobras "down" aparecem no lado oposto do
        /// material e suas linhas de centro são deslocadas correspondentemente.
        /// </summary>
        private static void HighlightBendCenterLines(FlatPattern flatPattern, LogFile lw, List<Curve> outerPerim, out List<Curve> bendLines)
        {
            bendLines = new List<Curve>();
            lw.WriteLine("--- Bend Center Lines ---");

            // Linhas de centro (Bend Up).
            int upCount = 0;
            FlatPattern.ObjectDataFace[] bendUp;
            flatPattern.GetBendUpCenterLines(out bendUp);
            if (bendUp != null)
            {
                lw.WriteLine("  [Bend Up]");
                for (int i = 0; i < bendUp.Length; i++)
                {
                    if (bendUp[i].FlatPatternObject == null) continue;
                    Curve c = bendUp[i].FlatPatternObject;
                    upCount++; bendLines.Add(c);
                    lw.WriteLine($"    [{i,2}] Tag={c.Tag,8}  Len={c.GetLength(),8:F2}  UP");
                }
            }

            // Linhas de centro (Bend Down).
            int downCount = 0;
            FlatPattern.ObjectDataFace[] bendDown;
            flatPattern.GetBendDownCenterLines(out bendDown);
            if (bendDown != null)
            {
                lw.WriteLine("  [Bend Down]");
                for (int i = 0; i < bendDown.Length; i++)
                {
                    if (bendDown[i].FlatPatternObject == null) continue;
                    Curve c = bendDown[i].FlatPatternObject;
                    downCount++; bendLines.Add(c);
                    lw.WriteLine($"    [{i,2}] Tag={c.Tag,8}  Len={c.GetLength(),8:F2}  DOWN");
                }
            }

            lw.WriteLine($"  Total bend lines: {bendLines.Count} ({upCount} up, {downCount} down)");
            lw.WriteLine("");

            var displayList = new List<DisplayableObject>();
            foreach (var c in bendLines) displayList.Add(c);

            if (displayList.Count == 0)
            {
                bendLines = null;
                return;
            }

            HighlightObjects(displayList);
        }

        // =====================================================================
        // ETAPA 3 — Análise de Proximidade Dobra-Perímetro
        // =====================================================================

        /// <summary>Helper: extrai uma coordenada de um Point3d pelo índice do eixo (0=X,1=Y,2=Z).</summary>
        private static double AxisCoord(Point3d p, int axis)
        {
            switch (axis) { case 0: return p.X; case 1: return p.Y; default: return p.Z; }
        }

        /// <summary>
        /// Detecta o eixo normal do plano do flat pattern medindo a dispersão
        /// dos endpoints de todas as curvas. O eixo com menor variação é a normal do plano.
        ///
        /// Algumas peças NX modelam o flat pattern no plano XZ ou YZ em vez do
        /// plano XY convencional. Este método torna a detecção do eixo automática.
        /// </summary>
        /// <returns>0 para X-normal, 1 para Y-normal, 2 para Z-normal.</returns>
        private static int DetectNormalAxis(List<Curve> bendLines, List<Curve> outerPerim)
        {
            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;

            void Track(Point3d p)
            {
                if (p.X < minX) minX = p.X; if (p.X > maxX) maxX = p.X;
                if (p.Y < minY) minY = p.Y; if (p.Y > maxY) maxY = p.Y;
                if (p.Z < minZ) minZ = p.Z; if (p.Z > maxZ) maxZ = p.Z;
            }

            foreach (var c in bendLines) { Point3d s, e; GetEndPoints(c, out s, out e); Track(s); Track(e); }
            foreach (var c in outerPerim) { Point3d s, e; GetEndPoints(c, out s, out e); Track(s); Track(e); }

            double rangeX = maxX - minX, rangeY = maxY - minY, rangeZ = maxZ - minZ;
            if (rangeX <= rangeY && rangeX <= rangeZ) return 0;
            if (rangeY <= rangeX && rangeY <= rangeZ) return 1;
            return 2;
        }

        /// <summary>
        /// Etapa 3: Para cada linha de centro de dobra, encontra a curva de perímetro paralela mais próxima
        /// de cada lado (direção normal e oposta). Compara essas distâncias contra
        /// a distância até a borda geral do bounding box para determinar se a curva de
        /// perímetro mais próxima É a borda externa da peça ou um recurso intermediário.
        ///
        /// Logs a comprehensive table and optionally creates chain PMI dimensions
        /// (see CreateChainDimensions).
        /// </summary>
        private static void AnalyzeBendToPerimeter(List<Curve> bendLines, List<Curve> outerPerim, LogFile lw, Part workPart)
        {
            int pmiCount = 0;

            lw.WriteLine("--- Bend Line → Nearest Perimeter (Parallel) ---");

            // Determina a orientação do plano do flat pattern.
            int normalAxis = DetectNormalAxis(bendLines, outerPerim);
            int uAxis, vAxis;
            if (normalAxis == 0) { uAxis = 1; vAxis = 2; }
            else if (normalAxis == 1) { uAxis = 0; vAxis = 2; }
            else { uAxis = 0; vAxis = 1; }
            string[] axisNames = { "X", "Y", "Z" };
            lw.WriteLine($"  [diag] Flat pattern plane: normal={axisNames[normalAxis]}  u={axisNames[uAxis]}  v={axisNames[vAxis]}");

            double GetU(Point3d p) => AxisCoord(p, uAxis);
            double GetV(Point3d p) => AxisCoord(p, vAxis);

            // Calcula o bounding box nas coordenadas UV do plano.
            double bboxMinU = double.MaxValue, bboxMinV = double.MaxValue;
            double bboxMaxU = double.MinValue, bboxMaxV = double.MinValue;

            var perimData = new List<(Point3d start, Point3d end, Vector3d dir, double len, Curve curve)>();
            int nonLineArcCount = 0;
            foreach (var c in outerPerim)
            {
                Point3d s, e; GetEndPoints(c, out s, out e);
                if (!(c is Line) && !(c is Arc)) nonLineArcCount++;
                double du = GetU(e) - GetU(s), dv = GetV(e) - GetV(s);
                double len = Math.Sqrt(du * du + dv * dv);
                Vector3d d = len > MinSegmentLength ? new Vector3d(du / len, dv / len, 0) : new Vector3d(0, 0, 0);
                perimData.Add((s, e, d, len, c));
                double su = GetU(s), sv = GetV(s), eu = GetU(e), ev = GetV(e);
                if (su < bboxMinU) bboxMinU = su; if (su > bboxMaxU) bboxMaxU = su;
                if (sv < bboxMinV) bboxMinV = sv; if (sv > bboxMaxV) bboxMaxV = sv;
                if (eu < bboxMinU) bboxMinU = eu; if (eu > bboxMaxU) bboxMaxU = eu;
                if (ev < bboxMinV) bboxMinV = ev; if (ev > bboxMaxV) bboxMaxV = ev;
            }

            double bboxCu = (bboxMinU + bboxMaxU) / 2;
            double bboxCv = (bboxMinV + bboxMaxV) / 2;

            lw.WriteLine($"  [diag] Non-Line/Arc perimeter curves: {nonLineArcCount} / {outerPerim.Count}");
            lw.WriteLine($"  Overall bbox: ({bboxMinU:F1},{bboxMinV:F1})-({bboxMaxU:F1},{bboxMaxV:F1})");
            lw.WriteLine($"  Bbox center: ({bboxCu:F1},{bboxCv:F1})");
            lw.WriteLine("");

            bool hasArcs = false;
            // ===================================================================
            // bendInfos — resultado consolidado de cada linha de dobra analisada.
            // Esta estrutura carrega tudo o que o Chain PMI precisa para
            // escolher a curva de boundary e criar as cotas. Campos por grupo:
            //
            //   Identidade / geometria:
            //     bi      - índice original em bendLines (debug)
            //     bend    - objeto Curve da linha de dobra
            //     pt      - midpoint 3D (ponto de pick da cota)
            //     bs, be  - endpoints 3D da dobra
            //     dir     - direção unitária no plano UV
            //     nml     - normal perpendicular à dobra (-dy, dx)
            //
            //   Candidatos de perímetro por LADO (A = nml+, B = nml-):
            //     bestIdx / bestDist       - curva PARALELA mais próxima (com overlap)
            //     farIdx                   - curva paralela mais DISTANTE (histórico;
            //                               usado como fallback de borda externa)
            //     nearIdx                  - curva mais próxima SEM filtro de
            //                               paralelismo (fallback p/ dobras
            //                               diagonais que não têm paralela)
            //     bestLineIdx / farLineIdx - mesmas métricas, mas restritas a Line
            //                               (ignora Arc); preferido na dimensão PMI
            // ===================================================================
            var bendInfos = new List<BendAnalysisInfo>();

            for (int bi = 0; bi < bendLines.Count; bi++)
            {
                Curve bend = bendLines[bi];
                Point3d bs, be; GetEndPoints(bend, out bs, out be);

                // Direção e comprimento no plano UV.
                double bdu = GetU(be) - GetU(bs), bdv = GetV(be) - GetV(bs);
                double blen = Math.Sqrt(bdu * bdu + bdv * bdv);
                if (blen < MinSegmentLength) continue;

                Vector3d bdir = new Vector3d(bdu / blen, bdv / blen, 0);
                // Direção perpendicular (normal) — rotação 90° anti-horária.
                Vector3d nml = new Vector3d(-bdir.Y, bdir.X, 0);

                double mu = (GetU(bs) + GetU(be)) / 2;
                double mv = (GetV(bs) + GetV(be)) / 2;

                // Pula linhas de dobra cujo ponto médio 3D está sobre (ou a menos de 0.5 mm) do
                // perímetro externo. Algumas configurações de flat pattern do NX retornam bordas
                // do perímetro por GetBendUpCenterLines / GetBendDownCenterLines como artefatos.
                {
                    Point3d bmid3D = new Point3d((bs.X + be.X) / 2.0, (bs.Y + be.Y) / 2.0, (bs.Z + be.Z) / 2.0);
                    bool onPerim = false;
                    foreach (var seg in perimData)
                    {
                        Point3d cl = ProjectPointOnSegment(bmid3D, seg.start, seg.end);
                        double d2 = (bmid3D.X - cl.X) * (bmid3D.X - cl.X)
                                  + (bmid3D.Y - cl.Y) * (bmid3D.Y - cl.Y)
                                  + (bmid3D.Z - cl.Z) * (bmid3D.Z - cl.Z);
                        if (d2 < ArtefactSkipDistanceSq) { onPerim = true; break; }
                    }
                    if (onPerim)
                    {
                        lw.WriteLine($"  Bend[{bi}] Tag={bend.Tag}  skipped - midpoint on outer perimeter (artefact)");
                        continue;
                    }
                }

                // Rastreia múltiplos candidatos POR LADO (A=nml+, B=nml-) simultaneamente.
                // Cada variante serve um propósito específico no pipeline de seleção:
                //
                //   best
                //     O segmento paralelo mais próximo. Pode ser a face interna de uma flange
                //     ou um notch fino colado à dobra (ex.: 2.85 mm) em vez da borda exterior
                //     real (22.85 mm).
                //
                //   far / farLine
                //     O segmento paralelo mais distante — este é normalmente a BORDA EXTERIOR
                //     (topo/base/lateral da aba). farLine restringe a somente objetos Line,
                //     pois PmiRapidDimension exige Line como 1ª referência.
                //     Corrigido após o scan por SmallEdgeRatio: se o segmento mais distante
                //     for um notch curto de canto bbox, troca pelo segmento mais LONGO.
                //
                //   longestIdx / longestDist
                //     Auxiliar do SmallEdgeRatio: segmento de maior comprimento encontrado.
                //     Guardamos também longestDist para evitar trocar por uma aba interna
                //     muito próxima da dobra (guarda >= 50% da distância do far atual).
                //
                //   near (sem filtro de paralelismo)
                //     Fallback de último recurso para dobras diagonais onde o filtro de
                //     paralelismo (|dot| >= 0.95) descarta todos os candidatos.
                //
                //   bestLine
                //     Segmento mais próximo que é Line. Usado na promoção de Arc→Line para
                //     dobras diagonais (onde bestIdx pode ser Arc).
                double bestDistA = double.MaxValue, bestDistB = double.MaxValue;
                int bestIdxA = -1, bestIdxB = -1;
                double farDistA = -1, farDistB = -1;
                int farIdxA = -1, farIdxB = -1;
                double longestLenA = -1, longestLenB = -1;
                int longestIdxA = -1, longestIdxB = -1;
                double longestDistA = -1, longestDistB = -1;
                double nearDistA = double.MaxValue, nearDistB = double.MaxValue;
                int nearIdxA = -1, nearIdxB = -1;
                double bestLineDistA = double.MaxValue, bestLineDistB = double.MaxValue;
                int bestLineIdxA = -1, bestLineIdxB = -1;
                double farLineDistA = -1, farLineDistB = -1;
                int farLineIdxA = -1, farLineIdxB = -1;

                // Varre todas as curvas de perímetro por segmentos paralelos em cada lado.
                for (int pi = 0; pi < perimData.Count; pi++)
                {
                    var (ps, pe, pdir, plen, _) = perimData[pi];

                    // Projeta o ponto médio do segmento de perímetro no eixo normal
                    // para determinar em qual lado (A = normal positiva, B = normal negativa).
                    double cu = (GetU(ps) + GetU(pe)) / 2;
                    double cv = (GetV(ps) + GetV(pe)) / 2;

                    double vu = cu - mu, vv = cv - mv;
                    double proj = vu * nml.X + vv * nml.Y;

                    double absProj = Math.Abs(proj);

                    // Rastreia a aresta de perímetro mais próxima em cada lado INDEPENDENTE de paralelismo.
                    // Isso garante que dobras diagonais sempre tenham um contorno de fallback.
                    if (proj > 0)
                    {
                        if (absProj < nearDistA) { nearDistA = absProj; nearIdxA = pi; }
                    }
                    else if (proj < 0)
                    {
                        if (absProj < nearDistB) { nearDistB = absProj; nearIdxB = pi; }
                    }

                    // Filtro: apenas curvas aproximadamente paralelas (dot >= ParallelismThreshold).
                    double dot = pdir.X * bdir.X + pdir.Y * bdir.Y;
                    if (Math.Abs(dot) < ParallelismThreshold) { hasArcs = true; continue; }

                    // Verifica sobreposição: o segmento de perímetro deve sobrepor a
                    // projeção da linha de dobra para não corresponder a segmentos irrelevantes laterais.
                    double t1 = (GetU(ps) - GetU(bs)) * bdir.X + (GetV(ps) - GetV(bs)) * bdir.Y;
                    double t2 = (GetU(pe) - GetU(bs)) * bdir.X + (GetV(pe) - GetV(bs)) * bdir.Y;
                    double tMin = Math.Min(t1, t2), tMax = Math.Max(t1, t2);
                    double overlap = Math.Min(blen, tMax) - Math.Max(0, tMin);
                    if (overlap <= OverlapEpsilon) continue;

                    double dist = Math.Abs(proj);

                    if (proj > 0)
                    {
                        if (dist < bestDistA)
                        {
                            bestDistA = dist; bestIdxA = pi;
                        }
                        if (dist > farDistA) { farDistA = dist; farIdxA = pi; }
                        if (plen > longestLenA) { longestLenA = plen; longestIdxA = pi; longestDistA = dist; }
                        if (dist < bestLineDistA && perimData[pi].curve is Line) { bestLineDistA = dist; bestLineIdxA = pi; }
                        if (dist > farLineDistA && perimData[pi].curve is Line) { farLineDistA = dist; farLineIdxA = pi; }
                    }
                    else if (proj < 0)
                    {
                        if (dist < bestDistB)
                        {
                            bestDistB = dist; bestIdxB = pi;
                        }
                        if (dist > farDistB) { farDistB = dist; farIdxB = pi; }
                        if (plen > longestLenB) { longestLenB = plen; longestIdxB = pi; longestDistB = dist; }
                        if (dist < bestLineDistB && perimData[pi].curve is Line) { bestLineDistB = dist; bestLineIdxB = pi; }
                        if (dist > farLineDistB && perimData[pi].curve is Line) { farLineDistB = dist; farLineIdxB = pi; }
                    }
                }

                // --- Correção SmallEdgeRatio (notch de canto bbox) ---
                // Problema: em certas geometrias o segmento mais distante é um notch
                // pequeno de canto (ex.: 3 mm de comprimento numa caixa com 400 mm de
                // largura), não a borda exterior real. O segmento mais longo paralelo
                // é melhor candidato.
                // Guarda extra "longestDist >= farDist * 0.5": evita trocar farIdx por
                // um segmento longo que esteja muito PRÓXIMO à dobra (e.g. face de
                // flange a 2.85 mm quando a borda real está a 29.85 mm).
                // Aplicado a farIdx E a farLineIdx (variante restrita a Line).
                if (farIdxA >= 0 && longestIdxA >= 0 && perimData[farIdxA].len < SmallEdgeRatio * longestLenA
                    && longestDistA >= farDistA * SmallEdgeGuardFactor)
                    farIdxA = longestIdxA;
                if (farIdxB >= 0 && longestIdxB >= 0 && perimData[farIdxB].len < SmallEdgeRatio * longestLenB
                    && longestDistB >= farDistB * SmallEdgeGuardFactor)
                    farIdxB = longestIdxB;
                // Aplica a mesma correção aos candidatos restritos a Line.
                if (farLineIdxA >= 0 && longestIdxA >= 0 && perimData[farLineIdxA].len < SmallEdgeRatio * longestLenA
                    && longestDistA >= farLineDistA * SmallEdgeGuardFactor)
                    farLineIdxA = longestIdxA;
                if (farLineIdxB >= 0 && longestIdxB >= 0 && perimData[farLineIdxB].len < SmallEdgeRatio * longestLenB
                    && longestDistB >= farLineDistB * SmallEdgeGuardFactor)
                    farLineIdxB = longestIdxB;

                // Calcula a distância do ponto médio da dobra até a borda do bounding box
                // ao longo de cada direção normal.
                double sideADist = bestIdxA >= 0 ? DistToBboxEdge(mu, mv, nml.X, nml.Y, bboxMinU, bboxMinV, bboxMaxU, bboxMaxV) : -1;
                double sideBDist = bestIdxB >= 0 ? DistToBboxEdge(mu, mv, -nml.X, -nml.Y, bboxMinU, bboxMinV, bboxMaxU, bboxMaxV) : -1;

                // Registra os resultados para esta dobra no log.
                lw.WriteLine($"  Bend[{bi}] Tag={bend.Tag}  Mid=({mu:F1},{mv:F1})  Dir=({bdir.X:F3},{bdir.Y:F3})  Len={blen:F1}");
                lw.WriteLine($"    Side A (nml+): nearest={bestDistA,8:F2}  bboxDist={sideADist,8:F2}" +
                    (bestIdxA >= 0 ? $"  perimTag={outerPerim[bestIdxA].Tag}" : "  (none)"));
                lw.WriteLine($"                   farthest  ={farDistA,8:F2}" +
                    (farIdxA >= 0 ? $"  perimTag={perimData[farIdxA].curve.Tag}  segLen={perimData[farIdxA].len:F1}" : "  (none)"));

                lw.WriteLine($"    Side B (nml-): nearest={bestDistB,8:F2}  bboxDist={sideBDist,8:F2}" +
                    (bestIdxB >= 0 ? $"  perimTag={outerPerim[bestIdxB].Tag}" : "  (none)"));
                lw.WriteLine($"                   farthest  ={farDistB,8:F2}" +
                    (farIdxB >= 0 ? $"  perimTag={perimData[farIdxB].curve.Tag}  segLen={perimData[farIdxB].len:F1}" : "  (none)"));

                lw.WriteLine("");

                // --- Promoção Arc→Line para dobras diagonais ---
                // Uma dobra é "diagonal" quando tem componente significativa em ambos os
                // eixos UV (|dir.X|>0.2 E |dir.Y|>0.2). Essas dobras raramente têm uma
                // curva de perímetro com paralelismo perfeito (|dot|>=0.95), então:
                //   1. Se bestIdx for Arc (fillet de canto), troca para bestLineIdx no
                //      mesmo lado — evita o erro 1175009 do builder PMI perpendicular.
                //   2. Se nenhum candidato paralelo foi encontrado, usa nearIdx (mais
                //      próximo sem filtro de paralelismo) como último recurso.
                if (Math.Abs(bdir.X) > DiagonalBendThreshold && Math.Abs(bdir.Y) > DiagonalBendThreshold)
                {
                    if (bestIdxA >= 0 && !(perimData[bestIdxA].curve is Line) && bestLineIdxA >= 0)
                        bestIdxA = bestLineIdxA;
                    if (bestIdxB >= 0 && !(perimData[bestIdxB].curve is Line) && bestLineIdxB >= 0)
                        bestIdxB = bestLineIdxB;
                    if (bestIdxA < 0) { bestIdxA = nearIdxA; }
                    if (bestIdxB < 0) { bestIdxB = nearIdxB; }
                }
                // Para dobras não-diagonais, garante farIdx mesmo sem paralelo.
                if (farIdxA < 0) farIdxA = nearIdxA;
                if (farIdxB < 0) farIdxB = nearIdxB;

                Point3d bendPoint = new Point3d((bs.X + be.X) / 2, (bs.Y + be.Y) / 2, (bs.Z + be.Z) / 2);
                bendInfos.Add(new BendAnalysisInfo
{
    Index = bi,
    Bend = bend,
    MidPoint = bendPoint,
    StartPoint = bs,
    EndPoint = be,
    Direction = bdir,
    Normal = nml,
    BestIdxA = bestIdxA, BestIdxB = bestIdxB,
    FarIdxA = farIdxA, FarIdxB = farIdxB,
    NearIdxA = nearIdxA, NearIdxB = nearIdxB,
    BestLineIdxA = bestLineIdxA, BestLineIdxB = bestLineIdxB,
    FarLineIdxA = farLineIdxA, FarLineIdxB = farLineIdxB,
    BestDistA = bestDistA, BestDistB = bestDistB
});
            }

            // Cria cotas PMI em cadeia (contorno → dobra mais próxima → próxima dobra → ...).
            pmiCount = CreateChainDimensions(workPart, bendInfos, perimData, outerPerim,
                bboxMinU, bboxMinV, bboxMaxU, bboxMaxV, uAxis, vAxis, normalAxis, lw);

            lw.WriteLine($"  PMI dimensions created: {pmiCount}");
            lw.WriteLine("");

            if (hasArcs)
                lw.WriteLine("  (some perimeter arcs were skipped — only straight lines compared)");
        }

        // =====================================================================
        // DIMENSIONAMENTO EM CADEIA — criação de PMI
        // =====================================================================

        /// <summary>
        /// Rastreador de nível de posicionamento de cotas PMI, usado para evitar
        /// sobreposição. Duas direções de posicionamento:
        ///   VDominant → texto colocado no lado direito (borda U máxima)
        ///   UDominant → texto colocado no lado superior (borda V máxima)
        /// Cada nova cota numa direção ganha um nível incremental (offset maior).
        /// </summary>
        private sealed class PlacementTracker
        {
            public int VDominantLevel;
            public int UDominantLevel;

            /// <summary>
            /// Conjunto de chaves de cota boundary ja criadas. A chave e' fornecida
            /// pelo chamador (que tem todo o contexto: isLowSide, normalAxis, etc.)
            /// para que cotas no mesmo valor mas em lados opostos da peca permanecam
            /// independentes.
            /// </summary>
            public HashSet<string> CreatedBoundaryKeys = new HashSet<string>();

            /// <summary>
            /// Retorna true se uma cota com esta chave ja foi criada.
            /// Nao registra — use RegisterBoundaryKey apos criacao bem-sucedida.
            /// </summary>
            public bool IsKeyDuplicate(string key)
            {
                return key != null && CreatedBoundaryKeys.Contains(key);
            }

            /// <summary>
            /// Registra uma chave de cota boundary como ja criada.
            /// </summary>
            public void RegisterBoundaryKey(string key)
            {
                if (key != null)
                    CreatedBoundaryKeys.Add(key);
            }
        }

        /// <summary>
        /// Remove todas as cotas PMI criadas por execuções anteriores do
        /// FlatPatternHighlight (identificadas pelo user attribute).
        /// </summary>
        private static void DeletePreviousPmiDimensions(Part workPart, LogFile lw)
        {
            int deleted = 0;
            var toDelete = new List<NXOpen.NXObject>();
            foreach (NXOpen.Annotations.Dimension dim in workPart.Dimensions)
            {
                try
                {
                    string val = dim.GetUserAttributeAsString(
                        "FlatPatternHighlight",
                        NXOpen.NXObject.AttributeType.String, 0);
                    if (val == "true")
                    {
                        toDelete.Add(dim);
                        deleted++;
                    }
                }
                catch { }
            }

            if (toDelete.Count > 0)
            {
                var undoMark = theSession.SetUndoMark(Session.MarkVisibility.Invisible, "Delete old PMI dims");
                theSession.UpdateManager.AddObjectsToDeleteList(toDelete.ToArray());
                theSession.UpdateManager.DoUpdate(undoMark);
                lw.WriteLine($"[PMI] Removed {deleted} existing dimension(s) from previous run");
            }
        }

        /// <summary>
        /// Projeta um ponto sobre um segmento de reta (ponto mais próximo no segmento).
        /// Usado para posicionar pontos de origem de dimensão nas curvas de contorno.
        /// </summary>
        private static Point3d ProjectPointOnSegment(Point3d point, Point3d segStart, Point3d segEnd)
        {
            double vx = segEnd.X - segStart.X, vy = segEnd.Y - segStart.Y, vz = segEnd.Z - segStart.Z;
            const double minLenSq = 1e-12;
            double lenSq = vx * vx + vy * vy + vz * vz;
            if (lenSq < minLenSq) return segStart;
            double t = ((point.X - segStart.X) * vx + (point.Y - segStart.Y) * vy + (point.Z - segStart.Z) * vz) / lenSq;
            t = Math.Max(0, Math.Min(1, t));
            return new Point3d(segStart.X + t * vx, segStart.Y + t * vy, segStart.Z + t * vz);
        }

        /// <summary>
        /// Ponto de entrada do dimensionamento em cadeia. Agrupa as dobras paralelas em
        /// "grupos de direção" (dot >= 0.95), depois divide cada grupo em lanes independentes
        /// via <see cref="ClusterByRangeOverlap"/>, e delega a criação de cotas PMI para
        /// cada lane via <see cref="CreateChainForGroup"/>.
        ///
        /// "Lane" = conjunto de dobras paralelas com sobreposição de range (projeção ao longo
        /// da direção da dobra) E comprimentos similares (>= 70%). Dobras na mesma lane
        /// pertencem à mesma aba/flange e são cotadas em uma única cadeia.
        /// Dobras em lanes diferentes (ex.: aba esquerda vs direita, ou flange total vs flange
        /// recortada) são cotadas independentemente.
        /// </summary>
        private static int CreateChainDimensions(Part workPart,
            List<BendAnalysisInfo> bendInfos,
            List<(Point3d start, Point3d end, Vector3d dir, double len, Curve curve)> perimData,
            List<Curve> outerPerim,
            double bboxMinU, double bboxMinV, double bboxMaxU, double bboxMaxV,
            int uAxis, int vAxis, int normalAxis,
            LogFile lw)
        {
            // Remove cotas de execuções anteriores para evitar duplicatas
            DeletePreviousPmiDimensions(workPart, lw);

            // Rastreador de posicionamento para evitar sobreposição entre chains
            var placement = new PlacementTracker();

            int count = 0;
            var used = new bool[bendInfos.Count];

            for (int i = 0; i < bendInfos.Count; i++)
            {
                if (used[i]) continue;

                // Agrupa dobras com aproximadamente a mesma direção (paralelas).
                var group = new List<int> { i };
                used[i] = true;
                for (int j = i + 1; j < bendInfos.Count; j++)
                {
                    if (used[j]) continue;
                    double dot = bendInfos[i].Direction.X * bendInfos[j].Direction.X + bendInfos[i].Direction.Y * bendInfos[j].Direction.Y;
                    if (Math.Abs(dot) >= ParallelismThreshold) { group.Add(j); used[j] = true; }
                }

                // Divide o grupo de direção em lanes por sobreposição de range.
                foreach (var lane in ClusterByRangeOverlap(bendInfos, group, uAxis, vAxis))
                {
                    count += CreateChainForGroup(workPart, bendInfos, lane, perimData, outerPerim,
                        bboxMinU, bboxMinV, bboxMaxU, bboxMaxV, uAxis, vAxis, normalAxis, placement, lw);
                }
            }

            return count;
        }



        /// <summary>
        /// Divide um grupo de dobras paralelas em lanes (abas/flanges independentes).
        ///
        /// Algoritmo (dois passos):
        ///   1. Atribuição greedy: para cada dobra, projeta seus endpoints na direção
        ///      de referência do grupo (refDir) para obter [lo, hi]. Coloca a dobra na
        ///      primeira lane existente que:
        ///        (a) tenha sobreposição de range com [lo, hi], E
        ///        (b) tenha comprimento similar (ratio >= LaneLengthRatioThreshold)
        ///            OU offset próximo ao range da lane (|offset - laneOffset| <= MaxChainGap).
        ///      A condição (b) com MaxChainGap permite que flanges de comprimentos
        ///      diferentes no mesmo nível Y (ex.: recorte parcial da aba superior)
        ///      sejam agrupadas com a aba contínua abaixo dela.
        ///      Se nenhuma lane combinar, cria uma nova lane.
        ///   2. Consolidação: repete até convergência, fundindo pares de lanes que se
        ///      tornaram sobrepostas e têm comprimentos similares OU offsets próximos.
        ///
        /// Propósito: manter a separação entre lados opostos de U-bends (comprimentos
        /// similares mas offsets distantes) enquanto permite que flanges recortadas
        /// parciais se juntem à cadeia principal.
        /// </summary>
        private static List<List<int>> ClusterByRangeOverlap(
            List<BendAnalysisInfo> bendInfos,
            List<int> groupIdx, int uAxis, int vAxis)
        {
            Vector3d refDir = bendInfos[groupIdx[0]].Direction;
            // Normal perpendicular à direção: rotação 90° anti-horária (-dy, dx)
            Vector3d refNml = new Vector3d(-refDir.Y, refDir.X, 0);

            var lanes = new List<List<int>>();
            var laneRanges = new List<(double lo, double hi)>();
            var laneLens = new List<double>();
            var laneOffsetLo = new List<double>();
            var laneOffsetHi = new List<double>();

            foreach (int gi in groupIdx)
            {
                var info = bendInfos[gi];
                double bsU = AxisCoord(info.StartPoint, uAxis), bsV = AxisCoord(info.StartPoint, vAxis);
                double beU = AxisCoord(info.EndPoint, uAxis), beV = AxisCoord(info.EndPoint, vAxis);
                double s = bsU * refDir.X + bsV * refDir.Y;
                double e = beU * refDir.X + beV * refDir.Y;
                double lo = Math.Min(s, e), hi = Math.Max(s, e);
                double len = Math.Sqrt(Math.Pow(beU - bsU, 2) + Math.Pow(beV - bsV, 2));

                // Offset do ponto médio ao longo da normal (distância perpendicular à dobra)
                double mu = (bsU + beU) / 2, mv = (bsV + beV) / 2;
                double offset = mu * refNml.X + mv * refNml.Y;

                bool placed = false;
                for (int k = 0; k < lanes.Count; k++)
                {
                    bool overlaps = lo <= laneRanges[k].hi && hi >= laneRanges[k].lo;
                    bool similarLength = Math.Min(len, laneLens[k]) / Math.Max(len, laneLens[k]) >= LaneLengthRatioThreshold;
                    bool withinOffsetGap = !similarLength &&
                        offset >= laneOffsetLo[k] - MaxChainGap &&
                        offset <= laneOffsetHi[k] + MaxChainGap;

                    if (overlaps && (similarLength || withinOffsetGap))
                    {
                        lanes[k].Add(gi);
                        laneRanges[k] = (Math.Min(laneRanges[k].lo, lo), Math.Max(laneRanges[k].hi, hi));
                        laneLens[k] = Math.Max(laneLens[k], len);
                        laneOffsetLo[k] = Math.Min(laneOffsetLo[k], offset);
                        laneOffsetHi[k] = Math.Max(laneOffsetHi[k], offset);
                        placed = true;
                        break;
                    }
                }
                if (!placed)
                {
                    lanes.Add(new List<int> { gi });
                    laneRanges.Add((lo, hi));
                    laneLens.Add(len);
                    laneOffsetLo.Add(offset);
                    laneOffsetHi.Add(offset);
                }
            }

            // Consolida lanes que se sobrepõem após merges/extensões.
            bool mergedAny = true;
            while (mergedAny)
            {
                mergedAny = false;
                for (int a = 0; a < lanes.Count && !mergedAny; a++)
                {
                    for (int b = a + 1; b < lanes.Count; b++)
                    {
                        bool overlaps = laneRanges[a].lo <= laneRanges[b].hi && laneRanges[a].hi >= laneRanges[b].lo;
                        bool similarLength = Math.Min(laneLens[a], laneLens[b]) / Math.Max(laneLens[a], laneLens[b]) >= LaneLengthRatioThreshold;
                        bool offsetClose = !similarLength &&
                            laneOffsetLo[a] <= laneOffsetHi[b] + MaxChainGap &&
                            laneOffsetLo[b] <= laneOffsetHi[a] + MaxChainGap;

                        if (overlaps && (similarLength || offsetClose))
                        {
                            lanes[a].AddRange(lanes[b]);
                            laneRanges[a] = (Math.Min(laneRanges[a].lo, laneRanges[b].lo), Math.Max(laneRanges[a].hi, laneRanges[b].hi));
                            laneLens[a] = Math.Max(laneLens[a], laneLens[b]);
                            laneOffsetLo[a] = Math.Min(laneOffsetLo[a], laneOffsetLo[b]);
                            laneOffsetHi[a] = Math.Max(laneOffsetHi[a], laneOffsetHi[b]);
                            lanes.RemoveAt(b);
                            laneRanges.RemoveAt(b);
                            laneLens.RemoveAt(b);
                            laneOffsetLo.RemoveAt(b);
                            laneOffsetHi.RemoveAt(b);
                            mergedAny = true;
                            break;
                        }
                    }
                }
            }

            return lanes;
        }

        /// <summary>
        /// Para uma lane (conjunto de dobras paralelas com sobreposição de range):
        ///   1. Projeta o midpoint de cada dobra na direção normal do grupo (refNml)
        ///      para obter um "offset" escalar.
        ///   2. Classifica cada dobra como "lowSide" ou "highSide" comparando a distância
        ///      do offset aos extremos do bbox (bboxLow / bboxHigh projetados na refNml).
        ///      A dobra mais próxima do extreme baixo vai para lowSide; do extreme alto,
        ///      para highSide. Isso corresponde fisicamente às duas extremidades opostas
        ///      da aba no padrão plano.
        ///   3. Ordena cada lado pelo offset (lowSide ASC, highSide DESC) para criar a
        ///      cadeia de cotas na ordem espacial correta.
        ///   4. Chama <see cref="CreateChainSide"/> para cada lado.
        ///
        /// "flipped" indica que a dobra tem direção oposta à dobra de referência do grupo
        /// (dot &lt; 0). Isso inverte qual lado (A=nml+ ou B=nml−) é "externo" na seleção
        /// do boundary em CreateChainSide.
        /// </summary>
        private static int CreateChainForGroup(Part workPart,
            List<BendAnalysisInfo> bendInfos,
            List<int> groupIdx,
            List<(Point3d start, Point3d end, Vector3d dir, double len, Curve curve)> perimData,
            List<Curve> outerPerim,
            double bboxMinU, double bboxMinV, double bboxMaxU, double bboxMaxV,
            int uAxis, int vAxis, int normalAxis,
            PlacementTracker placement,
            LogFile lw)
        {
            var refInfo = bendInfos[groupIdx[0]];
            Vector3d refNml = refInfo.Normal;

            // Projeta os cantos do bbox na direção normal para encontrar extremos.
            double[] cornersOffset = new double[]
            {
                bboxMinU * refNml.X + bboxMinV * refNml.Y,
                bboxMinU * refNml.X + bboxMaxV * refNml.Y,
                bboxMaxU * refNml.X + bboxMinV * refNml.Y,
                bboxMaxU * refNml.X + bboxMaxV * refNml.Y
            };
            double bboxLow = cornersOffset.Min();
            double bboxHigh = cornersOffset.Max();

            // Classifica cada dobra como pertencente ao lado "low" ou "high" dependendo
            // de qual metade do bbox ela está mais próxima.
            var entries = new List<(int idx, double offset, bool flipped)>();
            foreach (int gi in groupIdx)
            {
                var info = bendInfos[gi];
                double dot = info.Direction.X * refInfo.Direction.X + info.Direction.Y * refInfo.Direction.Y;
                bool flipped = dot < 0;
                double offset = AxisCoord(info.MidPoint, uAxis) * refNml.X + AxisCoord(info.MidPoint, vAxis) * refNml.Y;
                entries.Add((gi, offset, flipped));
            }

            var lowSide = entries.Where(e => (e.offset - bboxLow) <= (bboxHigh - e.offset)).OrderBy(e => e.offset).ToList();
            var highSide = entries.Where(e => (e.offset - bboxLow) > (bboxHigh - e.offset)).OrderByDescending(e => e.offset).ToList();

            int count = 0;
            count += CreateChainSide(workPart, bendInfos, lowSide, perimData, outerPerim, isLowSide: true, normalAxis,
                bboxMinU, bboxMinV, bboxMaxU, bboxMaxV, placement, lw);
            count += CreateChainSide(workPart, bendInfos, highSide, perimData, outerPerim, isLowSide: false, normalAxis,
                bboxMinU, bboxMinV, bboxMaxU, bboxMaxV, placement, lw);



            return count;
        }

        /// <summary>
        /// Cria as cotas PMI para um único lado de uma lane (cadeia de dobras paralelas).
        ///
        /// Estrutura da cadeia produzida:
        ///   [outer boundary] ←dim0→ [bend_0] ←dim1→ [bend_1] ←dim2→ ... ←dimN→ [bend_N]
        ///
        /// Seleção do boundary (borda exterior):
        ///   A cota boundary→bend_0 mede a largura da aba, não a espessura do material.
        ///   Por isso o boundary é SEMPRE o segmento paralelo mais DISTANTE (farLineIdx)
        ///   no lado correto — já corrigido por SmallEdgeRatio. Qual lado é "externo"
        ///   depende de isLowSide ⊕ flipped:
        ///     isLow=true,  flipped=false → Lado B (nml−)
        ///     isLow=true,  flipped=true  → Lado A (nml+)
        ///     isLow=false, flipped=false → Lado A (nml+)
        ///     isLow=false, flipped=true  → Lado B (nml−)
        ///   Fallback se farLineIdx não existir: farIdx (pode ser Arc → arc-guard converte
        ///   em linha indicadora); depois lado oposto; por último nearIdx.
        ///
        /// Cada cota é criada por <see cref="CreatePmiRapidDimension"/>.
        /// </summary>
        private static int CreateChainSide(Part workPart,
            List<BendAnalysisInfo> bendInfos,
            List<(int idx, double offset, bool flipped)> side,
            List<(Point3d start, Point3d end, Vector3d dir, double len, Curve curve)> perimData,
            List<Curve> outerPerim,
            bool isLowSide, int normalAxis,
            double bboxMinU, double bboxMinV, double bboxMaxU, double bboxMaxV,
            PlacementTracker placement,
            LogFile lw)
        {
            if (side.Count == 0) return 0;
            int count = 0;

            // Para a 1ª dobra da cadeia, criar uma cota dela até a curva de
            // boundary verdadeira.
            var first = bendInfos[side[0].idx];
            bool flipped = side[0].flipped;

            // --- Seleção do boundary pela borda MAIS DISTANTE ---
            // A cota boundary→dobra deve medir a largura da aba, não a espessura do
            // material. A borda exterior é SEMPRE o segmento paralelo mais distante
            // (farIdx), já corrigido pelo SmallEdgeRatio para ignorar notches de canto.
            //
            // Regra de lado: isLowSide ⊕ flipped determina o lado "para fora":
            //   isLow=true,  flipped=false → Lado B (nml-)
            //   isLow=true,  flipped=true  → Lado A (nml+)
            //   isLow=false, flipped=false → Lado A (nml+)
            //   isLow=false, flipped=true  → Lado B (nml-)
            // Preferimos farLineIdx (somente Lines) sobre farIdx (pode ser Arc, o Arc
            // guard converte em linha indicadora). Fallback para o lado oposto se o
            // lado primário estiver vazio.
            bool useB = isLowSide ^ flipped;

            // --- Corrige outward side para dobras diagonais ---
            // O cálculo de isLow usa a projeção do bbox sobre a normal da dobra,
            // que é preciso para dobras horizontais/verticais mas pode ser enganoso
            // para diagonais: a extensão do bbox numa direção diagonal não reflete
            // a distância real ao perímetro, porque a maioria dos segmentos do
            // perímetro não é paralela à direção diagonal.
            // Em vez disso, usa a distância real ao segmento paralelo mais próximo
            // em cada lado: o lado com a MENOR distância é a borda da aba (outward).
            if (Math.Abs(first.Direction.X) > DiagonalBendThreshold && Math.Abs(first.Direction.Y) > DiagonalBendThreshold)
            {
                double distA = first.BestDistA;
                double distB = first.BestDistB;
                if (distA < double.MaxValue && distB < double.MaxValue && distA > 0 && distB > 0)
                {
                    // --- Guarda de comprimento do segmento ---
                    // A comparação BestDist pode ser enganosa quando um dos lados
                    // tem um segmento muito curto (ex.: arco de 1mm de canto), que
                    // aparece como nearest mas não é a borda verdadeira.
                    double lenA = first.BestIdxA >= 0 ? perimData[first.BestIdxA].len : 0;
                    double lenB = first.BestIdxB >= 0 ? perimData[first.BestIdxB].len : 0;
                    const double minSegLen = 5.0;
                    bool validA = lenA >= minSegLen;
                    bool validB = lenB >= minSegLen;

                    if (validA && validB)
                    {
                        // --- Ambos os lados têm segmentos razoáveis → usa distância ---
                        // O lado com a MENOR distância ao segmento paralelo mais
                        // próximo é a borda da aba (outward).
                        bool correctUseB = distB < distA;
                        if (correctUseB != useB)
                        {
                            lw.WriteLine($"  [Chain] Diagonal bend: bbox-based useB={useB}" +
                                $" (A={distA:F1}/{lenA:F1} B={distB:F1}/{lenB:F1}) → flipping to useB={correctUseB}");
                            useB = correctUseB;
                        }
                    }
                    else if (!validA && validB && !useB)
                    {
                        // Side A tem segmento muito curto — força Side B
                        lw.WriteLine($"  [Chain] Diagonal bend: Side A boundary too short" +
                            $" (len={lenA:F1}<{minSegLen}), forcing Side B (len={lenB:F1})");
                        useB = true;
                    }
                    else if (!validB && validA && useB)
                    {
                        // Side B tem segmento muito curto — força Side A
                        lw.WriteLine($"  [Chain] Diagonal bend: Side B boundary too short" +
                            $" (len={lenB:F1}<{minSegLen}), forcing Side A (len={lenA:F1})");
                        useB = false;
                    }
                    // else: ambos inválidos ou já no lado correto → mantém bbox
                }
            }

            int primaryFarLine = useB ? first.FarLineIdxB : first.FarLineIdxA;
            int primaryFar     = useB ? first.FarIdxB     : first.FarIdxA;
            int secondFarLine  = useB ? first.FarLineIdxA : first.FarLineIdxB;
            int secondFar      = useB ? first.FarIdxA     : first.FarIdxB;

            int boundaryIdx = primaryFarLine >= 0 ? primaryFarLine
                           : primaryFar     >= 0 ? primaryFar
                           : secondFarLine  >= 0 ? secondFarLine
                           :                       secondFar;

            if (boundaryIdx < 0)
            {
                // --- Fallback final: curva mais próxima sem filtro de paralelismo ---
                // Quando nenhum candidato paralelo sobreviveu, usa o nearIdx do lado
                // correto. O lado depende de isLowSide e se a dobra está "flipped"
                // (direção invertida em relação à referência do grupo).
                int fallbackIdx = isLowSide
                    ? (flipped ? first.NearIdxA : first.NearIdxB)
                    : (flipped ? first.NearIdxB : first.NearIdxA);
                if (fallbackIdx >= 0 && perimData[fallbackIdx].curve is Line)
                {
                    lw.WriteLine($"  [Chain] Fallback to nearest perimeter for Bend Tag={first.Bend.Tag} on {(isLowSide ? "low" : "high")} side. perimTag={perimData[fallbackIdx].curve.Tag}");
                    boundaryIdx = fallbackIdx;
                }
                else
                {
                    lw.WriteLine($"  [Chain] No boundary curve found for Bend Tag={first.Bend.Tag} on {(isLowSide ? "low" : "high")} side.");
                }
            }
            lw.WriteLine($"  [Chain] Bend[{first.Index}] Tag={first.Bend.Tag} isLow={isLowSide} flipped={flipped} useB={useB}" +
                $"  primaryFarLine={( primaryFarLine >= 0 ? perimData[primaryFarLine].curve.Tag.ToString() : "-")}" +
                $"  primaryFar={( primaryFar >= 0 ? perimData[primaryFar].curve.Tag.ToString() : "-")}" +
                $"  → boundary={(boundaryIdx >= 0 ? perimData[boundaryIdx].curve.Tag.ToString() : "NONE")}");

            if (boundaryIdx >= 0)
            {
                var seg = perimData[boundaryIdx];
                // Projeta o midpoint da dobra sobre o segmento de boundary para obter
                // o ponto de pick exato sobre a curva (necessário p/ associatividade).
                Point3d boundaryPoint = ProjectPointOnSegment(first.MidPoint, seg.start, seg.end);
                if (!(seg.curve is Line))
                {
                    // --- Boundary é Arc: indicator-line vermelha em vez de cota ---
                    // PmiRapidDimensionBuilder com MeasurementMethod.Perpendicular exige
                    // uma Line como 1ª referência. Arcs (fillet de canto) causam o erro
                    // NX 1175009 e o point-fallback daria um valor errado. Em vez disso,
                    // desenhamos uma linha vermelha (cor 36) indicando a distância.
                    lw.WriteLine($"  [Chain] Boundary for Bend Tag={first.Bend.Tag} is {seg.curve.GetType().Name} (not Line) - indicator line only");
                    try
                    {
                        Line indicator = workPart.Curves.CreateLine(boundaryPoint, first.MidPoint);
                        indicator.Color = 36;
                        indicator.SetUserAttribute("FlatPatternHighlight", 0, "true", NXOpen.Update.Option.Later);
                    }
                    catch (Exception exL) { lw.WriteLine($"  [Chain] Indicator line failed: {exL.Message}"); }
                }
                else
                {
                    // --- Verifica se jah existe uma cota boundary com o mesmo valor ---
                    // Duas lanes diferentes podem ter a primeira dobra na mesma distancia
                    // do perimetro (ex.: ambas 23.4mm). Isso geraria cotas redundantes.
                    double boundaryDist = Math.Sqrt(
                        (boundaryPoint.X - first.MidPoint.X) * (boundaryPoint.X - first.MidPoint.X) +
                        (boundaryPoint.Y - first.MidPoint.Y) * (boundaryPoint.Y - first.MidPoint.Y) +
                        (boundaryPoint.Z - first.MidPoint.Z) * (boundaryPoint.Z - first.MidPoint.Z));
                    // --- Chave de deduplicacao: valor + direcao UV ---
                    // A direcao no plano da chapa determina de qual LADO a cota sai
                    // (U+ = direita, U- = esquerda, V+ = cima, V- = baixo). Varias
                    // lanes na mesma borda com o mesmo valor sao redundantes; lanes
                    // em bordas opostas sao independentes.
                    int uIdx = normalAxis == 0 ? 1 : 0;
                    int vIdx = normalAxis <= 1 ? 2 : 1;
                    double bdU = AxisCoord(boundaryPoint, uIdx) - AxisCoord(first.MidPoint, uIdx);
                    double bdV = AxisCoord(boundaryPoint, vIdx) - AxisCoord(first.MidPoint, vIdx);
                    string dirTag;
                    if (Math.Abs(bdU) >= Math.Abs(bdV))
                        dirTag = bdU >= 0 ? "U+" : "U-";
                    else
                        dirTag = bdV >= 0 ? "V+" : "V-";
                    string dedupKey = $"{boundaryDist.ToString("F" + DimensionDecimalPlaces, System.Globalization.CultureInfo.InvariantCulture)}|{dirTag}|{(isLowSide ? "L" : "H")}";
                    if (placement != null && placement.IsKeyDuplicate(dedupKey))
                    {
                        lw.WriteLine($"  [Chain] Skipping boundary dim for Bend Tag={first.Bend.Tag} -" +
                            $" duplicate key [{dedupKey}] already created");
                    }
                    else
                    {
                        Point3d origin = CreateChainOrigin(
                            boundaryPoint,
                            first.MidPoint,
                            0,
                            bboxMinU, bboxMinV, bboxMaxU, bboxMaxV,
                            normalAxis, placement);
                        if (CreatePmiRapidDimension(workPart, seg.curve, boundaryPoint, first.Bend, first.MidPoint, origin, normalAxis, lw))
                        {
                            placement?.RegisterBoundaryKey(dedupKey);
                            count++;
                        }
                    }
                }
            }

            // --- Cotas entre dobras consecutivas da cadeia ---
            // (sem boundary: midpoint→midpoint das dobras vizinhas)
            //
            // Pula dobras no mesmo offset (mesmo nível Y / mesmo lado da flange) —
            // são flanges paralelas diferentes na mesma borda (ex.: recorte central
            // + flanges laterais à mesma altura). A distância perpendicular entre
            // elas é ~0 e não deve gerar cota. O chain conecta corretamente a
            // primeira dobra de cada nível consecutivo.
            for (int k = 1; k < side.Count; k++)
            {
                var prev = bendInfos[side[k - 1].idx];
                var curr = bendInfos[side[k].idx];

                // Calcula a distância perpendicular entre as duas dobras paralelas.
                double du = curr.MidPoint.X - prev.MidPoint.X;
                double dv = curr.MidPoint.Y - prev.MidPoint.Y;
                double dw = curr.MidPoint.Z - prev.MidPoint.Z;
                double perpDist = Math.Abs(du * prev.Normal.X + dv * prev.Normal.Y + dw * prev.Normal.Z);
                const double sameLevelThreshold = 0.5;
                if (perpDist < sameLevelThreshold)
                {
                    lw.WriteLine($"  [Chain] Skipping dim between Bend[{prev.Index}] and Bend[{curr.Index}] — same level (dist={perpDist:F3})");
                    continue;
                }

                Point3d origin = CreateChainOrigin(
                    prev.MidPoint,
                    curr.MidPoint,
                    k,
                    bboxMinU, bboxMinV, bboxMaxU, bboxMaxV,
                    normalAxis, placement);
                if (CreatePmiRapidDimension(workPart, prev.Bend, prev.MidPoint, curr.Bend, curr.MidPoint, origin, normalAxis, lw))
                    count++;
            }

            return count;
        }

        /// <summary>
        /// Encontra a view de modeling do flat pattern. Usada como contexto de view para associatividade PMI,
        /// seguindo o padrão de gravação de journal do NX. Faz fallback para a work view se não encontrada.
        /// </summary>
        private static NXOpen.ModelingView FindFlatPatternView(Part workPart)
        {
            foreach (NXOpen.ModelingView mv in workPart.ModelingViews)
                if (mv.Name.IndexOf("FLAT", StringComparison.OrdinalIgnoreCase) >= 0)
                    return mv;
            return workPart.ModelingViews.WorkView;
        }

        /// <summary>
        /// Cria uma cota PMI perpendicular entre dois objetos Curve (curveA → curveB).
        ///
        /// Usa PmiRapidDimensionBuilder com MeasurementMethod.Perpendicular, que referencia
        /// os objetos Curve reais. O NX calcula a distância perpendicular geométrica
        /// verdadeira — funciona para dobras horizontais, verticais e diagonais sem nenhuma
        /// correção de coordenada pelo código.
        ///
        /// Cascata de fallback (ordem de preferência):
        ///   1. Cota PMI perpendicular (2 Curves, PmiRapidDimensionBuilder) — caminho ideal.
        ///      O texto da cota é posicionado em <paramref name="origin"/>.
        ///   2. Se ErrorCode 948802 (sem licença PMI/GD&amp;T) → indicator-line vermelha (cor 36)
        ///      entre os pontos de pick, retorna false (não cria cota).
        ///   3. Se outro NXException ou Exception do builder → point-fallback:
        ///      cria dois Points auxiliares (blanked) e uma cota Horizontal/Vertical entre eles.
        ///      Direção escolhida pelo eixo de maior variação entre os pontos vs. normalAxis.
        ///      Ver <see cref="CreatePmiPointFallbackDimension"/>.
        ///
        /// Cada tentativa usa um UndoMark para permitir rollback seguro em caso de falha.
        /// Cotas criadas recebem o atributo de usuário "FlatPatternHighlight=true" para
        /// identificação e limpeza posterior.
        /// </summary>
        private static bool CreatePmiRapidDimension(Part workPart,
            Curve curveA, Point3d pickA,
            Curve curveB, Point3d midB,
            Point3d origin,
            int normalAxis,
            LogFile lw)
        {
            NXOpen.Session.UndoMarkId undoMark =
                theSession.SetUndoMark(NXOpen.Session.MarkVisibility.Invisible, "PMI dim attempt");

            try
            {
                NXOpen.ModelingView flatView = FindFlatPatternView(workPart);

                NXOpen.Annotations.PmiRapidDimensionBuilder builder =
                    workPart.Dimensions.CreatePmiRapidDimensionBuilder(null);

                builder.Measurement.Method =
                    NXOpen.Annotations.DimensionMeasurementBuilder.MeasurementMethod.Perpendicular;

                builder.Origin.Plane.PlaneMethod =
                    NXOpen.Annotations.PlaneBuilder.PlaneMethodType.ModelView;
                builder.Origin.Anchor =
                    NXOpen.Annotations.OriginBuilder.AlignmentPosition.MidCenter;

                // Aplica precisão (casas decimais) configurada pelo usuário.
                // DimensionValuePrecision: 0=inteiro, 1=1 decimal, 2=2 decimais, etc.
                builder.Style.DimensionStyle.DimensionValuePrecision = DimensionDecimalPlaces;

                builder.FirstAssociativity.SetValue(curveA, flatView, pickA);
                builder.SecondAssociativity.SetValue(
                    NXOpen.InferSnapType.SnapType.Mid,
                    curveB, flatView, midB,
                    null, null,
                    new Point3d(0, 0, 0));

                builder.AssociatedObjects.Nxobjects.SetArray(
                    new NXOpen.NXObject[] { curveB, curveA });

                var assocOrigin = new NXOpen.Annotations.Annotation.AssociativeOriginData();
                assocOrigin.OriginType              = NXOpen.Annotations.AssociativeOriginType.Drag;
                assocOrigin.View                    = null;
                assocOrigin.ViewOfGeometry          = null;
                assocOrigin.PointOnGeometry         = null;
                assocOrigin.VertAnnotation          = null;
                assocOrigin.VertAlignmentPosition   = NXOpen.Annotations.AlignmentPosition.TopLeft;
                assocOrigin.HorizAnnotation         = null;
                assocOrigin.HorizAlignmentPosition  = NXOpen.Annotations.AlignmentPosition.TopLeft;
                assocOrigin.AlignedAnnotation       = null;
                assocOrigin.DimensionLine           = 0;
                assocOrigin.AssociatedView          = null;
                assocOrigin.AssociatedPoint         = null;
                assocOrigin.OffsetAnnotation        = null;
                assocOrigin.OffsetAlignmentPosition = NXOpen.Annotations.AlignmentPosition.TopLeft;
                assocOrigin.XOffsetFactor           = 0.0;
                assocOrigin.YOffsetFactor           = 0.0;
                assocOrigin.StackAlignmentPosition  = NXOpen.Annotations.StackAlignmentPosition.Above;
                builder.Origin.SetAssociativeOrigin(assocOrigin);
                builder.Origin.Origin.SetValue(null, null, origin);
                builder.Origin.SetInferRelativeToGeometry(false);

                NXOpen.NXObject dimObj = builder.Commit();
                theSession.DeleteUndoMark(undoMark, null);
                builder.Destroy();

                if (dimObj != null)
                    dimObj.SetUserAttribute("FlatPatternHighlight", 0, "true", NXOpen.Update.Option.Later);

                return dimObj != null;
            }
            catch (NXException nex) when (nex.ErrorCode == 948802)
            {
                // --- Sem licença PMI/GD&T (erro 948802) ---
                // Desfaz o builder e desenha uma indicator-line vermelha (cor 36) entre
                // os dois pontos. Não há cota, mas a distância fica visível/logada.
                try { theSession.UndoToMark(undoMark, null); } catch { }
                try { theSession.DeleteUndoMark(undoMark, null); } catch { }

                double dist = Math.Sqrt(
                    (midB.X - pickA.X) * (midB.X - pickA.X) +
                    (midB.Y - pickA.Y) * (midB.Y - pickA.Y));
                lw.WriteLine($"  [PMI] No PMI license (948802) - indicator line, dist={dist:F2} mm");

                try
                {
                    Line indicator = workPart.Curves.CreateLine(pickA, midB);
                    indicator.Color = 36;
                    indicator.SetUserAttribute("FlatPatternHighlight", 0, "true", NXOpen.Update.Option.Later);
                }
                catch (Exception exLine)
                {
                    lw.WriteLine($"  [PMI] Indicator line failed: {exLine.Message}");
                }
                return false;
            }
            catch (NXException nex)
            {
                // --- Erro do builder (ex.: referência inválida, geometria degenerada) ---
                // Desfaz e tenta o point-fallback (cota entre 2 Points auxiliares).
                try { theSession.UndoToMark(undoMark, null); } catch { }
                try { theSession.DeleteUndoMark(undoMark, null); } catch { }
                try { lw.WriteLine($"  [PMI] Rapid builder failed, trying point fallback. ErrorCode={nex.ErrorCode}  {nex.Message}"); } catch { }
                return CreatePmiPointFallbackDimension(workPart, pickA, midB, origin, normalAxis, lw);
            }
            catch (Exception ex)
            {
                // --- Erro não-NX (ex.: NullReference) ---
                // Mesma estratégia: desfaz e tenta o point-fallback.
                try { theSession.UndoToMark(undoMark, null); } catch { }
                try { theSession.DeleteUndoMark(undoMark, null); } catch { }
                try { lw.WriteLine($"  [PMI] Rapid builder failed, trying point fallback. {ex.GetType().Name}: {ex.Message}"); } catch { }
                return CreatePmiPointFallbackDimension(workPart, pickA, midB, origin, normalAxis, lw);
            }
        }

        /// <summary>
        /// Fallback de cota PMI quando o PmiRapidDimensionBuilder falha: cria dois Points
        /// auxiliares (blanked) e uma cota horizontal/vertical entre eles. Não é
        /// perpendicular como o caminho ideal, mas produz um valor dimensionável.
        /// A direção (horizontal vs vertical) é escolhida pelo eixo de maior variação
        /// entre os pontos, considerando a normal do plano do flat pattern.
        /// </summary>
        private static bool CreatePmiPointFallbackDimension(
            Part workPart,
            Point3d pointA,
            Point3d pointB,
            Point3d origin,
            int normalAxis,
            LogFile lw)
        {
            try
            {
                Point helperA = workPart.Points.CreatePoint(pointA);
                helperA.Blank();
                helperA.SetUserAttribute("FlatPatternHighlightHelper", 0, "true", NXOpen.Update.Option.Later);

                Point helperB = workPart.Points.CreatePoint(pointB);
                helperB.Blank();
                helperB.SetUserAttribute("FlatPatternHighlightHelper", 0, "true", NXOpen.Update.Option.Later);

                var dimData = workPart.Annotations.NewDimensionData();

                var assoc1 = workPart.Annotations.NewAssociativity();
                assoc1.FirstObject = helperA;
                assoc1.FirstDefinitionPoint = pointA;
                assoc1.PickPoint = pointA;
                assoc1.PointOption = NXOpen.Annotations.AssociativityPointOption.Control;
                dimData.SetAssociativity(1, new NXOpen.Annotations.Associativity[] { assoc1 });

                var assoc2 = workPart.Annotations.NewAssociativity();
                assoc2.FirstObject = helperB;
                assoc2.FirstDefinitionPoint = pointB;
                assoc2.PickPoint = pointB;
                assoc2.PointOption = NXOpen.Annotations.AssociativityPointOption.Control;
                dimData.SetAssociativity(2, new NXOpen.Annotations.Associativity[] { assoc2 });

                var pmiData = workPart.Annotations.NewPmiData();
                Xform plane = GetAnnotationPlaneForNormalAxis(workPart, normalAxis);

                double dx = Math.Abs(pointB.X - pointA.X);
                double dy = Math.Abs(pointB.Y - pointA.Y);
                double dz = Math.Abs(pointB.Z - pointA.Z);

                NXObject dimObj;
                if (normalAxis == 0)
                {
                    if (dy >= dz)
                        dimObj = workPart.Dimensions.CreatePmiHorizontalDimension(dimData, pmiData, plane, origin);
                    else
                        dimObj = workPart.Dimensions.CreatePmiVerticalDimension(dimData, pmiData, plane, origin);
                }
                else if (normalAxis == 1)
                {
                    if (dx >= dz)
                        dimObj = workPart.Dimensions.CreatePmiHorizontalDimension(dimData, pmiData, plane, origin);
                    else
                        dimObj = workPart.Dimensions.CreatePmiVerticalDimension(dimData, pmiData, plane, origin);
                }
                else
                {
                    if (dx >= dy)
                        dimObj = workPart.Dimensions.CreatePmiHorizontalDimension(dimData, pmiData, plane, origin);
                    else
                        dimObj = workPart.Dimensions.CreatePmiVerticalDimension(dimData, pmiData, plane, origin);
                }

                if (dimObj != null)
                    dimObj.SetUserAttribute("FlatPatternHighlight", 0, "true", NXOpen.Update.Option.Later);

                return dimObj != null;
            }
            catch (Exception fallbackEx)
            {
                try { lw.WriteLine($"  [PMI] Point fallback failed: {fallbackEx.Message}"); } catch { }
                return false;
            }
        }

        private static Xform GetAnnotationPlaneForNormalAxis(Part workPart, int normalAxis)
        {
            if (normalAxis == 0)
                return workPart.Annotations.GetDefaultAnnotationPlane(NXOpen.Annotations.PmiDefaultPlane.YzOfWcs);
            if (normalAxis == 1)
                return workPart.Annotations.GetDefaultAnnotationPlane(NXOpen.Annotations.PmiDefaultPlane.XzOfWcs);
            return workPart.Annotations.GetDefaultAnnotationPlane(NXOpen.Annotations.PmiDefaultPlane.XyOfWcs);
        }

        /// <summary>
        /// Calcula a origem (posição do texto da cota) para uma cota da cadeia.
        ///
        /// Posicionamento:
        ///   - As cotas são empilhadas fora do bbox com uma margem base + espaçamento por
        ///     nível (level 0 = cota mais externa, level 1 = próxima, etc.).
        ///   - Se a medição é predominantemente V-dominante (|ΔV| >= |ΔU|), o texto vai
        ///     para fora da borda U-máxima (lado direito do padrão plano).
        ///   - Se é predominantemente U-dominante (|ΔU| > |ΔV|), vai para fora da borda
        ///     V-máxima (topo do padrão plano).
        ///   - margin e spacing são proporcionais ao tamanho do bbox para funcionar
        ///     com peças de tamanhos muito diferentes.
        ///
        /// Conversão UV → XYZ: depende de normalAxis (eixo perpendicular ao plano do
        /// padrão plano):
        ///   normalAxis=0 (X-normal) → (normalVal, textU, textV) — U=Y, V=Z
        ///   normalAxis=1 (Y-normal) → (textU, normalVal, textV) — U=X, V=Z
        ///   normalAxis=2 (Z-normal) → (textU, textV, normalVal) — U=X, V=Y
        /// </summary>
        private static Point3d CreateChainOrigin(
            Point3d pointA,
            Point3d pointB,
            int level,  // used only for same-chain level offset (k-index)
            double bboxMinU,
            double bboxMinV,
            double bboxMaxU,
            double bboxMaxV,
            int normalAxis,
            PlacementTracker placement)
        {
            double bboxExtent = Math.Max(bboxMaxU - bboxMinU, bboxMaxV - bboxMinV);
            double margin = Math.Max(25.0, bboxExtent * 0.03);
            double spacing = Math.Max(18.0, bboxExtent * 0.025);
            double offset = margin + level * spacing;

            // Coordenada constante ao longo do eixo normal do flat pattern
            double normalVal = normalAxis == 0 ? pointA.X : normalAxis == 1 ? pointA.Y : pointA.Z;

            // UV axis indices: normalAxis=0 -> U=Y(1),V=Z(2);  normalAxis=1 -> U=X(0),V=Z(2);  normalAxis=2 -> U=X(0),V=Y(1)
            int uIdx = normalAxis == 0 ? 1 : 0;
            int vIdx = normalAxis <= 1  ? 2 : 1;

            double uA = AxisCoord(pointA, uIdx), vA = AxisCoord(pointA, vIdx);
            double uB = AxisCoord(pointB, uIdx), vB = AxisCoord(pointB, vIdx);

            double midU = (uA + uB) / 2.0;
            double midV = (vA + vB) / 2.0;

            // Determina orientação e nível: cada chain tem seu próprio nível
            // de posicionamento por lado, evitando sobreposição entre chains.
            double textU, textV;
            if (Math.Abs(vB - vA) >= Math.Abs(uB - uA))
            {
                // V-dominante → texto fora da borda U-alta (lado direito)
                int sideLevel = placement.VDominantLevel++;
                offset = margin + sideLevel * spacing;
                textU = bboxMaxU + offset;
                textV = midV;
            }
            else
            {
                // U-dominante → texto fora da borda V-alta (lado superior)
                int sideLevel = placement.UDominantLevel++;
                offset = margin + sideLevel * spacing;
                textU = midU;
                textV = bboxMaxV + offset;
            }

            // Converte UV + valor normal de volta para XYZ
            switch (normalAxis)
            {
                case 0:  return new Point3d(normalVal, textU, textV);   // U=Y, V=Z
                case 1:  return new Point3d(textU, normalVal, textV);   // U=X, V=Z
                default: return new Point3d(textU, textV, normalVal);   // U=X, V=Y
            }
        }

        // =====================================================================
        // GEOMETRY HELPERS
        // =====================================================================

        /// <summary>
        /// Calcula a distância de um ponto (px, py) até a borda do bounding box em
        /// uma determinada direção (dx, dy) usando ray-casting. Retorna a menor
        /// distância paramétrica positiva t ao longo do raio até qualquer face do bbox.
        /// Um valor de retorno -1 significa que nenhuma interseção foi encontrada.
        /// </summary>
        private static double DistToBboxEdge(double px, double py, double dx, double dy,
            double bminX, double bminY, double bmaxX, double bmaxY)
        {
            double txMin = dx != 0 ? (bminX - px) / dx : double.MaxValue;
            double txMax = dx != 0 ? (bmaxX - px) / dx : double.MaxValue;
            double tyMin = dy != 0 ? (bminY - py) / dy : double.MaxValue;
            double tyMax = dy != 0 ? (bmaxY - py) / dy : double.MaxValue;

            double t = double.MaxValue;
            foreach (double ti in new[] { txMin, txMax, tyMin, tyMax })
            {
                const double rayEpsilon = 1e-6; // pula interseções na/da origem
                if (ti > rayEpsilon && ti < t) t = ti;
            }
            return t == double.MaxValue ? -1 : t;
        }

        // =====================================================================
        // FLAT SOLID BODY LOCATION
        // =====================================================================

        /// <summary>
        /// Tenta encontrar o corpo sólido plano que corresponde à feature FlatPattern.
        /// Usa três estratégias em ordem de confiabilidade:
        ///
        ///   1. flatPattern.GetBodies() — a API oficial, mas pode retornar null
        ///      em algumas peças onde o sólido plano não está exposto.
        ///   2. flatPattern.GetEntities() + cast para Body — API alternativa.
        ///   3. Itera por workPart.Bodies, verifica GetFeatures() por uma
        ///      feature FlatPattern ou FlatSolid, e compara por tag.
        ///
        /// Um log de diagnóstico estendido é produzido no nível [diag] para ajudar
        /// a entender por que o corpo pode não ser encontrado em uma determinada peça.
        /// </summary>
        private static Body FindFlatSolidBody(Part workPart, FlatPattern flatPattern, LogFile lw)
        {
            // Estratégia 1: API oficial.
            Body[] bodies = flatPattern.GetBodies();
            lw.WriteLine($"  [diag] flatPattern.GetBodies() count: {(bodies != null ? bodies.Length : -1)}");
            if (bodies != null && bodies.Length > 0) { lw.WriteLine($"  Body via GetBodies(): {bodies[0].Tag}"); return bodies[0]; }

            // Estratégia 2: varredura de entidades.
            NXObject[] entities = flatPattern.GetEntities();
            lw.WriteLine($"  [diag] flatPattern.GetEntities() count: {(entities != null ? entities.Length : -1)}");
            if (entities != null)
            {
                foreach (var ent in entities)
                    lw.WriteLine($"  [diag]   entity type: {ent.GetType().Name}");
                foreach (var ent in entities) { Body b = ent as Body; if (b != null) { lw.WriteLine($"  Body via GetEntities(): {b.Tag}"); return b; } }
            }

            // Estratégia 3: varredura exaustiva de corpos (mais caro, mais confiável).
            int bodyIdx = 0;
            foreach (Body body in workPart.Bodies)
            {
                bodyIdx++;
                try
                {
                    bool isSolid = body.IsSolidBody;
                    Face[] bf = body.GetFaces();
                    Feature[] features = body.GetFeatures();
                    string featNames = "";
                    foreach (Feature f in features) featNames += $"{f.GetType().Name}(\"{f.GetFeatureName()}\") ";
                    lw.WriteLine($"  [diag] body #{bodyIdx} Tag={body.Tag}  IsSolidBody={isSolid}  Faces={bf.Length}  Features=[{featNames}]");

                    // Procura por uma feature FlatPattern com tag correspondente.
                    foreach (Feature f in features)
                        if (f is FlatPattern fp && fp.Tag == flatPattern.Tag)
                        { lw.WriteLine($"  Body via workPart.Bodies (FlatPattern feature): {body.Tag}"); return body; }

                    // Também corresponde a FlatSolid (o corpo 3D resultante do flat pattern).
                    foreach (Feature f in features)
                        if (f is NXOpen.Features.FlatSolid)
                        { lw.WriteLine($"  Body via workPart.Bodies (FlatSolid feature): {body.Tag}"); return body; }
                }
                catch (Exception ex)
                {
                    lw.WriteLine($"  [diag] body #{bodyIdx} Tag={body.Tag}  EXCEPTION: {ex.Message}");
                }
            }
            lw.WriteLine($"  [diag] workPart.Bodies total checked: {bodyIdx}");
            return null;
        }

        // =====================================================================
        // UTILITY METHODS
        // =====================================================================

        /// <summary>
        /// Locate the first FlatPattern feature in the part's feature tree.
        /// Returns null if no flat pattern exists.
        /// </summary>
        private static FlatPattern FindFlatPattern(Part workPart, LogFile lw = null)
        {
            // Iterating workPart.Features can trigger NX's internal PartCollection.FindObject
            // when the flat pattern feature resolves its source part (which may not be loaded).
            // Catch it here so the exception doesn't propagate to Main.
            try
            {
                foreach (Feature f in workPart.Features)
                    if (f is FlatPattern) return (FlatPattern)f;
            }
            catch (Exception ex)
            {
                lw?.WriteLine($"FindFlatPattern: {ex.GetType().Name} — {ex.Message}");
            }
            return null;
        }

        /// <summary>
        /// Get the start and end points of any Curve (Line or Arc).
        /// For arcs, computes the endpoints from the arc centre, radius, and
        /// start/end angles. Returns (0,0,0) for unsupported curve types.
        /// </summary>
        private static void GetEndPoints(Curve curve, out Point3d p1, out Point3d p2)
        {
            Line line = curve as Line;
            if (line != null) { p1 = line.StartPoint; p2 = line.EndPoint; return; }

            Arc arc = curve as Arc;
            if (arc != null)
            {
                // StartAngle / EndAngle are in RADIANS — do NOT convert from degrees.
                // The angles are measured relative to the X and Y axes of the arc's
                // orientation matrix (Conic.Matrix), so we apply the full 3x3 transform
                // to compute correct endpoint positions in 3D space.
                double r = arc.Radius;
                Point3d c = arc.CenterPoint;
                double sa = arc.StartAngle;
                double ea = arc.EndAngle;

                NXMatrix nxMat = arc.Matrix;
                Matrix3x3 mat = nxMat.Element;

                // Orientation X and Y axes (columns of the 3x3 matrix)
                Vector3d xDir = new Vector3d(mat.Xx, mat.Xy, mat.Xz);
                Vector3d yDir = new Vector3d(mat.Yx, mat.Yy, mat.Yz);

                double cosSa = Math.Cos(sa), sinSa = Math.Sin(sa);
                double cosEa = Math.Cos(ea), sinEa = Math.Sin(ea);

                p1 = new Point3d(
                    c.X + r * (cosSa * xDir.X + sinSa * yDir.X),
                    c.Y + r * (cosSa * xDir.Y + sinSa * yDir.Y),
                    c.Z + r * (cosSa * xDir.Z + sinSa * yDir.Z));
                p2 = new Point3d(
                    c.X + r * (cosEa * xDir.X + sinEa * yDir.X),
                    c.Y + r * (cosEa * xDir.Y + sinEa * yDir.Y),
                    c.Z + r * (cosEa * xDir.Z + sinEa * yDir.Z));
                return;
            }
            p1 = new Point3d(0, 0, 0); p2 = new Point3d(0, 0, 0);
        }

        /// <summary>
        /// Destaca todos os objetos na lista (usado para chamar atenção para
        /// o perímetro externo, linhas de dobra, etc. na janela gráfica do NX).
        /// </summary>
        private static void HighlightObjects(List<DisplayableObject> objects)
        {
            if (objects.Count == 0) return;
            foreach (var o in objects) o.Highlight();
        }

        /// <summary>
        /// Limpa o estado de highlight transitório dos objetos displayable após a análise.
        /// </summary>
        private static void UnhighlightObjects(IEnumerable<Curve> curves)
        {
            if (curves == null) return;
            foreach (var curve in curves)
                curve?.Unhighlight();
        }

        // =====================================================================
        // NX Menu / Lifecycle
        // =====================================================================

        /// <summary>
        /// Requerido pelo NX: retorna o comportamento de descarregamento.
        /// Immediately = a DLL pode ser descarregada após a ação completar.
        ///
        /// Nota: O arquivo .men agora usa a sintaxe NXOpen::ClassName.Method
        /// (ACTIONS NXOpen::FlatPatternHighlight.HighlightFlatPattern::Main),
        /// que chama Main() diretamente sem precisar de AddMenuAction() ou
        /// um Startup() assinado. Isso funciona tanto para Ctrl+U quanto para auto-load.
        /// </summary>
        public static int GetUnloadOption(string arg) { return (int)Session.LibraryUnloadOption.Immediately; }

        /// <summary>
        /// Chamado pelo NX quando a DLL é carregada na inicialização (requer assinatura).
        /// Não registra mais AddMenuAction — a sintaxe NXOpen:: do arquivo .men
        /// resolve a ação diretamente. Mantido como um método de ciclo de vida exigido pelo NX.
        /// </summary>
        public static int Startup(string[] args)
        {
            return 0;
        }
    }
}
