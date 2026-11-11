using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using ParsGate.Models;
using ParsGate.Services;

namespace ParsGate.Views
{
    public partial class MainWindow : Window
    {
        private AppSettings _settings;
        private readonly ObservableCollection<VpnConfigItem> _configs = new();
        private VpnConfigItem? _selectedConfig;
        private bool _isConnecting = false;
        
        // System Tray
        private System.Windows.Forms.NotifyIcon? _notifyIcon;
        private bool _isMinimizedToTray = false;

        // Global Hotkey
        private const int HOTKEY_ID = 9000;
        private const uint MOD_CONTROL = 0x0002;
        private const uint MOD_SHIFT = 0x0004;
        private const uint VK_V = 0x56;
        private HwndSource? _hwndSource;

        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        public MainWindow()
        {
            InitializeComponent();
            DataContext = this;
            LoadSettings();
            RefreshConfigComboBox();
            CoreManager.StatusChanged += OnCoreStatusChanged;
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            // Register global hotkey Ctrl+Shift+V
            var helper = new WindowInteropHelper(this);
            _hwndSource = HwndSource.FromHwnd(helper.Handle);
            _hwndSource?.AddHook(WndProc);
            RegisterHotKey(helper.Handle, HOTKEY_ID, MOD_CONTROL | MOD_SHIFT, VK_V);

            // Setup system tray
            SetupTrayIcon();

            // Focus the connect button
            BtnConnectDisconnect.Focus();

            // Auto-connect if enabled
            if (_settings.AutoConnectOnStart && _settings.SelectedConfigId != null)
            {
                var config = _configs.FirstOrDefault(c => c.Id == _settings.SelectedConfigId);
                if (config != null)
                {
                    _ = ConnectAsync(config);
                }
            }

            // Auto-update subscriptions if enabled
            if (_settings.AutoUpdateSubsOnStart && _settings.SubscriptionUrls.Count > 0)
            {
                _ = UpdateSubscriptionsAsync();
            }

            UpdateLiveStatus("ParsGate آماده است. Space برای اتصال، Ctrl+V برای ورود کانفیگ از کلیپ‌بورد، F5 برای تست پینگ.");
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (!_isMinimizedToTray)
            {
                e.Cancel = true;
                HideToTray();
            }
        }

        private void Window_StateChanged(object sender, EventArgs e)
        {
            if (WindowState == WindowState.Minimized)
            {
                HideToTray();
            }
        }

