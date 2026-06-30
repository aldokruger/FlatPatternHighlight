using System;
using System.Drawing;
using System.Windows.Forms;

namespace FlatPatternHighlight
{
    /// <summary>
    /// Janela Windows Forms para editar as configurações do FlatPatternHighlight
    /// sem precisar editar o settings.json manualmente.
    /// 
    /// Para abrir: Ctrl+U → selecionar FlatPatternHighlight.ShowSettings
    /// </summary>
    internal sealed class ConfigDialog : Form
    {
        private readonly Settings _original;
        private readonly Settings _edit;

        // Controles
        // Grupo: Boundary Detection
        private NumericUpDown nudParallelismThreshold;
        private NumericUpDown nudDiagonalBendThreshold;
        private NumericUpDown nudArtefactSkipDistanceSq;
        private NumericUpDown nudCutoutSkipRatio;
        private NumericUpDown nudSmallEdgeRatio;
        private NumericUpDown nudSmallEdgeGuardFactor;

        // Grupo: Lanes & Chains
        private NumericUpDown nudLaneLengthRatioThreshold;
        private NumericUpDown nudMaxChainGap;

        // Grupo: PMI Dimensions
        private NumericUpDown nudDimensionDecimalPlaces;

        private Button btnOk;
        private Button btnCancel;
        private Button btnReset;

        private ConfigDialog(Settings settings)
        {
            _original = settings;
            _edit = CloneSettings(settings);

            InitializeComponent();
            LoadSettings();
        }

        /// <summary>Exibe a janela de configuração (modal). Retorna true se o usuário confirmou.</summary>
        internal static bool ShowDialog(Settings settings)
        {
            using (var dlg = new ConfigDialog(settings))
            {
                if (dlg.ShowDialog() == DialogResult.OK)
                {
                    // Copia valores editados de volta
                    CopySettings(dlg._edit, settings);
                    return true;
                }
                return false;
            }
        }

        // =====================================================================
        // Constrói o formulário manualmente (sem designer)
        // =====================================================================
        private void InitializeComponent()
        {
            Text = "FlatPatternHighlight — Configurações";
            Size = new Size(520, 520);
            MinimumSize = new Size(480, 460);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9F);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowIcon = false;
            ShowInTaskbar = false;

            var tabControl = new TabControl
            {
                Dock = DockStyle.Fill,
                Padding = new Point(12, 8)
            };

            // ---- Tab 1: Detectação de Boundary ----
            tabControl.TabPages.Add(CreateBoundaryTab());

            // ---- Tab 2: Lanes & Chains ----
            tabControl.TabPages.Add(CreateLanesTab());

            // ---- Tab 3: PMI Dimensions ----
            tabControl.TabPages.Add(CreatePmiTab());

            // ---- Bottom buttons ----
            var panelBottom = new Panel { Height = 48, Dock = DockStyle.Bottom };
            btnReset = new Button
            {
                Text = "Restaurar Padrões",
                Size = new Size(140, 30),
                Location = new Point(12, 10),
                UseVisualStyleBackColor = true
            };
            btnReset.Click += (s, e) => ResetToDefaults();

            btnCancel = new Button
            {
                Text = "Cancelar",
                Size = new Size(90, 30),
                Location = new Point(panelBottom.Width - 204, 10),
                Anchor = AnchorStyles.Right | AnchorStyles.Top,
                UseVisualStyleBackColor = true
            };
            btnCancel.Click += (s, e) => DialogResult = DialogResult.Cancel;

            btnOk = new Button
            {
                Text = "OK",
                Size = new Size(90, 30),
                Location = new Point(panelBottom.Width - 104, 10),
                Anchor = AnchorStyles.Right | AnchorStyles.Top,
                UseVisualStyleBackColor = true
            };
            btnOk.Click += (s, e) => DialogResult = DialogResult.OK;
            AcceptButton = btnOk;

            panelBottom.Controls.AddRange(new Control[] { btnReset, btnCancel, btnOk });

            Controls.Add(tabControl);
            Controls.Add(panelBottom);
        }

        // =====================================================================
        // Abas
        // =====================================================================

