using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ParsGate.Models
{
    public class VpnConfigItem
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = string.Empty;
        public string RawUri { get; set; } = string.Empty;
        public string Protocol { get; set; } = string.Empty; // vless, vmess, trojan
        public string ServerAddress { get; set; } = string.Empty;
        public int Port { get; set; }
        public long LastPingMs { get; set; } = -1;
    }

    public class AppSettings
    {
        public List<VpnConfigItem> Configs { get; set; } = new();
        public List<string> SubscriptionUrls { get; set; } = new();
        public string? SelectedConfigId { get; set; }

        // Iran Direct Rules - MUST be ON by default
        public bool IranDirectEnabled { get; set; } = true;

        // Split Tunneling - OFF by default
        public bool SplitTunnelEnabled { get; set; } = false;
        public string SplitTunnelMode { get; set; } = "ExcludeApps"; // "OnlySelectedApps" or "ExcludeSelectedApps"
        public List<string> SplitTunnelProcessNames { get; set; } = new() { "chrome.exe" };
        public List<string> SplitTunnelCustomDomainsOrIps { get; set; } = new();

        // Kill Switch - OFF by default
        public bool KillSwitchEnabled { get; set; } = false;

        // Fragment Settings - OFF by default
        public bool ForceFragmentEnabled { get; set; } = false;
        public string FragmentLength { get; set; } = "10-20";
        public string FragmentInterval { get; set; } = "10-20";

        // Auto behaviors
        public bool AutoConnectOnStart { get; set; } = false;
        public bool AutoUpdateSubsOnStart { get; set; } = true;

        // Custom DNS
        public string CustomRemoteDns { get; set; } = "https://1.1.1.1/dns-query";
        public string CustomDirectDns { get; set; } = "1.1.1.1";

        // Metadata
        public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
        public string Version { get; set; } = "0.1.0";
    }

    public static class SettingsManager
    {
        private static readonly string SettingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json");
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    var json = File.ReadAllText(SettingsPath);
                    var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                    if (settings != null)
                    {
                        // Ensure defaults for missing properties
                        settings.Configs ??= new List<VpnConfigItem>();
                        settings.SubscriptionUrls ??= new List<string>();
                        settings.SplitTunnelProcessNames ??= new List<string> { "chrome.exe" };
                        settings.SplitTunnelCustomDomainsOrIps ??= new List<string>();
                        return settings;
                    }
                }
            }
            catch
            {
                // Ignore and return defaults
            }

            return new AppSettings();
        }

        public static void Save(AppSettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            settings.LastUpdated = DateTime.UtcNow;
            var json = JsonSerializer.Serialize(settings, JsonOptions);
            File.WriteAllText(SettingsPath, json);
        }

        public static void ExportBackup(AppSettings settings, string filePath)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (string.IsNullOrWhiteSpace(filePath)) throw new ArgumentException("File path cannot be empty", nameof(filePath));

            settings.LastUpdated = DateTime.UtcNow;
            var json = JsonSerializer.Serialize(settings, JsonOptions);
            File.WriteAllText(filePath, json);
        }

        public static AppSettings ImportBackup(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath)) throw new ArgumentException("File path cannot be empty", nameof(filePath));
            if (!File.Exists(filePath)) throw new FileNotFoundException("Backup file not found", filePath);

            var json = File.ReadAllText(filePath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
            if (settings == null) throw new InvalidOperationException("Failed to deserialize backup file");

            // Ensure defaults for missing properties
            settings.Configs ??= new List<VpnConfigItem>();
            settings.SubscriptionUrls ??= new List<string>();
            settings.SplitTunnelProcessNames ??= new List<string> { "chrome.exe" };
            settings.SplitTunnelCustomDomainsOrIps ??= new List<string>();

            return settings;
        }

        public static string GetSettingsPath() => SettingsPath;
    }
}