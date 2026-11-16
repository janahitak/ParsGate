using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using ParsGate.Models;
using ParsGate.Services;
using ParsGate.Views;

namespace ParsGate
{
    public partial class SettingsWindow : Window
    {
        private readonly AppSettings _settings;
        private readonly MainWindow _mainWindow;
        private List<ProcessInfo> _processes = new();

        public SettingsWindow(AppSettings settings, MainWindow mainWindow)
        {
            InitializeComponent();
            _settings = settings;
            _mainWindow = mainWindow;
            LoadSettings();
            LoadProcesses();
        }

        private void LoadSettings()
        {
            IranDirectCheck.IsChecked = _settings.IranDirect;
            KillSwitchCheck.IsChecked = _settings.KillSwitchEnabled;
            ForceFragmentCheck.IsChecked = _settings.ForceFragment;
            FragmentLengthBox.Text = _settings.FragmentLength;
            FragmentIntervalBox.Text = _settings.FragmentInterval;
            AutoConnectCheck.IsChecked = _settings.AutoConnect;
            AutoUpdateCheck.IsChecked = _settings.AutoUpdateSubscription;

            RemoteDnsBox.Text = _settings.CustomRemoteDns;
            DirectDnsBox.Text = _settings.CustomDirectDns;

            // Split Tunnel
            SplitTunnelCheck.IsChecked = _settings.SplitTunnelEnabled;

            // Load SplitTunnelMode
            string modeTag = _settings.SplitTunnelMode switch
            {
                SplitTunnelMode.ExcludeSelected => "ExcludeApps",
                SplitTunnelMode.OnlySelected => "OnlySelectedApps",
                _ => "ExcludeApps"
            };

            foreach (ComboBoxItem item in SplitTunnelModeBox.Items)
            {
                if (item.Tag?.ToString() == modeTag)
                {
                    SplitTunnelModeBox.SelectedItem = item;
                    break;
                }
            }

            // Load custom domains/IPs
            foreach (var entry in _settings.SplitTunnelCustomDomainsOrIps)
            {
                CustomDomainIpListBox.Items.Add(entry);
            }
        }

        private async void LoadProcesses()
        {
            try
            {
                _processes = await Task.Run(() =>
                {
                    var list = new List<ProcessInfo>();
                    foreach (var p in Process.GetProcesses())
                    {
                        try
                        {
                            if (!string.IsNullOrEmpty(p.MainWindowTitle) || p.SessionId == Process.GetCurrentProcess().SessionId)
                            {
                                list.Add(new ProcessInfo
                                {
                                    Id = p.Id,
                                    Name = p.ProcessName,
                                    DisplayName = $"{p.ProcessName} (PID: {p.Id})"
                                });
                            }
                        }
                        catch { }
                    }
                    return list.OrderBy(x => x.DisplayName).ToList();
                });

                ProcessListBox.ItemsSource = _processes;
            }
            catch (Exception ex)
            {
                LogError("LoadProcesses", ex);
            }
        }

        private void BtnRefreshProcesses_Click(object sender, RoutedEventArgs e)
        {
            LoadProcesses();
        }

        private void BtnAddSelectedProcesses_Click(object sender, RoutedEventArgs e)
        {
            var selected = ProcessListBox.SelectedItems.Cast<ProcessInfo>().ToList();
            foreach (var p in selected)
            {
                if (!_settings.SplitTunnelProcessNames.Contains(p.Name, StringComparer.OrdinalIgnoreCase))
                {
                    _settings.SplitTunnelProcessNames.Add(p.Name);
                    Announce($"برنامه {p.Name} به لیست اسپلیت تونل اضافه شد");
                }
            }
        }

        private void BtnAddCustomDomainIp_Click(object sender, RoutedEventArgs e)
        {
            var text = CustomDomainIpBox.Text?.Trim();
            if (string.IsNullOrEmpty(text))
                return;

            if (!_settings.SplitTunnelCustomDomainsOrIps.Contains(text, StringComparer.OrdinalIgnoreCase))
            {
                _settings.SplitTunnelCustomDomainsOrIps.Add(text);
                CustomDomainIpListBox.Items.Add(text);
                Announce($"دامنه یا آی‌پی {text} اضافه شد");
            }
            CustomDomainIpBox.Clear();
        }

