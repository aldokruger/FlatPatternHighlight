using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using NXOpen;
using NXOpen.Features;
using NXOpen.MenuBar;

namespace FlatPatternHighlight
{
    public class HighlightFlatPattern
    {
        [DllImport("libufun.dll", CallingConvention = CallingConvention.Cdecl)]
        static extern int UF_MODL_ask_face_loops(Tag face, out IntPtr loopList);

        [DllImport("libufun.dll", CallingConvention = CallingConvention.Cdecl)]
        static extern int UF_MODL_ask_loop_list_count(IntPtr loopList, out int count);

        [DllImport("libufun.dll", CallingConvention = CallingConvention.Cdecl)]
        static extern int UF_MODL_ask_loop_list_item(IntPtr loopList, int index, out int type, out IntPtr edgeList);

        [DllImport("libufun.dll", CallingConvention = CallingConvention.Cdecl)]
        static extern int UF_MODL_ask_list_count(IntPtr list, out int count);

        [DllImport("libufun.dll", CallingConvention = CallingConvention.Cdecl)]
        static extern int UF_MODL_ask_list_item(IntPtr list, int index, out Tag tag);

        [DllImport("libufun.dll", CallingConvention = CallingConvention.Cdecl)]
        static extern int UF_MODL_delete_loop_list(ref IntPtr loopList);

        private static Session theSession;
        private static UI theUI;

        public static void Main(string[] args)
        {
            try
            {
                theSession = Session.GetSession();
                theUI = UI.GetUI();

                Part workPart = theSession.Parts.Work;
                if (workPart == null)
                {
                    theUI.NXMessageBox.Show(
                        "Flat Pattern Highlight",
                        NXMessageBox.DialogType.Error,
                        "No active work part. Open or create a part first.");
                    return;
                }

                FlatPattern flatPattern = FindFlatPattern(workPart);
                if (flatPattern == null)
                {
                    theUI.NXMessageBox.Show(
                        "Flat Pattern Highlight",
                        NXMessageBox.DialogType.Error,
                        "No Flat Pattern feature found.\n\n" +
                        "The active part must contain a Sheet Metal flat pattern.\n" +
                        "Use Insert > Sheet Metal > Flat Pattern to create one first.");
                    return;
                }

                LogFile lw = theSession.LogFile;
                lw.WriteLine("=== FlatPatternHighlight Diagnostic Log ===");
                lw.WriteLine($"Part: {workPart.Name}");
                lw.WriteLine("");

                List<Curve> outerPerim = null;
                List<Curve> bendLines = null;

                HighlightOuterPerimeter(flatPattern, workPart, lw, out outerPerim);
                if (outerPerim != null && outerPerim.Count > 0)
                    HighlightBendCenterLines(flatPattern, lw, outerPerim, out bendLines);

                if (bendLines != null && bendLines.Count > 0 && outerPerim != null && outerPerim.Count > 0)
                    AnalyzeBendToPerimeter(bendLines, outerPerim, lw, workPart);

                lw.WriteLine("=== End of Diagnostic Log ===");
            }
            catch (NXException ex)
            {
                if (theUI != null)
                    theUI.NXMessageBox.Show(
                        "Flat Pattern Highlight - Error",
                        NXMessageBox.DialogType.Error,
                        $"NX Error: {ex.Message}");
            }
            catch (Exception ex)
            {
                if (theUI != null)
                    theUI.NXMessageBox.Show(
                        "Flat Pattern Highlight - Error",
                        NXMessageBox.DialogType.Error,
                        $"Error: {ex.Message}");
            }
        }

