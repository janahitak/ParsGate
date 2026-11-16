using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Tasks;
using ParsGate.Models;
using ParsGate.Services;

namespace ParsGate.Validation;

/// <summary>
/// Console app to generate sample configs and validate them with pinned sing-box and Xray binaries.
/// Run with: dotnet run --project src/ParsGate.Validation/ParsGate.Validation.csproj -c Release
/// </summary>
internal static class Program
{
    private const int XraySocksPort = 10808;

    private static async Task<int> Main(string[] args)
    {
        Console.WriteLine("[ParsGate Config Validation] Starting...");

        var coresDir = Path.GetFullPath("src/ParsGate/publish/cores");
        var rulesDir = Path.GetFullPath("src/ParsGate/publish/rules");

        var xrayExe = Path.Combine(coresDir, "xray.exe");
        var singBoxExe = Path.Combine(coresDir, "sing-box.exe");
        var geoipSrs = Path.Combine(rulesDir, "geoip-ir.srs");
        var geositeSrs = Path.Combine(rulesDir, "geosite-ir.srs");

        if (!File.Exists(xrayExe))
        {
            Console.Error.WriteLine($"ERROR: xray.exe not found at {xrayExe}");
            return 1;
        }
        if (!File.Exists(singBoxExe))
        {
            Console.Error.WriteLine($"ERROR: sing-box.exe not found at {singBoxExe}");
            return 1;
        }
        if (!File.Exists(geoipSrs) || !File.Exists(geositeSrs))
        {
            Console.Error.WriteLine($"ERROR: Iran .srs rule sets not found at {rulesDir}");
            return 1;
        }

        var generator = new SingBoxGenerator();
        var parser = new ConfigParser();

        // Test cases covering all required protocols and combinations
        var testCases = new List<TestCase>
        {
            new("VLESS+Reality", CreateVlessRealityConfig(), true, true),
            new("VLESS+Reality (IranDirect off)", CreateVlessRealityConfig(), false, true),
            new("VLESS+Reality (SplitTunnel on)", CreateVlessRealityConfig(), true, true),
            new("VLESS+TLS+WS", CreateVlessTlsWsConfig(), true, false),
            new("VLESS+TLS+WS (IranDirect off)", CreateVlessTlsWsConfig(), false, false),
            new("VMess+WS", CreateVMessWsConfig(), true, false),
            new("VMess+WS (IranDirect off)", CreateVMessWsConfig(), false, false),
            new("Trojan", CreateTrojanConfig(), true, false),
            new("Trojan (IranDirect off)", CreateTrojanConfig(), false, false),
            new("Shadowsocks 2022-blake3-aes-128-gcm", CreateShadowsocksConfig(), true, false),
            new("Shadowsocks (IranDirect off)", CreateShadowsocksConfig(), false, false),
        };

        int failed = 0;
        foreach (var tc in testCases)
        {
            Console.WriteLine($"\n=== Testing: {tc.Name} ===");
            Console.WriteLine($"  IranDirect={tc.IranDirect}, SplitTunnel={tc.SplitTunnel}");

            try
            {
                var singBoxConfig = generator.Generate(tc.ServerConfig, tc.IranDirect, tc.SplitTunnel);
                var xrayConfig = generator.GenerateXrayConfig(tc.ServerConfig);

                var singBoxPath = Path.GetTempFileName() + ".json";
                var xrayPath = Path.GetTempFileName() + ".json";

                await File.WriteAllTextAsync(singBoxPath, singBoxConfig);
                await File.WriteAllTextAsync(xrayPath, xrayConfig);

                Console.WriteLine($"  Generated sing-box config: {singBoxPath}");
                Console.WriteLine($"  Generated Xray config: {xrayPath}");

                // Validate sing-box config
                if (!RunValidation(singBoxExe, $"check -c \"{singBoxConfig}\"", "sing-box"))
                {
                    Console.Error.WriteLine($"  FAIL: sing-box check failed for {tc.Name}");
                    failed++;
                }
                else
                {
                    Console.WriteLine($"  OK: sing-box check passed");
                }

                // Validate Xray config
                if (!RunValidation(xrayExe, $"run -test -c \"{xrayConfig}\"", "Xray"))
                {
                    Console.Error.WriteLine($"  FAIL: xray -test failed for {tc.Name}");
                    failed++;
                }
                else
                {
                    Console.WriteLine($"  OK: xray -test passed");
                }

                // Cleanup temp files
                try { File.Delete(singBoxPath); } catch { }
                try { File.Delete(xrayPath); } catch { }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  ERROR: {ex.Message}");
                Console.Error.WriteLine(ex.StackTrace);
                failed++;
            }
        }

        Console.WriteLine($"\n=== Validation Summary ===");
        Console.WriteLine($"Total: {testCases.Count}, Failed: {failed}");

        if (failed > 0)
        {
            Console.Error.WriteLine("VALIDATION FAILED");
            return 1;
        }

        Console.WriteLine("ALL VALIDATIONS PASSED");
        return 0;
    }

