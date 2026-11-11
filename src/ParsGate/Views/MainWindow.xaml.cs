using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using ParsGate.Models;
using ParsGate.Services;

namespace ParsGate.Views
{
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        private AppSettings _settings;
        private ObservableCollection<VpnConfigItem> _configs = new();

        public ObservableCollection<VpnConfigItem> Configs
        {
            get => _configs;
            set { _configs = value; OnPropertyChanged(); }
        }

        public MainWindow()
        {
            InitializeComponent();
            DataContext = this;
            LoadSettings();
            LoadConfigs();
        }

        private void LoadSettings()
        {
            _settings = SettingsManager.Load();

            // Apply settings to UI
            IranDirectCheck.IsChecked = _settings.IranDirectEnabled;
            SplitTunnelCheck.IsChecked = _settings.SplitTunnelEnabled;
            SplitTunnelModeBox.SelectedIndex = _settings.SplitTunnelMode == "OnlySelectedApps" ? 1 : 0;
            SplitTunnelProcessesBox.Text = string.Join(", ", _settings.SplitTunnelProcessNames);
            SplitTunnelDomainsBox.Text = string.Join(", ", _settings.SplitTunnelCustomDomainsOrIps);
            KillSwitchCheck.IsChecked = _settings.KillSwitchEnabled;
            ForceFragmentCheck.IsChecked = _settings.ForceFragmentEnabled;
            FragmentLengthBox.Text = _settings.FragmentLength;
            FragmentIntervalBox.Text = _settings.FragmentInterval;
            AutoConnectCheck.IsChecked = _settings.AutoConnectOnStart;
            AutoUpdateCheck.IsChecked = _settings.AutoUpdateSubsOnStart;
            RemoteDnsBox.Text = _settings.CustomRemoteDns;
            DirectDnsBox.Text = _settings.CustomDirectDns;

            SubscriptionUrlBox.Text = _settings.SubscriptionUrls.FirstOrDefault() ?? "";
        }

        private void LoadConfigs()
        {
            Configs.Clear();
            foreach (var config in _settings.Configs)
            {
                Configs.Add(config);
            }
            ConfigListView.ItemsSource = Configs;
        }

        private void AddSubscription_Click(object sender, RoutedEventArgs e)
        {
            var url = SubscriptionUrlBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(url))
            {
                MessageBox.Show("Please enter a subscription URL", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!_settings.SubscriptionUrls.Contains(url))
            {
                _settings.SubscriptionUrls.Add(url);
                SettingsManager.Save(_settings);
                Log($"Added subscription: {url}");
            }
        }

