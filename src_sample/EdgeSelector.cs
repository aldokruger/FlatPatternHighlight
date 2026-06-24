using NXOpen;
using System;
using System.Collections.Generic;
using System.Linq;

namespace CotagemFlatPattern
{
    public class EdgeSelector
    {
        private readonly double _minContactRatio;

        public EdgeSelector(double minContactRatio = 0.30)
        {
            _minContactRatio = Math.Min(Math.Max(minContactRatio, 0.0), 1.0);
        }

        public List<BendEdgePair> SelectPairs(List<BendLineInfo> bends, List<ExternalEdge> contour)
        {
            List<BendEdgePair> pairs = new List<BendEdgePair>();
            if (bends == null || contour == null || bends.Count == 0 || contour.Count == 0)
                return pairs;

            List<BendLineInfo> horizontalBends = bends.Where(IsHorizontalBend).ToList();
            List<BendLineInfo> verticalBends = bends.Where(b => !IsHorizontalBend(b)).ToList();

            AddOrientationPairs(horizontalBends, contour, pairs, isHorizontal: true);
            AddOrientationPairs(verticalBends, contour, pairs, isHorizontal: false);

            return pairs;
        }

        private void AddOrientationPairs(List<BendLineInfo> bends, List<ExternalEdge> contour, List<BendEdgePair> pairs, bool isHorizontal)
        {
            if (bends.Count == 0)
                return;

            List<ExternalEdge> candidateEdges = contour
                .Where(e => IsHorizontalEdge(e) == isHorizontal)
                .ToList();

            if (candidateEdges.Count == 0)
                return;

            if (isHorizontal)
            {
                ExternalEdge top = candidateEdges.OrderByDescending(e => e.MidPoint.Y).First();
                ExternalEdge bottom = candidateEdges.OrderBy(e => e.MidPoint.Y).First();

                SplitByNearestSide(bends, b => b.MidPoint.Y, top.MidPoint.Y, bottom.MidPoint.Y,
                    out List<BendLineInfo> nearTop, out List<BendLineInfo> nearBottom);

                AddSideChain(nearTop, pairs, top, sideKey: "TOP", isHorizontal: true);
                AddSideChain(nearBottom, pairs, bottom, sideKey: "BOTTOM", isHorizontal: true);
                return;
            }

            ExternalEdge right = candidateEdges.OrderByDescending(e => e.MidPoint.X).First();
            ExternalEdge left = candidateEdges.OrderBy(e => e.MidPoint.X).First();

            SplitByNearestSide(bends, b => b.MidPoint.X, right.MidPoint.X, left.MidPoint.X,
                out List<BendLineInfo> nearRight, out List<BendLineInfo> nearLeft);

            AddSideChain(nearRight, pairs, right, sideKey: "RIGHT", isHorizontal: false);
            AddSideChain(nearLeft, pairs, left, sideKey: "LEFT", isHorizontal: false);
        }

        // Cada dobra deve pertencer a apenas uma cadeia (a do lado mais proximo).
        // Sem essa particao, toda dobra interna acaba sendo cotada duas vezes
        // (uma vez a partir de cada borda), gerando cotas redundantes que se
        // sobrepoem entre as linhas de dobra reais.
        private static void SplitByNearestSide(
            List<BendLineInfo> bends,
            Func<BendLineInfo, double> coordSelector,
            double firstSideCoord,
            double secondSideCoord,
            out List<BendLineInfo> nearFirst,
            out List<BendLineInfo> nearSecond)
        {
            nearFirst = new List<BendLineInfo>();
            nearSecond = new List<BendLineInfo>();

            foreach (BendLineInfo bend in bends)
            {
                double coord = coordSelector(bend);
                if (Math.Abs(firstSideCoord - coord) <= Math.Abs(secondSideCoord - coord))
                    nearFirst.Add(bend);
                else
                    nearSecond.Add(bend);
            }
        }

