using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
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
        private static readonly SemaphoreSlim _connectLock = new(1, 1);
        private static readonly object _processLock = new();
        private static string _baseDirectory = AppDomain.CurrentDomain.BaseDirectory;

        // Job Object for kill-on-close (Windows only)
        private static IntPtr _jobHandle = IntPtr.Zero;

        // Log file paths
        private static string _logDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "ParsGate", "logs");
        private static string _runDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "ParsGate", "run");

        public static event Action<string>? StatusChanged;

        public static bool IsConnected { get; private set; }
        public static VpnConfigItem? CurrentConfig { get; private set; }

        public static async Task<bool> ConnectAsync(VpnConfigItem config, AppSettings settings)
        {
            if (config == null || settings == null)
                return false;

            // Ensure single concurrent connect/disconnect
            await _connectLock.WaitAsync();
            try
            {
                return await ConnectInternalAsync(config, settings);
            }
            finally
            {
                _connectLock.Release();
            }
        }

        private static async Task<bool> ConnectInternalAsync(VpnConfigItem config, AppSettings settings)
        {
            try
            {
                StatusChanged?.Invoke("در حال اتصال...");
                AudioAndSpeechService.PlayConnectTone();

                lock (_processLock)
                {
                    DisconnectInternal();
                }

                // Resolve server address (domain -> IPs) before generating configs
                var resolvedIps = await ResolveServerAddressAsync(config.ServerAddress);
                if (resolvedIps.Count == 0)
                {
                    StatusChanged?.Invoke($"خطا: نمی‌توان آدرس سرور '{config.ServerAddress}' را حل کرد");
                    AudioAndSpeechService.PlayErrorTone();
                    return false;
                }

                // Generate config files
                var xrayConfig = ConfigParser.GenerateXrayConfigJson(config, settings);
                var singBoxConfig = SingBoxGenerator.GenerateSingBoxConfigJson(settings, config.ServerAddress, resolvedIps);

                // Ensure run directory exists
                Directory.CreateDirectory(_runDirectory);
                Directory.CreateDirectory(_logDirectory);

                var xrayConfigPath = Path.Combine(_runDirectory, "xray_run.json");
                var singBoxConfigPath = Path.Combine(_runDirectory, "singbox_run.json");

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

                var xrayLogPath = Path.Combine(_logDirectory, $"xray_{DateTime.Now:yyyyMMdd_HHmmss}.log");
                _xrayProcess = StartProcessWithLogging(xrayExePath, $"-c \"{xrayConfigPath}\"", xrayLogPath);
                if (_xrayProcess == null || _xrayProcess.HasExited)
                {
                    StatusChanged?.Invoke("خطا در شروع xray");
                    AudioAndSpeechService.PlayErrorTone();
                    return false;
                }

                // Real readiness check: wait for Xray SOCKS port to accept connections
                var xrayReady = await WaitForPortReadyAsync("127.0.0.1", ConfigParser.XraySocksPort, TimeSpan.FromSeconds(10));
                if (!xrayReady || _xrayProcess.HasExited)
                {
                    var exitCode = _xrayProcess.HasExited ? _xrayProcess.ExitCode : -1;
                    var lastLog = GetLastLogLines(xrayLogPath, 20);
                    StatusChanged?.Invoke($"xray آماده نشد (exit code: {exitCode}). لاگ: {lastLog}");
                    AudioAndSpeechService.PlayErrorTone();
                    await StopProcessAsync(_xrayProcess);
                    _xrayProcess = null;
                    return false;
                }

                // Start sing-box.exe
                var singBoxExePath = Path.Combine(_baseDirectory, "cores", "sing-box.exe");
                if (!File.Exists(singBoxExePath))
                {
                    StatusChanged?.Invoke("sing-box.exe یافت نشد در پوشه cores");
                    AudioAndSpeechService.PlayErrorTone();
                    await StopProcessAsync(_xrayProcess);
                    _xrayProcess = null;
                    return false;
                }

                var singBoxLogPath = Path.Combine(_logDirectory, $"singbox_{DateTime.Now:yyyyMMdd_HHmmss}.log");
                _singBoxProcess = StartProcessWithLogging(singBoxExePath, $"run -c \"{singBoxConfigPath}\"", singBoxLogPath);
                if (_singBoxProcess == null || _singBoxProcess.HasExited)
                {
                    StatusChanged?.Invoke("خطا در شروع sing-box");
                    AudioAndSpeechService.PlayErrorTone();
                    await StopProcessAsync(_xrayProcess);
                    _xrayProcess = null;
                    return false;
                }

                // Wait for sing-box to stay alive for a grace period
                await Task.Delay(500);
                if (_singBoxProcess.HasExited)
                {
                    var exitCode = _singBoxProcess.ExitCode;
                    var lastLog = GetLastLogLines(singBoxLogPath, 20);
                    StatusChanged?.Invoke($"sing-box به طور غیرمنتظره متوقف شد (exit code: {exitCode}). لاگ: {lastLog}");
                    AudioAndSpeechService.PlayErrorTone();
                    await StopProcessAsync(_xrayProcess);
                    await StopProcessAsync(_singBoxProcess);
                    _xrayProcess = null;
                    _singBoxProcess = null;
                    return false;
                }

                // Attach both processes to Job Object for kill-on-close
                AttachToJobObject(_xrayProcess, _singBoxProcess);

                // Subscribe to Exited events
                _xrayProcess.EnableRaisingEvents = true;
                _singBoxProcess.EnableRaisingEvents = true;
                _xrayProcess.Exited += OnCoreExited;
                _singBoxProcess.Exited += OnCoreExited;

                // Enable Kill Switch if configured
                if (settings.KillSwitchEnabled)
                {
                    var ksResult = FirewallManager.EnableKillSwitch(xrayExePath, singBoxExePath, resolvedIps);
                    if (!ksResult.Success)
                    {
                        StatusChanged?.Invoke($"هشدار: Kill Switch فعال نشد: {ksResult.ErrorMessage}");
                        // Don't fail connection, just warn
                    }
                }

                IsConnected = true;
                CurrentConfig = config;
                StatusChanged?.Invoke($"متصل: {config.Name}");
                AudioAndSpeechService.PlayConnectTone();

                // Clean up config files after successful start (optional, keep for debugging)
                // File.Delete(xrayConfigPath);
                // File.Delete(singBoxConfigPath);

                return true;
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke($"خطای اتصال: {ex.Message}");
                AudioAndSpeechService.PlayErrorTone();
                return false;
            }
        }

        private static async Task<List<string>> ResolveServerAddressAsync(string serverAddress)
        {
            var ips = new List<string>();

            if (IPAddress.TryParse(serverAddress, out var ipAddr))
            {
                ips.Add(serverAddress);
                return ips;
            }

            try
            {
                // Resolve with timeout
                var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var hostEntry = await Dns.GetHostEntryAsync(serverAddress).WaitAsync(cts.Token);

                foreach (var addr in hostEntry.AddressList)
                {
                    if (addr.AddressFamily == AddressFamily.InterNetwork || addr.AddressFamily == AddressFamily.InterNetworkV6)
                    {
                        ips.Add(addr.ToString());
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WARN] Failed to resolve {serverAddress}: {ex.Message}");
            }

            return ips.Distinct().ToList();
        }

        private static async Task<bool> WaitForPortReadyAsync(string host, int port, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    using var client = new TcpClient();
                    var connectTask = client.ConnectAsync(host, port);
                    var completedTask = await Task.WhenAny(connectTask, Task.Delay(200));
                    if (completedTask == connectTask && client.Connected)
                    {
                        return true;
                    }
                }
                catch
                {
                    // Ignore, retry
                }
                await Task.Delay(200);
            }
            return false;
        }

        private static Process? StartProcessWithLogging(string exePath, string arguments, string logPath)
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
                    // Write to log file asynchronously with size cap (5 MB)
                    const long MaxLogSize = 5 * 1024 * 1024; // 5 MB
                    var logFileInfo = new FileInfo(logPath);

                    Task.Run(async () =>
                    {
                        try
                        {
                            using var writer = new StreamWriter(logPath, append: true);
                            writer.AutoFlush = true;

                            var outputTask = ReadStreamAsync(proc.StandardOutput, writer, "OUT", logPath, MaxLogSize);
                            var errorTask = ReadStreamAsync(proc.StandardError, writer, "ERR", logPath, MaxLogSize);
                            await Task.WhenAll(outputTask, errorTask);
                        }
                        catch { }
                    });
                }
                return proc;
            }
            catch
            {
                return null;
            }
        }

        private static async Task ReadStreamAsync(StreamReader reader, StreamWriter writer, string prefix, string logPath, long maxSize)
        {
            var buffer = new char[4096];
            int read;
            while ((read = await reader.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                var line = new string(buffer, 0, read);
                var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
                writer.Write($"[{timestamp}] [{prefix}] {line}");

                // Check log file size and rotate if needed
                try
                {
                    var fi = new FileInfo(logPath);
                    if (fi.Exists && fi.Length > maxSize)
                    {
                        writer.Flush();
                        writer.Dispose();
                        // Rotate: move current to .old and start new
                        var oldPath = logPath + ".old";
                        if (File.Exists(oldPath)) File.Delete(oldPath);
                        File.Move(logPath, oldPath);
                        // Note: writer is closed, new writes will go to new file if we reopen
                        // For simplicity, we just stop logging if size exceeded
                        break;
                    }
                }
                catch { }
            }
        }

        private static string GetLastLogLines(string logPath, int lines)
        {
            try
            {
                if (!File.Exists(logPath)) return "(no log file)";
                var allLines = await File.ReadAllLinesAsync(logPath);
                var last = allLines.Skip(Math.Max(0, allLines.Length - lines));
                return string.Join(" | ", last);
            }
            catch
            {
                return "(cannot read log)";
            }
        }

        private static void AttachToJobObject(Process xrayProc, Process singBoxProc)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

            try
            {
                // Create Job Object with KILL_ON_JOB_CLOSE
                _jobHandle = CreateJobObject(IntPtr.Zero, null);
                if (_jobHandle == IntPtr.Zero) return;

                var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
                {
                    BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                    {
                        LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                    }
                };

                int length = Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
                var ptr = Marshal.AllocHGlobal(length);
                Marshal.StructureToPtr(info, ptr, false);
                SetInformationJobObject(_jobHandle, JobObjectInformationClass.ExtendedLimitInformation, ptr, (uint)length);
                Marshal.FreeHGlobal(ptr);

                // Assign processes to job
                AssignProcessToJobObject(_jobHandle, xrayProc.Handle);
                AssignProcessToJobObject(_jobHandle, singBoxProc.Handle);
            }
            catch
            {
                // Job Object creation failed, continue without it
                if (_jobHandle != IntPtr.Zero)
                {
                    CloseHandle(_jobHandle);
                    _jobHandle = IntPtr.Zero;
                }
            }
        }

        private static void OnCoreExited(object? sender, EventArgs e)
        {
            if (!IsConnected) return; // Only act if we were connected

            var process = sender as Process;
            var processName = process?.ProcessName ?? "unknown";
            var exitCode = process?.ExitCode ?? -1;

            // Fire and forget cleanup
            Task.Run(async () =>
            {
                try
                {
                    StatusChanged?.Invoke($"هشدار: {processName} با کد {exitCode} متوقف شد");
                    AudioAndSpeechService.PlayErrorTone();

                    // Disconnect and restore network
                    await DisconnectInternalAsync();
                }
                catch { }
            });
        }

        private static async Task DisconnectInternalAsync()
        {
            lock (_processLock)
            {
                DisconnectInternal();
            }

            FirewallManager.DisableKillSwitch();

            IsConnected = false;
            CurrentConfig = null;
            StatusChanged?.Invoke("قطع شده");
            AudioAndSpeechService.PlayDisconnectTone();

            // Clean up run directory configs
            try
            {
                if (Directory.Exists(_runDirectory))
                {
                    foreach (var file in Directory.GetFiles(_runDirectory, "*_run.json"))
                    {
                        File.Delete(file);
                    }
                }
            }
            catch { }
        }

        public static async Task DisconnectAsync()
        {
            await _connectLock.WaitAsync();
            try
            {
                await DisconnectInternalAsync();
            }
            finally
            {
                _connectLock.Release();
            }
        }

        private static void DisconnectInternal()
        {
            try
            {
                if (_singBoxProcess != null && !_singBoxProcess.HasExited)
                {
                    _singBoxProcess.Exited -= OnCoreExited;
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
                    _xrayProcess.Exited -= OnCoreExited;
                    _xrayProcess.Kill(true);
                    _xrayProcess.WaitForExit(3000);
                    _xrayProcess.Dispose();
                    _xrayProcess = null;
                }
            }
            catch { }
        }

        private static async Task StopProcessAsync(Process? proc)
        {
            if (proc == null || proc.HasExited) return;
            try
            {
                proc.Kill(true);
                await proc.WaitForExitAsync(3000);
                proc.Dispose();
            }
            catch { }
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

        // P/Invoke for Job Object
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

        [DllImport("kernel32.dll")]
        private static extern bool SetInformationJobObject(IntPtr hJob, JobObjectInformationClass jobObjectInformationClass, IntPtr lpJobObjectInformation, uint cbJobObjectInformationLength);

        [DllImport("kernel32.dll")]
        private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr hObject);

        private enum JobObjectInformationClass
        {
            ExtendedLimitInformation = 9
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public Int64 PerProcessUserTimeLimit;
            public Int64 PerJobUserTimeLimit;
            public UInt32 LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public UInt32 ActiveProcessLimit;
            public UIntPtr Affinity;
            public UInt32 PriorityClass;
            public UInt32 SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public Int64 IoReadBytes;
            public Int64 IoWriteBytes;
            public Int64 IoReadOperations;
            public Int64 IoWriteOperations;
            public Int64 IoOtherBytes;
            public Int64 IoOtherOperations;
            public Int64 PeakProcessMemoryUsed;
            public Int64 PeakJobMemoryUsed;
        }

        private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
    }
}