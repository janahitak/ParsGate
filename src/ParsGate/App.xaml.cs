using System;
using System.IO;
using System.Reflection;
using System.Windows;

namespace ParsGate
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Ensure the app uses RTL flow direction globally
            FrameworkElement.FlowDirectionProperty.OverrideMetadata(
                typeof(Window),
                new FrameworkPropertyMetadata(FlowDirection.RightToLeft));

            // Load embedded Vazirmatn font at startup to ensure it's registered
            LoadEmbeddedFont();
        }

        private void LoadEmbeddedFont()
        {
            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                var fontResourceName = "ParsGate.Assets.Fonts.Vazirmatn-Bold.ttf";

                using var stream = assembly.GetManifestResourceStream(fontResourceName);
                if (stream != null)
                {
                    // Font is loaded as a resource via pack URI in XAML
                    // This ensures the resource stream is accessible
                    // WPF automatically registers fonts from pack URIs in ResourceDictionary
                }
            }
            catch
            {
                // Font loading failure is non-fatal; fallback to Tahoma defined in FontFamily
            }
        }

        public static string BaseDirectory => AppDomain.CurrentDomain.BaseDirectory;
    }
}