        private void AddSideChain(List<BendLineInfo> bends, List<BendEdgePair> pairs, ExternalEdge outerEdge, string sideKey, bool isHorizontal)
        {
            List<RankedBend> ordered = bends
                .Select(b => new RankedBend(b, BuildOuterReferencePairCandidate(b, outerEdge, sideKey, isHorizontal)))
                .Where(x => x.Candidate != null)
                .OrderBy(x => x.Candidate.Distance)
                .ThenByDescending(x => x.Bend.Length)
                .ToList();

            if (ordered.Count == 0)
                return;

            pairs.Add(ordered[0].Candidate);

            for (int i = 1; i < ordered.Count; i++)
            {
                BendLineInfo previousBend = ordered[i - 1].Bend;
                BendLineInfo currentBend = ordered[i].Bend;
                BendEdgePair pair = BuildBendToBendPair(currentBend, previousBend);
                if (pair != null)
                    pairs.Add(pair);
            }
        }

        private BendEdgePair BuildOuterReferencePairCandidate(BendLineInfo bend, ExternalEdge outerEdge, string sideKey, bool isHorizontal)
        {
            double bendCoord = isHorizontal ? bend.MidPoint.Y : bend.MidPoint.X;
            double outerCoord = isHorizontal ? outerEdge.MidPoint.Y : outerEdge.MidPoint.X;
            double signedDelta = outerCoord - bendCoord;

            if ((sideKey == "TOP" || sideKey == "RIGHT") && signedDelta <= 0.0)
                return null;
            if ((sideKey == "BOTTOM" || sideKey == "LEFT") && signedDelta >= 0.0)
                return null;

            double contactRatio = GeometryUtils.ProjectedContactLength(bend, outerEdge) / Math.Max(bend.Length, GeometryUtils.Tolerance);
            if (contactRatio < _minContactRatio)
                return null;

            return new BendEdgePair
            {
                Bend = bend,
                TargetEdge = outerEdge,
                Distance = Math.Abs(signedDelta),
                ContactRatio = contactRatio,
                ReferenceType = sideKey,
                ReferenceJournalIdentifier = outerEdge.Edge?.JournalIdentifier ?? sideKey
            };
        }

        private BendEdgePair BuildBendToBendPair(BendLineInfo currentBend, BendLineInfo previousBend)
        {
            ExternalEdge previousAsEdge = CreateReferenceEdge(previousBend);
            double contactRatio = GeometryUtils.ProjectedContactLength(currentBend, previousAsEdge) / Math.Max(currentBend.Length, GeometryUtils.Tolerance);
            if (contactRatio < _minContactRatio)
                return null;

            double distance = IsHorizontalBend(currentBend)
                ? Math.Abs(currentBend.MidPoint.Y - previousBend.MidPoint.Y)
                : Math.Abs(currentBend.MidPoint.X - previousBend.MidPoint.X);

            if (distance <= GeometryUtils.Tolerance)
                return null;

            return new BendEdgePair
            {
                Bend = currentBend,
                TargetEdge = previousAsEdge,
                Distance = distance,
                ContactRatio = contactRatio,
                ReferenceType = "BEND",
                ReferenceJournalIdentifier = previousBend.Edge?.JournalIdentifier ?? string.Empty
            };
        }

        private static ExternalEdge CreateReferenceEdge(BendLineInfo bend)
        {
            GeometryUtils.GetEdgeEndPoints(bend.Edge, out Point3d start, out Point3d end);
            return new ExternalEdge
            {
                Edge = bend.Edge,
                StartPoint = start,
                EndPoint = end,
                MidPoint = bend.MidPoint,
                Direction = bend.Direction,
                Length = bend.Length
            };
        }

        private static bool IsHorizontalBend(BendLineInfo bend)
        {
            return Math.Abs(bend.Direction.X) >= Math.Abs(bend.Direction.Y);
        }

        private static bool IsHorizontalEdge(ExternalEdge edge)
        {
            return Math.Abs(edge.Direction.X) >= Math.Abs(edge.Direction.Y);
        }

        private sealed class RankedBend
        {
            public RankedBend(BendLineInfo bend, BendEdgePair candidate)
            {
                Bend = bend;
                Candidate = candidate;
            }

            public BendLineInfo Bend { get; }

            public BendEdgePair Candidate { get; }
        }
    }
}
