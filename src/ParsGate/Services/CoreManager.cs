using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using ParsGate.Models;
using ParsGate.Services;

namespace ParsGate.Services
{
    public static class CoreManager
    {
        private static Process? _xrayProcess;
        private static Process? _singBoxProcess;
        private static readonly object _processLock = new();
        private static string _baseDirectory = AppDomain.CurrentDomain.BaseDirectory;

        public static event Action<string>? StatusChanged;

        public static bool IsConnected { get; private set; }
        public static VpnConfigItem? CurrentConfig { get; private set; }

        public static async Task<bool> ConnectAsync(VpnConfigItem config, AppSettings settings)
        {
            if (config == null || settings == null)
                return false;

            try
            {
                StatusChanged?.Invoke("در حال اتصال...");
                AudioAndSpeechService.PlayConnectTone();

                lock (_processLock)
                {
                    DisconnectInternal();
                }

                // Generate config files
                var xrayConfig = ConfigParser.GenerateXrayConfigJson(config, settings);
                var singBoxConfig = SingBoxGenerator.GenerateSingBoxConfigJson(settings);

                var xrayConfigPath = Path.Combine(_baseDirectory, "xray_run.json");
                var singBoxConfigPath = Path.Combine(_baseDirectory, "singbox_run.json");

                await File.WriteAllTextAsync(xrayConfigPath, xrayConfig);
                await File.WriteAllTextAsync(singBoxConfigPath, singBoxConfig);

                // Start xray.exe
                var xrayExePath = Path.Combine(_baseDirectory, "cores", "xray.exe");
                if (!File.Exists(xrayExePath))
                {
                    StatusChanged?.Invoke("xray.exe یافت نشد در پوشه cores");
                    AudioAndSpeechService.PlayErrorTone();
                    return false;
                }

                _xrayProcess = StartProcess(xrayExePath, $"-c \"{xrayConfigPath}\"");
                if (_xrayProcess == null || _xrayProcess.HasExited)
                {
                    StatusChanged?.Invoke("خطا در شروع xray");
                    AudioAndSpeechService.PlayErrorTone();
                    return false;
                }

                // Wait for xray to start
                await Task.Delay(800);

                if (_xrayProcess.HasExited)
                {
                    StatusChanged?.Invoke("xray به طور غیرمنتظره متوقف شد");
                    AudioAndSpeechService.PlayErrorTone();
                    return false;
                }

                // Start sing-box.exe
                var singBoxExePath = Path.Combine(_baseDirectory, "cores", "sing-box.exe");
                if (!File.Exists(singBoxExePath))
                {
                    StatusChanged?.Invoke("sing-box.exe یافت نشد در پوشه cores");
                    AudioAndSpeechService.PlayErrorTone();
                    return false;
                }

                _singBoxProcess = StartProcess(singBoxExePath, $"run -c \"{singBoxConfigPath}\"");
                if (_singBoxProcess == null || _singBoxProcess.HasExited)
                {
                    StatusChanged?.Invoke("خطا در شروع sing-box");
                    AudioAndSpeechService.PlayErrorTone();
                    return false;
                }

                // Enable Kill Switch if configured
                if (settings.KillSwitchEnabled)
                {
                    var serverIp = config.ServerAddress;
                    FirewallManager.EnableKillSwitch(xrayExePath, singBoxExePath, serverIp);
                }

                IsConnected = true;
                CurrentConfig = config;
                StatusChanged?.Invoke($"متصل: {config.Name}");
                AudioAndSpeechService.PlayConnectTone();
                return true;
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke($"خطای اتصال: {ex.Message}");
                AudioAndSpeechService.PlayErrorTone();
                return false;
            }
        }

        public static async Task DisconnectAsync()
        {
            try
            {
                StatusChanged?.Invoke("در حال قطع اتصال...");
                AudioAndSpeechService.PlayDisconnectTone();

                lock (_processLock)
                {
                    DisconnectInternal();
                }

                FirewallManager.DisableKillSwitch();

                IsConnected = false;
                CurrentConfig = null;
                StatusChanged?.Invoke("قطع شده");
                AudioAndSpeechService.PlayDisconnectTone();
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke($"خطای قطع اتصال: {ex.Message}");
                AudioAndSpeechService.PlayErrorTone();
            }
        }

        private static void DisconnectInternal()
        {
            try
            {
                if (_singBoxProcess != null && !_singBoxProcess.HasExited)
                {
                    _singBoxProcess.Kill(true);
                    _singBoxProcess.WaitForExit(3000);
                    _singBoxProcess.Dispose();
                    _singBoxProcess = null;
                }
            }
            catch { }

            try
            {
                if (_xrayProcess != null && !_xrayProcess.HasExited)
                {
                    _xrayProcess.Kill(true);
                    _xrayProcess.WaitForExit(3000);
                    _xrayProcess.Dispose();
                    _xrayProcess = null;
                }
            }
            catch { }
        }

        private static Process? StartProcess(string exePath, string arguments)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = arguments,
                    WorkingDirectory = _baseDirectory,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };

                var proc = Process.Start(psi);
                if (proc != null)
                {
                    // Drain output to prevent buffer deadlock
                    Task.Run(() =>
                    {
                        try { proc.StandardOutput.ReadToEnd(); } catch { }
                        try { proc.StandardError.ReadToEnd(); } catch { }
                    });
                }
                return proc;
            }
            catch
            {
                return null;
            }
        }

        public static async Task TestAllRealPingsAsync(List<VpnConfigItem> configs, AppSettings settings, IProgress<string> progress)
        {
            if (configs == null || configs.Count == 0)
                return;

            var semaphore = new SemaphoreSlim(10);
            var tasks = new List<Task>();

            foreach (var config in configs)
            {
                await semaphore.WaitAsync();
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        progress?.Report($"تست پینگ: {config.Name} ({config.ServerAddress}:{config.Port})");
                        var ping = await TestTcpPingAsync(config.ServerAddress, config.Port);
                        config.LastPingMs = ping;
                        progress?.Report($"پینگ {config.Name}: {ping} ms");
                    }
                    catch (Exception ex)
                    {
                        config.LastPingMs = -1;
                        progress?.Report($"خطای پینگ {config.Name}: {ex.Message}");
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }));
            }

            await Task.WhenAll(tasks);
            SettingsManager.Save(settings);
            progress?.Report("تست پینگ تمام شد");
        }

        private static async Task<long> TestTcpPingAsync(string host, int port, int timeoutMs = 5000)
        {
            using var client = new TcpClient();
            var start = DateTime.UtcNow;

            var connectTask = client.ConnectAsync(host, port);
            var completedTask = await Task.WhenAny(connectTask, Task.Delay(timeoutMs));

            if (completedTask == connectTask && client.Connected)
            {
                return (long)(DateTime.UtcNow - start).TotalMilliseconds;
            }

            return -1;
        }
    }
}