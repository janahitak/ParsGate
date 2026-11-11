using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ParsGate.Services
{
    public static class FirewallManager
    {
        private const string BlockRuleName = "ParsGate_KillSwitch_Block";
        private const string AllowCoreRuleName = "ParsGate_KillSwitch_AllowCore";

        public static void EnsureAntiLockoutOnStartup()
        {
            try
            {
                DeleteRuleByName(BlockRuleName);
                DeleteRuleByName(AllowCoreRuleName);
            }
            catch { }
        }

        public static void EnableKillSwitch(string xrayExePath, string singBoxExePath, string serverIp)
        {
            try
            {
                EnsureAntiLockoutOnStartup();

                // Allow loopback
                RunNetsh("advfirewall firewall add rule name=\"ParsGate_KillSwitch_AllowLoopback\" dir=out action=allow protocol=any remoteip=127.0.0.1,::1");

                // Allow DHCP
                RunNetsh("advfirewall firewall add rule name=\"ParsGate_KillSwitch_AllowDHCP\" dir=out action=allow protocol=udp remoteport=67,68");

                // Allow DNS (UDP 53)
                RunNetsh("advfirewall firewall add rule name=\"ParsGate_KillSwitch_AllowDNS\" dir=out action=allow protocol=udp remoteport=53");

                // Allow xray.exe
                if (!string.IsNullOrWhiteSpace(xrayExePath))
                {
                    RunNetsh($"advfirewall firewall add rule name=\"{AllowCoreRuleName}_Xray\" dir=out action=allow program=\"{xrayExePath}\" protocol=any");
                }

                // Allow sing-box.exe
                if (!string.IsNullOrWhiteSpace(singBoxExePath))
                {
                    RunNetsh($"advfirewall firewall add rule name=\"{AllowCoreRuleName}_SingBox\" dir=out action=allow program=\"{singBoxExePath}\" protocol=any");
                }

                // Allow server IP (the VPN server)
                if (!string.IsNullOrWhiteSpace(serverIp))
                {
                    RunNetsh($"advfirewall firewall add rule name=\"{AllowCoreRuleName}_Server\" dir=out action=allow remoteip={serverIp} protocol=any");
                }

                // Block all other outbound traffic
                RunNetsh($"advfirewall firewall add rule name=\"{BlockRuleName}\" dir=out action=block protocol=any enable=yes");
            }
            catch { }
        }

        public static void DisableKillSwitch()
        {
            try
            {
                DeleteRuleByName(BlockRuleName);
                DeleteRuleByName($"{AllowCoreRuleName}_Xray");
                DeleteRuleByName($"{AllowCoreRuleName}_SingBox");
                DeleteRuleByName($"{AllowCoreRuleName}_Server");
                DeleteRuleByName("ParsGate_KillSwitch_AllowLoopback");
                DeleteRuleByName("ParsGate_KillSwitch_AllowDHCP");
                DeleteRuleByName("ParsGate_KillSwitch_AllowDNS");
            }
            catch { }
        }

        private static void DeleteRuleByName(string ruleName)
        {
            RunNetsh($"advfirewall firewall delete rule name=\"{ruleName}\"");
        }

        private static void RunNetsh(string arguments)
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
                proc?.WaitForExit(5000);
            }
            catch { }
        }
    }
}