using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Runtime.InteropServices;

namespace ParsGate.Services
{
    public static class FirewallManager
    {
        private const string RulePrefix = "ParsGate_KS_";
        private static readonly string StateFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "ParsGate", "firewall_state.json");

        public class KillSwitchResult
        {
            public bool Success { get; set; }
            public string? ErrorMessage { get; set; }
        }

        public class FirewallState
        {
            public List<string> RuleNames { get; set; } = new();
            public Dictionary<string, string> SavedDefaultPolicies { get; set; } = new(); // profile -> policy
            public List<string> AllowedServerIps { get; set; } = new();
            public string? TunSubnet { get; set; }
            public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        }

        public static void EnsureAntiLockoutOnStartup()
        {
            try
            {
                // Check for stale state file from previous crash
                if (File.Exists(StateFilePath))
                {
                    var json = File.ReadAllText(StateFilePath);
                    var state = JsonSerializer.Deserialize<FirewallState>(json);
                    if (state != null && state.RuleNames.Count > 0)
                    {
                        RestoreNetworkInternal(state);
                    }
                }
            }
            catch { }
        }

        public static KillSwitchResult EnableKillSwitch(string xrayExePath, string singBoxExePath, IEnumerable<string> serverIps)
        {
            try
            {
                var state = new FirewallState();
                var profiles = new[] { "domain", "private", "public" };
                var allErrors = new List<string>();

                // 1. Save current default outbound policy for each profile
                foreach (var profile in profiles)
                {
                    var policy = GetDefaultOutboundPolicy(profile);
                    state.SavedDefaultPolicies[profile] = policy;
                }

                // 2. Set default outbound policy to BLOCK for all profiles
                foreach (var profile in profiles)
                {
                    var result = RunNetsh($"advfirewall set {profile}profile firewallpolicy blockinbound,blockoutbound");
                    if (!result.Success)
                    {
                        allErrors.Add($"Failed to set {profile} profile policy: {result.Error}");
                    }
                }

                if (allErrors.Count > 0)
                {
                    // Rollback on failure
                    RestoreDefaultPolicies(state.SavedDefaultPolicies);
                    return new KillSwitchResult { Success = false, ErrorMessage = string.Join("; ", allErrors) };
                }

                // 3. Add explicit ALLOW rules (prefix with ParsGate_KS_)
                var ruleNames = new List<string>();

                // Allow loopback (IPv4 and IPv6)
                ruleNames.AddRange(AddAllowRule("AllowLoopback", "dir=out action=allow protocol=any remoteip=127.0.0.1,::1"));

                // Allow DHCP (UDP 67/68)
                ruleNames.AddRange(AddAllowRule("AllowDHCP", "dir=out action=allow protocol=udp remoteport=67,68"));

                // Allow xray.exe
                if (!string.IsNullOrWhiteSpace(xrayExePath) && File.Exists(xrayExePath))
                {
                    ruleNames.AddRange(AddAllowRule("AllowXray", $"dir=out action=allow program=\"{xrayExePath}\" protocol=any"));
                }

                // Allow sing-box.exe
                if (!string.IsNullOrWhiteSpace(singBoxExePath) && File.Exists(singBoxExePath))
                {
                    ruleNames.AddRange(AddAllowRule("AllowSingBox", $"dir=out action=allow program=\"{singBoxExePath}\" protocol=any"));
                }

                // Allow resolved VPN server IPs (IPv4 and IPv6)
                foreach (var ip in serverIps.Distinct())
                {
                    if (IPAddress.TryParse(ip, out var addr))
                    {
                        var cidr = addr.AddressFamily == AddressFamily.InterNetworkV6 ? $"{ip}/128" : $"{ip}/32";
                        ruleNames.AddRange(AddAllowRule($"AllowServer_{ip.Replace(':', '_').Replace('.', '_')}", $"dir=out action=allow remoteip={cidr} protocol=any"));
                        state.AllowedServerIps.Add(ip);
                    }
                }

                // Allow TUN adapter subnet (172.19.0.0/30) so app traffic entering tunnel isn't blocked
                // This allows traffic FROM local apps TO the TUN interface
                ruleNames.AddRange(AddAllowRule("AllowTUNSubnet", "dir=out action=allow remoteip=172.19.0.0/30 protocol=any"));
                state.TunSubnet = "172.19.0.0/30";

                // Also allow IPv6 ULA if TUN gets IPv6 (fc00::/7 covers ULA)
                ruleNames.AddRange(AddAllowRule("AllowTUNIPv6", "dir=out action=allow remoteip=fc00::/7 protocol=any"));

                state.RuleNames = ruleNames;
                state.CreatedAt = DateTime.UtcNow;

                // 4. Save state to file
                Directory.CreateDirectory(Path.GetDirectoryName(StateFilePath)!);
                var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(StateFilePath, json);

                return new KillSwitchResult { Success = true };
            }
            catch (Exception ex)
            {
                return new KillSwitchResult { Success = false, ErrorMessage = ex.Message };
            }
        }

