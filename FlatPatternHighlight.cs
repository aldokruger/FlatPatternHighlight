using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using NXOpen;
using NXOpen.Features;

namespace FlatPatternHighlight
{
    /// <summary>
    /// NXOpen plugin (C# .NET) for analyzing Sheet Metal flat patterns in NX 2512.
    ///
    /// The plugin runs in 3 sequential steps that build on each other:
    ///
    ///   1. OUTER PERIMETER FILTERING
    ///      Reduces the raw exterior curves (which include notch/cutout boundaries) to only
    ///      the true external boundary edges. Strategy: uses UF_MODL_ask_face_loops (P/Invoke
    ///      into libufun.dll) to ask each face of the flat solid for its loops, keeps only
    ///      outer loops (type == 1), then intersects with the FlatSolid body edges to filter
    ///      out any inner holes. Falls back to a purely geometric convex-hull approximation
    ///      when the P/Invoke path is unavailable.
    ///
    ///   2. BEND CENTER LINES
    ///      Enumerates bend-up and bend-down center lines via FlatPattern.GetBendUpCenterLines /
    ///      GetBendDownCenterLines. Highlights each line in NX with a dedicated color and
    ///      attaches a "FlatPatternHighlight" user attribute so they can be identified later.
    ///      Skips artefact curves whose midpoint lies on (or within 0.5 mm of) the outer
    ///      perimeter — some NX configurations return perimeter edges from these APIs.
    ///
    ///   3. CHAIN PMI DIMENSIONS (AnalyzeBendToPerimeter → CreateChainDimensions)
    ///      For each bend line, scans all outer-perimeter curves and collects parallel
    ///      candidates on both sides of the bend (Side A = nml+, Side B = nml-). Six indices
    ///      are tracked per side (nearest, 2nd-nearest, farthest, farthest-Line, nearest-Line,
    ///      nearest-any). Applies a SmallEdgeRatio correction to replace corner-notch candidates
    ///      with the longest (true boundary) segment. Groups parallel bends into direction
    ///      groups, then splits each group into independent lanes (flanges) via range-overlap
    ///      clustering. For each lane, partitions bends into "low side" and "high side"
    ///      relative to the bbox centre, and creates a PMI chain:
    ///         outer boundary → 1st bend → 2nd bend → ... → last bend.
    ///      The boundary is always the FARTHEST parallel Line on the correct outer side
    ///      (isLowSide ⊕ flipped determines which of Side A / Side B faces the outside).
    ///      Uses PmiRapidDimensionBuilder with MeasurementMethod.Perpendicular so NX
    ///      computes true geometric distances without any coordinate correction.
    /// </summary>
    public class HighlightFlatPattern
    {
        // =====================================================================
        // P/Invoke: native UF_MODL functions required because the managed NXOpen
        // API does not expose loop-level topology (outer vs inner loops). We call
        // into libufun.dll to ask each planar face for its loops, then filter
        // for outer loops (type == 1).
        // =====================================================================

        /// <summary>Get the list of loops on a face (each loop = a closed chain of edges).</summary>
        [DllImport("libufun.dll", CallingConvention = CallingConvention.Cdecl)]
        static extern int UF_MODL_ask_face_loops(Tag face, out IntPtr loopList);

        /// <summary>Count how many loops are in the loop list.</summary>
        [DllImport("libufun.dll", CallingConvention = CallingConvention.Cdecl)]
        static extern int UF_MODL_ask_loop_list_count(IntPtr loopList, out int count);

        /// <summary>
        /// Retrieve a specific loop from the list.
        /// Loop type: 1 = outer loop, 2 = inner loop (hole/cutout).
        /// edgeList is a list of edge tags belonging to that loop.
        /// </summary>
        [DllImport("libufun.dll", CallingConvention = CallingConvention.Cdecl)]
        static extern int UF_MODL_ask_loop_list_item(IntPtr loopList, int index, out int type, out IntPtr edgeList);

        /// <summary>Count items in a generic UF object list.</summary>
        [DllImport("libufun.dll", CallingConvention = CallingConvention.Cdecl)]
        static extern int UF_MODL_ask_list_count(IntPtr list, out int count);