    private static bool RunValidation(string exePath, string args, string name)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8
            };

            using var proc = Process.Start(psi);
            if (proc == null) return false;

            var output = proc.StandardOutput.ReadToEnd();
            var error = proc.StandardError.ReadToEnd();
            proc.WaitForExit(30000);

            if (proc.ExitCode != 0)
            {
                Console.Error.WriteLine($"  {name} exit code: {proc.ExitCode}");
                if (!string.IsNullOrWhiteSpace(output)) Console.Error.WriteLine($"  stdout: {output}");
                if (!string.IsNullOrWhiteSpace(error)) Console.Error.WriteLine($"  stderr: {error}");
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  {name} validation exception: {ex.Message}");
            return false;
        }
    }

    private static VpnConfigItem CreateVlessRealityConfig() => new VpnConfigItem
    {
        Name = "Test Reality",
        Protocol = "vless",
        Address = "test.example.com",
        Port = 443,
        Uuid = "00000000-0000-0000-0000-000000000000",
        Security = "reality",
        Sni = "test.example.com",
        Fingerprint = "chrome",
        PublicKey = "AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA=",
        ShortId = "0123456789abcdef",
        SpiderX = "/",
        Flow = "xtls-rprx-vision",
        Type = "tcp",
        Encryption = "none",
        // Remarks = "Test"
    };

    private static VpnConfigItem CreateVlessTlsWsConfig() => new VpnConfigItem
    {
        Name = "Test VLESS TLS WS",
        Protocol = "vless",
        Address = "test.example.com",
        Port = 443,
        Uuid = "00000000-0000-0000-0000-000000000000",
        Security = "tls",
        Sni = "test.example.com",
        Fingerprint = "chrome",
        Alpn = "h2,http/1.1",
        Type = "ws",
        Path = "/ws",
        Host = "test.example.com",
        Encryption = "none",
        Flow = "xtls-rprx-vision"
    };

    private static VpnConfigItem CreateVMessWsConfig() => new VpnConfigItem
    {
        Name = "Test VMess WS",
        Protocol = "vmess",
        Address = "test.example.com",
        Port = 443,
        Uuid = "00000000-0000-0000-0000-000000000000",
        Security = "tls",
        Sni = "test.example.com",
        Fingerprint = "chrome",
        Alpn = "h2,http/1.1",
        Type = "ws",
        Path = "/vmess",
        Host = "test.example.com"
    };

    private static VpnConfigItem CreateTrojanConfig() => new VpnConfigItem
    {
        Name = "Test Trojan",
        Protocol = "trojan",
        Address = "test.example.com",
        Port = 443,
        Password = "trojan-password",
        Security = "tls",
        Sni = "test.example.com",
        Fingerprint = "chrome",
        Alpn = "h2,http/1.1",
        Type = "tcp"
    };

    private static VpnConfigItem CreateShadowsocksConfig() => new VpnConfigItem
    {
        Name = "Test Shadowsocks",
        Protocol = "shadowsocks",
        Address = "test.example.com",
        Port = 443,
        Password = "base64encodedpassword==",
        Security = "none",
        ShadowsocksMethod = "2022-blake3-aes-128-gcm"
    };

    private sealed class TestCase(string name, VpnConfigItem config, bool iranDirect, bool splitTunnel)
    {
        public string Name { get; } = name;
        public VpnConfigItem ServerConfig { get; } = config;
        public bool IranDirect { get; } = iranDirect;
        public bool SplitTunnel { get; } = splitTunnel;
    }
}