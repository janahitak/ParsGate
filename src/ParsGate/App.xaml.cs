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
            try
            {
                // FIRST: Check administrator privileges BEFORE acquiring mutex
                // If we acquire mutex first then restart as admin, the elevated process
                // sees the mutex held by the non-elevated process and exits immediately!
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

                // SECOND: Now that we're admin, acquire the single-instance mutex
                bool createdNew;
                _singleInstanceMutex = new Mutex(true, MutexName, out createdNew);
                if (!createdNew)
                {
                    ActivateExistingInstance();
                    Shutdown();
                    return;
                }

                base.OnStartup(e);

                // Anti-lockout: remove any leftover kill-switch rules from previous crashes
                FirewallManager.EnsureAntiLockoutOnStartup();

                // Load embedded Vazirmatn font at startup to ensure it's registered
                LoadEmbeddedFont();

                // Hook global exception handlers BEFORE creating the main window
                this.DispatcherUnhandledException += OnDispatcherUnhandledException;
                AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

                // Explicitly create and show the main window
                var mainWin = new Views.MainWindow();
                this.MainWindow = mainWin;
                mainWin.WindowState = WindowState.Normal;
                mainWin.ShowInTaskbar = true;
                mainWin.Show();
                mainWin.Activate();
                mainWin.Focus();
            }
            catch (Exception ex)
            {
                LogStartupError(ex);
                ShowErrorAndExit(ex);
            }
        }

        private void OnDispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            LogStartupError(e.Exception);
            ShowErrorAndExit(e.Exception);
            e.Handled = true;
        }

        private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            if (e.ExceptionObject is Exception ex)
            {
                LogStartupError(ex);
                // Can't show MessageBox here reliably, but log is written
            }
        }

        private static void LogStartupError(Exception ex)
        {
            try
            {
                var logPaths = new[]
                {
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ParsGate", "startup_error.log"),
                    Path.Combine(Path.GetTempPath(), "ParsGate_startup_error.log")
                };

                var logContent = $"=== ParsGate Startup Error ===\n" +
                                 $"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n" +
                                 $"Exception: {ex.GetType().FullName}\n" +
                                 $"Message: {ex.Message}\n" +
                                 $"StackTrace:\n{ex.StackTrace}\n\n";

                foreach (var logPath in logPaths)
                {
                    try
                    {
                        var dir = Path.GetDirectoryName(logPath);
                        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                            Directory.CreateDirectory(dir);
                        File.AppendAllText(logPath, logContent);
                    }
                    catch { /* ignore individual log failures */ }
                }
            }
            catch { /* logging failure should not crash */ }
        }

        private static void ShowErrorAndExit(Exception ex)
        {
            try
            {
                var msg = $"خطای راه‌اندازی ParsGate:\n\n{ex.Message}";
                if (ex.InnerException != null)
                {
                    msg += $"\n\nInner Exception: {ex.InnerException.Message}";
                }
                msg += $"\n\nجزئیات در فایل لاگ ذخیره شد:\n" +
                       $"{Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)}\\ParsGate\\startup_error.log\n" +
                       $"%TEMP%\\ParsGate_startup_error.log";
                
                MessageBox.Show(
                    msg,
                    "خطای راه‌اندازی",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            catch { /* ignore */ }
            finally
            {
                Environment.Exit(1);
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