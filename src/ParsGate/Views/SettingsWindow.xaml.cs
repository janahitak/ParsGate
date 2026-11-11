using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using ParsGate.Models;
using ParsGate.Services;

namespace ParsGate.Views
{
    public partial class SettingsWindow : Window
    {
        private AppSettings _settings;
        private readonly ObservableCollection<ProcessItem> _processItems = new();
        private readonly ObservableCollection<string> _customDomainIps = new();

        public event Action<AppSettings>? SettingsSaved;

        public SettingsWindow(AppSettings settings)
        {
            InitializeComponent();
            _settings = settings;
            DataContext = this;
            LoadSettings();
            LoadProcesses();
            LoadCustomDomainIps();
        }

        private void LoadSettings()
        {
            IranDirectCheck.IsChecked = _settings.IranDirectEnabled;
            KillSwitchCheck.IsChecked = _settings.KillSwitchEnabled;
            ForceFragmentCheck.IsChecked = _settings.ForceFragmentEnabled;
            FragmentLengthBox.Text = _settings.FragmentLength;
            FragmentIntervalBox.Text = _settings.FragmentInterval;
            AutoConnectCheck.IsChecked = _settings.AutoConnectOnStart;
            AutoUpdateCheck.IsChecked = _settings.AutoUpdateSubsOnStart;
            RemoteDnsBox.Text = _settings.CustomRemoteDns;
            DirectDnsBox.Text = _settings.CustomDirectDns;
            SplitTunnelCheck.IsChecked = _settings.SplitTunnelEnabled;
            SplitTunnelModeBox.SelectedIndex = _settings.SplitTunnelMode == "OnlySelectedApps" ? 1 : 0;

            _customDomainIps.Clear();
            foreach (var item in _settings.SplitTunnelCustomDomainsOrIps)
            {
                _customDomainIps.Add(item);
            }
            CustomDomainIpListBox.ItemsSource = _customDomainIps;

            _processItems.Clear();
            foreach (var procName in _settings.SplitTunnelProcessNames)
            {
                _processItems.Add(new ProcessItem { DisplayName = procName, IsSelected = true });
            }
            ProcessListBox.ItemsSource = _processItems;
        }

        private void LoadProcesses()
        {
            try
            {
                var processes = Process.GetProcesses()
                    .Where(p => !string.IsNullOrWhiteSpace(p.ProcessName) && p.MainWindowHandle != IntPtr.Zero)
                    .Select(p => p.ProcessName)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(n => n)
                    .ToList();

                _processItems.Clear();
                foreach (var name in processes)
                {
                    var isSelected = _settings.SplitTunnelProcessNames.Contains(name, StringComparer.OrdinalIgnoreCase);
                    _processItems.Add(new ProcessItem { DisplayName = name + ".exe", IsSelected = isSelected });
                }
            }
            catch { }
        }

        private void LoadCustomDomainIps()
        {
            _customDomainIps.Clear();
            foreach (var item in _settings.SplitTunnelCustomDomainsOrIps)
            {
                _customDomainIps.Add(item);
            }
        }

        private void BtnRefreshProcesses_Click(object sender, RoutedEventArgs e)
        {
            LoadProcesses();
            UpdateLiveStatus("لیست برنامه‌ها بروزرسانی شد");
        }

        private void BtnAddSelectedProcesses_Click(object sender, RoutedEventArgs e)
        {
            var selected = _processItems.Where(p => p.IsSelected).Select(p => p.DisplayName).ToList();
            foreach (var name in selected)
            {
                if (!_settings.SplitTunnelProcessNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    _settings.SplitTunnelProcessNames.Add(name);
                }
            }
            UpdateLiveStatus($"{selected.Count} برنامه به لیست اسپلیت تونل اضافه شد");
        }

        private void BtnAddCustomDomainIp_Click(object sender, RoutedEventArgs e)
        {
            var input = CustomDomainIpBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(input))
            {
                MessageBox.Show("لطفاً یک دامنه یا آی‌پی وارد کنید", "خطا", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!_customDomainIps.Contains(input))
            {
                _customDomainIps.Add(input);
                _settings.SplitTunnelCustomDomainsOrIps.Add(input);
                CustomDomainIpBox.Clear();
                UpdateLiveStatus($"دامنه/آی‌پی اضافه شد: {input}");
            }
        }