        public static void DisableKillSwitch()
        {
            try
            {
                if (File.Exists(StateFilePath))
                {
                    var json = File.ReadAllText(StateFilePath);
                    var state = JsonSerializer.Deserialize<FirewallState>(json);
                    if (state != null)
                    {
                        RestoreNetworkInternal(state);
                    }
                }
                else
                {
                    // Fallback: delete all ParsGate_KS_ rules and restore default policy
                    DeleteAllParsGateRules();
                    RunNetsh("advfirewall set allprofiles firewallpolicy blockinbound,allowoutbound");
                }
            }
            catch { }
        }

        public static KillSwitchResult RestoreNetwork()
        {
            try
            {
                if (!File.Exists(StateFilePath))
                {
                    return new KillSwitchResult { Success = false, ErrorMessage = "No firewall state file found" };
                }

                var json = File.ReadAllText(StateFilePath);
                var state = JsonSerializer.Deserialize<FirewallState>(json);
                if (state == null)
                {
                    return new KillSwitchResult { Success = false, ErrorMessage = "Invalid firewall state file" };
                }

                RestoreNetworkInternal(state);
                return new KillSwitchResult { Success = true };
            }
            catch (Exception ex)
            {
                return new KillSwitchResult { Success = false, ErrorMessage = ex.Message };
            }
        }

        private static void RestoreNetworkInternal(FirewallState state)
        {
            // 1. Delete all ParsGate_KS_ rules
            foreach (var ruleName in state.RuleNames)
            {
                DeleteRuleByName(ruleName);
            }
            // Also catch any strays
            DeleteAllParsGateRules();

            // 2. Restore saved default policies
            RestoreDefaultPolicies(state.SavedDefaultPolicies);

            // 3. Delete state file
            try { File.Delete(StateFilePath); } catch { }
        }

        private static void RestoreDefaultPolicies(Dictionary<string, string> policies)
        {
            foreach (var kvp in policies)
            {
                var profile = kvp.Key;
                var policy = kvp.Value;
                RunNetsh($"advfirewall set {profile}profile firewallpolicy {policy}");
            }
            // Fallback for any profiles not saved
            var allProfiles = new[] { "domain", "private", "public" };
            foreach (var profile in allProfiles)
            {
                if (!policies.ContainsKey(profile))
                {
                    RunNetsh($"advfirewall set {profile}profile firewallpolicy blockinbound,allowoutbound");
                }
            }
        }

        private static void DeleteAllParsGateRules()
        {
            // Enumerate all rules with our prefix and delete them
            var psi = new ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = "advfirewall firewall show rule name=all",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            using var proc = Process.Start(psi);
            if (proc != null)
            {
                var output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(5000);

                var lines = output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                string? currentRuleName = null;
                foreach (var line in lines)
                {
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith("Rule Name:", StringComparison.OrdinalIgnoreCase))
                    {
                        currentRuleName = trimmed.Substring("Rule Name:".Length).Trim();
                    }
                    else if (!string.IsNullOrWhiteSpace(currentRuleName) && currentRuleName.StartsWith(RulePrefix))
                    {
                        DeleteRuleByName(currentRuleName);
                        currentRuleName = null;
                    }
                }
            }
        }

        private static List<string> AddAllowRule(string suffix, string parameters)
        {
            var ruleName = $"{RulePrefix}{suffix}";
            var result = RunNetsh($"advfirewall firewall add rule name=\"{ruleName}\" {parameters} enable=yes");
            if (result.Success)
            {
                return new List<string> { ruleName };
            }
            else
            {
                Console.WriteLine($"[WARN] Failed to add firewall rule {ruleName}: {result.Error}");
                return new List<string>();
            }
        }

        private static void DeleteRuleByName(string ruleName)
        {
            RunNetsh($"advfirewall firewall delete rule name=\"{ruleName}\"");
        }

        private static string GetDefaultOutboundPolicy(string profile)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = $"advfirewall show {profile}profile",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            using var proc = Process.Start(psi);
            if (proc != null)
            {
                var output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(5000);

                foreach (var line in output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith("Outbound:", StringComparison.OrdinalIgnoreCase) ||
                        trimmed.StartsWith("DefaultOutbound:", StringComparison.OrdinalIgnoreCase))
                    {
                        var parts = trimmed.Split(':');
                        if (parts.Length >= 2)
                        {
                            return parts[1].Trim().ToLowerInvariant();
                        }
                    }
                }
            }
            return "allowoutbound"; // Default fallback
        }

        private static (bool Success, string? Error) RunNetsh(string arguments)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "netsh",
                    Arguments = arguments,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                using var proc = Process.Start(psi);
                if (proc == null) return (false, "Process start failed");

                var stdout = proc.StandardOutput.ReadToEnd();
                var stderr = proc.StandardError.ReadToEnd();
                var exited = proc.WaitForExit(10000);

                if (!exited)
                {
                    try { proc.Kill(); } catch { }
                    return (false, "Timeout");
                }

                if (proc.ExitCode != 0)
                {
                    var error = !string.IsNullOrWhiteSpace(stderr) ? stderr : stdout;
                    Console.WriteLine($"[WARN] netsh exit code {proc.ExitCode}: {arguments} -> {error}");
                    return (false, error);
                }

                return (true, null);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WARN] netsh exception: {arguments} -> {ex.Message}");
                return (false, ex.Message);
            }
        }
    }
}