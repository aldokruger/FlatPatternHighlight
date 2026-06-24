using NXOpen;
using NXOpenUI;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace CotagemFlatPattern
{
    public class CotagemFlatPatternMain
    {
        private const int ExternalEdgeColor = 36;
        private const int BendEdgeColor = 186;

        private static Session _session;
        private static Part _workPart;
        private static UI _ui;

        private PluginSettings _settings;
        private PluginSettingsRepository _settingsRepository;
        private PluginSettingsStore _settingsStore;
        private DimensioningResult _lastResult;

        public static void Main(string[] args)
        {
            CotagemFlatPatternMain plugin = new CotagemFlatPatternMain();
            plugin.Run();
        }

        public static int GetUnloadOption(string arg)
        {
            return (int)Session.LibraryUnloadOption.Immediately;
        }

        public void Run()
        {
            try
            {
                _session = Session.GetSession();
                _workPart = _session.Parts.Work;
                _ui = NXOpen.UI.GetUI();

                if (_workPart == null)
                {
                    _ui.NXMessageBox.Show("Cotagem Flat Pattern", NXMessageBox.DialogType.Information, "Nenhuma peca ativa. Abra um flat pattern primeiro.");
                    return;
                }

                _settingsRepository = new PluginSettingsRepository();
                _settingsStore = _settingsRepository.Load();
                if (!PromptForSettings())
                    return;

                PrepareFlatPatternDisplay(includePluginDimensions: false);

                var previewResult = RunDimensioningEngine(previewMode: true);
                ShowPreviewResult(previewResult);

                int confirm = _ui.NXMessageBox.Show("Cotagem Flat Pattern", NXMessageBox.DialogType.Question,
                    "Criar as cotas mostradas no preview?");

                if (confirm == 1)
                    ExecuteDimensioning();
            }
            catch (Exception ex)
            {
                if (_ui != null)
                    _ui.NXMessageBox.Show("Erro", NXMessageBox.DialogType.Information, $"Erro: {ex.Message}");
                else
                    Console.WriteLine($"Erro: {ex.Message}");
            }
        }

        private bool PromptForSettings()
        {
            using (SettingsForm form = new SettingsForm(_settingsStore))
            {
                DialogResult result = form.ShowDialog();
                if (result != DialogResult.OK || form.SelectedSettings == null)
                    return false;

                _settings = form.SelectedSettings.Clone();
                _settingsStore.SelectedPresetName = form.SelectedPresetName;
                _settingsRepository.Save(_settingsStore);
                return true;
            }
        }

        private DimensioningResult RunDimensioningEngine(bool previewMode)
        {
            var result = new DimensioningResult();

            BendDetector detector = new BendDetector(_session, _workPart, _settings);
            List<BendLineInfo> bends = detector.Detect();
            result.TotalBends = bends.Count;

            if (bends.Count == 0)
            {
                result.Warnings.Add("Nenhuma linha de dobra detectada.");
                return result;
            }

            Body flatBody = detector.GetFlatSolidBody();
            PrepareFlatPatternDisplay(flatBody, includePluginDimensions: !previewMode);

            ContourAnalyzer analyzer = new ContourAnalyzer(_session, _workPart);
            List<ExternalEdge> contour = analyzer.GetOuterContour(flatBody);

            if (contour.Count == 0)
            {
                result.Warnings.Add("Nenhum contorno externo encontrado.");
                return result;
            }

            ShowAndLogReferenceGeometry(contour, bends);

            EdgeSelector selector = new EdgeSelector(_settings.MinContactRatio);
            List<BendEdgePair> pairs = selector.SelectPairs(bends, contour);
            pairs = RemoveDuplicatePairs(pairs, _settings.Tolerance);
            result.Pairs = pairs;

            PlacementEngine placement = new PlacementEngine(_settings.Margin, _settings.Spacing);
            pairs = placement.CalculateOffsets(pairs, contour);

            if (!previewMode)
            {
                DimensionCreator creator = new DimensionCreator(_session, _workPart);
                creator.ClearExistingDimensions();
                var dimensions = creator.CreateDimensions(pairs);
                result.DimensionsCreated = dimensions.Count;

                foreach (var dim in dimensions)
                    creator.MarkDimension(dim);

                PrepareFlatPatternDisplay(flatBody, includePluginDimensions: true);
            }

            return result;
        }

        private void ShowAndLogReferenceGeometry(List<ExternalEdge> contour, List<BendLineInfo> bends)
        {
            ListingWindow lw = _session.ListingWindow;
            lw.Open();
            lw.WriteLine("=== Linhas externas selecionadas ===");
            foreach (ExternalEdge edge in contour.OrderBy(e => GetOrientationKey(e.Direction)).ThenBy(e => e.MidPoint.Y).ThenBy(e => e.MidPoint.X))
            {
                lw.WriteLine(string.Format(
                    CultureInfo.InvariantCulture,
                    "EXT id={0} len={1:F1} start=({2:F1},{3:F1}) end=({4:F1},{5:F1}) mid=({6:F1},{7:F1}) layer={8} color={9} font={10}",
                    edge.Edge?.JournalIdentifier ?? string.Empty,
                    edge.Length,
                    edge.StartPoint.X,
                    edge.StartPoint.Y,
                    edge.EndPoint.X,
                    edge.EndPoint.Y,
                    edge.MidPoint.X,
                    edge.MidPoint.Y,
                    edge.Edge?.Layer ?? 0,
                    edge.Edge?.Color ?? 0,
                    edge.Edge != null ? edge.Edge.LineFont.ToString() : string.Empty));
            }

            lw.WriteLine("=== Linhas de dobra selecionadas ===");
            foreach (BendLineInfo bend in bends.OrderBy(b => GetOrientationKey(b.Direction)).ThenBy(b => b.MidPoint.Y).ThenBy(b => b.MidPoint.X))
            {
                lw.WriteLine(string.Format(
                    CultureInfo.InvariantCulture,
                    "BEND id={0} len={1:F1} mid=({2:F1},{3:F1}) dir=({4:F3},{5:F3}) layer={6} color={7} font={8}",
                    bend.Edge?.JournalIdentifier ?? string.Empty,
                    bend.Length,
                    bend.MidPoint.X,
                    bend.MidPoint.Y,
                    bend.Direction.X,
                    bend.Direction.Y,
                    bend.Edge?.Layer ?? 0,
                    bend.Edge?.Color ?? 0,
                    bend.Edge != null ? bend.Edge.LineFont.ToString() : string.Empty));
            }

            ApplyDebugColor(contour.Select(e => (DisplayableObject)e.Edge).Where(e => e != null).ToArray(), ExternalEdgeColor);
            ApplyDebugColor(bends.Select(b => (DisplayableObject)b.Edge).Where(e => e != null).ToArray(), BendEdgeColor);
        }

        private void ApplyDebugColor(DisplayableObject[] objects, int color)
        {
            if (objects == null || objects.Length == 0)
                return;

            using (DisplayModification modification = _session.DisplayManager.NewDisplayModification())
            {
                modification.ApplyToAllFaces = false;
                modification.ApplyToOwningParts = false;
                modification.NewColor = color;
                modification.Apply(objects);
            }

            foreach (DisplayableObject obj in objects)
                obj.RedisplayObject();
        }

        private static string GetOrientationKey(Vector3d direction)
        {
            return Math.Abs(direction.X) >= Math.Abs(direction.Y) ? "H" : "V";
        }

        private void PrepareFlatPatternDisplay(bool includePluginDimensions)
        {
            BendDetector detector = new BendDetector(_session, _workPart, _settings);
            Body flatBody = detector.GetFlatSolidBody();
            PrepareFlatPatternDisplay(flatBody, includePluginDimensions);
        }

        private void PrepareFlatPatternDisplay(Body flatBody, bool includePluginDimensions)
        {
            if (flatBody == null)
                return;

            foreach (Body body in _workPart.Bodies)
            {
                if (ReferenceEquals(body, flatBody) || body.Tag == flatBody.Tag)
                    body.Unblank();
                else
                    body.Blank();
            }

            foreach (NXOpen.Annotations.Dimension dim in _workPart.Dimensions)
            {
                if (includePluginDimensions && IsPluginDimension(dim))
                    dim.Unblank();
                else
                    dim.Blank();
            }

            foreach (Point point in _workPart.Points)
                point.Blank();

            NXOpen.View workView = _workPart.Layouts.Current.WorkView;
            workView.MakeWork();
            workView.ChangePerspective(false);
            workView.WcsVisibility = false;
            workView.TriadVisibility = false;
            workView.Orient(CreateTopViewMatrix());
            workView.FitToObjects(new NXOpen.IFitTo[] { flatBody });
            workView.Regenerate();
            workView.UpdateDisplay();
        }

        private static Matrix3x3 CreateTopViewMatrix()
        {
            return new Matrix3x3
            {
                Xx = 1.0,
                Xy = 0.0,
                Xz = 0.0,
                Yx = 0.0,
                Yy = 1.0,
                Yz = 0.0,
                Zx = 0.0,
                Zy = 0.0,
                Zz = 1.0
            };
        }

        private static bool IsPluginDimension(NXOpen.Annotations.Dimension dim)
        {
            try
            {
                string value = dim.GetUserAttributeAsString("CotagemFlatPattern", NXOpen.NXObject.AttributeType.String, 0);
                return value == "true";
            }
            catch
            {
                return false;
            }
        }

        private List<BendEdgePair> RemoveDuplicatePairs(List<BendEdgePair> pairs, double tolerance)
        {
            double effectiveTolerance = Math.Max(tolerance, 0.5);
            var validGroups = new Dictionary<string, GroupedPair>(StringComparer.Ordinal);
            var invalidPairs = new List<BendEdgePair>();

            for (int i = 0; i < pairs.Count; i++)
            {
                BendEdgePair pair = pairs[i];
                if (pair == null)
                    continue;

                if (!pair.IsValid)
                {
                    invalidPairs.Add(pair);
                    continue;
                }

                string groupKey = BuildDuplicateGroupKey(pair, effectiveTolerance);
                if (!validGroups.TryGetValue(groupKey, out GroupedPair existing))
                {
                    validGroups[groupKey] = new GroupedPair(i, pair);
                    continue;
                }

                if (IsBetterPair(pair, existing.Pair, effectiveTolerance))
                    validGroups[groupKey] = new GroupedPair(existing.FirstIndex, pair);
            }

            List<BendEdgePair> uniqueValidPairs = validGroups.Values
                .OrderBy(g => g.FirstIndex)
                .Select(g => g.Pair)
                .ToList();

            invalidPairs.AddRange(uniqueValidPairs);
            return invalidPairs;
        }

        private static string BuildDuplicateGroupKey(BendEdgePair pair, double tolerance)
        {
            string orientation = GetDimensionOrientation(pair.Bend.Direction);
            string side = GetExternalSideKey(pair);
            double roundedDistance = RoundByTolerance(pair.Distance, tolerance);
            double roundedBendPosition = RoundByTolerance(GetBendPosition(pair.Bend), tolerance);

            return string.Format(
                CultureInfo.InvariantCulture,
                "{0}|{1}|{2:F3}|{3:F3}|{4}",
                orientation,
                side,
                roundedDistance,
                roundedBendPosition,
                pair.ReferenceJournalIdentifier ?? string.Empty);
        }

        private static double GetBendPosition(BendLineInfo bend)
        {
            return Math.Abs(bend.Direction.X) >= Math.Abs(bend.Direction.Y)
                ? bend.MidPoint.Y
                : bend.MidPoint.X;
        }

        private static string GetExternalSideKey(BendEdgePair pair)
        {
            if (Math.Abs(pair.Bend.Direction.X) >= Math.Abs(pair.Bend.Direction.Y))
                return pair.TargetEdge.MidPoint.Y >= pair.Bend.MidPoint.Y ? "TOP" : "BOTTOM";

            return pair.TargetEdge.MidPoint.X >= pair.Bend.MidPoint.X ? "RIGHT" : "LEFT";
        }

        private static bool IsBetterPair(BendEdgePair candidate, BendEdgePair current, double tolerance)
        {
            double contactDelta = candidate.ContactRatio - current.ContactRatio;
            if (Math.Abs(contactDelta) > 0.01)
                return contactDelta > 0.0;

            double lengthDelta = candidate.Bend.Length - current.Bend.Length;
            if (Math.Abs(lengthDelta) > tolerance)
                return lengthDelta > 0.0;

            double distanceDelta = current.Distance - candidate.Distance;
            if (Math.Abs(distanceDelta) > tolerance)
                return distanceDelta > 0.0;

            string candidateBendKey = candidate.Bend?.Edge?.JournalIdentifier ?? string.Empty;
            string currentBendKey = current.Bend?.Edge?.JournalIdentifier ?? string.Empty;
            return string.CompareOrdinal(candidateBendKey, currentBendKey) < 0;
        }

        private static string GetDimensionOrientation(Vector3d bendDirection)
        {
            return Math.Abs(bendDirection.X) >= Math.Abs(bendDirection.Y) ? "V" : "H";
        }

        private static double RoundByTolerance(double value, double tolerance)
        {
            return Math.Round(value / tolerance, MidpointRounding.AwayFromZero) * tolerance;
        }

        private void ExecuteDimensioning()
        {
            try
            {
                var result = RunDimensioningEngine(previewMode: false);
                _lastResult = result;

                string msg = $"Cotagem concluida!\n" +
                    $"Dobras detectadas: {result.TotalBends}\n" +
                    $"Cotas criadas: {result.DimensionsCreated}\n";

                if (result.Warnings.Count > 0)
                    msg += "\nAvisos:\n" + string.Join("\n", result.Warnings);

                _ui.NXMessageBox.Show("Cotagem Flat Pattern", NXMessageBox.DialogType.Information, msg);
            }
            catch (Exception ex)
            {
                _ui.NXMessageBox.Show("Erro", NXMessageBox.DialogType.Information, $"Erro na execucao: {ex.Message}");
            }
        }

        private void ShowPreviewResult(DimensioningResult result)
        {
            const int maxPreviewLines = 40;

            string msg = $"Preview da cotagem:\n" +
                $"Dobras detectadas: {result.TotalBends}\n" +
                $"Pares processados: {result.Pairs.Count}\n\n";

            int validPairs = 0;
            int shownPairs = 0;
            foreach (var pair in result.Pairs)
            {
                if (!pair.IsValid)
                    continue;

                validPairs++;
                if (shownPairs >= maxPreviewLines)
                    continue;

                shownPairs++;
                msg += $"* {pair.Bend}: dist={pair.Distance:F1}mm contato={pair.ContactRatio * 100:F0}%\n";
            }

            if (validPairs > shownPairs)
                msg += $"... e mais {validPairs - shownPairs} cotas validas no preview\n";

            msg += $"\nCotas a gerar: {validPairs}";

            if (result.Warnings.Count > 0)
                msg += "\n\nAvisos:\n" + string.Join("\n", result.Warnings);

            _ui.NXMessageBox.Show("Preview", NXMessageBox.DialogType.Information, msg);
        }

        private sealed class GroupedPair
        {
            public GroupedPair(int firstIndex, BendEdgePair pair)
            {
                FirstIndex = firstIndex;
                Pair = pair;
            }

            public int FirstIndex { get; }

            public BendEdgePair Pair { get; }
        }
    }
}
