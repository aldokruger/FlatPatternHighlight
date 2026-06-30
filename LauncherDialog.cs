using System;
using System.Drawing;
using System.Windows.Forms;

namespace FlatPatternHighlight
{
    /// <summary>
    /// Janela de inicialização: pergunta se o usuário quer executar o
    /// dimensionamento normal ou abrir as configurações.
    /// </summary>
    internal sealed class LauncherDialog : Form
    {
        public bool RunDimensioning { get; private set; }
        public bool OpenSettings    { get; private set; }

        private LauncherDialog()
        {
            Text = "FlatPatternHighlight";
            Size = new Size(400, 180);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9F);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowIcon = false;
            ShowInTaskbar = false;
            ControlBox = false;

            var lbl = new Label
            {
                Text = "O que deseja fazer?",
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleCenter,
                Dock = DockStyle.Top,
                Height = 50,
                Padding = new Padding(0, 12, 0, 0),
                Font = new Font("Segoe UI", 11F, FontStyle.Bold)
            };

            var btnRun = new Button
            {
                Text = "Executar Dimensionamento",
                Size = new Size(220, 34),
                Location = new Point(85, 60),
                UseVisualStyleBackColor = true
            };
            btnRun.Click += (s, e) => { RunDimensioning = true; Close(); };

            var btnSettings = new Button
            {
                Text = "Configurações",
                Size = new Size(220, 34),
                Location = new Point(85, 100),
                UseVisualStyleBackColor = true
            };
            btnSettings.Click += (s, e) => { OpenSettings = true; Close(); };

            Controls.Add(lbl);
            Controls.Add(btnRun);
            Controls.Add(btnSettings);
            AcceptButton = btnRun;
        }

        /// <summary>
        /// Exibe a janela de escolha e retorna a opção selecionada.
        /// 'null' se o usuário fechou a janela.
        /// </summary>
        internal static new LauncherChoice Show()
        {
            using (var dlg = new LauncherDialog())
            {
                dlg.ShowDialog();
                if (dlg.RunDimensioning) return LauncherChoice.Run;
                if (dlg.OpenSettings)    return LauncherChoice.Settings;
                return LauncherChoice.Cancel;
            }
        }

        internal enum LauncherChoice
        {
            Cancel,
            Run,
            Settings
        }
    }
}