        /// <summary>Get the tag of an item by index from a generic UF object list.</summary>
        [DllImport("libufun.dll", CallingConvention = CallingConvention.Cdecl)]
        static extern int UF_MODL_ask_list_item(IntPtr list, int index, out Tag tag);

        /// <summary>Free the memory allocated by UF_MODL_ask_face_loops.</summary>
        [DllImport("libufun.dll", CallingConvention = CallingConvention.Cdecl)]
        static extern int UF_MODL_delete_loop_list(ref IntPtr loopList);

        // =====================================================================
        // CONSTANTS — named thresholds used throughout the analysis pipeline
        // =====================================================================

        /// <summary>Minimum dot product for considering two curve directions as parallel.</summary>
        private const double ParallelismThreshold = 0.95;

        /// <summary>Minimum direction component (|dir.X| or |dir.Y|) for a bend to be considered "diagonal".
        /// Diagonal bends have significant components in both UV axes and may lack parallel perimeter segments.</summary>
        private const double DiagonalBendThreshold = 0.2;

        /// <summary>Squared distance threshold (mm²) for skipping bend lines whose midpoint lies on the outer perimeter.
        /// Corresponds to a 0.5 mm linear threshold (0.5² = 0.25).</summary>
        private const double ArtefactSkipDistanceSq = 0.25; // 0.5 mm

        /// <summary>Ratio threshold for cutout detection: if bestDist / secondBestDist is below this,
        /// the "nearest" perimeter segment is likely a thin notch and should be skipped.</summary>
        private const double CutoutSkipRatio = 0.3;

        /// <summary>Ratio for SmallEdge correction: if the farthest perimeter segment is shorter than
        /// SmallEdgeRatio × longestSegment, it's likely a corner notch and should be replaced by the longest.</summary>
        private const double SmallEdgeRatio = 0.5;

        /// <summary>Guard factor for SmallEdge correction: only replace farthest by longest if
        /// longestDist >= farDist * SmallEdgeGuardFactor, preventing replacement by a segment too close to the bend.</summary>
        private const double SmallEdgeGuardFactor = 0.5;

        /// <summary>Minimum length ratio for two bends to be considered similar and thus belong to the same lane.</summary>
        private const double LaneLengthRatioThreshold = 0.7;

        /// <summary>Minimum segment-projection length to avoid division by zero in direction calculations.</summary>
        private const double MinSegmentLength = 1e-6;

        /// <summary>Overlap guard: minimum overlap along direction axis for a perimeter segment to be considered relevant.</summary>
        private const double OverlapEpsilon = 1e-6;

        private static Session theSession;
        private static UI theUI;

        // =====================================================================
        // ENTRY POINT — Ctrl+U execution
        // =====================================================================

        /// <summary>
        /// Main entry — called when the user runs the DLL via File → Execute → NX Open
        /// (Ctrl+U), or from the menu callback.
        /// </summary>
        public static int Main(string[] args)
        {
            List<Curve> outerPerim = null;
            List<Curve> bendLines = null;

            try
            {
                theSession = Session.GetSession();
                theUI = UI.GetUI();

                // Require an active work part (not just a displayed part).
                Part workPart = theSession.Parts.Work;
                if (workPart == null)
                {
                    theUI.NXMessageBox.Show(
                        "Flat Pattern Highlight",
                        NXMessageBox.DialogType.Error,
                        "No active work part. Open or create a part first.");
                    return 1;
                }

                // Write everything to the NX LogFile (available from Help → Log File).
                LogFile lw = theSession.LogFile;

                // Locate the FlatPattern feature — without it there is nothing to analyse.
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

                // Step 1 — identify the true outer boundary.
                HighlightOuterPerimeter(flatPattern, workPart, lw, out outerPerim);

                // Step 2 — collect and highlight bend center lines.
                if (outerPerim != null && outerPerim.Count > 0)
                    HighlightBendCenterLines(flatPattern, lw, outerPerim, out bendLines);

                // Step 3 — analyse each bend line's proximity to the outer perimeter.
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
                // Highlights are transient analysis aids only; remove them before exit.
                // Executes even if an exception occurs mid-analysis.
                try { if (outerPerim != null) UnhighlightObjects(outerPerim); } catch { }
                try { if (bendLines != null) UnhighlightObjects(bendLines); } catch { }
            }
        }