        // ──────────────────────────────────────────────────
        // STEP 1: Outer Perimeter
        // ──────────────────────────────────────────────────
        private static void HighlightOuterPerimeter(FlatPattern flatPattern, Part workPart, LogFile lw, out List<Curve> outerPerim)
        {
            outerPerim = null;
            lw.WriteLine("--- Outer Perimeter (True External Boundary) ---");

            FlatPattern.ObjectDataEdge[] exteriorCurves;
            flatPattern.GetExteriorCurves(out exteriorCurves);

            if (exteriorCurves == null || exteriorCurves.Length == 0)
            {
                lw.WriteLine("  (no exterior curves found)");
                return;
            }

            lw.WriteLine($"  Total exterior curves (raw): {exteriorCurves.Length}");
            lw.WriteLine("");

            Body flatBody = FindFlatSolidBody(workPart, flatPattern, lw);
            if (flatBody == null)
            {
                lw.WriteLine("  ERROR: Could not locate flat solid body.");
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

            var outerEdgeTags = new HashSet<Tag>();

            Face[] faces = flatBody.GetFaces();
            lw.WriteLine($"  Flat body faces: {faces.Length}");

            foreach (var face in faces)
            {
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
                    if (loopType != 1) continue;

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

        // ──────────────────────────────────────────────────
        // STEP 2: Bend Center Lines
        // ──────────────────────────────────────────────────
        private static void HighlightBendCenterLines(FlatPattern flatPattern, LogFile lw, List<Curve> outerPerim, out List<Curve> bendLines)
        {
            bendLines = new List<Curve>();
            lw.WriteLine("--- Bend Center Lines ---");

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

        // ──────────────────────────────────────────────────
        // STEP 3: Bend Line → Nearest Perimeter Analysis
        // ──────────────────────────────────────────────────
        private static double AxisCoord(Point3d p, int axis)
        {
            switch (axis) { case 0: return p.X; case 1: return p.Y; default: return p.Z; }
        }

        // Flat pattern curves don't always lie in the Z=const (XY) plane — some parts are
        // modeled with the flat pattern in the XZ or YZ plane. Detect which axis has the
        // smallest spread across all curve endpoints; that's the plane's normal, and the
        // other two axes are the ones that actually carry the 2D geometry.
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

        private static void AnalyzeBendToPerimeter(List<Curve> bendLines, List<Curve> outerPerim, LogFile lw, Part workPart)
        {
            int pmiCount = 0;

            lw.WriteLine("--- Bend Line → Nearest Perimeter (Parallel) ---");

            int normalAxis = DetectNormalAxis(bendLines, outerPerim);
            int uAxis, vAxis;
            if (normalAxis == 0) { uAxis = 1; vAxis = 2; }
            else if (normalAxis == 1) { uAxis = 0; vAxis = 2; }
            else { uAxis = 0; vAxis = 1; }
            string[] axisNames = { "X", "Y", "Z" };
            lw.WriteLine($"  [diag] Flat pattern plane: normal={axisNames[normalAxis]}  u={axisNames[uAxis]}  v={axisNames[vAxis]}");

            double GetU(Point3d p) => AxisCoord(p, uAxis);
            double GetV(Point3d p) => AxisCoord(p, vAxis);

            double bboxMinU = double.MaxValue, bboxMinV = double.MaxValue;
            double bboxMaxU = double.MinValue, bboxMaxV = double.MinValue;

            var perimData = new List<(Point3d start, Point3d end, Vector3d dir, double len)>();
            int nonLineArcCount = 0;
            foreach (var c in outerPerim)
            {
                Point3d s, e; GetEndPoints(c, out s, out e);
                if (!(c is Line) && !(c is Arc)) nonLineArcCount++;
                double du = GetU(e) - GetU(s), dv = GetV(e) - GetV(s);
                double len = Math.Sqrt(du * du + dv * dv);
                Vector3d d = len > 1e-6 ? new Vector3d(du / len, dv / len, 0) : new Vector3d(0, 0, 0);
                perimData.Add((s, e, d, len));
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
                int bestIdxA, int bestIdxB, int farIdxA, int farIdxB)>();

            for (int bi = 0; bi < bendLines.Count; bi++)
            {
                Curve bend = bendLines[bi];
                Point3d bs, be; GetEndPoints(bend, out bs, out be);

                double bdu = GetU(be) - GetU(bs), bdv = GetV(be) - GetV(bs);
                double blen = Math.Sqrt(bdu * bdu + bdv * bdv);
                if (blen < 1e-6) continue;

                Vector3d bdir = new Vector3d(bdu / blen, bdv / blen, 0);
                Vector3d nml = new Vector3d(-bdir.Y, bdir.X, 0);

                double mu = (GetU(bs) + GetU(be)) / 2;
                double mv = (GetV(bs) + GetV(be)) / 2;

                double bestDistA = double.MaxValue, bestDistB = double.MaxValue;
                int bestIdxA = -1, bestIdxB = -1;
                double farDistA = -1, farDistB = -1;
                int farIdxA = -1, farIdxB = -1;
                double longestLenA = -1, longestLenB = -1;
                int longestIdxA = -1, longestIdxB = -1;

                for (int pi = 0; pi < perimData.Count; pi++)
                {
                    var (ps, pe, pdir, plen) = perimData[pi];

                    double dot = pdir.X * bdir.X + pdir.Y * bdir.Y;
                    if (Math.Abs(dot) < 0.95) { hasArcs = true; continue; }

                    double t1 = (GetU(ps) - GetU(bs)) * bdir.X + (GetV(ps) - GetV(bs)) * bdir.Y;
                    double t2 = (GetU(pe) - GetU(bs)) * bdir.X + (GetV(pe) - GetV(bs)) * bdir.Y;
                    double tMin = Math.Min(t1, t2), tMax = Math.Max(t1, t2);
                    double overlap = Math.Min(blen, tMax) - Math.Max(0, tMin);
                    if (overlap <= 0) continue;

                    double cu = (GetU(ps) + GetU(pe)) / 2;
                    double cv = (GetV(ps) + GetV(pe)) / 2;

                    double vu = cu - mu, vv = cv - mv;
                    double proj = vu * nml.X + vv * nml.Y;

                    double dist = Math.Abs(proj);

                    if (proj > 0)
                    {
                        if (dist < bestDistA) { bestDistA = dist; bestIdxA = pi; }
                        if (dist > farDistA) { farDistA = dist; farIdxA = pi; }
                        if (plen > longestLenA) { longestLenA = plen; longestIdxA = pi; }
                    }
                    else if (proj < 0)
                    {
                        if (dist < bestDistB) { bestDistB = dist; bestIdxB = pi; }
                        if (dist > farDistB) { farDistB = dist; farIdxB = pi; }
                        if (plen > longestLenB) { longestLenB = plen; longestIdxB = pi; }
                    }
                }

                // Prefer the longer, more reliable perimeter line over the one that happens
                // to touch the bounding box edge, when the latter is clearly a small/short
                // segment (e.g. a relief notch) rather than the actual panel edge.
                const double SmallEdgeRatio = 0.5;
                if (farIdxA >= 0 && longestIdxA >= 0 && perimData[farIdxA].len < SmallEdgeRatio * longestLenA)
                    farIdxA = longestIdxA;
                if (farIdxB >= 0 && longestIdxB >= 0 && perimData[farIdxB].len < SmallEdgeRatio * longestLenB)
                    farIdxB = longestIdxB;

                double sideADist = bestIdxA >= 0 ? DistToBboxEdge(mu, mv, nml.X, nml.Y, bboxMinU, bboxMinV, bboxMaxU, bboxMaxV) : -1;
                double sideBDist = bestIdxB >= 0 ? DistToBboxEdge(mu, mv, -nml.X, -nml.Y, bboxMinU, bboxMinV, bboxMaxU, bboxMaxV) : -1;

                lw.WriteLine($"  Bend[{bi}] Tag={bend.Tag}  Mid=({mu:F1},{mv:F1})  Dir=({bdir.X:F3},{bdir.Y:F3})  Len={blen:F1}");
                lw.WriteLine($"    Side A (nml+): nearest={bestDistA,8:F2}  bboxDist={sideADist,8:F2}" +
                    (bestIdxA >= 0 ? $"  perimTag={outerPerim[bestIdxA].Tag}" : "  (none)"));

                lw.WriteLine($"    Side B (nml-): nearest={bestDistB,8:F2}  bboxDist={sideBDist,8:F2}" +
                    (bestIdxB >= 0 ? $"  perimTag={outerPerim[bestIdxB].Tag}" : "  (none)"));

                lw.WriteLine("");

                Point3d bendPoint = new Point3d((bs.X + be.X) / 2, (bs.Y + be.Y) / 2, (bs.Z + be.Z) / 2);
                bendInfos.Add((bi, bend, bendPoint, bs, be, bdir, nml, bestIdxA, bestIdxB, farIdxA, farIdxB));
            }

            pmiCount = CreateChainDimensions(workPart, bendInfos, perimData, outerPerim,
                bboxMinU, bboxMinV, bboxMaxU, bboxMaxV, uAxis, vAxis, normalAxis, lw);

            lw.WriteLine($"  PMI dimensions created: {pmiCount}");
            lw.WriteLine("");

            if (hasArcs)
                lw.WriteLine("  (some perimeter arcs were skipped — only straight lines compared)");
        }

        private static Point3d ProjectPointOnSegment(Point3d point, Point3d segStart, Point3d segEnd)
        {
            double vx = segEnd.X - segStart.X, vy = segEnd.Y - segStart.Y, vz = segEnd.Z - segStart.Z;
            double lenSq = vx * vx + vy * vy + vz * vz;
            if (lenSq < 1e-12) return segStart;
            double t = ((point.X - segStart.X) * vx + (point.Y - segStart.Y) * vy + (point.Z - segStart.Z) * vz) / lenSq;
            t = Math.Max(0, Math.Min(1, t));
            return new Point3d(segStart.X + t * vx, segStart.Y + t * vy, segStart.Z + t * vz);
        }

        // ──────────────────────────────────────────────────
        // Chain dimensioning: boundary → nearest bend → next bend → ...
        // on each side, instead of dimensioning every bend independently.
        // ──────────────────────────────────────────────────
        private static int CreateChainDimensions(Part workPart,
            List<(int bi, Curve bend, Point3d pt, Point3d bs, Point3d be, Vector3d dir, Vector3d nml, int bestIdxA, int bestIdxB, int farIdxA, int farIdxB)> bendInfos,
            List<(Point3d start, Point3d end, Vector3d dir, double len)> perimData,
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

                var group = new List<int> { i };
                used[i] = true;
                for (int j = i + 1; j < bendInfos.Count; j++)
                {
                    if (used[j]) continue;
                    double dot = bendInfos[i].dir.X * bendInfos[j].dir.X + bendInfos[i].dir.Y * bendInfos[j].dir.Y;
                    if (Math.Abs(dot) >= 0.95) { group.Add(j); used[j] = true; }
                }

                // Bends that are parallel but sit in different "lanes" (no overlap of their
                // own extent) are independent flanges, not links in the same chain — split
                // the direction-group further by range overlap before chaining each lane.
                foreach (var lane in ClusterByRangeOverlap(bendInfos, group, uAxis, vAxis))
                {
                    count += CreateChainForGroup(workPart, bendInfos, lane, perimData, outerPerim,
                        bboxMinU, bboxMinV, bboxMaxU, bboxMaxV, uAxis, vAxis, normalAxis, lw);
                }
            }

            return count;
        }

        private const double LaneLengthRatioThreshold = 0.7;

        private static List<List<int>> ClusterByRangeOverlap(
            List<(int bi, Curve bend, Point3d pt, Point3d bs, Point3d be, Vector3d dir, Vector3d nml, int bestIdxA, int bestIdxB, int farIdxA, int farIdxB)> bendInfos,
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

            // Consolidate lanes that ended up overlapping (and similar in length) after later merges.
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

        private static int CreateChainForGroup(Part workPart,
            List<(int bi, Curve bend, Point3d pt, Point3d bs, Point3d be, Vector3d dir, Vector3d nml, int bestIdxA, int bestIdxB, int farIdxA, int farIdxB)> bendInfos,
            List<int> groupIdx,
            List<(Point3d start, Point3d end, Vector3d dir, double len)> perimData,
            List<Curve> outerPerim,
            double bboxMinU, double bboxMinV, double bboxMaxU, double bboxMaxV,
            int uAxis, int vAxis, int normalAxis,
            LogFile lw)
        {
            var refInfo = bendInfos[groupIdx[0]];
            Vector3d refNml = refInfo.nml;

            double[] cornersOffset = new double[]
            {
                bboxMinU * refNml.X + bboxMinV * refNml.Y,
                bboxMinU * refNml.X + bboxMaxV * refNml.Y,
                bboxMaxU * refNml.X + bboxMinV * refNml.Y,
                bboxMaxU * refNml.X + bboxMaxV * refNml.Y
            };
            double bboxLow = cornersOffset.Min();
            double bboxHigh = cornersOffset.Max();

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
            count += CreateChainSide(workPart, bendInfos, lowSide, perimData, isLowSide: true, normalAxis, lw);
            count += CreateChainSide(workPart, bendInfos, highSide, perimData, isLowSide: false, normalAxis, lw);
            return count;
        }

        private static int CreateChainSide(Part workPart,
            List<(int bi, Curve bend, Point3d pt, Point3d bs, Point3d be, Vector3d dir, Vector3d nml, int bestIdxA, int bestIdxB, int farIdxA, int farIdxB)> bendInfos,
            List<(int idx, double offset, bool flipped)> side,
            List<(Point3d start, Point3d end, Vector3d dir, double len)> perimData,
            bool isLowSide, int normalAxis, LogFile lw)
        {
            if (side.Count == 0) return 0;
            int count = 0;

            var first = bendInfos[side[0].idx];
            bool flipped = side[0].flipped;
            int farIdxBoundary = isLowSide
                ? (flipped ? first.farIdxA : first.farIdxB)
                : (flipped ? first.farIdxB : first.farIdxA);

            if (farIdxBoundary >= 0)
            {
                var seg = perimData[farIdxBoundary];
                Point3d boundaryPoint = ProjectPointOnSegment(first.pt, seg.start, seg.end);
                if (CreatePmiPointDimension(workPart, boundaryPoint, first.pt, first.dir, normalAxis, lw))
                    count++;
            }
            else
            {
                lw.WriteLine($"  [Chain] No boundary curve found for Bend Tag={first.bend.Tag} on {(isLowSide ? "low" : "high")} side.");
            }

            for (int k = 1; k < side.Count; k++)
            {
                var prev = bendInfos[side[k - 1].idx];
                var curr = bendInfos[side[k].idx];
                if (CreatePmiPointDimension(workPart, prev.pt, curr.pt, curr.dir, normalAxis, lw))
                    count++;
            }

            return count;
        }

        private static bool CreatePmiPointDimension(Part workPart, Point3d pointA, Point3d pointB, Vector3d dir, int normalAxis, LogFile lw)
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
                NXOpen.Annotations.PmiDefaultPlane planeKind;
                switch (normalAxis)
                {
                    case 0: planeKind = NXOpen.Annotations.PmiDefaultPlane.YzOfWcs; break;
                    case 1: planeKind = NXOpen.Annotations.PmiDefaultPlane.XzOfWcs; break;
                    default: planeKind = NXOpen.Annotations.PmiDefaultPlane.XyOfWcs; break;
                }
                Xform annotationPlane = workPart.Annotations.GetDefaultAnnotationPlane(planeKind);

                Point3d origin = new Point3d(
                    (pointA.X + pointB.X) / 2,
                    (pointA.Y + pointB.Y) / 2,
                    (pointA.Z + pointB.Z) / 2);

                bool horizontalBend = Math.Abs(dir.X) >= Math.Abs(dir.Y);
                NXObject dimObj;
                if (horizontalBend)
                    dimObj = workPart.Dimensions.CreatePmiVerticalDimension(dimData, pmiData, annotationPlane, origin);
                else
                    dimObj = workPart.Dimensions.CreatePmiHorizontalDimension(dimData, pmiData, annotationPlane, origin);

                if (dimObj != null)
                    dimObj.SetUserAttribute("FlatPatternHighlight", 0, "true", NXOpen.Update.Option.Later);

                return dimObj != null;
            }
            catch (NXException nex)
            {
                lw.WriteLine($"  [PMI] NXException: ErrorCode={nex.ErrorCode}  {nex.Message}");
                return false;
            }
            catch (Exception ex)
            {
                lw.WriteLine($"  [PMI] {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

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

        // ──────────────────────────────────────────────────
        // HELPERS
        // ──────────────────────────────────────────────────
        private static Body FindFlatSolidBody(Part workPart, FlatPattern flatPattern, LogFile lw)
        {
            Body[] bodies = flatPattern.GetBodies();
            lw.WriteLine($"  [diag] flatPattern.GetBodies() count: {(bodies != null ? bodies.Length : -1)}");
            if (bodies != null && bodies.Length > 0) { lw.WriteLine($"  Body via GetBodies(): {bodies[0].Tag}"); return bodies[0]; }

            NXObject[] entities = flatPattern.GetEntities();
            lw.WriteLine($"  [diag] flatPattern.GetEntities() count: {(entities != null ? entities.Length : -1)}");
            if (entities != null)
            {
                foreach (var ent in entities)
                    lw.WriteLine($"  [diag]   entity type: {ent.GetType().Name}");
                foreach (var ent in entities) { Body b = ent as Body; if (b != null) { lw.WriteLine($"  Body via GetEntities(): {b.Tag}"); return b; } }
            }

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

                    foreach (Feature f in features)
                        if (f is FlatPattern fp && fp.Tag == flatPattern.Tag)
                        { lw.WriteLine($"  Body via workPart.Bodies (FlatPattern feature): {body.Tag}"); return body; }

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

        private static FlatPattern FindFlatPattern(Part workPart)
        {
            foreach (Feature f in workPart.Features)
                if (f is FlatPattern) return (FlatPattern)f;
            return null;
        }

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

        private static void HighlightObjects(List<DisplayableObject> objects)
        {
            if (objects.Count == 0) return;
            foreach (var o in objects) o.Highlight();
        }

        public static int GetUnloadOption(string arg) { return (int)Session.LibraryUnloadOption.Immediately; }

        public static int Startup()
        {
            try
            {
                theSession = Session.GetSession();
                theUI = UI.GetUI();
                theUI.MenuBarManager.AddMenuAction("FLAT_PATTERN_HIGHLIGHT",
                    new MenuBarManager.ActionCallback(OnMenuCallback));
                return 0;
            }
            catch { return 1; }
        }

        private static MenuBarManager.CallbackStatus OnMenuCallback(MenuButtonEvent buttonEvent)
        {
            Main(new string[0]);
            return MenuBarManager.CallbackStatus.Continue;
        }
    }
}
