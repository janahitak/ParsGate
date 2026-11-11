using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Principal;
using System.Threading;
using System.Windows;
using ParsGate.Services;

namespace ParsGate
{
    public partial class App : System.Windows.Application
    {
        private static Mutex? _singleInstanceMutex;
        private const string MutexName = "Global\\ParsGate_SingleInstance_Mutex";

        protected override void OnStartup(StartupEventArgs e)
        {
            // Single-instance check
            bool createdNew;
            _singleInstanceMutex = new Mutex(true, MutexName, out createdNew);
            if (!createdNew)
            {
                // Another instance is running - activate it and exit
                ActivateExistingInstance();
                Shutdown();
                return;
            }

            base.OnStartup(e);

            // Anti-lockout: remove any leftover kill-switch rules from previous crashes
            FirewallManager.EnsureAntiLockoutOnStartup();

            // Ensure the app uses RTL flow direction globally
            FrameworkElement.FlowDirectionProperty.OverrideMetadata(
                typeof(Window),
                new FrameworkPropertyMetadata(FlowDirection.RightToLeft));

            // Load embedded Vazirmatn font at startup to ensure it's registered
            LoadEmbeddedFont();

            // Verify we have Administrator privileges (required for TUN and netsh)
            if (!IsRunningAsAdministrator())
            {
                var result = MessageBox.Show(
                    "ParsGate نیاز به مجوز مدیر (Administrator) دارد برای:\n" +
                    "• ایجاد اینترفیس TUN (ParsGate-TUN)\n" +
                    "• مدیریت قوانین فایروال (Kill Switch)\n\n" +
                    "آیا می‌خواهید با مجوز مدیر مجدداً اجرا شود؟",
                    "مجوز مدیر لازم است",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (result == MessageBoxResult.Yes)
                {
                    RestartAsAdministrator();
                }
                Shutdown();
                return;
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _singleInstanceMutex?.ReleaseMutex();
            _singleInstanceMutex?.Dispose();
            base.OnExit(e);
        }

        private void ActivateExistingInstance()
        {
            try
            {
                var current = Process.GetCurrentProcess();
                var processes = Process.GetProcessesByName(current.ProcessName);
                foreach (var process in processes)
                {
                    if (process.Id != current.Id)
                    {
                        // Found another instance - try to bring it to foreground
                        var handle = process.MainWindowHandle;
                        if (handle != IntPtr.Zero)
                        {
                            NativeMethods.ShowWindow(handle, NativeMethods.SW_RESTORE);
                            NativeMethods.SetForegroundWindow(handle);
                        }
                        break;
                    }
                }
            }
            catch { }
        }

        private static bool IsRunningAsAdministrator()
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }

        private static void RestartAsAdministrator()
        {
            try
            {
                var exePath = Process.GetCurrentProcess().MainModule?.FileName;
                if (string.IsNullOrEmpty(exePath)) return;

                var startInfo = new ProcessStartInfo
                {
                    FileName = exePath,
                    UseShellExecute = true,
                    Verb = "runas",
                    WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory
                };
                Process.Start(startInfo);
            }
            catch { }
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

    internal static class NativeMethods
    {
        public const int SW_RESTORE = 9;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);
    }
}