        private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == Key.Space && Keyboard.FocusedElement == BtnConnectDisconnect)
            {
                BtnConnectDisconnect_Click(sender, new RoutedEventArgs());
                e.Handled = true;
            }
            else if (e.Key == Key.V && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                BtnAddFromClipboard_Click(sender, new RoutedEventArgs());
                e.Handled = true;
            }
            else if (e.Key == Key.F5)
            {
                BtnTestPing_Click(sender, new RoutedEventArgs());
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && IsConnected)
            {
                BtnConnectDisconnect_Click(sender, new RoutedEventArgs());
                e.Handled = true;
            }
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_HOTKEY = 0x0312;
            if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID)
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    BtnConnectDisconnect_Click(this, new RoutedEventArgs());
                }));
                handled = true;
            }
            return IntPtr.Zero;
        }

        private void SetupTrayIcon()
        {
            try
            {
                // Load icon from application resources (app.ico)
                System.Drawing.Icon appIcon;
                using (var stream = System.Windows.Application.GetResourceStream(
                    new Uri("pack://application:,,,/Assets/app.ico"))?.Stream)
                {
                    if (stream != null)
                    {
                        appIcon = new System.Drawing.Icon(stream);
                    }
                    else
                    {
                        appIcon = System.Drawing.SystemIcons.Application;
                    }
                }

                _notifyIcon = new System.Windows.Forms.NotifyIcon
                {
                    Icon = appIcon,
                    Text = "ParsGate v0.1",
                    Visible = true
                };

                var menu = new System.Windows.Forms.ContextMenuStrip();
                menu.Items.Add("باز کردن ParsGate", null, (s, e) => ShowFromTray());
                menu.Items.Add("خروج کامل", null, (s, e) => ExitApplication());
                _notifyIcon.ContextMenuStrip = menu;
                _notifyIcon.DoubleClick += (s, e) => ShowFromTray();
            }
            catch { }
        }

        private void HideToTray()
        {
            Hide();
            _isMinimizedToTray = true;
            if (_notifyIcon != null)
            {
                _notifyIcon.ShowBalloonTip(1000, "ParsGate", "برنامه در نوار وظیفه در حال اجراست", System.Windows.Forms.ToolTipIcon.Info);
            }
        }

        private void ShowFromTray()
        {
            Show();
            WindowState = WindowState.Normal;
            _isMinimizedToTray = false;
            Activate();
            BtnConnectDisconnect.Focus();
        }

        private void ExitApplication()
        {
            _isMinimizedToTray = false;
            CoreManager.StatusChanged -= OnCoreStatusChanged;
            _hwndSource?.RemoveHook(WndProc);
            var helper = new WindowInteropHelper(this);
            UnregisterHotKey(helper.Handle, HOTKEY_ID);
            _notifyIcon?.Dispose();
            CoreManager.DisconnectAsync().Wait(3000);
            Application.Current.Shutdown();
        }

        private void LoadSettings()
        {
            _settings = SettingsManager.Load();
        }

        private void RefreshConfigComboBox()
        {
            ConfigComboBox.ItemsSource = null;
            ConfigComboBox.ItemsSource = _configs;
            
            if (_settings.SelectedConfigId != null)
            {
                _selectedConfig = _configs.FirstOrDefault(c => c.Id == _settings.SelectedConfigId);
                ConfigComboBox.SelectedItem = _selectedConfig;
            }
            else if (_configs.Count > 0)
            {
                ConfigComboBox.SelectedIndex = 0;
            }
        }

        private void ConfigComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ConfigComboBox.SelectedItem is VpnConfigItem config)
            {
                _selectedConfig = config;
                _settings.SelectedConfigId = config.Id;
                SettingsManager.Save(_settings);
                UpdateLiveStatus($"کانفیگ انتخاب شد: {config.Name} - پینگ: {(config.LastPingMs >= 0 ? config.LastPingMs + " ms" : "نامشخص")}");
            }
        }

        private void BtnAddFromClipboard_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var clipboardText = Clipboard.GetText(TextDataFormat.Text);
                if (string.IsNullOrWhiteSpace(clipboardText))
                {
                    UpdateLiveStatus("کلیپ‌بورد خالی است یا حاوی متن نیست");
                    AudioAndSpeechService.PlayErrorTone();
                    return;
                }

                var lines = clipboardText.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                var added = 0;
                var errors = 0;

                foreach (var line in lines)
                {
                    var trimmed = line.Trim();
                    if (string.IsNullOrWhiteSpace(trimmed)) continue;

                    try
                    {
                        var config = ConfigParser.ParseUri(trimmed);
                        if (!_configs.Any(c => c.RawUri == config.RawUri))
                        {
                            _configs.Add(config);
                            _settings.Configs.Add(config);
                            added++;
                        }
                    }
                    catch
                    {
                        errors++;
                    }
                }

                if (added > 0)
                {
                    SettingsManager.Save(_settings);
                    RefreshConfigComboBox();
                    UpdateLiveStatus($"{added} کانفیگ از کلیپ‌بورد اضافه شد");
                    AudioAndSpeechService.PlayConnectTone();
                }
                else if (errors > 0)
                {
                    UpdateLiveStatus($"هیچ کانفیگ معتبری یافت نشد ({errors} خطا)");
                    AudioAndSpeechService.PlayErrorTone();
                }
                else
                {
                    UpdateLiveStatus("کانفیگ‌ها تکراری بودند و اضافه نشدند");
                }
            }
            catch (Exception ex)
            {
                UpdateLiveStatus($"خطای کلیپ‌بورد: {ex.Message}");
                AudioAndSpeechService.PlayErrorTone();
            }
        }

        private void BtnOpenSettings_Click(object sender, RoutedEventArgs e)
        {
            var settingsWindow = new SettingsWindow(_settings);
            settingsWindow.Owner = this;
            settingsWindow.SettingsSaved += OnSettingsSaved;
            settingsWindow.ShowDialog();
        }

        private void OnSettingsSaved(AppSettings newSettings)
        {
            _settings = newSettings;
            LoadSettings();
            UpdateLiveStatus("تنظیمات ذخیره شد");
        }

        private void BtnManageSubscriptions_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new SubscriptionDialog(_settings.SubscriptionUrls);
            dialog.Owner = this;
            if (dialog.ShowDialog() == true)
            {
                _settings.SubscriptionUrls = dialog.Urls;
                SettingsManager.Save(_settings);
                UpdateLiveStatus($"لینک‌های اشتراک به‌روزرسانی شد ({dialog.Urls.Count} لینک)");
            }
        }

        private async void BtnConnectDisconnect_Click(object sender, RoutedEventArgs e)
        {
            if (CoreManager.IsConnected)
            {
                await DisconnectAsync();
            }
            else
            {
                if (_selectedConfig == null)
                {
                    UpdateLiveStatus("هیچ کانفیگی انتخاب نشده. ابتدا کانفیگ اضافه کنید یا از لیست انتخاب کنید");
                    AudioAndSpeechService.PlayErrorTone();
                    return;
                }
                await ConnectAsync(_selectedConfig);
            }
        }

        private async Task ConnectAsync(VpnConfigItem config)
        {
            if (_isConnecting) return;
            _isConnecting = true;

            SetConnectingState(true);
            UpdateLiveStatus($"در حال اتصال به {config.Name}...");

            var success = await CoreManager.ConnectAsync(config, _settings);

            if (success)
            {
                SetConnectedState(true);
                UpdateLiveStatus($"متصل شد: {config.Name} | Iran Direct: {(_settings.IranDirectEnabled ? "فعال" : "غیرفعال")} | Split Tunnel: {(_settings.SplitTunnelEnabled ? "فعال" : "غیرفعال")} | Kill Switch: {(_settings.KillSwitchEnabled ? "فعال" : "غیرفعال")}");
            }
            else
            {
                SetConnectedState(false);
            }

            _isConnecting = false;
        }

        private async Task DisconnectAsync()
        {
            SetConnectingState(false);
            UpdateLiveStatus("در حال قطع اتصال...");
            await CoreManager.DisconnectAsync();
            SetConnectedState(false);
            UpdateLiveStatus("قطع اتصال شد");
        }

        private void SetConnectingState(bool connecting)
        {
            if (connecting)
            {
                BtnConnectDisconnect.Content = "در حال اتصال...";
                BtnConnectDisconnect.Background = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x00));
                BtnConnectDisconnect.BorderBrush = new SolidColorBrush(Colors.Yellow);
                BtnConnectDisconnect.Tag = "Connecting";
            }
            else if (!CoreManager.IsConnected)
            {
                SetConnectedState(false);
            }
        }

        private void SetConnectedState(bool connected)
        {
            if (connected)
            {
                BtnConnectDisconnect.Content = "قطع اتصال";
                BtnConnectDisconnect.Background = new SolidColorBrush(Color.FromRgb(0x55, 0x00, 0x00));
                BtnConnectDisconnect.BorderBrush = new SolidColorBrush(Colors.Red);
                BtnConnectDisconnect.Tag = "Connected";
            }
            else
            {
                BtnConnectDisconnect.Content = "اتصال";
                BtnConnectDisconnect.Background = new SolidColorBrush(Color.FromRgb(0x00, 0x44, 0x00));
                BtnConnectDisconnect.BorderBrush = new SolidColorBrush(Color.FromRgb(0x00, 0xFF, 0x00));
                BtnConnectDisconnect.Tag = "Disconnected";
            }
        }

        private void OnCoreStatusChanged(string status)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                UpdateLiveStatus(status);
            }));
        }

        private async void BtnTestPing_Click(object sender, RoutedEventArgs e)
        {
            if (_configs.Count == 0)
            {
                UpdateLiveStatus("هیچ کانفیگی برای تست پینگ وجود ندارد");
                AudioAndSpeechService.PlayErrorTone();
                return;
            }

            UpdateLiveStatus("شروع تست پینگ واقعی برای همه کانفیگ‌ها...");
            BtnTestPing.IsEnabled = false;
            BtnTestPing.Content = "در حال تست...";

            var progress = new Progress<string>(msg => 
            {
                Dispatcher.BeginInvoke(new Action(() => UpdateLiveStatus(msg)));
            });

            await CoreManager.TestAllRealPingsAsync(_configs.ToList(), _settings, progress);
            RefreshConfigComboBox();
            ConfigComboBox.Items.Refresh();

            BtnTestPing.IsEnabled = true;
            BtnTestPing.Content = "تست پینگ واقعی";
            UpdateLiveStatus("تست پینگ تمام شد");
            AudioAndSpeechService.PlayConnectTone();
        }

        private void BtnSortByPing_Click(object sender, RoutedEventArgs e)
        {
            var sorted = _configs
                .Where(c => c.LastPingMs >= 0)
                .OrderBy(c => c.LastPingMs)
                .Concat(_configs.Where(c => c.LastPingMs < 0))
                .ToList();

            _configs.Clear();
            foreach (var c in sorted) _configs.Add(c);
            
            _settings.Configs = sorted;
            SettingsManager.Save(_settings);
            RefreshConfigComboBox();
            UpdateLiveStatus("کانفیگ‌ها بر اساس بهترین پینگ مرتب شدند");
        }

        private void BtnCleanupConfigs_Click(object sender, RoutedEventArgs e)
        {
            var before = _configs.Count;
            
            // Remove duplicates by URI
            var unique = _configs.GroupBy(c => c.RawUri).Select(g => g.First()).ToList();
            
            // Remove failed pings
            var valid = unique.Where(c => c.LastPingMs >= 0).ToList();
            
            _configs.Clear();
            foreach (var c in valid) _configs.Add(c);
            
            _settings.Configs = valid;
            SettingsManager.Save(_settings);
            RefreshConfigComboBox();
            
            var removed = before - valid.Count;
            UpdateLiveStatus($"{removed} کانفیگ تکراری یا با پینگ منفی حذف شد. باقی‌مانده: {valid.Count}");
        }

        private async Task UpdateSubscriptionsAsync()
        {
            try
            {
                UpdateLiveStatus("بروزرسانی اشتراک‌ها...");
                var count = await ConfigParser.FetchAndUpdateSubscriptions(_settings);
                RefreshConfigComboBox();
                UpdateLiveStatus($"{count} کانفیگ از اشتراک‌ها بارگذاری شد");
            }
            catch (Exception ex)
            {
                UpdateLiveStatus($"خطای بروزرسانی اشتراک: {ex.Message}");
            }
        }

        private void UpdateLiveStatus(string message)
        {
            LiveStatusText.Text = message;
            var peer = System.Windows.Automation.Peers.UIElementAutomationPeer.FromElement(LiveStatusText);
            peer?.RaiseAutomationEvent(System.Windows.Automation.Peers.AutomationEvents.LiveRegionChanged);
        }

        public bool IsConnected => CoreManager.IsConnected;
    }

    // Subscription Dialog
    public class SubscriptionDialog : Window
    {
        public List<string> Urls { get; private set; }
        private System.Windows.Controls.TextBox _textBox;

        public SubscriptionDialog(List<string> currentUrls)
        {
            Urls = new List<string>(currentUrls);
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            Title = "مدیریت لینک‌های اشتراک";
            Width = 600;
            Height = 400;
            Background = new SolidColorBrush(Colors.Black);
            Foreground = new SolidColorBrush(Colors.White);
            FontFamily = new FontFamily("pack://application:,,,/Assets/Fonts/#Vazirmatn Bold, Tahoma");
            FontSize = 18;
            FlowDirection = FlowDirection.RightToLeft;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;

            var grid = new Grid { Margin = new Thickness(16) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var label = new TextBlock
            {
                Text = "لینک‌های اشتراک (هر خط یک لینک):",
                FontSize = 18,
                Foreground = new SolidColorBrush(Colors.White),
                Margin = new Thickness(0, 0, 0, 8)
            };
            Grid.SetRow(label, 0);
            grid.Children.Add(label);

            _textBox = new TextBox
            {
                Text = string.Join("\n", Urls),
                Background = new SolidColorBrush(Colors.Black),
                Foreground = new SolidColorBrush(Colors.White),
                BorderBrush = new SolidColorBrush(Colors.White),
                BorderThickness = new Thickness(3),
                FontSize = 16,
                FontFamily = new FontFamily("Consolas, Tahoma"),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Padding = new Thickness(8),
                AcceptsReturn = true
            };
            Grid.SetRow(_textBox, 1);
            grid.Children.Add(_textBox);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 16, 0, 0)
            };

            var btnOk = new Button
            {
                Content = "ذخیره",
                Width = 120,
                Height = 44,
                Background = new SolidColorBrush(Color.FromRgb(0x00, 0x44, 0x00)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x00, 0xFF, 0x00)),
                BorderThickness = new Thickness(3),
                Foreground = new SolidColorBrush(Colors.White),
                FontSize = 18,
                FontFamily = new FontFamily("pack://application:,,,/Assets/Fonts/#Vazirmatn Bold, Tahoma")
            };
            btnOk.Click += (s, e) =>
            {
                Urls = _textBox.Text.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(u => u.Trim())
                    .Where(u => !string.IsNullOrWhiteSpace(u))
                    .ToList();
                DialogResult = true;
                Close();
            };
            buttons.Children.Add(btnOk);

            var btnCancel = new Button
            {
                Content = "انصراف",
                Width = 120,
                Height = 44,
                Margin = new Thickness(8, 0, 0, 0),
                Background = new SolidColorBrush(Color.FromRgb(0x55, 0x00, 0x00)),
                BorderBrush = new SolidColorBrush(Colors.Red),
                BorderThickness = new Thickness(3),
                Foreground = new SolidColorBrush(Colors.White),
                FontSize = 18,
                FontFamily = new FontFamily("pack://application:,,,/Assets/Fonts/#Vazirmatn Bold, Tahoma")
            };
            btnCancel.Click += (s, e) => { DialogResult = false; Close(); };
            buttons.Children.Add(btnCancel);

            Grid.SetRow(buttons, 2);
            grid.Children.Add(buttons);

            Content = grid;
        }
    }
}