        // =====================================================================
        // STEP 1 — Outer Perimeter Identification
        // =====================================================================

        /// <summary>
        /// Step 1: Filter the raw exterior curves from GetExteriorCurves() to obtain
        /// only the true outer boundary, excluding internal notch/cutout loops.
        ///
        /// Approach:
        ///   a) Find the flat solid body via 3 strategies (see FindFlatSolidBody).
        ///   b) If found, call UF_MODL_ask_face_loops on each planar face and collect
        ///      tags of edges belonging to outer loops (type == 1).
        ///   c) Cross-reference those tags with the FlatSolidObject.Tag of each
        ///      exterior curve.
        ///   d) If no body is found, ALL exterior curves are returned (fallback).
        /// </summary>
        private static void HighlightOuterPerimeter(FlatPattern flatPattern, Part workPart, LogFile lw, out List<Curve> outerPerim)
        {
            outerPerim = null;
            const int OuterLoopType = 1; // UF_MODL loop type for outer boundary
            lw.WriteLine("--- Outer Perimeter (True External Boundary) ---");

            // Fetch ALL exterior curves — includes outer boundary + every notch/cutout edge.
            FlatPattern.ObjectDataEdge[] exteriorCurves;
            flatPattern.GetExteriorCurves(out exteriorCurves);

            if (exteriorCurves == null || exteriorCurves.Length == 0)
            {
                lw.WriteLine("  (no exterior curves found)");
                return;
            }

            lw.WriteLine($"  Total exterior curves (raw): {exteriorCurves.Length}");
            lw.WriteLine("");

            // Try to locate the flat solid body to distinguish outer loops from inner ones.
            Body flatBody = FindFlatSolidBody(workPart, flatPattern, lw);
            if (flatBody == null)
            {
                // Fallback: use ALL exterior curves (no filtering possible).
                // This happens on parts where the flat solid is internal or non-queryable.
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

            // Collect tags from outer loops of all planar faces.
            var outerEdgeTags = new HashSet<Tag>();

            Face[] faces = flatBody.GetFaces();
            lw.WriteLine($"  Flat body faces: {faces.Length}");

            foreach (var face in faces)
            {
                // Only planar faces carry the 2D flat geometry — cylindrical (bend) faces do not.
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
                        if (loopType != OuterLoopType) continue; // Skip inner loops (type 2).

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

            // Keep only those exterior curves whose corresponding solid edge tag
            // is in the set of outer-loop edge tags.
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
        // STEP 2 — Bend Center Lines
        // =====================================================================

        /// <summary>
        /// Step 2: Retrieve and highlight bend center lines (both up and down faces).
        /// These are the axial lines at the centres of cylindrical bend regions.
        ///
        /// Bend Up   = the bend centre line when the flange folds upward.
        /// Bend Down = the bend centre line when the flange folds downward.
        ///
        /// In many flat patterns, "down" bends appear on the opposite side of the
        /// material and their center lines are offset accordingly.
        /// </summary>
        private static void HighlightBendCenterLines(FlatPattern flatPattern, LogFile lw, List<Curve> outerPerim, out List<Curve> bendLines)
        {
            bendLines = new List<Curve>();
            lw.WriteLine("--- Bend Center Lines ---");

            // Bend Up lines.
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

            // Bend Down lines.
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
        // STEP 3 — Bend-to-Perimeter Proximity Analysis
        // =====================================================================

        /// <summary>Helper: extract a coordinate from a Point3d by axis index (0=X,1=Y,2=Z).</summary>
        private static double AxisCoord(Point3d p, int axis)
        {
            switch (axis) { case 0: return p.X; case 1: return p.Y; default: return p.Z; }
        }

        /// <summary>
        /// Detect the normal axis of the flat pattern plane by measuring the spread
        /// of all curve endpoints. The axis with the smallest range is the plane normal.
        ///
        /// Some NX parts model the flat pattern in the XZ or YZ plane rather than the
        /// conventional XY plane. This method makes the axis detection automatic.
        /// </summary>
        /// <returns>0 for X-normal, 1 for Y-normal, 2 for Z-normal.</returns>
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
        /// Step 3: For each bend centre line, find the nearest parallel perimeter curve
        /// on each side (normal direction and opposite). Compare those distances against
        /// the distance to the overall bounding box edge to determine whether the nearest
        /// perimeter curve IS the part's outer boundary or an intermediate feature.
        ///
        /// Logs a comprehensive table and optionally creates chain PMI dimensions
        /// (see CreateChainDimensions).
        /// </summary>
        private static void AnalyzeBendToPerimeter(List<Curve> bendLines, List<Curve> outerPerim, LogFile lw, Part workPart)
        {
            int pmiCount = 0;

            lw.WriteLine("--- Bend Line → Nearest Perimeter (Parallel) ---");

            // Determine the flat pattern plane orientation.
            int normalAxis = DetectNormalAxis(bendLines, outerPerim);
            int uAxis, vAxis;
            if (normalAxis == 0) { uAxis = 1; vAxis = 2; }
            else if (normalAxis == 1) { uAxis = 0; vAxis = 2; }
            else { uAxis = 0; vAxis = 1; }
            string[] axisNames = { "X", "Y", "Z" };
            lw.WriteLine($"  [diag] Flat pattern plane: normal={axisNames[normalAxis]}  u={axisNames[uAxis]}  v={axisNames[vAxis]}");

            double GetU(Point3d p) => AxisCoord(p, uAxis);
            double GetV(Point3d p) => AxisCoord(p, vAxis);

            // Compute bounding box in the plane UV coordinates.
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
            // Esta tupla (23 campos) carrega tudo o que o Chain PMI precisa para
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
            //     secondBestIdx / Dist     - 2ª curva paralela mais próxima;
            //                               usada para detectar cutout: quando
            //                               best/secondBest < 0.3, a "mais próxima"
            //                               é provavelmente um recorte fino e
            //                               segundo a real.
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

                // Direction and length in the UV plane.
                double bdu = GetU(be) - GetU(bs), bdv = GetV(be) - GetV(bs);
                double blen = Math.Sqrt(bdu * bdu + bdv * bdv);
                if (blen < MinSegmentLength) continue;

                Vector3d bdir = new Vector3d(bdu / blen, bdv / blen, 0);
                // Perpendicular (normal) direction — rotate 90° counter-clockwise.
                Vector3d nml = new Vector3d(-bdir.Y, bdir.X, 0);

                double mu = (GetU(bs) + GetU(be)) / 2;
                double mv = (GetV(bs) + GetV(be)) / 2;

                // Skip bend lines whose 3-D midpoint lies on (or within 0.5 mm of) the outer
                // perimeter. Some NX flat-pattern configurations return perimeter edges through
                // GetBendUpCenterLines / GetBendDownCenterLines as artefacts.
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
                //   best / secondBest
                //     O segmento paralelo mais próximo e o segundo mais próximo. O "best"
                //     pode ser a face interna de uma flange ou um notch fino colado à dobra
                //     (ex.: 2.85 mm) em vez da borda exterior real (22.85 mm).
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
                double secondBestDistA = double.MaxValue, secondBestDistB = double.MaxValue;
                int secondBestIdxA = -1, secondBestIdxB = -1;
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

                // Scan all perimeter curves for parallel segments on each side.
                for (int pi = 0; pi < perimData.Count; pi++)
                {
                    var (ps, pe, pdir, plen, _) = perimData[pi];

                    // Project the midpoint of the perimeter segment onto the normal axis
                    // to determine which side (A = positive normal, B = negative normal).
                    double cu = (GetU(ps) + GetU(pe)) / 2;
                    double cv = (GetV(ps) + GetV(pe)) / 2;

                    double vu = cu - mu, vv = cv - mv;
                    double proj = vu * nml.X + vv * nml.Y;

                    double absProj = Math.Abs(proj);

                    // Track nearest perimeter edge on each side REGARDLESS of parallelism.
                    // This ensures diagonal bends always have a fallback boundary.
                    if (proj > 0)
                    {
                        if (absProj < nearDistA) { nearDistA = absProj; nearIdxA = pi; }
                    }
                    else if (proj < 0)
                    {
                        if (absProj < nearDistB) { nearDistB = absProj; nearIdxB = pi; }
                    }

                    // Filter: only curves that are approximately parallel (dot >= ParallelismThreshold).
                    double dot = pdir.X * bdir.X + pdir.Y * bdir.Y;
                    if (Math.Abs(dot) < ParallelismThreshold) { hasArcs = true; continue; }

                    // Check overlap: the perimeter segment must overlap the bend line's
                    // projection range so we don't match irrelevant segments off to the side.
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
                            secondBestDistA = bestDistA; secondBestIdxA = bestIdxA;
                            bestDistA = dist; bestIdxA = pi;
                        }
                        else if (dist < secondBestDistA)
                            { secondBestDistA = dist; secondBestIdxA = pi; }
                        if (dist > farDistA) { farDistA = dist; farIdxA = pi; }
                        if (plen > longestLenA) { longestLenA = plen; longestIdxA = pi; longestDistA = dist; }
                        if (dist < bestLineDistA && perimData[pi].curve is Line) { bestLineDistA = dist; bestLineIdxA = pi; }
                        if (dist > farLineDistA && perimData[pi].curve is Line) { farLineDistA = dist; farLineIdxA = pi; }
                    }
                    else if (proj < 0)
                    {
                        if (dist < bestDistB)
                        {
                            secondBestDistB = bestDistB; secondBestIdxB = bestIdxB;
                            bestDistB = dist; bestIdxB = pi;
                        }
                        else if (dist < secondBestDistB)
                            { secondBestDistB = dist; secondBestIdxB = pi; }
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

                // Compute the distance from the bend midpoint to the bounding box edge
                // along each normal direction.
                double sideADist = bestIdxA >= 0 ? DistToBboxEdge(mu, mv, nml.X, nml.Y, bboxMinU, bboxMinV, bboxMaxU, bboxMaxV) : -1;
                double sideBDist = bestIdxB >= 0 ? DistToBboxEdge(mu, mv, -nml.X, -nml.Y, bboxMinU, bboxMinV, bboxMaxU, bboxMaxV) : -1;

                // Log the results for this bend.
                lw.WriteLine($"  Bend[{bi}] Tag={bend.Tag}  Mid=({mu:F1},{mv:F1})  Dir=({bdir.X:F3},{bdir.Y:F3})  Len={blen:F1}");
                lw.WriteLine($"    Side A (nml+): nearest={bestDistA,8:F2}  bboxDist={sideADist,8:F2}" +
                    (bestIdxA >= 0 ? $"  perimTag={outerPerim[bestIdxA].Tag}" : "  (none)"));
                lw.WriteLine($"                   2ndNearest={secondBestDistA,8:F2}" +
                    (secondBestIdxA >= 0 ? $"  perimTag={perimData[secondBestIdxA].curve.Tag}" : "  (none)"));
                lw.WriteLine($"                   farthest  ={farDistA,8:F2}" +
                    (farIdxA >= 0 ? $"  perimTag={perimData[farIdxA].curve.Tag}  segLen={perimData[farIdxA].len:F1}" : "  (none)"));

                lw.WriteLine($"    Side B (nml-): nearest={bestDistB,8:F2}  bboxDist={sideBDist,8:F2}" +
                    (bestIdxB >= 0 ? $"  perimTag={outerPerim[bestIdxB].Tag}" : "  (none)"));
                lw.WriteLine($"                   2ndNearest={secondBestDistB,8:F2}" +
                    (secondBestIdxB >= 0 ? $"  perimTag={perimData[secondBestIdxB].curve.Tag}" : "  (none)"));
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
    BestDistA = bestDistA, BestDistB = bestDistB,
    SecondBestIdxA = secondBestIdxA, SecondBestIdxB = secondBestIdxB,
    SecondBestDistA = secondBestDistA, SecondBestDistB = secondBestDistB
});
            }

            // Create chain PMI dimensions (boundary → nearest bend → next bend → ...).
            pmiCount = CreateChainDimensions(workPart, bendInfos, perimData, outerPerim,
                bboxMinU, bboxMinV, bboxMaxU, bboxMaxV, uAxis, vAxis, normalAxis, lw);

            lw.WriteLine($"  PMI dimensions created: {pmiCount}");
            lw.WriteLine("");

            if (hasArcs)
                lw.WriteLine("  (some perimeter arcs were skipped — only straight lines compared)");
        }

        // =====================================================================
        // CHAIN DIMENSIONING — PMI creation
        // =====================================================================

        /// <summary>
        /// Project a point onto a line segment (closest point on segment).
        /// Used to place dimension origin points on boundary curves.
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
            int count = 0;
            var used = new bool[bendInfos.Count];

            for (int i = 0; i < bendInfos.Count; i++)
            {
                if (used[i]) continue;

                // Group bends with approximately the same direction (parallel).
                var group = new List<int> { i };
                used[i] = true;
                for (int j = i + 1; j < bendInfos.Count; j++)
                {
                    if (used[j]) continue;
                    double dot = bendInfos[i].Direction.X * bendInfos[j].Direction.X + bendInfos[i].Direction.Y * bendInfos[j].Direction.Y;
                    if (Math.Abs(dot) >= ParallelismThreshold) { group.Add(j); used[j] = true; }
                }

                // Split the direction-group into lanes by range overlap.
                foreach (var lane in ClusterByRangeOverlap(bendInfos, group, uAxis, vAxis))
                {
                    count += CreateChainForGroup(workPart, bendInfos, lane, perimData, outerPerim,
                        bboxMinU, bboxMinV, bboxMaxU, bboxMaxV, uAxis, vAxis, normalAxis, lw);
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
        ///        (b) tenha comprimento similar (ratio >= LaneLengthRatioThreshold = 0.7).
        ///      Se nenhuma lane combinar, cria uma nova lane.
        ///   2. Consolidação: repete até convergência, fundindo pares de lanes que se
        ///      tornaram sobrepostas após extensões de range causadas pelas atribuições.
        ///
        /// Propósito: distinguir a aba horizontal inteira (ex.: 337 mm) de uma aba
        /// recortada menor (ex.: 271 mm) que, mesmo paralela, pertence a uma cadeia
        /// de cotas separada (flange diferente na dobra em U).
        /// </summary>
        private static List<List<int>> ClusterByRangeOverlap(
            List<BendAnalysisInfo> bendInfos,
            List<int> groupIdx, int uAxis, int vAxis)
        {
            Vector3d refDir = bendInfos[groupIdx[0]].Direction;
            var lanes = new List<List<int>>();
            var laneRanges = new List<(double lo, double hi)>();
            var laneLens = new List<double>();

            foreach (int gi in groupIdx)
            {
                var info = bendInfos[gi];
                double bsU = AxisCoord(info.StartPoint, uAxis), bsV = AxisCoord(info.StartPoint, vAxis);
                double beU = AxisCoord(info.EndPoint, uAxis), beV = AxisCoord(info.EndPoint, vAxis);
                double s = bsU * refDir.X + bsV * refDir.Y;
                double e = beU * refDir.X + beV * refDir.Y;
                double lo = Math.Min(s, e), hi = Math.Max(s, e);
                double len = Math.Sqrt(Math.Pow(beU - bsU, 2) + Math.Pow(beV - bsV, 2));

                bool placed = false;
                for (int k = 0; k < lanes.Count; k++)
                {
                    bool overlaps = lo <= laneRanges[k].hi && hi >= laneRanges[k].lo;
                    bool similarLength = Math.Min(len, laneLens[k]) / Math.Max(len, laneLens[k]) >= LaneLengthRatioThreshold;
                    if (overlaps && similarLength)
                    {
                        lanes[k].Add(gi);
                        laneRanges[k] = (Math.Min(laneRanges[k].lo, lo), Math.Max(laneRanges[k].hi, hi));
                        laneLens[k] = Math.Max(laneLens[k], len);
                        placed = true;
                        break;
                    }
                }
                if (!placed)
                {
                    lanes.Add(new List<int> { gi });
                    laneRanges.Add((lo, hi));
                    laneLens.Add(len);
                }
            }

            // Consolidate lanes that overlap after merges/extensions.
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
                        if (overlaps && similarLength)
                        {
                            lanes[a].AddRange(lanes[b]);
                            laneRanges[a] = (Math.Min(laneRanges[a].lo, laneRanges[b].lo), Math.Max(laneRanges[a].hi, laneRanges[b].hi));
                            laneLens[a] = Math.Max(laneLens[a], laneLens[b]);
                            lanes.RemoveAt(b);
                            laneRanges.RemoveAt(b);
                            laneLens.RemoveAt(b);
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
            LogFile lw)
        {
            var refInfo = bendInfos[groupIdx[0]];
            Vector3d refNml = refInfo.Normal;

            // Project bbox corners onto the normal direction to find extents.
            double[] cornersOffset = new double[]
            {
                bboxMinU * refNml.X + bboxMinV * refNml.Y,
                bboxMinU * refNml.X + bboxMaxV * refNml.Y,
                bboxMaxU * refNml.X + bboxMinV * refNml.Y,
                bboxMaxU * refNml.X + bboxMaxV * refNml.Y
            };
            double bboxLow = cornersOffset.Min();
            double bboxHigh = cornersOffset.Max();

            // Classify each bend as belonging to the "low" or "high" side depending
            // on which half of the bbox it is closer to.
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
                bboxMinU, bboxMinV, bboxMaxU, bboxMaxV, lw);
            count += CreateChainSide(workPart, bendInfos, highSide, perimData, outerPerim, isLowSide: false, normalAxis,
                bboxMinU, bboxMinV, bboxMaxU, bboxMaxV, lw);



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
                    Point3d origin = CreateChainOrigin(
                        boundaryPoint,
                        first.MidPoint,
                        0,
                        bboxMinU, bboxMinV, bboxMaxU, bboxMaxV,
                        normalAxis);
                    if (CreatePmiRapidDimension(workPart, seg.curve, boundaryPoint, first.Bend, first.MidPoint, origin, normalAxis, lw))
                        count++;
                }
            }

            // --- Cotas entre dobras consecutivas da cadeia ---
            // (sem boundary: midpoint→midpoint das dobras vizinhas)
            for (int k = 1; k < side.Count; k++)
            {
                var prev = bendInfos[side[k - 1].idx];
                var curr = bendInfos[side[k].idx];
                Point3d origin = CreateChainOrigin(
                    prev.MidPoint,
                    curr.MidPoint,
                    k,
                    bboxMinU, bboxMinV, bboxMaxU, bboxMaxV,
                    normalAxis);
                if (CreatePmiRapidDimension(workPart, prev.Bend, prev.MidPoint, curr.Bend, curr.MidPoint, origin, normalAxis, lw))
                    count++;
            }

            return count;
        }

        /// <summary>
        /// Find the flat-pattern modeling view. Used as view context for PMI associativity,
        /// matching the NX journal recording pattern. Falls back to work view if not found.
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
            int level,
            double bboxMinU,
            double bboxMinV,
            double bboxMaxU,
            double bboxMaxV,
            int normalAxis)
        {
            double bboxExtent = Math.Max(bboxMaxU - bboxMinU, bboxMaxV - bboxMinV);
            double margin = Math.Max(25.0, bboxExtent * 0.03);
            double spacing = Math.Max(18.0, bboxExtent * 0.025);
            double offset = margin + level * spacing;

            // Constant coordinate along the flat pattern normal axis
            double normalVal = normalAxis == 0 ? pointA.X : normalAxis == 1 ? pointA.Y : pointA.Z;

            // UV axis indices: normalAxis=0 -> U=Y(1),V=Z(2);  normalAxis=1 -> U=X(0),V=Z(2);  normalAxis=2 -> U=X(0),V=Y(1)
            int uIdx = normalAxis == 0 ? 1 : 0;
            int vIdx = normalAxis <= 1  ? 2 : 1;

            double uA = AxisCoord(pointA, uIdx), vA = AxisCoord(pointA, vIdx);
            double uB = AxisCoord(pointB, uIdx), vB = AxisCoord(pointB, vIdx);

            double midU = (uA + uB) / 2.0;
            double midV = (vA + vB) / 2.0;

            // V-dominant measurement -> text outside the high-U edge (right side).
            // U-dominant measurement -> text outside the high-V edge (top side).
            double textU, textV;
            if (Math.Abs(vB - vA) >= Math.Abs(uB - uA))
            {
                textU = bboxMaxU + offset;
                textV = midV;
            }
            else
            {
                textU = midU;
                textV = bboxMaxV + offset;
            }

            // Convert UV + normal value back to XYZ
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
        /// Compute the distance from a point (px, py) to the bounding box edge in
        /// a given direction (dx, dy) using ray-casting. Returns the smallest
        /// positive parametric distance t along the ray to any bbox face.
        /// A return value of -1 means no intersection was found.
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
                const double rayEpsilon = 1e-6; // skip intersections at/near origin
                if (ti > rayEpsilon && ti < t) t = ti;
            }
            return t == double.MaxValue ? -1 : t;
        }