        private TabPage CreateBoundaryTab()
        {
            var page = new TabPage("Boundary Detection") { UseVisualStyleBackColor = true };
            var tbl = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(12),
                ColumnCount = 2,
                RowCount = 7,
                AutoSize = true
            };
            tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 200));
            tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            AddRow(tbl, 0, "ParallelismThreshold", "Produto escalar mínimo para direções paralelas",
                   out nudParallelismThreshold, 0.8m, 1.0m, 0.05m, 4);
            AddRow(tbl, 1, "DiagonalBendThreshold", "Componente min. |dir.X|/|dir.Y| para dobra diagonal",
                   out nudDiagonalBendThreshold, 0.05m, 0.5m, 0.05m, 4);
            AddRow(tbl, 2, "ArtefactSkipDistanceSq", "Distância² (mm²) para ignorar dobra no perímetro",
                   out nudArtefactSkipDistanceSq, 0.0m, 10.0m, 0.05m, 4);
            AddRow(tbl, 3, "CutoutSkipRatio", "Proporção bestDist/secondBestDist para ignorar entalhe",
                   out nudCutoutSkipRatio, 0.0m, 1.0m, 0.05m, 4);
            AddRow(tbl, 4, "SmallEdgeRatio", "Proporção para correção de segmento curto",
                   out nudSmallEdgeRatio, 0.0m, 1.0m, 0.05m, 4);
            AddRow(tbl, 5, "SmallEdgeGuardFactor", "Fator de proteção para substituição SmallEdge",
                   out nudSmallEdgeGuardFactor, 0.0m, 1.0m, 0.05m, 4);

            // Espaçador na última linha
            tbl.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            tbl.Controls.Add(new Label(), 0, 6);

            page.Controls.Add(tbl);
            return page;
        }

        private TabPage CreateLanesTab()
        {
            var page = new TabPage("Lanes & Chains") { UseVisualStyleBackColor = true };
            var tbl = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(12),
                ColumnCount = 2,
                RowCount = 3,
                AutoSize = true
            };
            tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 200));
            tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            AddRow(tbl, 0, "LaneLengthRatioThreshold",
                   "Proporção min. de comprimento para dobras na mesma lane (0.7)",
                   out nudLaneLengthRatioThreshold, 0.1m, 1.0m, 0.05m, 4);
            AddRow(tbl, 1, "MaxChainGap",
                   "Distância máxima (mm) para agrupar dobras de comprimentos diferentes",
                   out nudMaxChainGap, 0m, 500m, 5m, 1);

            tbl.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            tbl.Controls.Add(new Label(), 0, 2);

            page.Controls.Add(tbl);
            return page;
        }

        private TabPage CreatePmiTab()
        {
            var page = new TabPage("PMI Dimensions") { UseVisualStyleBackColor = true };
            var tbl = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(12),
                ColumnCount = 2,
                RowCount = 2,
                AutoSize = true
            };
            tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 200));
            tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            AddRow(tbl, 0, "DimensionDecimalPlaces",
                   "Casas decimais nas cotas PMI (0=inteiro, 1=1 casa, 2=2 casas...)",
                   out nudDimensionDecimalPlaces, 0m, 6m, 1m, 0);

            tbl.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            tbl.Controls.Add(new Label(), 0, 1);

            page.Controls.Add(tbl);
            return page;
        }

        // =====================================================================
        // Helpers
        // =====================================================================

        /// <summary>Adiciona uma linha label + numeric no TableLayoutPanel.</summary>
        private void AddRow(TableLayoutPanel tbl, int row, string name, string tooltip,
                            out NumericUpDown nud, decimal min, decimal max,
                            decimal increment, int decimals)
        {
            var lbl = new Label
            {
                Text = name,
                TextAlign = ContentAlignment.MiddleLeft,
                Dock = DockStyle.Fill,
                Padding = new Padding(0, 4, 6, 4),
            };
            // Tooltip com a descrição do campo
            var tip = new ToolTip();
            tip.SetToolTip(lbl, tooltip);
            tbl.Controls.Add(lbl, 0, row);

            nud = new NumericUpDown
            {
                Minimum = min,
                Maximum = max,
                Increment = increment,
                DecimalPlaces = decimals,
                Width = 120,
                Dock = DockStyle.Left,
                TextAlign = HorizontalAlignment.Right
            };
            tip.SetToolTip(nud, tooltip);
            tbl.Controls.Add(nud, 1, row);
        }

        private void LoadSettings()
        {
            nudParallelismThreshold.Value = (decimal)_edit.ParallelismThreshold;
            nudDiagonalBendThreshold.Value = (decimal)_edit.DiagonalBendThreshold;
            nudArtefactSkipDistanceSq.Value = (decimal)_edit.ArtefactSkipDistanceSq;
            nudCutoutSkipRatio.Value = (decimal)_edit.CutoutSkipRatio;
            nudSmallEdgeRatio.Value = (decimal)_edit.SmallEdgeRatio;
            nudSmallEdgeGuardFactor.Value = (decimal)_edit.SmallEdgeGuardFactor;
            nudLaneLengthRatioThreshold.Value = (decimal)_edit.LaneLengthRatioThreshold;
            nudMaxChainGap.Value = (decimal)_edit.MaxChainGap;
            nudDimensionDecimalPlaces.Value = _edit.DimensionDecimalPlaces;
        }

        private void SaveSettings()
        {
            _edit.ParallelismThreshold = (double)nudParallelismThreshold.Value;
            _edit.DiagonalBendThreshold = (double)nudDiagonalBendThreshold.Value;
            _edit.ArtefactSkipDistanceSq = (double)nudArtefactSkipDistanceSq.Value;
            _edit.CutoutSkipRatio = (double)nudCutoutSkipRatio.Value;
            _edit.SmallEdgeRatio = (double)nudSmallEdgeRatio.Value;
            _edit.SmallEdgeGuardFactor = (double)nudSmallEdgeGuardFactor.Value;
            _edit.LaneLengthRatioThreshold = (double)nudLaneLengthRatioThreshold.Value;
            _edit.MaxChainGap = (double)nudMaxChainGap.Value;
            _edit.DimensionDecimalPlaces = (int)nudDimensionDecimalPlaces.Value;
        }

        private void ResetToDefaults()
        {
            var defaults = new Settings();
            nudParallelismThreshold.Value = (decimal)defaults.ParallelismThreshold;
            nudDiagonalBendThreshold.Value = (decimal)defaults.DiagonalBendThreshold;
            nudArtefactSkipDistanceSq.Value = (decimal)defaults.ArtefactSkipDistanceSq;
            nudCutoutSkipRatio.Value = (decimal)defaults.CutoutSkipRatio;
            nudSmallEdgeRatio.Value = (decimal)defaults.SmallEdgeRatio;
            nudSmallEdgeGuardFactor.Value = (decimal)defaults.SmallEdgeGuardFactor;
            nudLaneLengthRatioThreshold.Value = (decimal)defaults.LaneLengthRatioThreshold;
            nudMaxChainGap.Value = (decimal)defaults.MaxChainGap;
            nudDimensionDecimalPlaces.Value = defaults.DimensionDecimalPlaces;
        }

        private static Settings CloneSettings(Settings src)
        {
            return new Settings
            {
                ParallelismThreshold = src.ParallelismThreshold,
                DiagonalBendThreshold = src.DiagonalBendThreshold,
                ArtefactSkipDistanceSq = src.ArtefactSkipDistanceSq,
                CutoutSkipRatio = src.CutoutSkipRatio,
                SmallEdgeRatio = src.SmallEdgeRatio,
                SmallEdgeGuardFactor = src.SmallEdgeGuardFactor,
                LaneLengthRatioThreshold = src.LaneLengthRatioThreshold,
                MaxChainGap = src.MaxChainGap,
                DimensionDecimalPlaces = src.DimensionDecimalPlaces
            };
        }

        private static void CopySettings(Settings src, Settings dst)
        {
            dst.ParallelismThreshold = src.ParallelismThreshold;
            dst.DiagonalBendThreshold = src.DiagonalBendThreshold;
            dst.ArtefactSkipDistanceSq = src.ArtefactSkipDistanceSq;
            dst.CutoutSkipRatio = src.CutoutSkipRatio;
            dst.SmallEdgeRatio = src.SmallEdgeRatio;
            dst.SmallEdgeGuardFactor = src.SmallEdgeGuardFactor;
            dst.LaneLengthRatioThreshold = src.LaneLengthRatioThreshold;
            dst.MaxChainGap = src.MaxChainGap;
            dst.DimensionDecimalPlaces = src.DimensionDecimalPlaces;
        }
    }
}