        private void BtnRemoveCustomDomainIp_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string entry)
            {
                _settings.SplitTunnelCustomDomainsOrIps.Remove(entry);
                CustomDomainIpListBox.Items.Remove(entry);
                Announce($"دامنه یا آی‌پی {entry} حذف شد");
            }
        }

        private async void BtnUpdateIranRules_Click(object sender, RoutedEventArgs e)
        {
            var btn = sender as Button;
            btn.IsEnabled = false;
            btn.Content = "در حال بروزرسانی...";

            try
            {
                var ruleDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "rules");
                Directory.CreateDirectory(ruleDir);

                var client = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
                var geoipUrl = "https://github.com/Chocolate4U/Iran-sing-box-rules/releases/download/2025.09.01/geoip-ir.srs";
                var geositeUrl = "https://github.com/Chocolate4U/Iran-sing-box-rules/releases/download/2025.09.01/geosite-ir.srs";

                await client.DownloadFileTaskAsync(geoipUrl, Path.Combine(ruleDir, "geoip-ir.srs"));
                await client.DownloadFileTaskAsync(geositeUrl, Path.Combine(ruleDir, "geosite-ir.srs"));

                Announce("قوانین ایران با موفقیت بروزرسانی شدند");
            }
            catch (Exception ex)
            {
                Announce($"خطا در بروزرسانی قوانین ایران: {ex.Message}");
                LogError("BtnUpdateIranRules_Click", ex);
            }
            finally
            {
                btn.IsEnabled = true;
                btn.Content = "بروزرسانی قوانین ایران";
            }
        }

        private void BtnExportSettings_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new SaveFileDialog
            {
                Filter = "JSON Files (*.json)|*.json",
                FileName = "ParsGate-settings-backup.json"
            };
            if (dlg.ShowDialog() == true)
            {
                try
                {
                    var json = JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(dlg.FileName, json);
                    Announce("پشتیبان‌گیری تنظیمات با موفقیت انجام شد");
                }
                catch (Exception ex)
                {
                    Announce($"خطا در پشتیبان‌گیری: {ex.Message}");
                    LogError("BtnExportSettings_Click", ex);
                }
            }
        }

        private void BtnImportSettings_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "JSON Files (*.json)|*.json"
            };
            if (dlg.ShowDialog() == true)
            {
                try
                {
                    var json = File.ReadAllText(dlg.FileName);
                    var imported = JsonSerializer.Deserialize<AppSettings>(json);
                    if (imported != null)
                    {
                        // Migrate legacy fields
                        imported.MigrateLegacyFields();

                        // Copy all settings
                        _settings.IranDirect = imported.IranDirect;
                        _settings.KillSwitchEnabled = imported.KillSwitchEnabled;
                        _settings.ForceFragment = imported.ForceFragment;
                        _settings.FragmentLength = imported.FragmentLength;
                        _settings.FragmentInterval = imported.FragmentInterval;
                        _settings.AutoConnect = imported.AutoConnect;
                        _settings.AutoUpdateSubscription = imported.AutoUpdateSubscription;
                        _settings.CustomRemoteDns = imported.CustomRemoteDns;
                        _settings.CustomDirectDns = imported.CustomDirectDns;
                        _settings.SplitTunnelEnabled = imported.SplitTunnelEnabled;
                        _settings.SplitTunnelMode = imported.SplitTunnelMode;
                        _settings.SplitTunnelProcessNames = imported.SplitTunnelProcessNames;
                        _settings.SplitTunnelCustomDomainsOrIps = imported.SplitTunnelCustomDomainsOrIps;
                        _settings.SelectedServerIndex = imported.SelectedServerIndex;

                        LoadSettings();
                        Announce("تنظیمات با موفقیت بازیابی شدند");
                    }
                }
                catch (Exception ex)
                {
                    Announce($"خطا در بازیابی تنظیمات: {ex.Message}");
                    LogError("BtnImportSettings_Click", ex);
                }
            }
        }

        private void BtnSaveClose_Click(object sender, RoutedEventArgs e)
        {
            // Save Split Tunnel settings
            _settings.SplitTunnelEnabled = SplitTunnelCheck.IsChecked == true;

            if (SplitTunnelModeBox.SelectedItem is ComboBoxItem selectedMode)
            {
                _settings.SplitTunnelMode = selectedMode.Tag?.ToString() switch
                {
                    "ExcludeApps" => SplitTunnelMode.ExcludeSelected,
                    "OnlySelectedApps" => SplitTunnelMode.OnlySelected,
                    _ => SplitTunnelMode.ExcludeSelected
                };
            }

            _settings.CustomRemoteDns = RemoteDnsBox.Text?.Trim() ?? "";
            _settings.CustomDirectDns = DirectDnsBox.Text?.Trim() ?? "";
            _settings.FragmentLength = FragmentLengthBox.Text?.Trim() ?? "10-20";
            _settings.FragmentInterval = FragmentIntervalBox.Text?.Trim() ?? "10-20";

            _settings.Save();
            Announce("تنظیمات ذخیره شد");
            Close();
        }

        private void Announce(string message)
        {
            _mainWindow?.AnnounceToLiveRegion(message);
        }

        private void LogError(string context, Exception ex)
        {
            try
            {
                var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ParsGate", "logs");
                Directory.CreateDirectory(logDir);
                var logFile = Path.Combine(logDir, "settings_errors.log");
                File.AppendAllText(logFile, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {context}: {ex}\n");
            }
            catch { }
        }

        public class ProcessInfo
        {
            public int Id { get; set; }
            public string Name { get; set; } = "";
            public string DisplayName { get; set; } = "";
            public bool IsSelected { get; set; }
        }
    }
}