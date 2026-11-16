using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using ParsGate.Models;
using ParsGate.Services;

namespace ParsGate
{
    public partial class SettingsWindow : Window
    {
        private AppSettings _settings;
        private readonly MainWindow _mainWindow;

        public SettingsWindow(MainWindow mainWindow, AppSettings settings)
        {
            InitializeComponent();
            _mainWindow = mainWindow;
            _settings = settings;

            // Load settings into UI
            LoadSettingsToUi();
        }

        private void LoadSettingsToUi()
        {
            // Iran Direct
            chkIranDirect.IsChecked = _settings.IranDirectEnabled;

            // Kill Switch
            chkKillSwitch.IsChecked = _settings.KillSwitchEnabled;

            // Split Tunneling
            chkSplitTunnel.IsChecked = _settings.SplitTunnelEnabled;

            // Split Tunnel Mode
            cmbSplitTunnelMode.Items.Clear();
            cmbSplitTunnelMode.Items.Add(new ComboBoxItem { Content = "استثنای انتخاب شده (پیش‌فرض)", Tag = SplitTunnelMode.ExcludeSelected });
            cmbSplitTunnelMode.Items.Add(new ComboBoxItem { Content = "فقط انتخاب شده", Tag = SplitTunnelMode.OnlySelected });

            var selectedMode = _settings.SplitTunnelMode;
            foreach (ComboBoxItem item in cmbSplitTunnelMode.Items)
            {
                if (item.Tag is SplitTunnelMode mode && mode == selectedMode)
                {
                    cmbSplitTunnelMode.SelectedItem = item;
                    break;
                }
            }

            // Process list
            lstSplitTunnelProcesses.ItemsSource = _settings.SplitTunnelProcessNames;

            // Custom domains/IPs
            txtSplitTunnelCustom.Text = string.Join(Environment.NewLine, _settings.SplitTunnelCustomDomainsOrIps);

            // Fragment
            chkForceFragment.IsChecked = _settings.ForceFragmentEnabled;
            txtFragmentLength.Text = _settings.FragmentLength;
            txtFragmentInterval.Text = _settings.FragmentInterval;

            // Auto behaviors
            chkAutoConnect.IsChecked = _settings.AutoConnectOnStart;
            chkAutoUpdateSubs.IsChecked = _settings.AutoUpdateSubsOnStart;

            // Custom DNS
            txtCustomRemoteDns.Text = _settings.CustomRemoteDns;
            txtCustomDirectDns.Text = _settings.CustomDirectDns;
        }

        private void BtnAddProcess_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "انتخاب فایل اجرایی",
                Filter = "Executable Files (*.exe)|*.exe|All Files (*.*)|*.*"
            };
            if (dlg.ShowDialog() == true)
            {
                var processName = Path.GetFileName(dlg.FileName);
                if (!_settings.SplitTunnelProcessNames.Contains(processName, StringComparer.OrdinalIgnoreCase))
                {
                    _settings.SplitTunnelProcessNames.Add(processName);
                    lstSplitTunnelProcesses.Items.Refresh();
                }
            }
        }

        private void BtnRemoveProcess_Click(object sender, RoutedEventArgs e)
        {
            if (lstSplitTunnelProcesses.SelectedItem is string processName)
            {
                _settings.SplitTunnelProcessNames.Remove(processName);
                lstSplitTunnelProcesses.Items.Refresh();
            }
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            // Save from UI to settings
            _settings.IranDirectEnabled = chkIranDirect.IsChecked ?? true;
            _settings.KillSwitchEnabled = chkKillSwitch.IsChecked ?? false;
            _settings.SplitTunnelEnabled = chkSplitTunnel.IsChecked ?? false;

            if (cmbSplitTunnelMode.SelectedItem is ComboBoxItem selectedItem && selectedItem.Tag is SplitTunnelMode mode)
            {
                _settings.SplitTunnelMode = mode;
            }

            _settings.SplitTunnelCustomDomainsOrIps = txtSplitTunnelCustom.Text
                .Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList();

            _settings.ForceFragmentEnabled = chkForceFragment.IsChecked ?? false;
            _settings.FragmentLength = txtFragmentLength.Text;
            _settings.FragmentInterval = txtFragmentInterval.Text;

            _settings.AutoConnectOnStart = chkAutoConnect.IsChecked ?? false;
            _settings.AutoUpdateSubsOnStart = chkAutoUpdateSubs.IsChecked ?? false;

            _settings.CustomRemoteDns = txtCustomRemoteDns.Text.Trim();
            _settings.CustomDirectDns = txtCustomDirectDns.Text.Trim();

            // Save to disk
            SettingsManager.Save(_settings);

            // Apply Kill Switch change immediately if connected
            if (CoreManager.IsConnected)
            {
                if (_settings.KillSwitchEnabled)
                {
                    var currentConfig = CoreManager.CurrentConfig;
                    if (currentConfig != null)
                    {
                        var ips = ResolveServerAddresses(currentConfig.ServerAddress);
                        var result = FirewallManager.EnableKillSwitch(
                            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cores", "xray.exe"),
                            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cores", "sing-box.exe"),
                            ips);
                        if (!result.Success)
                        {
                            _mainWindow.Announce($"Kill Switch فعال نشد: {result.ErrorMessage}");
                        }
                    }
                }
                else
                {
                    FirewallManager.DisableKillSwitch();
                }
            }

            _mainWindow.Announce("تنظیمات ذخیره شد");
            Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void BtnExport_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new SaveFileDialog
            {
                Title = "صادرات تنظیمات",
                Filter = "JSON Files (*.json)|*.json",
                FileName = $"ParsGate-Backup-{DateTime.Now:yyyyMMdd}.json"
            };
            if (dlg.ShowDialog() == true)
            {
                try
                {
                    SettingsManager.ExportBackup(_settings, dlg.FileName);
                    _mainWindow.Announce("تنظیمات صادر شد");
                }
                catch (Exception ex)
                {
                    _mainWindow.Announce($"خطای صادرات: {ex.Message}");
                }
            }
        }

        private void BtnImport_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "ورود تنظیمات",
                Filter = "JSON Files (*.json)|*.json"
            };
            if (dlg.ShowDialog() == true)
            {
                try
                {
                    var imported = SettingsManager.ImportBackup(dlg.FileName);
                    _settings = imported;
                    LoadSettingsToUi();
                    _mainWindow.Announce("تنظیمات وارد شد");
                }
                catch (Exception ex)
                {
                    _mainWindow.Announce($"خطای ورود: {ex.Message}");
                }
            }
        }

        private List<string> ResolveServerAddresses(string serverAddress)
        {
            var ips = new List<string>();
            if (IPAddress.TryParse(serverAddress, out var ip))
            {
                ips.Add(serverAddress);
            }
            else
            {
                try
                {
                    var hostEntry = Dns.GetHostEntry(serverAddress);
                    foreach (var addr in hostEntry.AddressList)
                    {
                        if (addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ||
                            addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
                        {
                            ips.Add(addr.ToString());
                        }
                    }
                }
                catch { }
            }
            return ips.Distinct().ToList();
        }
    }
}