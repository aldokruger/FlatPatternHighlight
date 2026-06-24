using NXOpen;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Serialization;

namespace CotagemFlatPattern
{
    public enum BendDirection
    {
        Up,
        Down
    }

    public enum BendDetectionMode
    {
        Auto,
        ByLayer,
        ByLineType,
        Hybrid,
        Geometry
    }

    public class BendLineInfo
    {
        public Edge Edge { get; set; }
        public Point3d MidPoint { get; set; }
        public Vector3d Direction { get; set; }
        public double Length { get; set; }
        public double Angle { get; set; }
        public BendDirection BendDir { get; set; }
        public double InnerRadius { get; set; }

        public override string ToString()
        {
            return $"Bend: len={Length:F1}mm angle={Angle:F1} deg dir={BendDir}";
        }
    }

    public class ExternalEdge
    {
        public Edge Edge { get; set; }
        public Point3d StartPoint { get; set; }
        public Point3d EndPoint { get; set; }
        public Point3d MidPoint { get; set; }
        public Vector3d Direction { get; set; }
        public double Length { get; set; }

        public override string ToString()
        {
            return $"Edge: len={Length:F1}mm";
        }
    }

    public class BendEdgePair
    {
        public BendLineInfo Bend { get; set; }
        public ExternalEdge TargetEdge { get; set; }
        public double Distance { get; set; }
        public double ContactRatio { get; set; }
        public int OffsetIndex { get; set; }
        public double Offset { get; set; }
        public string ReferenceType { get; set; } = string.Empty;
        public string ReferenceJournalIdentifier { get; set; } = string.Empty;
        public bool IsValid => TargetEdge != null && Distance > 0;
    }

    public class PluginSettings
    {
        public double MinContactRatio { get; set; } = 0.30;
        public double Margin { get; set; } = 8.0;
        public double Spacing { get; set; } = 10.0;
        public double Tolerance { get; set; } = 1.0;
        public int Precision { get; set; } = 1;
        public BendDetectionMode DetectionMode { get; set; } = BendDetectionMode.Auto;
        public string BendLayersCsv { get; set; } = "251,252";
        public string BendLineFontsCsv { get; set; } = "Centerline,Dashed,Phantom";
        public bool LogLineDiagnostics { get; set; } = true;

        public PluginSettings Clone()
        {
            return new PluginSettings
            {
                MinContactRatio = MinContactRatio,
                Margin = Margin,
                Spacing = Spacing,
                Tolerance = Tolerance,
                Precision = Precision,
                DetectionMode = DetectionMode,
                BendLayersCsv = BendLayersCsv,
                BendLineFontsCsv = BendLineFontsCsv,
                LogLineDiagnostics = LogLineDiagnostics
            };
        }
    }

    public class PluginSettingsPreset
    {
        public string Name { get; set; } = "Padrao";
        public PluginSettings Settings { get; set; } = new PluginSettings();

        public override string ToString()
        {
            return Name;
        }
    }

    [XmlRoot("CotagemFlatPatternConfig")]
    public class PluginSettingsStore
    {
        public string SelectedPresetName { get; set; } = "Padrao";

        [XmlArray("Presets")]
        [XmlArrayItem("Preset")]
        public List<PluginSettingsPreset> Presets { get; set; } = new List<PluginSettingsPreset>();

        public static PluginSettingsStore CreateDefault()
        {
            return new PluginSettingsStore
            {
                SelectedPresetName = "Padrao",
                Presets = new List<PluginSettingsPreset>
                {
                    new PluginSettingsPreset
                    {
                        Name = "Padrao",
                        Settings = new PluginSettings()
                    }
                }
            };
        }

        public void Normalize()
        {
            if (Presets == null)
                Presets = new List<PluginSettingsPreset>();

            Presets = Presets
                .Where(p => p != null && !string.IsNullOrWhiteSpace(p.Name))
                .GroupBy(p => p.Name.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(g =>
                {
                    PluginSettingsPreset preset = g.First();
                    preset.Name = preset.Name.Trim();
                    if (preset.Settings == null)
                        preset.Settings = new PluginSettings();
                    return preset;
                })
                .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            if (Presets.Count == 0)
                Presets.Add(new PluginSettingsPreset { Name = "Padrao", Settings = new PluginSettings() });

            if (string.IsNullOrWhiteSpace(SelectedPresetName) ||
                Presets.All(p => !string.Equals(p.Name, SelectedPresetName, StringComparison.OrdinalIgnoreCase)))
            {
                SelectedPresetName = Presets[0].Name;
            }
        }
    }

    public sealed class PluginSettingsRepository
    {
        private static readonly XmlSerializer Serializer = new XmlSerializer(typeof(PluginSettingsStore));

        public string ConfigPath { get; }

        public PluginSettingsRepository()
        {
            string baseDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CotagemFlatPattern");

            Directory.CreateDirectory(baseDirectory);
            ConfigPath = Path.Combine(baseDirectory, "settings.xml");
        }

        public PluginSettingsStore Load()
        {
            if (!File.Exists(ConfigPath))
            {
                PluginSettingsStore defaultStore = PluginSettingsStore.CreateDefault();
                Save(defaultStore);
                return defaultStore;
            }

            try
            {
                using (FileStream stream = File.OpenRead(ConfigPath))
                {
                    PluginSettingsStore store = (PluginSettingsStore)Serializer.Deserialize(stream);
                    if (store == null)
                        store = PluginSettingsStore.CreateDefault();
                    store.Normalize();
                    return store;
                }
            }
            catch
            {
                PluginSettingsStore fallback = PluginSettingsStore.CreateDefault();
                Save(fallback);
                return fallback;
            }
        }

        public void Save(PluginSettingsStore store)
        {
            if (store == null)
                store = PluginSettingsStore.CreateDefault();

            store.Normalize();

            using (FileStream stream = File.Create(ConfigPath))
            {
                Serializer.Serialize(stream, store);
            }
        }
    }

    public class DimensioningResult
    {
        public int TotalBends { get; set; }
        public int DimensionsCreated { get; set; }
        public List<BendEdgePair> Pairs { get; set; } = new List<BendEdgePair>();
        public List<string> Warnings { get; set; } = new List<string>();
        public bool Success => Warnings.Count == 0 || DimensionsCreated > 0;
    }
}