        private void BtnRemoveCustomDomainIp_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string item)
            {
                _customDomainIps.Remove(item);
                _settings.SplitTunnelCustomDomainsOrIps.Remove(item);
                UpdateLiveStatus($"حذف شد: {item}");
            }
        }

        private void BtnUpdateIranRules_Click(object sender, RoutedEventArgs e)
        {
            // This would download geoip-ir.srs and geosite-ir.srs from chocolate4u/Iran-v2ray-rules
            // For now, just show a message
            MessageBox.Show(
                "این قابلیت در نسخه بعدی پیاده‌سازی می‌شود.\nفایل‌های geoip-ir.srs و geosite-ir.srs باید در پوشه rules قرار گیرند.",
                "بروزرسانی قوانین ایران",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        private void BtnExportSettings_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new SaveFileDialog
            {
                Filter = "JSON Files (*.json)|*.json",
                FileName = $"ParsGate-Backup-{DateTime.Now:yyyyMMdd-HHmmss}.json"
            };
            if (dlg.ShowDialog() == true)
            {
                SettingsManager.ExportBackup(_settings, dlg.FileName);
                UpdateLiveStatus($"تنظیمات در {dlg.FileName} ذخیره شد");
                MessageBox.Show("پشتیبان‌گیری با موفقیت انجام شد", "موفقیت", MessageBoxButton.OK, MessageBoxImage.Information);
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
                    _settings = SettingsManager.ImportBackup(dlg.FileName);
                    SettingsManager.Save(_settings);
                    LoadSettings();
                    UpdateLiveStatus($"تنظیمات از {dlg.FileName} بازیابی شد");
                    MessageBox.Show("بازیابی با موفقیت انجام شد", "موفقیت", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    UpdateLiveStatus($"خطای بازیابی: {ex.Message}");
                    MessageBox.Show($"خطا: {ex.Message}", "خطا", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void BtnSaveClose_Click(object sender, RoutedEventArgs e)
        {
            _settings.IranDirectEnabled = IranDirectCheck.IsChecked == true;
            _settings.KillSwitchEnabled = KillSwitchCheck.IsChecked == true;
            _settings.ForceFragmentEnabled = ForceFragmentCheck.IsChecked == true;
            _settings.FragmentLength = FragmentLengthBox.Text;
            _settings.FragmentInterval = FragmentIntervalBox.Text;
            _settings.AutoConnectOnStart = AutoConnectCheck.IsChecked == true;
            _settings.AutoUpdateSubsOnStart = AutoUpdateCheck.IsChecked == true;
            _settings.CustomRemoteDns = RemoteDnsBox.Text;
            _settings.CustomDirectDns = DirectDnsBox.Text;
            _settings.SplitTunnelEnabled = SplitTunnelCheck.IsChecked == true;
            _settings.SplitTunnelMode = SplitTunnelModeBox.SelectedIndex == 1 ? "OnlySelectedApps" : "ExcludeApps";
            _settings.SplitTunnelProcessNames = _processItems.Where(p => p.IsSelected).Select(p => p.DisplayName).ToList();
            _settings.SplitTunnelCustomDomainsOrIps = _customDomainIps.ToList();

            SettingsManager.Save(_settings);
            SettingsSaved?.Invoke(_settings);
            Close();
        }

        private void UpdateLiveStatus(string message)
        {
            // Find the MainWindow and update its live status
            var mainWindow = Application.Current.Windows.OfType<MainWindow>().FirstOrDefault();
            mainWindow?.GetType().GetMethod("UpdateLiveStatus", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.Invoke(mainWindow, new object[] { message });
        }
    }

    public class ProcessItem : INotifyPropertyChanged
    {
        private string _displayName = string.Empty;
        private bool _isSelected;

        public string DisplayName
        {
            get => _displayName;
            set { _displayName = value; OnPropertyChanged(); }
        }

        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}