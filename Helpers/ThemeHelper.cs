using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using OneDrive_Simple_Management_Tool.Models;
using System;

namespace OneDrive_Simple_Management_Tool.Helpers
{
    public static class ThemeHelper
    {
        // Explicitly receive the live window; the app replaces its startup window.
        public static bool Apply(MainWindow window, AppearancePreferences preferences)
        {
            if (window.Content is FrameworkElement root)
            {
                root.RequestedTheme = preferences.Theme switch
                {
                    AppearanceTheme.Light => ElementTheme.Light,
                    AppearanceTheme.Dark => ElementTheme.Dark,
                    _ => ElementTheme.Default
                };
            }

            bool supported;
            try
            {
                SystemBackdrop backdrop = preferences.Material switch
                {
                    AppearanceMaterial.Mica when MicaController.IsSupported() => new MicaBackdrop { Kind = MicaKind.Base },
                    AppearanceMaterial.MicaAlt when MicaController.IsSupported() => new MicaBackdrop { Kind = MicaKind.BaseAlt },
                    AppearanceMaterial.Acrylic when DesktopAcrylicController.IsSupported() => new DesktopAcrylicBackdrop(),
                    _ => null
                };
                window.SystemBackdrop = backdrop;
                supported = preferences.Material == AppearanceMaterial.None || backdrop != null;
            }
            catch (Exception)
            {
                window.SystemBackdrop = null;
                supported = false;
            }
            window.SetBackdropActive(window.SystemBackdrop != null);
            return supported;
        }
    }
}