        // =====================================================================
        // FLAT SOLID BODY LOCATION
        // =====================================================================

        /// <summary>
        /// Attempt to find the flat solid body that corresponds to the FlatPattern
        /// feature. Uses three strategies in order of reliability:
        ///
        ///   1. flatPattern.GetBodies() — the official API, but may return null
        ///      on some parts where the flat solid is not exposed.
        ///   2. flatPattern.GetEntities() + cast to Body — alternative API.
        ///   3. Iterate through workPart.Bodies, check GetFeatures() for a
        ///      FlatPattern or FlatSolid feature, and match by tag.
        ///
        /// An extended diagnostic log is produced at the [diag] level to help
        /// understand why the body might not be found on a given part.
        /// </summary>
        private static Body FindFlatSolidBody(Part workPart, FlatPattern flatPattern, LogFile lw)
        {
            // Strategy 1: official API.
            Body[] bodies = flatPattern.GetBodies();
            lw.WriteLine($"  [diag] flatPattern.GetBodies() count: {(bodies != null ? bodies.Length : -1)}");
            if (bodies != null && bodies.Length > 0) { lw.WriteLine($"  Body via GetBodies(): {bodies[0].Tag}"); return bodies[0]; }

            // Strategy 2: entity scan.
            NXObject[] entities = flatPattern.GetEntities();
            lw.WriteLine($"  [diag] flatPattern.GetEntities() count: {(entities != null ? entities.Length : -1)}");
            if (entities != null)
            {
                foreach (var ent in entities)
                    lw.WriteLine($"  [diag]   entity type: {ent.GetType().Name}");
                foreach (var ent in entities) { Body b = ent as Body; if (b != null) { lw.WriteLine($"  Body via GetEntities(): {b.Tag}"); return b; } }
            }

            // Strategy 3: exhaustive body scan (most expensive, most reliable).
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

                    // Look for a FlatPattern feature with matching tag.
                    foreach (Feature f in features)
                        if (f is FlatPattern fp && fp.Tag == flatPattern.Tag)
                        { lw.WriteLine($"  Body via workPart.Bodies (FlatPattern feature): {body.Tag}"); return body; }

                    // Also match FlatSolid (the 3D result body of the flat pattern).
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
        /// Highlight all objects in the list (used to draw attention to
        /// the outer perimeter, bend lines, etc. in the NX graphics window).
        /// </summary>
        private static void HighlightObjects(List<DisplayableObject> objects)
        {
            if (objects.Count == 0) return;
            foreach (var o in objects) o.Highlight();
        }

        /// <summary>
        /// Clear transient highlight state from displayable objects after analysis.
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
        /// Required by NX: return the unload behaviour.
        /// Immediately = the DLL can be unloaded after the action completes.
        ///
        /// Note: The .men file now uses the NXOpen::ClassName.Method syntax
        /// (ACTIONS NXOpen::FlatPatternHighlight.HighlightFlatPattern::Main),
        /// which calls Main() directly without needing AddMenuAction() or a
        /// signed Startup(). This works for both Ctrl+U and auto-load scenarios.
        /// </summary>
        public static int GetUnloadOption(string arg) { return (int)Session.LibraryUnloadOption.Immediately; }

        /// <summary>
        /// Called by NX when the DLL is loaded on startup (requires signing).
        /// No longer registers AddMenuAction — the .men file's NXOpen:: syntax
        /// resolves the action directly. Kept as a required NX lifecycle method.
        /// </summary>
        public static int Startup(string[] args)
        {
            return 0;
        }
    }
}
