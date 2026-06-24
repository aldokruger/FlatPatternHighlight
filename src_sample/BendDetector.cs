using NXOpen;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace CotagemFlatPattern
{
    public class BendDetector
    {
        private readonly Session _session;
        private readonly Part _workPart;
        private readonly PluginSettings _settings;
        private readonly HashSet<int> _configuredLayers;
        private readonly HashSet<DisplayableObject.ObjectFont> _configuredLineFonts;

        // Linhas de dobra em um flat pattern sao planas (sem angulo diedro real) e se
        // estendem por quase toda a largura/altura da peca, ao contrario das pequenas
        // arestas de alivio de canto. Esse e o criterio geometrico usado como fallback.
        private const double MinBendLineRatio = 0.5;

        public BendDetector(Session session, Part workPart, PluginSettings settings = null)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _workPart = workPart ?? throw new ArgumentNullException(nameof(workPart));
            _settings = settings ?? new PluginSettings();
            _configuredLayers = ParseLayers(_settings.BendLayersCsv);
            _configuredLineFonts = ParseLineFonts(_settings.BendLineFontsCsv);
        }

        public List<BendLineInfo> Detect()
        {
            Body flatBody = GetFlatSolidBody();
            LogLinearEdges(flatBody);

            List<BendLineInfo> bends;
            switch (_settings.DetectionMode)
            {
                case BendDetectionMode.ByLayer:
                    bends = DetectFromLayer(flatBody);
                    break;
                case BendDetectionMode.ByLineType:
                    bends = DetectFromLineType(flatBody);
                    break;
                case BendDetectionMode.Hybrid:
                    bends = DetectHybrid(flatBody, includeGeometryFallback: false);
                    break;
                case BendDetectionMode.Geometry:
                    bends = DetectFromLongInternalLines(flatBody);
                    break;
                default:
                    bends = DetectHybrid(flatBody, includeGeometryFallback: true);
                    break;
            }

            return CollapseEquivalentBends(bends);
        }

        private List<BendLineInfo> DetectHybrid(Body flatBody, bool includeGeometryFallback)
        {
            Dictionary<string, BendLineInfo> merged = new Dictionary<string, BendLineInfo>(StringComparer.Ordinal);

            AddRange(merged, DetectFromLayer(flatBody));
            AddRange(merged, DetectFromLineType(flatBody));

            if (merged.Count == 0 && includeGeometryFallback)
                AddRange(merged, DetectFromLongInternalLines(flatBody));

            return merged.Values.ToList();
        }

        private static void AddRange(Dictionary<string, BendLineInfo> target, List<BendLineInfo> bends)
        {
            foreach (BendLineInfo bend in bends)
            {
                string key = GetEdgeKey(bend.Edge);
                if (string.IsNullOrEmpty(key))
                    continue;

                if (!target.TryGetValue(key, out BendLineInfo current) || bend.Length > current.Length)
                    target[key] = bend;
            }
        }

        private List<BendLineInfo> DetectFromLongInternalLines(Body flatBody)
        {
            List<BendLineInfo> bends = new List<BendLineInfo>();
            HashSet<string> seenEdges = new HashSet<string>(StringComparer.Ordinal);
            GetBoundingBox(flatBody, out double xMin, out double xMax, out double yMin, out double yMax);

            try
            {
                foreach (Edge edge in GetCandidateEdges(flatBody))
                {
                    if (!IsLikelyBendLine(edge, xMin, xMax, yMin, yMax))
                        continue;

                    if (!seenEdges.Add(GetEdgeKey(edge)))
                        continue;

                    bends.Add(CreateBendLineInfo(edge, 90.0));
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Erro na deteccao por linhas internas longas: {ex.Message}", ex);
            }

            return bends;
        }

        private List<BendLineInfo> DetectFromLayer(Body flatBody)
        {
            List<BendLineInfo> bends = new List<BendLineInfo>();
            HashSet<string> seenEdges = new HashSet<string>(StringComparer.Ordinal);
            GetBoundingBox(flatBody, out double xMin, out double xMax, out double yMin, out double yMax);

            try
            {
                foreach (Edge edge in GetCandidateEdges(flatBody))
                {
                    if (!IsLikelyBendLine(edge, xMin, xMax, yMin, yMax))
                        continue;

                    if (!seenEdges.Add(GetEdgeKey(edge)))
                        continue;

                    if (_configuredLayers.Contains(edge.Layer))
                        bends.Add(CreateBendLineInfo(edge, 90.0));
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Erro na deteccao por layer: {ex.Message}", ex);
            }

            return bends;
        }

        private List<BendLineInfo> DetectFromLineType(Body flatBody)
        {
            List<BendLineInfo> bends = new List<BendLineInfo>();
            HashSet<string> seenEdges = new HashSet<string>(StringComparer.Ordinal);
            GetBoundingBox(flatBody, out double xMin, out double xMax, out double yMin, out double yMax);

            try
            {
                foreach (Edge edge in GetCandidateEdges(flatBody))
                {
                    if (!IsLikelyBendLine(edge, xMin, xMax, yMin, yMax))
                        continue;

                    if (!seenEdges.Add(GetEdgeKey(edge)))
                        continue;

                    if (_configuredLineFonts.Contains(edge.LineFont))
                        bends.Add(CreateBendLineInfo(edge, 90.0));
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Erro na deteccao por tipo de linha: {ex.Message}", ex);
            }

            return bends;
        }

        // Independente do criterio de selecao (layer, tipo de linha, etc.), uma linha de
        // dobra real de flat pattern atravessa quase toda a largura/altura da peca. Esse
        // filtro descarta arestas curtas (alivios de canto, furos, etc.) que por acaso
        // estejam na mesma layer/fonte configurada mas nao sao dobras.
        private bool IsLikelyBendLine(Edge edge, double xMin, double xMax, double yMin, double yMax)
        {
            if (!IsPotentialBendEdge(edge) || IsBoundaryEdge(edge, xMin, xMax, yMin, yMax))
                return false;

            double bboxWidth = xMax - xMin;
            double bboxHeight = yMax - yMin;

            Vector3d dir = GeometryUtils.GetEdgeDirection(edge);
            bool horizontal = Math.Abs(dir.X) >= Math.Abs(dir.Y);
            double extent = horizontal ? bboxWidth : bboxHeight;
            if (extent <= GeometryUtils.Tolerance)
                return false;

            double ratio = edge.GetLength() / extent;
            return ratio >= MinBendLineRatio;
        }

        private void GetBoundingBox(Body flatBody, out double xMin, out double xMax, out double yMin, out double yMax)
        {
            xMin = double.MaxValue;
            xMax = double.MinValue;
            yMin = double.MaxValue;
            yMax = double.MinValue;

            foreach (Edge edge in GetCandidateEdges(flatBody))
            {
                if (!GeometryUtils.IsEdgeLinear(edge))
                    continue;

                GeometryUtils.GetEdgeEndPoints(edge, out Point3d start, out Point3d end);
                xMin = Math.Min(xMin, Math.Min(start.X, end.X));
                xMax = Math.Max(xMax, Math.Max(start.X, end.X));
                yMin = Math.Min(yMin, Math.Min(start.Y, end.Y));
                yMax = Math.Max(yMax, Math.Max(start.Y, end.Y));
            }
        }

        private static bool IsBoundaryEdge(Edge edge, double xMin, double xMax, double yMin, double yMax)
        {
            GeometryUtils.GetEdgeEndPoints(edge, out Point3d start, out Point3d end);
            Vector3d dir = GeometryUtils.GetEdgeDirection(edge);
            bool horizontal = Math.Abs(dir.X) >= Math.Abs(dir.Y);
            double tol = 1.0;

            if (horizontal)
            {
                return IsNear(start.Y, yMin, tol) && IsNear(end.Y, yMin, tol) ||
                       IsNear(start.Y, yMax, tol) && IsNear(end.Y, yMax, tol);
            }

            return IsNear(start.X, xMin, tol) && IsNear(end.X, xMin, tol) ||
                   IsNear(start.X, xMax, tol) && IsNear(end.X, xMax, tol);
        }

        private static bool IsNear(double value, double target, double tol)
        {
            return Math.Abs(value - target) <= tol;
        }

        private IEnumerable<Edge> GetCandidateEdges(Body flatBody)
        {
            if (flatBody != null)
                return flatBody.GetEdges();

            List<Edge> edges = new List<Edge>();
            foreach (Body body in _workPart.Bodies)
                edges.AddRange(body.GetEdges());
            return edges;
        }

        private void LogLinearEdges(Body flatBody)
        {
            if (!_settings.LogLineDiagnostics)
                return;

            ListingWindow lw = _session.ListingWindow;
            lw.Open();
            lw.WriteLine("=== CotagemFlatPattern: diagnostico de linhas do flat pattern ===");
            lw.WriteLine($"Modo deteccao: {_settings.DetectionMode}");
            lw.WriteLine($"Layers configuradas: {_settings.BendLayersCsv}");
            lw.WriteLine($"Tipos de linha configurados: {_settings.BendLineFontsCsv}");

            foreach (Edge edge in GetCandidateEdges(flatBody))
            {
                if (!GeometryUtils.IsEdgeLinear(edge))
                    continue;

                string journalId = edge.JournalIdentifier ?? string.Empty;
                string isLayerMatch = _configuredLayers.Contains(edge.Layer) ? "Y" : "N";
                string isFontMatch = _configuredLineFonts.Contains(edge.LineFont) ? "Y" : "N";
                string line = string.Format(
                    CultureInfo.InvariantCulture,
                    "EDGE id={0} len={1:F1} layer={2} color={3} lineFont={4}({5}) layerMatch={6} fontMatch={7}",
                    journalId,
                    edge.GetLength(),
                    edge.Layer,
                    edge.Color,
                    edge.LineFont,
                    (int)edge.LineFont,
                    isLayerMatch,
                    isFontMatch);
                lw.WriteLine(line);
            }

            lw.WriteLine("=== fim do diagnostico ===");
        }

        private static HashSet<int> ParseLayers(string csv)
        {
            HashSet<int> layers = new HashSet<int>();
            foreach (string token in SplitCsv(csv))
            {
                if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out int layer))
                    layers.Add(layer);
            }
            return layers;
        }

        private static HashSet<DisplayableObject.ObjectFont> ParseLineFonts(string csv)
        {
            HashSet<DisplayableObject.ObjectFont> fonts = new HashSet<DisplayableObject.ObjectFont>();
            foreach (string token in SplitCsv(csv))
            {
                if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out int fontValue))
                {
                    if (Enum.IsDefined(typeof(DisplayableObject.ObjectFont), fontValue))
                        fonts.Add((DisplayableObject.ObjectFont)fontValue);
                    continue;
                }

                if (Enum.TryParse(token, true, out DisplayableObject.ObjectFont font))
                    fonts.Add(font);
            }
            return fonts;
        }

        private static IEnumerable<string> SplitCsv(string csv)
        {
            return (csv ?? string.Empty)
                .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.Trim())
                .Where(t => t.Length > 0);
        }

        private List<BendLineInfo> CollapseEquivalentBends(List<BendLineInfo> bends)
        {
            const double groupingTolerance = 1.0;
            Dictionary<string, BendLineInfo> grouped = new Dictionary<string, BendLineInfo>(StringComparer.Ordinal);

            foreach (BendLineInfo bend in bends)
            {
                if (bend == null || bend.Edge == null)
                    continue;

                string key = BuildBendGroupKey(bend, groupingTolerance);
                if (!grouped.TryGetValue(key, out BendLineInfo current))
                {
                    grouped[key] = bend;
                    continue;
                }

                if (bend.Length > current.Length)
                    grouped[key] = bend;
            }

            return grouped.Values
                .OrderByDescending(b => b.Length)
                .ThenBy(b => BuildBendGroupKey(b, groupingTolerance), StringComparer.Ordinal)
                .ToList();
        }

        private static string BuildBendGroupKey(BendLineInfo bend, double tolerance)
        {
            string orientation = Math.Abs(bend.Direction.X) >= Math.Abs(bend.Direction.Y) ? "H" : "V";
            Vector3d perpendicular = GetPerpendicularDirection(bend.Direction, orientation);
            double offset = GeometryUtils.Dot(
                new Vector3d { X = bend.MidPoint.X, Y = bend.MidPoint.Y, Z = bend.MidPoint.Z },
                perpendicular);

            double roundedOffset = Math.Round(offset / tolerance, MidpointRounding.AwayFromZero) * tolerance;
            return string.Format(CultureInfo.InvariantCulture, "{0}|{1:F3}", orientation, roundedOffset);
        }

        private static Vector3d GetPerpendicularDirection(Vector3d bendDirection, string orientation)
        {
            Vector3d perpendicular = GeometryUtils.Normalize(
                GeometryUtils.Cross(bendDirection, new Vector3d { X = 0, Y = 0, Z = 1 }));

            if (GeometryUtils.Magnitude(perpendicular) >= GeometryUtils.Tolerance)
                return perpendicular;

            if (orientation == "H")
                return new Vector3d { X = 0, Y = 1, Z = 0 };

            return new Vector3d { X = 1, Y = 0, Z = 0 };
        }

        private bool IsPotentialBendEdge(Edge edge)
        {
            if (!GeometryUtils.IsEdgeLinear(edge))
                return false;

            return edge.GetLength() >= 5.0;
        }

        private BendLineInfo CreateBendLineInfo(Edge edge, double angle)
        {
            return new BendLineInfo
            {
                Edge = edge,
                MidPoint = GeometryUtils.GetEdgeMidPoint(edge),
                Direction = GeometryUtils.GetEdgeDirection(edge),
                Length = edge.GetLength(),
                Angle = angle,
                BendDir = BendDirection.Up,
                InnerRadius = 0.0
            };
        }

        private static string GetEdgeKey(Edge edge)
        {
            return edge?.JournalIdentifier ?? string.Empty;
        }

        public Body GetFlatSolidBody()
        {
            try
            {
                foreach (NXOpen.Features.Feature feat in _workPart.Features)
                {
                    string className = feat.GetType().Name;
                    if (!className.Contains("FlatSolid"))
                        continue;

                    Body[] bodies = feat.GetBodies();
                    if (bodies != null && bodies.Length > 0)
                        return bodies[0];
                }
            }
            catch
            {
            }

            foreach (Body body in _workPart.Bodies)
                return body;

            return null;
        }
    }
}
