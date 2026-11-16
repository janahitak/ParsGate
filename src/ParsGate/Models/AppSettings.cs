using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ParsGate.Models
{
    public enum SplitTunnelMode
    {
        ExcludeSelected,  // Selected apps/domains go direct, rest through proxy
        OnlySelected      // Only selected apps/domains go through proxy, rest direct
    }

    public class VpnConfigItem
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = string.Empty;
        public string RawUri { get; set; } = string.Empty;
        public string Protocol { get; set; } = string.Empty; // vless, vmess, trojan, shadowsocks
        public string ServerAddress { get; set; } = string.Empty;
        public int Port { get; set; }
        public long LastPingMs { get; set; } = -1;

        // Shadowsocks specific
        public string? ShadowsocksMethod { get; set; }
        public string? ShadowsocksPassword { get; set; }

        public string DisplayText => $"{Name} - پینگ: {(LastPingMs >= 0 ? LastPingMs + " ms" : "نامشخص")}";
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
        public SplitTunnelMode SplitTunnelMode { get; set; } = SplitTunnelMode.ExcludeSelected;
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
        public string Version { get; set; } = "0.8.0";
    }

    public static class SettingsManager
    {
        private static readonly string SettingsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "ParsGate");
        private static readonly string SettingsPath = Path.Combine(SettingsDirectory, "settings.json");
        private static readonly string OldSettingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json");

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter() }
        };

        public static AppSettings Load()
        {
            // 1. Try new location first
            if (File.Exists(SettingsPath))
            {
                try
                {
                    var json = File.ReadAllText(SettingsPath);
                    var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                    if (settings != null)
                    {
                        return EnsureDefaults(settings);
                    }
                }
                catch (Exception ex)
                {
                    // Corrupt file - back it up and continue to old location or defaults
                    BackupCorruptFile(SettingsPath);
                }
            }

            // 2. Try old location (migrate)
            if (File.Exists(OldSettingsPath))
            {
                try
                {
                    var json = File.ReadAllText(OldSettingsPath);
                    var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                    if (settings != null)
                    {
                        // Migrate to new location
                        Save(settings);
                        // Optionally delete old file after successful migration
                        try { File.Delete(OldSettingsPath); } catch { }
                        return EnsureDefaults(settings);
                    }
                }
                catch
                {
                    // Corrupt old file - back it up
                    BackupCorruptFile(OldSettingsPath);
                }
            }

            // 3. Return defaults
            return new AppSettings();
        }

        private static AppSettings EnsureDefaults(AppSettings settings)
        {
            settings.Configs ??= new List<VpnConfigItem>();
            settings.SubscriptionUrls ??= new List<string>();
            settings.SplitTunnelProcessNames ??= new List<string> { "chrome.exe" };
            settings.SplitTunnelCustomDomainsOrIps ??= new List<string>();
            settings.SplitTunnelMode = settings.SplitTunnelMode == SplitTunnelMode.ExcludeSelected || settings.SplitTunnelMode == SplitTunnelMode.OnlySelected
                ? settings.SplitTunnelMode
                : SplitTunnelMode.ExcludeSelected;
            return settings;
        }

        private static void BackupCorruptFile(string path)
        {
            try
            {
                var backupPath = path + ".bad";
                if (File.Exists(backupPath)) File.Delete(backupPath);
                File.Move(path, backupPath);
            }
            catch { }
        }

        public static void Save(AppSettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));

            settings.LastUpdated = DateTime.UtcNow;
            settings.Version = "0.8.0";

            // Atomic write: write to temp file, then replace
            Directory.CreateDirectory(SettingsDirectory);
            var tempPath = SettingsPath + ".tmp";
            var json = JsonSerializer.Serialize(settings, JsonOptions);
            File.WriteAllText(tempPath, json);

            // Replace atomically
            if (File.Exists(SettingsPath))
            {
                File.Replace(tempPath, SettingsPath, null);
            }
            else
            {
                File.Move(tempPath, SettingsPath);
            }
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

            return EnsureDefaults(settings);
        }

        public static string GetSettingsPath() => SettingsPath;
    }
}