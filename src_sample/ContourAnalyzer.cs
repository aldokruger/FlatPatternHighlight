using NXOpen;
using System;
using System.Collections.Generic;
using System.Linq;

namespace CotagemFlatPattern
{
    public class ContourAnalyzer
    {
        private readonly Session _session;
        private readonly Part _workPart;

        public ContourAnalyzer(Session session, Part workPart)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _workPart = workPart ?? throw new ArgumentNullException(nameof(workPart));
        }

        public List<ExternalEdge> GetOuterContour(Body flatBody = null)
        {
            List<ExternalEdge> contour = new List<ExternalEdge>();

            Body targetBody = flatBody ?? GetDefaultBody();
            if (targetBody == null)
                return contour;

            List<ExternalEdge> allLinearEdges = new List<ExternalEdge>();
            HashSet<string> seenEdges = new HashSet<string>(StringComparer.Ordinal);

            try
            {
                foreach (Edge edge in targetBody.GetEdges())
                {
                    if (!GeometryUtils.IsEdgeLinear(edge))
                        continue;

                    if (!seenEdges.Add(GetEdgeKey(edge)))
                        continue;

                    GeometryUtils.GetEdgeEndPoints(edge, out Point3d start, out Point3d end);

                    allLinearEdges.Add(new ExternalEdge
                    {
                        Edge = edge,
                        StartPoint = start,
                        EndPoint = end,
                        MidPoint = GeometryUtils.GetEdgeMidPoint(edge),
                        Direction = GeometryUtils.GetEdgeDirection(edge),
                        Length = edge.GetLength()
                    });
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Erro ao analisar contorno externo: {ex.Message}", ex);
            }

            if (allLinearEdges.Count == 0)
                return contour;

            GetBoundingBox(allLinearEdges, out double xMin, out double xMax, out double yMin, out double yMax);
            double tol = 1.0;

            contour = allLinearEdges
                .Where(e => IsExtremeBoundaryEdge(e, xMin, xMax, yMin, yMax, tol))
                .ToList();

            return contour;
        }

        private static bool IsExtremeBoundaryEdge(ExternalEdge edge, double xMin, double xMax, double yMin, double yMax, double tol)
        {
            bool horizontal = Math.Abs(edge.Direction.X) >= Math.Abs(edge.Direction.Y);
            if (horizontal)
            {
                return IsNear(edge.StartPoint.Y, yMin, tol) && IsNear(edge.EndPoint.Y, yMin, tol) ||
                       IsNear(edge.StartPoint.Y, yMax, tol) && IsNear(edge.EndPoint.Y, yMax, tol);
            }

            return IsNear(edge.StartPoint.X, xMin, tol) && IsNear(edge.EndPoint.X, xMin, tol) ||
                   IsNear(edge.StartPoint.X, xMax, tol) && IsNear(edge.EndPoint.X, xMax, tol);
        }

        private static bool IsNear(double value, double target, double tol)
        {
            return Math.Abs(value - target) <= tol;
        }

        private static string GetEdgeKey(Edge edge)
        {
            return edge?.JournalIdentifier ?? string.Empty;
        }

        private Body GetDefaultBody()
        {
            foreach (Body body in _workPart.Bodies)
                return body;
            return null;
        }

        public void GetBoundingBox(List<ExternalEdge> edges, out double xMin, out double xMax, out double yMin, out double yMax)
        {
            xMin = double.MaxValue;
            xMax = double.MinValue;
            yMin = double.MaxValue;
            yMax = double.MinValue;

            foreach (var e in edges)
            {
                xMin = Math.Min(xMin, Math.Min(e.StartPoint.X, e.EndPoint.X));
                xMax = Math.Max(xMax, Math.Max(e.StartPoint.X, e.EndPoint.X));
                yMin = Math.Min(yMin, Math.Min(e.StartPoint.Y, e.EndPoint.Y));
                yMax = Math.Max(yMax, Math.Max(e.StartPoint.Y, e.EndPoint.Y));
            }
        }
    }
}
