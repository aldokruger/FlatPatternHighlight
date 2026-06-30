using System;
using NXOpen;

namespace FlatPatternHighlightConfig
{
    /// <summary>
    /// Entry point for the FlatPatternHighlight configuration dialog.
    ///
    /// Compile this project separately to produce FlatPatternHighlightConfig.dll.
    /// In NX, create a button or Ctrl+U entry that calls FlatPatternHighlightConfig.Main()
    /// to open the settings editor without running the dimensioning process.
    /// </summary>
    public static class ConfigMain
    {
        /// <summary>
        /// Opens the FlatPatternHighlight settings editor dialog.
        /// Reads settings from %APPDATA%/FlatPatternHighlight/settings.json,
        /// shows the editor, and saves changes on OK.
        /// </summary>
        public static int Main(string[] args)
        {
            try
            {
                // Still need Session/UI for proper NX context
                var session = Session.GetSession();
                var ui = UI.GetUI();

                var settings = FlatPatternHighlight.Settings.Load();
                if (FlatPatternHighlight.ConfigDialog.ShowDialog(settings))
                {
                    FlatPatternHighlight.Settings.Save(settings);
                }

                return 0;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[FlatPatternHighlightConfig] Error: {ex.Message}");
                return 1;
            }
        }
    }
}
