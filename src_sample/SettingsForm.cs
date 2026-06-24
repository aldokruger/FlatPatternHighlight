using System;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace CotagemFlatPattern
{
    public sealed class SettingsForm : Form
    {
        private readonly PluginSettingsStore _store;

        private ComboBox _presetCombo;
        private TextBox _presetNameTextBox;
        private TextBox _marginTextBox;
        private TextBox _spacingTextBox;
        private TextBox _contactTextBox;
        private TextBox _toleranceTextBox;
        private NumericUpDown _precisionUpDown;
        private ComboBox _detectionModeCombo;
        private TextBox _layersTextBox;
        private TextBox _lineFontsTextBox;
        private CheckBox _logCheckBox;
        private Button _saveButton;
        private Button _newButton;
        private Button _deleteButton;
        private Button _okButton;
        private Button _cancelButton;

        public PluginSettings SelectedSettings { get; private set; }
        public string SelectedPresetName { get; private set; }

        public SettingsForm(PluginSettingsStore store)
        {
            _store = store ?? PluginSettingsStore.CreateDefault();
            _store.Normalize();

            InitializeComponents();
            LoadPresets();
        }

        private void InitializeComponents()
        {
            Text = "Cotagem Flat Pattern - Configuracao";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new System.Drawing.Size(620, 430);

            Label presetLabel = CreateLabel("Padrao:", 16, 18);
            _presetCombo = new ComboBox
            {
                Left = 140,
                Top = 14,
                Width = 260,
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            _presetCombo.SelectedIndexChanged += (_, __) => LoadSelectedPresetIntoFields();

            _newButton = CreateButton("Novo", 410, 13, 90, (_, __) => CreateNewPreset());
            _deleteButton = CreateButton("Excluir", 510, 13, 90, (_, __) => DeletePreset());

            Label presetNameLabel = CreateLabel("Nome:", 16, 54);
            _presetNameTextBox = new TextBox { Left = 140, Top = 50, Width = 260 };
            _saveButton = CreateButton("Salvar", 410, 49, 190, (_, __) => SaveCurrentPreset());

            _marginTextBox = CreateValueTextBox(140, 92, 120);
            _spacingTextBox = CreateValueTextBox(140, 128, 120);
            _contactTextBox = CreateValueTextBox(140, 164, 120);
            _toleranceTextBox = CreateValueTextBox(140, 200, 120);
            _precisionUpDown = new NumericUpDown
            {
                Left = 140,
                Top = 236,
                Width = 80,
                Minimum = 0,
                Maximum = 4
            };

            _detectionModeCombo = new ComboBox
            {
                Left = 140,
                Top = 272,
                Width = 180,
                DropDownStyle = ComboBoxStyle.DropDownList,
                DataSource = Enum.GetValues(typeof(BendDetectionMode))
            };

            _layersTextBox = CreateValueTextBox(140, 308, 220);
            _lineFontsTextBox = CreateValueTextBox(140, 344, 220);
            _logCheckBox = new CheckBox
            {
                Left = 140,
                Top = 380,
                Width = 260,
                Text = "Gerar log detalhado das linhas no Listing Window"
            };

            Controls.AddRange(new Control[]
            {
                presetLabel,
                _presetCombo,
                _newButton,
                _deleteButton,
                presetNameLabel,
                _presetNameTextBox,
                _saveButton,
                CreateLabel("Margem (mm):", 16, 96),
                _marginTextBox,
                CreateLabel("Espacamento (mm):", 16, 132),
                _spacingTextBox,
                CreateLabel("Contato minimo (%):", 16, 168),
                _contactTextBox,
                CreateLabel("Tolerancia (mm):", 16, 204),
                _toleranceTextBox,
                CreateLabel("Precisao:", 16, 240),
                _precisionUpDown,
                CreateLabel("Modo de deteccao:", 16, 276),
                _detectionModeCombo,
                CreateLabel("Layers de dobra:", 16, 312),
                _layersTextBox,
                CreateLabel("Tipos de linha:", 16, 348),
                _lineFontsTextBox,
                CreateHintLabel("Ex.: Centerline,Dashed,Phantom ou 4,2,3", 370, 348, 230),
                _logCheckBox
            });

            _okButton = CreateButton("OK", 444, 386, 75, (_, __) => ConfirmSelection());
            _cancelButton = CreateButton("Cancelar", 525, 386, 75, (_, __) =>
            {
                DialogResult = DialogResult.Cancel;
                Close();
            });

            AcceptButton = _okButton;
            CancelButton = _cancelButton;
            Controls.Add(_okButton);
            Controls.Add(_cancelButton);
        }

        private void LoadPresets()
        {
            _presetCombo.BeginUpdate();
            _presetCombo.Items.Clear();
            foreach (PluginSettingsPreset preset in _store.Presets)
                _presetCombo.Items.Add(preset);
            _presetCombo.EndUpdate();

            int selectedIndex = _store.Presets.FindIndex(p =>
                string.Equals(p.Name, _store.SelectedPresetName, StringComparison.OrdinalIgnoreCase));

            _presetCombo.SelectedIndex = selectedIndex >= 0 ? selectedIndex : 0;
        }

        private void LoadSelectedPresetIntoFields()
        {
            if (!(_presetCombo.SelectedItem is PluginSettingsPreset preset))
                return;

            _presetNameTextBox.Text = preset.Name;
            _marginTextBox.Text = FormatDouble(preset.Settings.Margin);
            _spacingTextBox.Text = FormatDouble(preset.Settings.Spacing);
            _contactTextBox.Text = FormatDouble(preset.Settings.MinContactRatio * 100.0);
            _toleranceTextBox.Text = FormatDouble(preset.Settings.Tolerance);
            _detectionModeCombo.SelectedItem = preset.Settings.DetectionMode;
            _layersTextBox.Text = preset.Settings.BendLayersCsv ?? string.Empty;
            _lineFontsTextBox.Text = preset.Settings.BendLineFontsCsv ?? string.Empty;
            _logCheckBox.Checked = preset.Settings.LogLineDiagnostics;

            decimal precision = preset.Settings.Precision;
            if (precision < _precisionUpDown.Minimum)
                precision = _precisionUpDown.Minimum;
            if (precision > _precisionUpDown.Maximum)
                precision = _precisionUpDown.Maximum;
            _precisionUpDown.Value = precision;
        }

        private void CreateNewPreset()
        {
            PluginSettings settings = ReadSettingsFromFields(showErrors: false) ?? new PluginSettings();
            string baseName = "Novo padrao";
            string name = baseName;
            int suffix = 2;

            while (_store.Presets.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                name = $"{baseName} {suffix}";
                suffix++;
            }

            PluginSettingsPreset preset = new PluginSettingsPreset
            {
                Name = name,
                Settings = settings.Clone()
            };

            _store.Presets.Add(preset);
            _store.SelectedPresetName = preset.Name;
            _store.Normalize();
            LoadPresets();
        }

        private void DeletePreset()
        {
            if (!(_presetCombo.SelectedItem is PluginSettingsPreset preset))
                return;

            if (_store.Presets.Count == 1)
            {
                MessageBox.Show(this, "Deve existir pelo menos um padrao cadastrado.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            _store.Presets.RemoveAll(p => string.Equals(p.Name, preset.Name, StringComparison.OrdinalIgnoreCase));
            _store.Normalize();
            LoadPresets();
        }

        private void SaveCurrentPreset()
        {
            if (!(_presetCombo.SelectedItem is PluginSettingsPreset selectedPreset))
                return;

            PluginSettings settings = ReadSettingsFromFields(showErrors: true);
            if (settings == null)
                return;

            string newName = (_presetNameTextBox.Text ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(newName))
            {
                MessageBox.Show(this, "Informe um nome para o padrao.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (_store.Presets.Any(p =>
                !string.Equals(p.Name, selectedPreset.Name, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(p.Name, newName, StringComparison.OrdinalIgnoreCase)))
            {
                MessageBox.Show(this, "Ja existe um padrao com esse nome.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            selectedPreset.Name = newName;
            selectedPreset.Settings = settings;
            _store.SelectedPresetName = selectedPreset.Name;
            _store.Normalize();
            LoadPresets();
        }

        private void ConfirmSelection()
        {
            if (!(_presetCombo.SelectedItem is PluginSettingsPreset selectedPreset))
                return;

            PluginSettings settings = ReadSettingsFromFields(showErrors: true);
            if (settings == null)
                return;

            string presetName = (_presetNameTextBox.Text ?? selectedPreset.Name).Trim();
            if (string.IsNullOrWhiteSpace(presetName))
            {
                MessageBox.Show(this, "Informe um nome para o padrao.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            selectedPreset.Name = presetName;
            selectedPreset.Settings = settings;
            _store.SelectedPresetName = selectedPreset.Name;
            _store.Normalize();

            SelectedSettings = settings.Clone();
            SelectedPresetName = selectedPreset.Name;

            DialogResult = DialogResult.OK;
            Close();
        }

        private PluginSettings ReadSettingsFromFields(bool showErrors)
        {
            if (!TryParseDouble(_marginTextBox.Text, out double margin) || margin < 0.0)
                return Fail("Margem invalida.", showErrors);

            if (!TryParseDouble(_spacingTextBox.Text, out double spacing) || spacing < 0.0)
                return Fail("Espacamento invalido.", showErrors);

            if (!TryParseDouble(_contactTextBox.Text, out double contactPercent) || contactPercent < 0.0 || contactPercent > 100.0)
                return Fail("Contato minimo invalido. Use um valor entre 0 e 100.", showErrors);

            if (!TryParseDouble(_toleranceTextBox.Text, out double tolerance) || tolerance <= 0.0)
                return Fail("Tolerancia invalida. Use um valor maior que zero.", showErrors);

            return new PluginSettings
            {
                Margin = margin,
                Spacing = spacing,
                MinContactRatio = contactPercent / 100.0,
                Tolerance = tolerance,
                Precision = Decimal.ToInt32(_precisionUpDown.Value),
                DetectionMode = (BendDetectionMode)_detectionModeCombo.SelectedItem,
                BendLayersCsv = (_layersTextBox.Text ?? string.Empty).Trim(),
                BendLineFontsCsv = (_lineFontsTextBox.Text ?? string.Empty).Trim(),
                LogLineDiagnostics = _logCheckBox.Checked
            };
        }

        private PluginSettings Fail(string message, bool showErrors)
        {
            if (showErrors)
                MessageBox.Show(this, message, Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return null;
        }

        private static bool TryParseDouble(string text, out double value)
        {
            return double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) ||
                   double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        private static string FormatDouble(double value)
        {
            return value.ToString("0.###", CultureInfo.CurrentCulture);
        }

        private static Label CreateLabel(string text, int left, int top)
        {
            return new Label
            {
                Left = left,
                Top = top,
                Width = 120,
                Text = text
            };
        }

        private static Label CreateHintLabel(string text, int left, int top, int width)
        {
            return new Label
            {
                Left = left,
                Top = top + 3,
                Width = width,
                Text = text
            };
        }

        private static TextBox CreateValueTextBox(int left, int top, int width)
        {
            return new TextBox
            {
                Left = left,
                Top = top,
                Width = width
            };
        }

        private static Button CreateButton(string text, int left, int top, int width, EventHandler onClick)
        {
            Button button = new Button
            {
                Left = left,
                Top = top,
                Width = width,
                Text = text
            };
            button.Click += onClick;
            return button;
        }
    }
}