        private async void UpdateSubscriptions_Click(object sender, RoutedEventArgs e)
        {
            Log("Updating subscriptions...");
            try
            {
                var count = await ConfigParser.FetchAndUpdateSubscriptions(_settings);
                LoadConfigs();
                Log($"Updated: {count} configs loaded");
            }
            catch (Exception ex)
            {
                Log($"Update failed: {ex.Message}");
                MessageBox.Show($"Failed to update subscriptions: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Connect_Click(object sender, RoutedEventArgs e)
        {
            if (ConfigListView.SelectedItem is VpnConfigItem selected)
            {
                _settings.SelectedConfigId = selected.Id;
                SettingsManager.Save(_settings);

                // Generate configs
                var xrayConfig = ConfigParser.GenerateXrayConfigJson(selected, _settings);
                var singBoxConfig = SingBoxGenerator.GenerateSingBoxConfigJson(_settings);

                // Save to files for external launchers
                File.WriteAllText(Path.Combine(App.BaseDirectory, "xray-config.json"), xrayConfig);
                File.WriteAllText(Path.Combine(App.BaseDirectory, "sing-box-config.json"), singBoxConfig);

                Log($"Generated configs for: {selected.Name}");
                Log($"Xray config saved to xray-config.json");
                Log($"Sing-box config saved to sing-box-config.json");

                ConnectionStatus.Text = "Config Generated";
                ConnectionStatus.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 255, 0));
            }
            else
            {
                MessageBox.Show("Please select a configuration first", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private async void TestPing_Click(object sender, RoutedEventArgs e)
        {
            if (ConfigListView.SelectedItem is VpnConfigItem selected)
            {
                Log($"Testing ping to {selected.ServerAddress}:{selected.Port}...");
                // Simple TCP ping
                try
                {
                    using var client = new System.Net.Sockets.TcpClient();
                    var start = DateTime.UtcNow;
                    await client.ConnectAsync(selected.ServerAddress, selected.Port);
                    var ping = (long)(DateTime.UtcNow - start).TotalMilliseconds;
                    selected.LastPingMs = ping;
                    ConfigListView.Items.Refresh();
                    Log($"Ping: {ping} ms");
                }
                catch (Exception ex)
                {
                    selected.LastPingMs = -1;
                    ConfigListView.Items.Refresh();
                    Log($"Ping failed: {ex.Message}");
                }
            }
        }

        private void RemoveConfig_Click(object sender, RoutedEventArgs e)
        {
            if (ConfigListView.SelectedItem is VpnConfigItem selected)
            {
                _settings.Configs.RemoveAll(c => c.Id == selected.Id);
                Configs.Remove(selected);
                SettingsManager.Save(_settings);
                Log($"Removed: {selected.Name}");
            }
        }

        private void SaveSettings_Click(object sender, RoutedEventArgs e)
        {
            _settings.IranDirectEnabled = IranDirectCheck.IsChecked == true;
            _settings.SplitTunnelEnabled = SplitTunnelCheck.IsChecked == true;
            _settings.SplitTunnelMode = SplitTunnelModeBox.SelectedIndex == 1 ? "OnlySelectedApps" : "ExcludeApps";
            _settings.SplitTunnelProcessNames = SplitTunnelProcessesBox.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            _settings.SplitTunnelCustomDomainsOrIps = SplitTunnelDomainsBox.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            _settings.KillSwitchEnabled = KillSwitchCheck.IsChecked == true;
            _settings.ForceFragmentEnabled = ForceFragmentCheck.IsChecked == true;
            _settings.FragmentLength = FragmentLengthBox.Text;
            _settings.FragmentInterval = FragmentIntervalBox.Text;
            _settings.AutoConnectOnStart = AutoConnectCheck.IsChecked == true;
            _settings.AutoUpdateSubsOnStart = AutoUpdateCheck.IsChecked == true;
            _settings.CustomRemoteDns = RemoteDnsBox.Text;
            _settings.CustomDirectDns = DirectDnsBox.Text;

            SettingsManager.Save(_settings);
            Log("Settings saved");
            MessageBox.Show("Settings saved successfully", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void ExportSettings_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new SaveFileDialog
            {
                Filter = "JSON Files (*.json)|*.json",
                FileName = $"ParsGate-Backup-{DateTime.Now:yyyyMMdd-HHmmss}.json"
            };
            if (dlg.ShowDialog() == true)
            {
                SettingsManager.ExportBackup(_settings, dlg.FileName);
                Log($"Settings exported to: {dlg.FileName}");
                MessageBox.Show("Settings exported successfully", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void ImportSettings_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "JSON Files (*.json)|*.json"
            };
            if (dlg.ShowDialog() == true)
            {
                try
                {
                    _settings = SettingsManager.ImportBackup(dlg.FileName);
                    SettingsManager.Save(_settings);
                    LoadSettings();
                    LoadConfigs();
                    Log($"Settings imported from: {dlg.FileName}");
                    MessageBox.Show("Settings imported successfully", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    Log($"Import failed: {ex.Message}");
                    MessageBox.Show($"Failed to import: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void Log(string message)
        {
            var timestamp = DateTime.Now.ToString("HH:mm:ss");
            var logEntry = $"[{timestamp}] {message}\n";
            Dispatcher.Invoke(() =>
            {
                LogBox.AppendText(logEntry);
                LogBox.ScrollToEnd();
            });
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}