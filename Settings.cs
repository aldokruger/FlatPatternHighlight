using System;
using System.Collections.Generic;
using System.IO;

namespace FlatPatternHighlight
{
    /// <summary>Configurações carregadas de arquivo JSON por usuário.
    /// Cria o arquivo em %APPDATA%\FlatPatternHighlight\settings.json com os valores
    /// padrão se ele não existir. Cada usuário pode ajustar os parâmetros sem recompilar.
    /// </summary>
    internal sealed class Settings
    {
        // ---- Campos com valores padrão (usados se o JSON não fornecer o valor) ----

        /// <summary>Produto escalar mínimo para considerar duas direções de curva como paralelas.</summary>
        public double ParallelismThreshold = 0.95;

        /// <summary>
        /// Componente de direção mínimo (|dir.X| ou |dir.Y|) para uma dobra ser considerada "diagonal".
        /// Dobras diagonais têm componentes significativas em ambos os eixos UV e podem não ter
        /// segmentos de perímetro paralelos.
        /// </summary>
        public double DiagonalBendThreshold = 0.2;

        /// <summary>
        /// Limiar de distância ao quadrado (mm²) para pular linhas de dobra cujo ponto médio
        /// está sobre o perímetro externo. Corresponde a um limiar linear de 0.5 mm (0.5² = 0.25).
        /// </summary>
        public double ArtefactSkipDistanceSq = 0.25;

        /// <summary>
        /// Limiar de proporção para detecção de recorte: se bestDist / secondBestDist estiver
        /// abaixo disso, o segmento de perímetro "mais próximo" é provavelmente um entalhe fino
        /// e deve ser ignorado.
        /// </summary>
        public double CutoutSkipRatio = 0.3;

        /// <summary>
        /// Proporção para correção SmallEdge: se o segmento de perímetro mais distante for
        /// mais curto que SmallEdgeRatio × longestSegment, é provavelmente um entalhe de canto
        /// e deve ser substituído pelo mais longo.
        /// </summary>
        public double SmallEdgeRatio = 0.5;

        /// <summary>
        /// Fator de proteção para correção SmallEdge: só substituir o mais distante pelo mais
        /// longo se longestDist >= farDist * SmallEdgeGuardFactor, evitando substituição por
        /// um segmento muito próximo da dobra.
        /// </summary>
        public double SmallEdgeGuardFactor = 0.5;

        /// <summary>
        /// Proporção mínima de comprimento para duas dobras serem consideradas similares
        /// e assim pertencerem à mesma lane.
        /// </summary>
        public double LaneLengthRatioThreshold = 0.7;

        /// <summary>
        /// Distância máxima (mm) ao longo da normal para permitir que duas dobras
        /// de comprimentos diferentes sejam agrupadas na mesma lane.
        /// Quando similarLength falha mas as dobras estão dentro deste gap de offset,
        /// elas são consideradas parte da mesma região (ex.: aba superior recortada
        /// + aba continua abaixo dela) e unidas na mesma cadeia de cotas.
        /// Default: 50 mm (cobre a maioria dos casos de flanges consecutivas).
        /// </summary>
        public double MaxChainGap = 50.0;

        /// <summary>
        /// Número de casas decimais para as cotas PMI.
        /// 0 = inteiro, 1 = 1 casa decimal, 2 = 2 casas decimais, etc.
        /// O usuário pode alterar este valor a qualquer momento editando o
        /// settings.json, sem precisar recompilar a DLL.
        /// </summary>
        public int DimensionDecimalPlaces = 1;

        // =====================================================================
        // Carga / salvamento
        // =====================================================================

        /// <summary>Diretório do settings.json do usuário.</summary>
        internal static readonly string ConfigDir =
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "FlatPatternHighlight");

        /// <summary>Caminho completo do settings.json do usuário.</summary>
        internal static readonly string ConfigPath = Path.Combine(ConfigDir, "settings.json");

