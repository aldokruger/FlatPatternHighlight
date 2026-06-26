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
    ///   1. Outer Perimeter Filtering — reduces the raw exterior curves (incl. notch/cutout
    ///      boundaries) to only the true external boundary edges via UF_MODL_ask_face_loops
    ///      (P/Invoke) or a purely geometric fallback.
    ///   2. Bend Center Lines — enumerates and highlights bend-up/bend-down center lines.
    ///   3. Bend-to-Perimeter Proximity — for each bend, finds the nearest parallel perimeter
    ///      curve on each side, measures distance to the bounding box, and optionally creates
    ///      chain PMI dimensions.
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

                // Locate the FlatPattern feature — without it there is nothing to analyse.
                FlatPattern flatPattern = FindFlatPattern(workPart);
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

                // Write everything to the NX LogFile (available from Help → Log File).
                LogFile lw = theSession.LogFile;
                lw.WriteLine("=== FlatPatternHighlight Diagnostic Log ===");
                lw.WriteLine($"Part: {workPart.Name}");
                lw.WriteLine("");

                List<Curve> outerPerim = null;
                List<Curve> bendLines = null;

                // Step 1 — identify the true outer boundary.
                HighlightOuterPerimeter(flatPattern, workPart, lw, out outerPerim);

                // Step 2 — collect and highlight bend center lines.
                if (outerPerim != null && outerPerim.Count > 0)
                    HighlightBendCenterLines(flatPattern, lw, outerPerim, out bendLines);

                // Step 3 — analyse each bend line's proximity to the outer perimeter.
                if (bendLines != null && bendLines.Count > 0 && outerPerim != null && outerPerim.Count > 0)
                    AnalyzeBendToPerimeter(bendLines, outerPerim, lw, workPart);

                // Highlights are transient analysis aids only; remove them before exit.
                UnhighlightObjects(outerPerim);
                UnhighlightObjects(bendLines);

                lw.WriteLine("=== End of Diagnostic Log ===");
                return 0;
            }
            catch (NXException ex)
            {
                if (theUI != null)
                    try { theUI.NXMessageBox.Show("Flat Pattern Highlight - Error", NXMessageBox.DialogType.Error, $"NX Error: {ex.Message}"); } catch { }
                return 0;
            }
            catch (Exception ex)
            {
                if (theUI != null)
                    try { theUI.NXMessageBox.Show("Flat Pattern Highlight - Error", NXMessageBox.DialogType.Error, $"Error: {ex.Message}"); } catch { }
                return 0;
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
                NXOpen.Utilities.JAM.StartUFCall("FlatPatternHighlight");
                int err = UF_MODL_ask_face_loops(face.Tag, out loopList);
                NXOpen.Utilities.JAM.EndUFCall();

                if (err != 0 || loopList == IntPtr.Zero) continue;

                int loopCount;
                UF_MODL_ask_loop_list_count(loopList, out loopCount);

                for (int li = 0; li < loopCount; li++)
                {
                    int loopType;
                    IntPtr edgeList;
                    UF_MODL_ask_loop_list_item(loopList, li, out loopType, out edgeList);
                    if (loopType != 1) continue; // Skip inner loops (type 2).

                    int edgeCount;
                    UF_MODL_ask_list_count(edgeList, out edgeCount);
                    for (int ei = 0; ei < edgeCount; ei++)
                    {
                        Tag et; UF_MODL_ask_list_item(edgeList, ei, out et);
                        outerEdgeTags.Add(et);
                    }
                }
                UF_MODL_delete_loop_list(ref loopList);
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
                Vector3d d = len > 1e-6 ? new Vector3d(du / len, dv / len, 0) : new Vector3d(0, 0, 0);
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
            var bendInfos = new List<(int bi, Curve bend, Point3d pt, Point3d bs, Point3d be, Vector3d dir, Vector3d nml,
                int bestIdxA, int bestIdxB, int farIdxA, int farIdxB, int nearIdxA, int nearIdxB,
                int bestLineIdxA, int bestLineIdxB, double bestDistA, double bestDistB)>();

            for (int bi = 0; bi < bendLines.Count; bi++)
            {
                Curve bend = bendLines[bi];
                Point3d bs, be; GetEndPoints(bend, out bs, out be);

                // Direction and length in the UV plane.
                double bdu = GetU(be) - GetU(bs), bdv = GetV(be) - GetV(bs);
                double blen = Math.Sqrt(bdu * bdu + bdv * bdv);
                if (blen < 1e-6) continue;

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
                        if (d2 < 0.25) { onPerim = true; break; } // 0.5 mm threshold
                    }
                    if (onPerim)
                    {
                        lw.WriteLine($"  Bend[{bi}] Tag={bend.Tag}  skipped - midpoint on outer perimeter (artefact)");
                        continue;
                    }
                }

                double bestDistA = double.MaxValue, bestDistB = double.MaxValue;
                int bestIdxA = -1, bestIdxB = -1;
                double farDistA = -1, farDistB = -1;
                int farIdxA = -1, farIdxB = -1;
                double longestLenA = -1, longestLenB = -1;
                int longestIdxA = -1, longestIdxB = -1;
                double nearDistA = double.MaxValue, nearDistB = double.MaxValue;
                int nearIdxA = -1, nearIdxB = -1;
                double bestLineDistA = double.MaxValue, bestLineDistB = double.MaxValue;
                int bestLineIdxA = -1, bestLineIdxB = -1;

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

                    // Filter: only curves that are approximately parallel (dot > 0.95).
                    double dot = pdir.X * bdir.X + pdir.Y * bdir.Y;
                    if (Math.Abs(dot) < 0.95) { hasArcs = true; continue; }

                    // Check overlap: the perimeter segment must overlap the bend line's
                    // projection range so we don't match irrelevant segments off to the side.
                    double t1 = (GetU(ps) - GetU(bs)) * bdir.X + (GetV(ps) - GetV(bs)) * bdir.Y;
                    double t2 = (GetU(pe) - GetU(bs)) * bdir.X + (GetV(pe) - GetV(bs)) * bdir.Y;
                    double tMin = Math.Min(t1, t2), tMax = Math.Max(t1, t2);
                    double overlap = Math.Min(blen, tMax) - Math.Max(0, tMin);
                    if (overlap <= 0) continue;

                    double dist = Math.Abs(proj);

                    if (proj > 0)
                    {
                        if (dist < bestDistA) { bestDistA = dist; bestIdxA = pi; }
                        if (dist > farDistA) { farDistA = dist; farIdxA = pi; }
                        if (plen > longestLenA) { longestLenA = plen; longestIdxA = pi; }
                        if (dist < bestLineDistA && perimData[pi].curve is Line) { bestLineDistA = dist; bestLineIdxA = pi; }
                    }
                    else if (proj < 0)
                    {
                        if (dist < bestDistB) { bestDistB = dist; bestIdxB = pi; }
                        if (dist > farDistB) { farDistB = dist; farIdxB = pi; }
                        if (plen > longestLenB) { longestLenB = plen; longestIdxB = pi; }
                        if (dist < bestLineDistB && perimData[pi].curve is Line) { bestLineDistB = dist; bestLineIdxB = pi; }
                    }
                }

                // When the farthest perimeter curve on a side is a short segment (e.g. a
                // small relief notch at the bounding box corner), prefer the longest
                // perimeter curve instead — it is more likely to be the actual panel edge.
                const double SmallEdgeRatio = 0.5;
                if (farIdxA >= 0 && longestIdxA >= 0 && perimData[farIdxA].len < SmallEdgeRatio * longestLenA)
                    farIdxA = longestIdxA;
                if (farIdxB >= 0 && longestIdxB >= 0 && perimData[farIdxB].len < SmallEdgeRatio * longestLenB)
                    farIdxB = longestIdxB;

                // Compute the distance from the bend midpoint to the bounding box edge
                // along each normal direction.
                double sideADist = bestIdxA >= 0 ? DistToBboxEdge(mu, mv, nml.X, nml.Y, bboxMinU, bboxMinV, bboxMaxU, bboxMaxV) : -1;
                double sideBDist = bestIdxB >= 0 ? DistToBboxEdge(mu, mv, -nml.X, -nml.Y, bboxMinU, bboxMinV, bboxMaxU, bboxMaxV) : -1;

                // Log the results for this bend.
                lw.WriteLine($"  Bend[{bi}] Tag={bend.Tag}  Mid=({mu:F1},{mv:F1})  Dir=({bdir.X:F3},{bdir.Y:F3})  Len={blen:F1}");
                lw.WriteLine($"    Side A (nml+): nearest={bestDistA,8:F2}  bboxDist={sideADist,8:F2}" +
                    (bestIdxA >= 0 ? $"  perimTag={outerPerim[bestIdxA].Tag}" : "  (none)"));

                lw.WriteLine($"    Side B (nml-): nearest={bestDistB,8:F2}  bboxDist={sideBDist,8:F2}" +
                    (bestIdxB >= 0 ? $"  perimTag={outerPerim[bestIdxB].Tag}" : "  (none)"));

                lw.WriteLine("");

                // For diagonal bends with no parallel perimeter on a side, fall back
                // to the nearest perimeter edge (regardless of parallelism).
                if (Math.Abs(bdir.X) > 0.2 && Math.Abs(bdir.Y) > 0.2)
                {
                    // Prefer parallel Line over Arc; fall back to nearIdx if nothing found.
                    if (bestIdxA >= 0 && !(perimData[bestIdxA].curve is Line) && bestLineIdxA >= 0)
                        bestIdxA = bestLineIdxA;
                    if (bestIdxB >= 0 && !(perimData[bestIdxB].curve is Line) && bestLineIdxB >= 0)
                        bestIdxB = bestLineIdxB;
                    if (bestIdxA < 0) { bestIdxA = nearIdxA; }
                    if (bestIdxB < 0) { bestIdxB = nearIdxB; }
                }
                // For non-diagonal bends, also ensure farIdx fallback.
                if (farIdxA < 0) farIdxA = nearIdxA;
                if (farIdxB < 0) farIdxB = nearIdxB;

                Point3d bendPoint = new Point3d((bs.X + be.X) / 2, (bs.Y + be.Y) / 2, (bs.Z + be.Z) / 2);
                bendInfos.Add((bi, bend, bendPoint, bs, be, bdir, nml, bestIdxA, bestIdxB, farIdxA, farIdxB, nearIdxA, nearIdxB, bestLineIdxA, bestLineIdxB, bestDistA, bestDistB));
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
            double lenSq = vx * vx + vy * vy + vz * vz;
            if (lenSq < 1e-12) return segStart;
            double t = ((point.X - segStart.X) * vx + (point.Y - segStart.Y) * vy + (point.Z - segStart.Z) * vz) / lenSq;
            t = Math.Max(0, Math.Min(1, t));
            return new Point3d(segStart.X + t * vx, segStart.Y + t * vy, segStart.Z + t * vz);
        }

        /// <summary>
        /// Group parallel bend lines into clusters, then split each cluster into
        /// independent lanes (flanges) based on range overlap and length similarity,
        /// then create chain dimensions for each lane.
        ///
        /// "Lanes" are sets of parallel bends that share the same spatial extent
        /// along the bend direction — they belong to the same flange chain.
        /// Parallel bends in different lanes (e.g. left flange vs right flange)
        /// are dimensioned independently.
        /// </summary>
        private static int CreateChainDimensions(Part workPart,
            List<(int bi, Curve bend, Point3d pt, Point3d bs, Point3d be, Vector3d dir, Vector3d nml, int bestIdxA, int bestIdxB, int farIdxA, int farIdxB, int nearIdxA, int nearIdxB, int bestLineIdxA, int bestLineIdxB, double bestDistA, double bestDistB)> bendInfos,
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
                    double dot = bendInfos[i].dir.X * bendInfos[j].dir.X + bendInfos[i].dir.Y * bendInfos[j].dir.Y;
                    if (Math.Abs(dot) >= 0.95) { group.Add(j); used[j] = true; }
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
        /// Threshold for considering two bends as having "similar length".
        /// A value of 0.7 means the shorter bend must be at least 70% of the longer one.
        /// </summary>
        private const double LaneLengthRatioThreshold = 0.7;

        /// <summary>
        /// Split a direction-group of bends into separate lanes (independent flanges)
        /// by checking whether their projected ranges along the bend direction overlap
        /// AND their lengths are similar.
        ///
        /// After initial assignment, overlapping lanes are consolidated greedily.
        /// </summary>
        private static List<List<int>> ClusterByRangeOverlap(
            List<(int bi, Curve bend, Point3d pt, Point3d bs, Point3d be, Vector3d dir, Vector3d nml, int bestIdxA, int bestIdxB, int farIdxA, int farIdxB, int nearIdxA, int nearIdxB, int bestLineIdxA, int bestLineIdxB, double bestDistA, double bestDistB)> bendInfos,
            List<int> groupIdx, int uAxis, int vAxis)
        {
            Vector3d refDir = bendInfos[groupIdx[0]].dir;
            var lanes = new List<List<int>>();
            var laneRanges = new List<(double lo, double hi)>();
            var laneLens = new List<double>();

            foreach (int gi in groupIdx)
            {
                var info = bendInfos[gi];
                double bsU = AxisCoord(info.bs, uAxis), bsV = AxisCoord(info.bs, vAxis);
                double beU = AxisCoord(info.be, uAxis), beV = AxisCoord(info.be, vAxis);
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
        /// For a single lane (set of parallel, overlapping bends), partition bends
        /// into two sides ("low" and "high" relative to the bounding box centre),
        /// then create chain dimensions from the farthest perimeter curve → first bend
        /// → next bend → etc. on each side.
        /// </summary>
        private static int CreateChainForGroup(Part workPart,
            List<(int bi, Curve bend, Point3d pt, Point3d bs, Point3d be, Vector3d dir, Vector3d nml, int bestIdxA, int bestIdxB, int farIdxA, int farIdxB, int nearIdxA, int nearIdxB, int bestLineIdxA, int bestLineIdxB, double bestDistA, double bestDistB)> bendInfos,
            List<int> groupIdx,
            List<(Point3d start, Point3d end, Vector3d dir, double len, Curve curve)> perimData,
            List<Curve> outerPerim,
            double bboxMinU, double bboxMinV, double bboxMaxU, double bboxMaxV,
            int uAxis, int vAxis, int normalAxis,
            LogFile lw)
        {
            var refInfo = bendInfos[groupIdx[0]];
            Vector3d refNml = refInfo.nml;

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
                double dot = info.dir.X * refInfo.dir.X + info.dir.Y * refInfo.dir.Y;
                bool flipped = dot < 0;
                double offset = AxisCoord(info.pt, uAxis) * refNml.X + AxisCoord(info.pt, vAxis) * refNml.Y;
                entries.Add((gi, offset, flipped));
            }

            var lowSide = entries.Where(e => (e.offset - bboxLow) <= (bboxHigh - e.offset)).OrderBy(e => e.offset).ToList();
            var highSide = entries.Where(e => (e.offset - bboxLow) > (bboxHigh - e.offset)).OrderByDescending(e => e.offset).ToList();

            int count = 0;
            count += CreateChainSide(workPart, bendInfos, lowSide, perimData, isLowSide: true, normalAxis,
                bboxMinU, bboxMinV, bboxMaxU, bboxMaxV, lw);
            count += CreateChainSide(workPart, bendInfos, highSide, perimData, isLowSide: false, normalAxis,
                bboxMinU, bboxMinV, bboxMaxU, bboxMaxV, lw);

            // If a lane spans both halves of the bounding box, each side builds its own
            // chain from the boundary toward the center. Add one bridge dimension between
            // the innermost bends so the central gap is not left un-dimensioned.
            // Bridge dimension: connects innermost bends from each side.
            // Skip when there's exactly 1 bend per side — the bridge would just be
            // the sum of the two boundary-to-bend dimensions, which is redundant.
            if (lowSide.Count > 0 && highSide.Count > 0 &&
                !(lowSide.Count == 1 && highSide.Count == 1))
            {
                var lowInner = bendInfos[lowSide[lowSide.Count - 1].idx];
                var highInner = bendInfos[highSide[highSide.Count - 1].idx];

                if (lowInner.bi != highInner.bi)
                {
                    Point3d origin = CreateChainOrigin(
                        lowInner.pt,
                        highInner.pt,
                        Math.Max(lowSide.Count, highSide.Count),
                        bboxMinU, bboxMinV, bboxMaxU, bboxMaxV,
                        normalAxis);

                    if (CreatePmiRapidDimension(workPart, lowInner.bend, lowInner.pt, highInner.bend, highInner.pt, origin, normalAxis, lw))
                        count++;
                }
            }

            return count;
        }

        /// <summary>
        /// Create dimensions along one side of the chain: first dimension from the
        /// farthest boundary curve to the nearest bend, then dimensions between
        /// consecutive bends in offset order.
        /// </summary>
        private static int CreateChainSide(Part workPart,
            List<(int bi, Curve bend, Point3d pt, Point3d bs, Point3d be, Vector3d dir, Vector3d nml, int bestIdxA, int bestIdxB, int farIdxA, int farIdxB, int nearIdxA, int nearIdxB, int bestLineIdxA, int bestLineIdxB, double bestDistA, double bestDistB)> bendInfos,
            List<(int idx, double offset, bool flipped)> side,
            List<(Point3d start, Point3d end, Vector3d dir, double len, Curve curve)> perimData,
            bool isLowSide, int normalAxis,
            double bboxMinU, double bboxMinV, double bboxMaxU, double bboxMaxV,
            LogFile lw)
        {
            if (side.Count == 0) return 0;
            int count = 0;

            // For the first bend in the chain, create a dimension from it
            // to the nearest boundary perimeter curve on that side.
            //
            // Diagonal bends: "farthest" parallel perimeter can be the far bbox corner,
            // 3-4x the actual flange width. The Y-correction then pushes the reference
            // point outside the bbox - nonsensical dimension. Use NEAREST (bestIdx) for
            // diagonal bends so the corrected reference stays inside the part and the
            // value equals the true perpendicular flange width.
            var first = bendInfos[side[0].idx];
            bool flipped = side[0].flipped;
            bool isDiagonalBend = Math.Abs(first.dir.X) > 0.2 && Math.Abs(first.dir.Y) > 0.2;
            int boundaryIdx;
            if (isDiagonalBend)
            {
                // Use the NEAREST parallel perimeter edge (regardless of side classification).
                // The isLowSide heuristic can select the wrong side when the bend is near
                // one edge — the nearest parallel perimeter gives the correct flange width.
                int rawIdx;
                if (first.bestDistA <= first.bestDistB)
                    rawIdx = first.bestIdxA;
                else
                    rawIdx = first.bestIdxB;
                // Prefer parallel Line over Arc.
                int lineIdx;
                if (first.bestDistA <= first.bestDistB)
                    lineIdx = first.bestLineIdxA;
                else
                    lineIdx = first.bestLineIdxB;
                boundaryIdx = lineIdx >= 0 ? lineIdx : rawIdx;
            }
            else
                boundaryIdx = isLowSide
                    ? (flipped ? first.farIdxA : first.farIdxB)
                    : (flipped ? first.farIdxB : first.farIdxA);

            if (boundaryIdx < 0)
            {
                // Fallback: use the nearest perimeter edge on this side (regardless of parallelism).
                int fallbackIdx = isLowSide
                    ? (flipped ? first.nearIdxA : first.nearIdxB)
                    : (flipped ? first.nearIdxB : first.nearIdxA);
                if (fallbackIdx >= 0 && perimData[fallbackIdx].curve is Line)
                {
                    lw.WriteLine($"  [Chain] Fallback to nearest perimeter for Bend Tag={first.bend.Tag} on {(isLowSide ? "low" : "high")} side. perimTag={outerPerim[fallbackIdx].Tag}");
                    boundaryIdx = fallbackIdx;
                }
                else
                {
                    lw.WriteLine($"  [Chain] No boundary curve found for Bend Tag={first.bend.Tag} on {(isLowSide ? "low" : "high")} side.");
                }
            }
            if (boundaryIdx >= 0)
            {
                var seg = perimData[boundaryIdx];
                Point3d boundaryPoint = ProjectPointOnSegment(first.pt, seg.start, seg.end);
                if (!(seg.curve is Line))
                {
                    // PmiRapidDimensionBuilder.Perpendicular requires a Line as first reference.
                    // Arcs (corner fillets etc.) cause error 1175009 and the point-fallback
                    // would produce a wrong value. Use a red indicator line instead.
                    lw.WriteLine($"  [Chain] Boundary for Bend Tag={first.bend.Tag} is {seg.curve.GetType().Name} (not Line) - indicator line only");
                    try
                    {
                        Line indicator = workPart.Curves.CreateLine(boundaryPoint, first.pt);
                        indicator.Color = 36;
                        indicator.SetUserAttribute("FlatPatternHighlight", 0, "true", NXOpen.Update.Option.Later);
                    }
                    catch (Exception exL) { lw.WriteLine($"  [Chain] Indicator line failed: {exL.Message}"); }
                }
                else
                {
                    Point3d origin = CreateChainOrigin(
                        boundaryPoint,
                        first.pt,
                        0,
                        bboxMinU, bboxMinV, bboxMaxU, bboxMaxV,
                        normalAxis);
                    if (CreatePmiRapidDimension(workPart, seg.curve, boundaryPoint, first.bend, first.pt, origin, normalAxis, lw))
                        count++;
                }
            }

            // Chain dimensions between consecutive bends.
            for (int k = 1; k < side.Count; k++)
            {
                var prev = bendInfos[side[k - 1].idx];
                var curr = bendInfos[side[k].idx];
                Point3d origin = CreateChainOrigin(
                    prev.pt,
                    curr.pt,
                    k,
                    bboxMinU, bboxMinV, bboxMaxU, bboxMaxV,
                    normalAxis);
                if (CreatePmiRapidDimension(workPart, prev.bend, prev.pt, curr.bend, curr.pt, origin, normalAxis, lw))
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
        /// Create a PMI perpendicular dimension using PmiRapidDimensionBuilder with
        /// MeasurementMethod.Perpendicular. References actual Curve objects so NX computes
        /// the true geometric perpendicular distance - works correctly for horizontal,
        /// vertical, and diagonal bends with no Y-correction needed.
        /// Falls back to a red indicator line when the PMI/GD&T license is unavailable.
        /// </summary>
        private static bool CreatePmiRapidDimension(Part workPart,
            Curve curveA, Point3d pickA,
            Curve curveB, Point3d midB,
            Point3d origin,
            int normalAxis,
            LogFile lw)
        {
            NXOpen.Session theSession = NXOpen.Session.GetSession();
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
                builder.Origin.SetInferRelativeToGeometry(false);
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
                try { theSession.UndoToMark(undoMark, null); } catch { }
                try { theSession.DeleteUndoMark(undoMark, null); } catch { }
                try { lw.WriteLine($"  [PMI] Rapid builder failed, trying point fallback. ErrorCode={nex.ErrorCode}  {nex.Message}"); } catch { }
                return CreatePmiPointFallbackDimension(workPart, pickA, midB, origin, normalAxis, lw);
            }
            catch (Exception ex)
            {
                try { theSession.UndoToMark(undoMark, null); } catch { }
                try { theSession.DeleteUndoMark(undoMark, null); } catch { }
                try { lw.WriteLine($"  [PMI] Rapid builder failed, trying point fallback. {ex.GetType().Name}: {ex.Message}"); } catch { }
                return CreatePmiPointFallbackDimension(workPart, pickA, midB, origin, normalAxis, lw);
            }
        }

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
        /// Compute the origin (annotation text location) for a chain dimension.
        /// Places the text outside the bounding box, stacked vertically/horizontally
        /// by level (0 = outermost, 1 = next, etc.).
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
                if (ti > 1e-6 && ti < t) t = ti;
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
        private static FlatPattern FindFlatPattern(Part workPart)
        {
            // Iterating workPart.Features can trigger NX's internal PartCollection.FindObject
            // when the flat pattern feature resolves its source part (which may not be loaded).
            // Catch it here so the exception doesn't propagate to Main.
            try
            {
                foreach (Feature f in workPart.Features)
                    if (f is FlatPattern) return (FlatPattern)f;
            }
            catch { }
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
                double r = arc.Radius, cx = arc.CenterPoint.X, cy = arc.CenterPoint.Y, cz = arc.CenterPoint.Z;
                double sa = arc.StartAngle * Math.PI / 180.0, ea = arc.EndAngle * Math.PI / 180.0;
                p1 = new Point3d(cx + r * Math.Cos(sa), cy + r * Math.Sin(sa), cz);
                p2 = new Point3d(cx + r * Math.Cos(ea), cy + r * Math.Sin(ea), cz);
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