        /// <summary>
        /// Carrega as configurações do arquivo JSON do usuário.
        /// Se o arquivo não existir, cria com os valores padrão e retorna os padrões.
        /// Se houver erro de parsing, loga e retorna os padrões.
        /// </summary>
        internal static Settings Load()
        {
            try
            {
                var configDir = ConfigDir;
                var configPath = ConfigPath;

                if (!Directory.Exists(configDir))
                    Directory.CreateDirectory(configDir);

                if (!File.Exists(configPath))
                {
                    var defaults = new Settings();
                    Save(defaults);
                    return defaults;
                }

                var json = File.ReadAllText(configPath);
                return ParseJson(json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[FlatPatternHighlight] Falha ao carregar configurações de '{ConfigPath}': {ex.Message}");
                return new Settings();
            }
        }

        /// <summary>Salva as configurações no arquivo JSON do usuário.</summary>
        internal static void Save(Settings s)
        {
            try
            {
                if (!Directory.Exists(ConfigDir))
                    Directory.CreateDirectory(ConfigDir);
                File.WriteAllText(ConfigPath, ToJson(s));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[FlatPatternHighlight] Falha ao salvar configurações em '{ConfigPath}': {ex.Message}");
            }
        }

        // =====================================================================
        // JSON serialization manual (sem dependência externa)
        // =====================================================================

        private static Settings ParseJson(string json)
        {
            var s = new Settings();
            var map = SimpleJsonParse(json);

            if (map == null) return s;

            TrySet(ref s.ParallelismThreshold, map, nameof(ParallelismThreshold));
            TrySet(ref s.DiagonalBendThreshold, map, nameof(DiagonalBendThreshold));
            TrySet(ref s.ArtefactSkipDistanceSq, map, nameof(ArtefactSkipDistanceSq));
            TrySet(ref s.CutoutSkipRatio, map, nameof(CutoutSkipRatio));
            TrySet(ref s.SmallEdgeRatio, map, nameof(SmallEdgeRatio));
            TrySet(ref s.SmallEdgeGuardFactor, map, nameof(SmallEdgeGuardFactor));
            TrySet(ref s.LaneLengthRatioThreshold, map, nameof(LaneLengthRatioThreshold));
            TrySet(ref s.MaxChainGap, map, nameof(MaxChainGap));
            TrySet(ref s.DimensionDecimalPlaces, map, nameof(DimensionDecimalPlaces));

            return s;
        }

        private static void TrySet(ref double field, Dictionary<string, object> map, string key)
        {
            if (map.TryGetValue(key, out var val) && val is double d)
                field = d;
        }

        private static void TrySet(ref int field, Dictionary<string, object> map, string key)
        {
            if (map.TryGetValue(key, out var val))
            {
                if (val is double d)
                    field = (int)Math.Round(d);
                else if (val is long l)
                    field = (int)l;
            }
        }

        /// <summary>
        /// Parseia um JSON simples de um nível (sem arrays ou objetos aninhados).
        /// Suporta: "chave": 123.45  e  "chave": "texto" (string ignorada).
        /// </summary>
        private static Dictionary<string, object> SimpleJsonParse(string json)
        {
            var result = new Dictionary<string, object>();
            json = json.Trim();
            if (!json.StartsWith("{") || !json.EndsWith("}"))
                return null;

            // Remove chaves externas
            json = json.Substring(1, json.Length - 2).Trim();
            if (string.IsNullOrEmpty(json))
                return result;

            // Split por vírgulas (simples, não lida com strings contendo vírgula)
            var pairs = SplitTopLevel(json);
            foreach (var pair in pairs)
            {
                var eqIdx = pair.IndexOf(':');
                if (eqIdx < 0) continue;

                var key = pair.Substring(0, eqIdx).Trim().Trim('"');
                var rawVal = pair.Substring(eqIdx + 1).Trim();

                if (string.IsNullOrEmpty(key)) continue;

                // Tenta double
                if (double.TryParse(rawVal,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var dval))
                {
                    result[key] = dval;
                }
            }

            return result;
        }

        /// <summary>Divide por vírgulas no primeiro nível (ignora vírgulas dentro de strings/chaves).</summary>
        private static List<string> SplitTopLevel(string s)
        {
            var parts = new List<string>();
            int depth = 0, start = 0;
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '{' || s[i] == '[') depth++;
                else if (s[i] == '}' || s[i] == ']') depth--;
                else if (s[i] == ',' && depth == 0)
                {
                    parts.Add(s.Substring(start, i - start));
                    start = i + 1;
                }
            }
            if (start < s.Length)
                parts.Add(s.Substring(start));
            return parts;
        }

        private static string ToJson(Settings s)
        {
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            return $"{{\n" +
                   $"  \"{nameof(s.ParallelismThreshold)}\": {s.ParallelismThreshold.ToString(ci)},\n" +
                   $"  \"{nameof(s.DiagonalBendThreshold)}\": {s.DiagonalBendThreshold.ToString(ci)},\n" +
                   $"  \"{nameof(s.ArtefactSkipDistanceSq)}\": {s.ArtefactSkipDistanceSq.ToString(ci)},\n" +
                   $"  \"{nameof(s.CutoutSkipRatio)}\": {s.CutoutSkipRatio.ToString(ci)},\n" +
                   $"  \"{nameof(s.SmallEdgeRatio)}\": {s.SmallEdgeRatio.ToString(ci)},\n" +
                   $"  \"{nameof(s.SmallEdgeGuardFactor)}\": {s.SmallEdgeGuardFactor.ToString(ci)},\n" +
                   $"  \"{nameof(s.LaneLengthRatioThreshold)}\": {s.LaneLengthRatioThreshold.ToString(ci)},\n" +
                   $"  \"{nameof(s.MaxChainGap)}\": {s.MaxChainGap.ToString(ci)},\n" +
                   $"  \"{nameof(s.DimensionDecimalPlaces)}\": {s.DimensionDecimalPlaces}\n" +
                   "}";
        }
    }
}
