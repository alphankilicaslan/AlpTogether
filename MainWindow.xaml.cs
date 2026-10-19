using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using AlpTogether.Models;
using AlpTogether.Services;

namespace AlpTogether
{
    public partial class MainWindow : Window
    {
        private AlpAppItem? _selectedApp;
        private string _activeFilter = "ALL"; // ALL, INSTALLED, UPDATES
        private bool _suppressRadioEvent;
        private readonly DispatcherTimer _processMonitorTimer;
        private int _logoClickCount = 0;
        private DateTime _lastLogoClickTime = DateTime.MinValue;
        private List<TelemetryEventRecord> _allDevEvents = new();
        private string _devEventFilter = "ALL";
        private List<AlpTogetherUserRecord> _allAdminUsers = new();
        private AlpTogetherUserRecord? _selectedAdminUser;

        public MainWindow()
        {
            InitializeComponent();

            _processMonitorTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(3)
            };
            _processMonitorTimer.Tick += ProcessMonitorTimer_Tick;

            Loaded += MainWindow_Loaded;
            SourceInitialized += MainWindow_SourceInitialized;
            StateChanged += Window_StateChanged;
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            TxtDeviceIdFooter.Text = $"Cihaz ID: {SettingsService.Instance.DeviceId}";

            // Bind live log collection
            LstLiveLogs.ItemsSource = AppLoggerService.Instance.Entries;
            AppLoggerService.Instance.OnNewLog += Instance_OnNewLog;

            // Bind speed callback
            CatalogAndHybridInstallerService.Instance.OnGlobalBandwidthTick += Instance_OnGlobalBandwidthTick;
            CatalogAndHybridInstallerService.Instance.OnCatalogUpdated += Instance_OnCatalogUpdated;

            // Developer / Admin Mode initialization
            DeveloperAuthService.Instance.OnAdminModeChanged += Instance_OnAdminModeChanged;
            UpdateAdminVisibility(DeveloperAuthService.Instance.IsAdminMode);

            // Populate device HWID in dev tools
            TxtDevThisHwid.Text = DeveloperAuthService.Instance.DeviceHwid;
            TxtModalDeviceHwid.Text = DeveloperAuthService.Instance.DeviceHwid;

            // Load settings into UI
            LoadSettingsIntoUi();

            // Initialize Catalog
            await CatalogAndHybridInstallerService.Instance.InitializeCatalogAsync();

            LstQuickApps.ItemsSource = CatalogAndHybridInstallerService.Instance.Apps;
            IcDownloadQueueList.ItemsSource = CatalogAndHybridInstallerService.Instance.Apps;
            IcRepairAppsList.ItemsSource = CatalogAndHybridInstallerService.Instance.Apps;
            IcAllPatchNotes.ItemsSource = CatalogAndHybridInstallerService.Instance.AllPatchNotes;

            RefreshFilteredAppCards();

            if (CatalogAndHybridInstallerService.Instance.Apps.Count > 0)
            {
                SelectApp(CatalogAndHybridInstallerService.Instance.Apps[0]);
                LstQuickApps.SelectedIndex = 0;
            }

            PopulateFeedbackAppCombo();
            DrawSpeedChart();
            _processMonitorTimer.Start();

            // Send client startup telemetry
            _ = TelemetryAndHelpService.Instance.ReportEventAsync(
                "CLIENT_STARTED",
                "AlpTogether",
                "AlpTogether Client",
                "1.0.0",
                $"İstemci başlatıldı ({Environment.OSVersion}, .NET {Environment.Version})");

            // Load incoming tickets & error telemetry in background
            _ = RefreshTelemetryInboxAsync();

            if (DeveloperAuthService.Instance.IsAdminMode)
            {
                _ = RefreshDevTelemetryAsync();
            }

            // Listen to live permission changes (grant / revoke)
            UserAuthAndPermissionService.Instance.OnPermissionsChanged += () =>
            {
                Dispatcher.Invoke(() =>
                {
                    if (_selectedApp != null) UpdateHeroPanel(_selectedApp);
                    RefreshFilteredAppCards();
                });
            };

            // Kullanıcı Kayıt Kontrolü (1 HWID = 1 Benzersiz Takma İsim)
            if (!UserAuthAndPermissionService.Instance.IsRegistered)
            {
                TxtPromptDeviceHwid.Text = UserAuthAndPermissionService.Instance.DeviceHwid;
                ModalUsernamePrompt.Visibility = Visibility.Visible;
                TxtPromptUsernameInput.Focus();
            }
            else
            {
                await UserAuthAndPermissionService.Instance.SyncSelfAndHeartbeatAsync();
                if (_selectedApp != null) UpdateHeroPanel(_selectedApp);
                RefreshFilteredAppCards();
            }
        }

        private void ProcessMonitorTimer_Tick(object? sender, EventArgs e)
        {
            CatalogAndHybridInstallerService.Instance.RefreshAllAppsState();
            if (_selectedApp != null && !_selectedApp.IsBusy)
            {
                UpdateHeroPanel(_selectedApp);
            }
            UpdateInstalledSummaryText();
        }

        private void Instance_OnNewLog(LogEntryItem entry)
        {
            TxtBottomLiveLog.Text = $"[{entry.TimeText}] [{entry.AppName}] {entry.Message}";
        }

        private void Instance_OnGlobalBandwidthTick(double mbps, string status)
        {
            TxtTopBandwidth.Text = mbps > 0.05 ? $"İndirme: {mbps:0.0} MB/sn" : "Ağ: Hazır";
            TxtDownloadCenterSpeed.Text = $"{mbps:0.0} MB/sn";

            var busyApp = CatalogAndHybridInstallerService.Instance.Apps.FirstOrDefault(a => a.IsBusy);
            TxtDownloadCenterActiveTask.Text = busyApp != null
                ? $"{busyApp.Name}: {busyApp.StatusText} (%{busyApp.ProgressPercent:0})"
                : "Aktif indirme bekleniyor — Tüm uygulamalar senkronize.";

            DrawSpeedChart();
        }

        private void Instance_OnCatalogUpdated()
        {
            TxtGlobalAnnouncement.Text = CatalogAndHybridInstallerService.Instance.GlobalAnnouncement;
            TxtSidebarAppCount.Text = CatalogAndHybridInstallerService.Instance.Apps.Count.ToString();
            RefreshFilteredAppCards();
            PopulateFeedbackAppCombo();
            UpdateInstalledSummaryText();

            if (_selectedApp != null)
            {
                var match = CatalogAndHybridInstallerService.Instance.Apps.FirstOrDefault(x => x.Id == _selectedApp.Id);
                if (match != null)
                    SelectApp(match);
            }
        }

        private void UpdateInstalledSummaryText()
        {
            int total = CatalogAndHybridInstallerService.Instance.Apps.Count;
            int inst = CatalogAndHybridInstallerService.Instance.Apps.Count(a => a.IsInstalled);
            int upd = CatalogAndHybridInstallerService.Instance.Apps.Count(a => a.UpdateAvailable);
            TxtInstalledSummary.Text = upd > 0
                ? $"Kurulu: {inst} / {total}  •  {upd} Güncelleme Mevcut"
                : $"Kurulu: {inst} / {total} Uygulama";
        }

        private void SelectApp(AlpAppItem app)
        {
            if (_selectedApp != null)
                _selectedApp.PropertyChanged -= SelectedApp_PropertyChanged;

            _selectedApp = app;
            _selectedApp.PropertyChanged += SelectedApp_PropertyChanged;

            UpdateHeroPanel(app);
        }

        private void SelectedApp_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (_selectedApp != null)
            {
                Dispatcher.BeginInvoke(new Action(() => UpdateHeroPanel(_selectedApp)));
            }
        }

        private void UpdateHeroPanel(AlpAppItem app)
        {
            try
            {
                ImgHeroAppIcon.Source = new BitmapImage(new Uri(app.IconPackUri, UriKind.RelativeOrAbsolute));
            }
            catch
            {
                ImgHeroAppIcon.Source = new BitmapImage(new Uri("pack://application:,,,/Assets/alptogether_logo_pink.png", UriKind.RelativeOrAbsolute));
            }

            TxtHeroAppName.Text = app.Name;
            TxtHeroSubtitle.Text = app.Subtitle;
            TxtHeroDescription.Text = app.Description;
            TxtHeroCategory.Text = app.Category;
            TxtHeroStateBadge.Text = app.StateBadgeText;
            TxtHeroHybridBadge.Text = app.HybridBadgeText;
            TxtHeroHealthBadge.Text = app.HealthStatus;
            TxtHeroInstallPath.Text = $"Konum: {app.InstallPath}";
            TxtHeroVersionCompare.Text = app.VersionDisplay;
            TxtHeroSizeAndDate.Text = $"Boyut: {app.DownloadSizeText}  •  Yayın: {app.ReleaseDate}";
            BtnHeroPrimaryAction.Content = app.PrimaryButtonText;
            BtnHeroRunInstallerExplicit.Visibility = app.HasCustomInstaller ? Visibility.Visible : Visibility.Collapsed;

            TxtHeroProgressStatus.Text = app.StatusText;
            TxtHeroSpeed.Text = app.SpeedText;
            PbHeroProgress.Value = app.IsBusy ? app.ProgressPercent : (app.IsInstalled ? 100 : 0);

            _suppressRadioEvent = true;
            RbHybridAuto.IsChecked = app.SelectedHybridMode == HybridInstallMode.HybridAuto;
            RbStandaloneInstaller.IsChecked = app.SelectedHybridMode == HybridInstallMode.StandaloneInstaller;
            RbDirectFiles.IsChecked = app.SelectedHybridMode == HybridInstallMode.DirectFiles;
            _suppressRadioEvent = false;

            TxtSelectedAppChangelogTitle.Text = $"✦ {app.Name} — Son Yenilikler & Sürüm Notları";
            IcSelectedAppPatchNotes.ItemsSource = app.PatchNotes;
            TxtInspectorExeInfo.Text = $"Ana Çalıştırılabilir Dosya: {app.ExecutableName}";
            TxtInspectorInstallerInfo.Text = app.HasCustomInstaller
                ? $"Hibrit Kurulum Aracı: {app.InstallerFileName} ({app.InstallerSilentArgs})"
                : "Kurulum Tipi: Doğrudan Paket / Arşiv Senkronizasyonu";
            IcSelectedAppCompanions.ItemsSource = app.CompanionFiles;

            if (ChkAppAutoStart != null)
            {
                ChkAppAutoStart.IsChecked = CatalogAndHybridInstallerService.Instance.IsAppAutoStartEnabled(app.Id);
                ChkAppAutoStart.IsEnabled = app.Id != "alpnet-pulse";
                if (app.Id == "alpnet-pulse")
                {
                    ChkAppAutoStart.ToolTip = "AlpNet Pulse sistem bileşeni olduğu için başlangıçta her zaman çalışır ve kapatılamaz.";
                }
                else
                {
                    ChkAppAutoStart.ToolTip = "Bu uygulamanın Windows açıldığında otomatik olarak arka planda başlamasını sağlar.";
                }
            }
        }

        private void RefreshFilteredAppCards()
        {
            string query = (TxtSearchApps?.Text ?? "").Trim();
            if (TxtSearchPlaceholder != null)
                TxtSearchPlaceholder.Visibility = string.IsNullOrEmpty(query) ? Visibility.Visible : Visibility.Collapsed;

            IEnumerable<AlpAppItem> filtered = CatalogAndHybridInstallerService.Instance.Apps;

            if (_activeFilter == "INSTALLED")
                filtered = filtered.Where(a => a.IsInstalled);
            else if (_activeFilter == "UPDATES")
                filtered = filtered.Where(a => a.UpdateAvailable);

            if (!string.IsNullOrWhiteSpace(query))
            {
                filtered = filtered.Where(a =>
                    a.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    a.Subtitle.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    a.Category.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    a.Description.Contains(query, StringComparison.OrdinalIgnoreCase));
            }

            IcAppCards.ItemsSource = filtered.ToList();
            UpdateInstalledSummaryText();
        }

        // ==================== SMOOTH PAGE TRANSITION ANIMATION ====================
        private void SwitchToView(UIElement targetView)
        {
            UIElement[] allViews =
            {
                ViewStoreLibrary,
                ViewDownloads,
                ViewChangelog,
                ViewRepairAndPaths,
                ViewCustomLogs,
                ViewHelpAndFeedback,
                ViewPublisherAndSettings
            };

            foreach (var v in allViews)
            {
                v.Visibility = ReferenceEquals(v, targetView) ? Visibility.Visible : Visibility.Collapsed;
            }

            // Smooth slide + fade animation
            var slideAnim = new DoubleAnimation
            {
                From = 18,
                To = 0,
                Duration = TimeSpan.FromMilliseconds(220),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            var fadeAnim = new DoubleAnimation
            {
                From = 0.25,
                To = 1.0,
                Duration = TimeSpan.FromMilliseconds(220),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };

            ViewportSlideTransform.BeginAnimation(TranslateTransform.YProperty, slideAnim);
            ViewportContainer.BeginAnimation(OpacityProperty, fadeAnim);
        }

        private void BtnNavStore_Click(object sender, RoutedEventArgs e) => SwitchToView(ViewStoreLibrary);
        private void BtnNavDownloads_Click(object sender, RoutedEventArgs e)
        {
            SwitchToView(ViewDownloads);
            DrawSpeedChart();
        }
        private void BtnNavChangelog_Click(object sender, RoutedEventArgs e) => SwitchToView(ViewChangelog);
        private void BtnNavRepair_Click(object sender, RoutedEventArgs e)
        {
            TxtDefaultRootPath.Text = SettingsService.Instance.Current.DefaultInstallRoot;
            CatalogAndHybridInstallerService.Instance.RefreshAllAppsState();
            SwitchToView(ViewRepairAndPaths);
        }
        private void BtnNavCustomLogs_Click(object sender, RoutedEventArgs e) => SwitchToView(ViewCustomLogs);
        private void BtnNavHelp_Click(object sender, RoutedEventArgs e)
        {
            SwitchToView(ViewHelpAndFeedback);
            _ = RefreshTelemetryInboxAsync();
        }
        private void Instance_OnAdminModeChanged(bool isAdmin)
        {
            Dispatcher.Invoke(() =>
            {
                UpdateAdminVisibility(isAdmin);
                if (isAdmin)
                {
                    _ = RefreshDevTelemetryAsync();
                }
            });
        }

        private void UpdateAdminVisibility(bool isAdmin)
        {
            BtnNavDevStudio.Visibility = isAdmin ? Visibility.Visible : Visibility.Collapsed;
        }

        private void BtnNavDevStudio_Click(object sender, RoutedEventArgs e)
        {
            SwitchToView(ViewPublisherAndSettings);
            SwitchDevSubView(DevSubViewUsers, BtnDevSubTabUsers);
            _ = RefreshAdminUsersAsync();
        }

        // ==================== WINDOW CHROME & TOP BAR ====================
        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                BtnMaximize_Click(sender, e);
                return;
            }
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                try { DragMove(); } catch { }
            }
        }

        private void BtnMinimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void BtnMaximize_Click(object sender, RoutedEventArgs e)
        {
            WindowState = (WindowState == WindowState.Maximized) ? WindowState.Normal : WindowState.Maximized;
        }

        private void Window_StateChanged(object? sender, EventArgs e)
        {
            if (WindowState == WindowState.Maximized)
            {
                OuterWindowBorder.CornerRadius = new CornerRadius(0);
                OuterWindowBorder.Margin = new Thickness(6);
            }
            else
            {
                OuterWindowBorder.CornerRadius = new CornerRadius(16);
                OuterWindowBorder.Margin = new Thickness(0);
            }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Application.Current.Shutdown();

        #region Win32 Non-Client / Work Area Maximization Fix
        private void MainWindow_SourceInitialized(object? sender, EventArgs e)
        {
            IntPtr handle = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(handle)?.AddHook(WindowProc);
        }

        private IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_GETMINMAXINFO)
            {
                WmGetMinMaxInfo(hwnd, lParam);
                handled = true;
            }
            return IntPtr.Zero;
        }

        private const int WM_GETMINMAXINFO = 0x0024;
        private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

        [DllImport("user32.dll")]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, [In, Out] MONITORINFO lpmi);

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int x;
            public int y;
            public POINT(int x, int y) { this.x = x; this.y = y; }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MINMAXINFO
        {
            public POINT ptReserved;
            public POINT ptMaxSize;
            public POINT ptMaxPosition;
            public POINT ptMinTrackSize;
            public POINT ptMaxTrackSize;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        public class MONITORINFO
        {
            public int cbSize = Marshal.SizeOf(typeof(MONITORINFO));
            public RECT rcMonitor = new RECT();
            public RECT rcWork = new RECT();
            public int dwFlags = 0;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int left;
            public int top;
            public int right;
            public int bottom;
        }

        private void WmGetMinMaxInfo(IntPtr hwnd, IntPtr lParam)
        {
            MINMAXINFO mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
            IntPtr monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (monitor != IntPtr.Zero)
            {
                MONITORINFO mi = new MONITORINFO();
                if (GetMonitorInfo(monitor, mi))
                {
                    RECT rcWork = mi.rcWork;
                    RECT rcMonitor = mi.rcMonitor;

                    mmi.ptMaxPosition.x = Math.Abs(rcWork.left - rcMonitor.left);
                    mmi.ptMaxPosition.y = Math.Abs(rcWork.top - rcMonitor.top);
                    mmi.ptMaxSize.x = Math.Abs(rcWork.right - rcWork.left);
                    mmi.ptMaxSize.y = Math.Abs(rcWork.bottom - rcWork.top);
                }
            }
            Marshal.StructureToPtr(mmi, lParam, true);
        }
        #endregion

        #region Anime Mascot Interactive Showcase Handlers
        private void BtnCloseMascotBanner_Click(object sender, RoutedEventArgs e)
        {
            AnimeMascotBanner.Visibility = Visibility.Collapsed;
        }

        private void BtnMascotApp_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string appId)
            {
                var app = CatalogAndHybridInstallerService.Instance.Apps.FirstOrDefault(a => string.Equals(a.Id, appId, StringComparison.OrdinalIgnoreCase));
                if (app != null)
                {
                    SelectApp(app);
                    TxtAnimeSpeechBubble.Text = appId switch
                    {
                        "alpnet-pulse" => "🌐 AlpNet Pulse seçildi! Akıllı WARP optimizasyonu ve canlı ses çevirisi ile oyunlarda en düşük ping ve anında iletişim seninle.",
                        "alphagravity" => "🤖 AlphaGravity seçildi! Gemini, Claude ve yerel LLM modellerini bir araya getiren güçlü AI merkezine hoş geldin.",
                        "alpfinans" => "📈 AlpFinans seçildi! Borsa, portföy ve finansal hareketlerini gerçek zamanlı takip etmeye hemen başlayabilirsin.",
                        "alplingo" => "🎮 AlpLingo seçildi! Oyun içi ekran üzeri anlık çeviri ve OCR destekli yapay zeka çevirmeni hazır.",
                        "alprust-plus" => "🛡 AlpRust+ seçildi! Rust akıllı cihazların, elektrik santrallerin ve bildirimlerin tek masaüstü ekranında.",
                        _ => $"✨ {app.Name} uygulaması seçildi! Hemen sağdaki butondan başlatabilir veya kurabilirsin."
                    };
                }
            }
        }
        #endregion

        #region Windows AutoStart Toggle
        private void ChkAppAutoStart_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedApp == null) return;
            if (_selectedApp.Id == "alpnet-pulse")
            {
                ChkAppAutoStart.IsChecked = true;
                MessageBox.Show("AlpNet Pulse sistem çekirdeği olduğu için Windows başlangıcında otomatik başlar ve değiştirilemez.", "Bilgi", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            bool enable = ChkAppAutoStart.IsChecked == true;
            string exePath = !string.IsNullOrEmpty(_selectedApp.InstallPath) && !string.IsNullOrEmpty(_selectedApp.ExecutableName)
                ? Path.Combine(_selectedApp.InstallPath, _selectedApp.ExecutableName)
                : string.Empty;
            bool ok = CatalogAndHybridInstallerService.Instance.SetAppAutoStart(_selectedApp.Id, exePath, enable);
            if (ok)
            {
                AppLoggerService.Instance.Info("OTORUN", $"{_selectedApp.Name} başlangıç ayarı {(enable ? "etkinleştirildi" : "devre dışı bırakıldı")}.", "AlpTogether");
            }
            else
            {
                MessageBox.Show("Başlangıç ayarı kaydedilemedi. Uygulamanın kurulu olduğundan emin olun.", "Hata", MessageBoxButton.OK, MessageBoxImage.Warning);
                ChkAppAutoStart.IsChecked = !enable;
            }
        }
        #endregion

        private void TxtSearchApps_TextChanged(object sender, TextChangedEventArgs e) => RefreshFilteredAppCards();

        private void BtnFilterAll_Click(object sender, RoutedEventArgs e)
        {
            _activeFilter = "ALL";
            SwitchToView(ViewStoreLibrary);
            RefreshFilteredAppCards();
        }

        private void BtnFilterInstalled_Click(object sender, RoutedEventArgs e)
        {
            _activeFilter = "INSTALLED";
            SwitchToView(ViewStoreLibrary);
            RefreshFilteredAppCards();
        }

        private void BtnFilterUpdates_Click(object sender, RoutedEventArgs e)
        {
            _activeFilter = "UPDATES";
            SwitchToView(ViewStoreLibrary);
            RefreshFilteredAppCards();
        }

        private async void BtnSyncCatalog_Click(object sender, RoutedEventArgs e)
        {
            await CatalogAndHybridInstallerService.Instance.SyncRemoteCatalogAsync(silent: false);
            RefreshFilteredAppCards();
        }

        private void BtnQuickReportIssue_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedApp != null)
                SelectAppInFeedbackCombo(_selectedApp);
            SwitchToView(ViewHelpAndFeedback);
        }

        private void LstQuickApps_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (LstQuickApps.SelectedItem is AlpAppItem app)
            {
                SelectApp(app);
                SwitchToView(ViewStoreLibrary);
            }
        }

        private void AppCard_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is AlpAppItem app)
            {
                SelectApp(app);
                LstQuickApps.SelectedItem = app;
            }
        }

        // ==================== HERO & APP CARD ACTIONS ====================
        private void HybridModeRadio_Checked(object sender, RoutedEventArgs e)
        {
            if (_suppressRadioEvent || _selectedApp == null) return;

            if (RbHybridAuto.IsChecked == true)
                _selectedApp.SelectedHybridMode = HybridInstallMode.HybridAuto;
            else if (RbStandaloneInstaller.IsChecked == true)
                _selectedApp.SelectedHybridMode = HybridInstallMode.StandaloneInstaller;
            else if (RbDirectFiles.IsChecked == true)
                _selectedApp.SelectedHybridMode = HybridInstallMode.DirectFiles;

            SettingsService.Instance.Current.AppHybridModes[_selectedApp.Id] = _selectedApp.SelectedHybridMode;
            SettingsService.Instance.Save();

            AppLoggerService.Instance.Info("HİBRİT-MOD", $"{_selectedApp.Name} kurulum yöntemi değiştirildi: {_selectedApp.SelectedHybridModeDescription}", _selectedApp.Name);
        }

        private async void BtnHeroPrimaryAction_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedApp == null) return;
            await ExecutePrimaryAppActionAsync(_selectedApp);
        }

        private async void BtnCardPrimary_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (sender is Button btn && btn.Tag is AlpAppItem app)
            {
                SelectApp(app);
                await ExecutePrimaryAppActionAsync(app);
            }
        }

        private async Task ExecutePrimaryAppActionAsync(AlpAppItem app)
        {
            if (app.IsBusy) return;

            if (!app.IsInstalled || app.UpdateAvailable)
            {
                await CatalogAndHybridInstallerService.Instance.InstallOrUpdateAppAsync(app);
            }
            else
            {
                await CatalogAndHybridInstallerService.Instance.LaunchOrStopAppAsync(app);
            }
            UpdateHeroPanel(app);
            RefreshFilteredAppCards();
        }

        private async void BtnHeroForceUpdate_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedApp == null) return;
            _selectedApp.SelectedHybridMode = HybridInstallMode.StandaloneInstaller;
            UpdateHeroPanel(_selectedApp);
            await CatalogAndHybridInstallerService.Instance.InstallOrUpdateAppAsync(_selectedApp);
        }

        private async void BtnHeroRepair_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedApp == null) return;
            await RunRepairWithDialogAsync(_selectedApp);
        }

        private async void BtnCardRepair_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (sender is Button btn && btn.Tag is AlpAppItem app)
            {
                SelectApp(app);
                await RunRepairWithDialogAsync(app);
            }
        }

        private async Task RunRepairWithDialogAsync(AlpAppItem app)
        {
            var (success, summary) = await CatalogAndHybridInstallerService.Instance.RepairAppAsync(app);
            UpdateHeroPanel(app);
            RefreshFilteredAppCards();

            MessageBox.Show(
                $"{app.Name} Onarım & Bütünlük Doğrulama Raporu:\n\n{summary}",
                success ? "Onarım Tamamlandı" : "Onarım Uyarısı",
                MessageBoxButton.OK,
                success ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }

        private async void BtnHeroChangePath_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedApp == null) return;
            await PromptAndChangeAppInstallPathAsync(_selectedApp);
        }

        private async void BtnCardChangePath_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (sender is Button btn && btn.Tag is AlpAppItem app)
            {
                SelectApp(app);
                await PromptAndChangeAppInstallPathAsync(app);
            }
        }

        private async Task PromptAndChangeAppInstallPathAsync(AlpAppItem app)
        {
            var dlg = new OpenFolderDialog
            {
                Title = $"{app.Name} İçin Yeni Kurulum Klasörü Seçin",
                InitialDirectory = Directory.Exists(app.InstallPath)
                    ? app.InstallPath
                    : SettingsService.Instance.Current.DefaultInstallRoot
            };

            if (dlg.ShowDialog(this) == true && !string.IsNullOrWhiteSpace(dlg.FolderName))
            {
                string chosen = dlg.FolderName;
                if (!chosen.EndsWith(app.DefaultInstallSubFolder, StringComparison.OrdinalIgnoreCase) &&
                    !chosen.EndsWith(app.Id, StringComparison.OrdinalIgnoreCase))
                {
                    chosen = Path.Combine(chosen, string.IsNullOrWhiteSpace(app.DefaultInstallSubFolder) ? app.Id : app.DefaultInstallSubFolder);
                }

                var (ok, msg) = await CatalogAndHybridInstallerService.Instance.ChangeAppInstallPathAsync(app, chosen, moveExistingFiles: true);
                UpdateHeroPanel(app);
                RefreshFilteredAppCards();

                MessageBox.Show(msg, ok ? "Dosya Yolu Güncellendi" : "Hata", MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Error);
            }
        }

        private void BtnHeroOpenFolder_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedApp == null) return;
            OpenAppDirectory(_selectedApp);
        }

        private void BtnCardOpenFolder_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is AlpAppItem app)
            {
                OpenAppDirectory(app);
            }
        }

        private static void OpenAppDirectory(AlpAppItem app)
        {
            try
            {
                if (!Directory.Exists(app.InstallPath))
                    Directory.CreateDirectory(app.InstallPath);

                Process.Start(new ProcessStartInfo
                {
                    FileName = app.InstallPath,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                AppLoggerService.Instance.Error("KLASÖR", $"Klasör açılamadı: {ex.Message}", app.Name);
            }
        }

        private void BtnHeroCreateShortcut_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedApp == null) return;
            string exePath = Path.Combine(_selectedApp.InstallPath, _selectedApp.ExecutableName);
            CatalogAndHybridInstallerService.CreateDesktopShortcut(exePath, _selectedApp.InstallPath, _selectedApp.Name);
            AppLoggerService.Instance.Success("KISAYOL", $"{_selectedApp.Name} masaüstü kısayolu oluşturuldu.", _selectedApp.Name);
            MessageBox.Show($"{_selectedApp.Name} için masaüstü kısayolu oluşturuldu!", "Kısayol Hazır", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void BtnHeroReportBug_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedApp != null)
            {
                SelectAppInFeedbackCombo(_selectedApp);
                TxtFeedbackSubject.Text = $"{_selectedApp.Name} (v{_selectedApp.LatestVersion}) Hata / Sorun Bildirimi";
            }
            SwitchToView(ViewHelpAndFeedback);
        }

        // ==================== DOWNLOADS & REPAIR CENTER ACTIONS ====================
        private void CanvasSpeedGraph_SizeChanged(object sender, SizeChangedEventArgs e) => DrawSpeedChart();

        private void DrawSpeedChart()
        {
            if (CanvasSpeedGraph == null || PolylineSpeedStroke == null) return;
            double w = CanvasSpeedGraph.ActualWidth;
            double h = CanvasSpeedGraph.ActualHeight;
            if (w <= 10 || h <= 10) return;

            var samples = CatalogAndHybridInstallerService.Instance.SpeedHistoryMbps.ToList();
            if (samples.Count == 0) return;

            double maxVal = Math.Max(60.0, samples.Max() * 1.2);
            var pts = new PointCollection();
            double stepX = w / Math.Max(1, samples.Count - 1);

            for (int i = 0; i < samples.Count; i++)
            {
                double x = i * stepX;
                double normalized = Math.Clamp(samples[i] / maxVal, 0.0, 1.0);
                double y = h - (normalized * (h - 16)) - 6;
                pts.Add(new Point(x, y));
            }
            PolylineSpeedStroke.Points = pts;
        }

        private async void BtnUpdateAllApps_Click(object sender, RoutedEventArgs e)
        {
            foreach (var app in CatalogAndHybridInstallerService.Instance.Apps.ToList())
            {
                if (app.IsInstalled || app.UpdateAvailable)
                {
                    await CatalogAndHybridInstallerService.Instance.InstallOrUpdateAppAsync(app);
                }
            }
        }

        private void BtnChangeDefaultRoot_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFolderDialog
            {
                Title = "AlpTogether Varsayılan Ana Kurulum Klasörünü Seçin",
                InitialDirectory = SettingsService.Instance.Current.DefaultInstallRoot
            };
            if (dlg.ShowDialog(this) == true && !string.IsNullOrWhiteSpace(dlg.FolderName))
            {
                SettingsService.Instance.Current.DefaultInstallRoot = dlg.FolderName;
                SettingsService.Instance.Save();
                TxtDefaultRootPath.Text = dlg.FolderName;
                CatalogAndHybridInstallerService.Instance.RefreshAllAppsState();
                AppLoggerService.Instance.Success("AYARLAR", $"Varsayılan ana kurulum dizini güncellendi: {dlg.FolderName}");
            }
        }

        private async void BtnRepairAllApps_Click(object sender, RoutedEventArgs e)
        {
            foreach (var app in CatalogAndHybridInstallerService.Instance.Apps.Where(x => x.IsInstalled).ToList())
            {
                await CatalogAndHybridInstallerService.Instance.RepairAppAsync(app);
            }
            MessageBox.Show("Kurulu tüm uygulamaların dosya bütünlüğü ve ayarları başarıyla doğrulandı ve onarıldı!", "Toplu Onarım Tamamlandı", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // ==================== CUSTOM WORKSPACE SLOT & LIVE LOGS ====================
        private void BtnCopyLogs_Click(object sender, RoutedEventArgs e)
        {
            string text = AppLoggerService.Instance.GetRecentLogsText(100);
            Clipboard.SetText(string.IsNullOrWhiteSpace(text) ? "Log kaydı yok." : text);
            AppLoggerService.Instance.Info("LOG", "Son işlem logları panoya kopyalandı.");
        }

        private void BtnOpenLogFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = AppLoggerService.Instance.GetLogDirectory(),
                    UseShellExecute = true
                });
            }
            catch { }
        }

        private void BtnClearLogs_Click(object sender, RoutedEventArgs e) => AppLoggerService.Instance.ClearUiLogs();

        private async void BtnTestAutoErrorTelemetry_Click(object sender, RoutedEventArgs e)
        {
            string targetName = _selectedApp?.Name ?? "AlpTogether Client";
            string targetId = _selectedApp?.Id ?? "AlpTogether";
            string targetVer = _selectedApp?.LatestVersion ?? "1.0.0";

            await TelemetryAndHelpService.Instance.ReportErrorImmediatelyAsync(
                targetId,
                targetName,
                targetVer,
                "SimulatedInstantCrashTest",
                "Kullanıcı/Geliştirici tarafından tetiklenen anlık hata telemetrisi testi — Bulut ve yerel hata kaydı doğrulandı.",
                "at AlpTogether.MainWindow.BtnTestAutoErrorTelemetry_Click()");

            await RefreshTelemetryInboxAsync();
            MessageBox.Show("Anlık Hata Raporu (Auto-Crash Telemetry) başarıyla oluşturuldu ve geliştirici bulutuna (Firebase RTDB) iletildi!", "Anlık Telemetri Testi Başarılı", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void BtnAddCustomSlotCard_Click(object sender, RoutedEventArgs e)
        {
            string title = TxtNewSlotTitle.Text.Trim();
            string content = TxtNewSlotContent.Text.Trim();
            if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(content))
                return;

            var item = new CustomWorkspaceSlotItem
            {
                Title = string.IsNullOrWhiteSpace(title) ? "Özel Modül Notu" : title,
                Content = content,
                CreatedAt = DateTime.Now.ToString("dd.MM.yyyy HH:mm")
            };

            SettingsService.Instance.Current.CustomSlotItems.Insert(0, item);
            SettingsService.Instance.Save();
            RefreshCustomSlotUi();

            TxtNewSlotTitle.Text = string.Empty;
            TxtNewSlotContent.Text = string.Empty;
            AppLoggerService.Instance.Info("ÖZEL-BOŞLUK", $"Yeni özel alan kartı eklendi: {item.Title}");
        }

        private void BtnDeleteCustomSlotCard_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is CustomWorkspaceSlotItem item)
            {
                SettingsService.Instance.Current.CustomSlotItems.Remove(item);
                SettingsService.Instance.Save();
                RefreshCustomSlotUi();
            }
        }

        private void BtnSaveCustomScratchpad_Click(object sender, RoutedEventArgs e)
        {
            SettingsService.Instance.Current.CustomSlotScratchpad = TxtCustomScratchpad.Text;
            SettingsService.Instance.Save();
            AppLoggerService.Instance.Success("ÖZEL-BOŞLUK", "Özel boşluk ve geliştirici not alanı kaydedildi.");
        }

        private void RefreshCustomSlotUi()
        {
            LstCustomSlotItems.ItemsSource = null;
            LstCustomSlotItems.ItemsSource = SettingsService.Instance.Current.CustomSlotItems;
            TxtCustomScratchpad.Text = SettingsService.Instance.Current.CustomSlotScratchpad;
        }

        // ==================== HELP, ISSUE REPORTING & LIVE TELEMETRY INBOX ====================
        private void PopulateFeedbackAppCombo()
        {
            CmbFeedbackApp.Items.Clear();
            foreach (var app in CatalogAndHybridInstallerService.Instance.Apps)
            {
                CmbFeedbackApp.Items.Add(new ComboBoxItem
                {
                    Content = $"{app.Name} (v{app.LatestVersion})",
                    Tag = app
                });
            }
            CmbFeedbackApp.Items.Add(new ComboBoxItem
            {
                Content = "AlpTogether Client (Genel İstemci)",
                Tag = null
            });
            CmbFeedbackApp.SelectedIndex = 0;
        }

        private void SelectAppInFeedbackCombo(AlpAppItem app)
        {
            for (int i = 0; i < CmbFeedbackApp.Items.Count; i++)
            {
                if (CmbFeedbackApp.Items[i] is ComboBoxItem cbi && cbi.Tag is AlpAppItem item && item.Id == app.Id)
                {
                    CmbFeedbackApp.SelectedIndex = i;
                    return;
                }
            }
        }

        private async void BtnSubmitFeedback_Click(object sender, RoutedEventArgs e)
        {
            string subject = TxtFeedbackSubject.Text.Trim();
            string desc = TxtFeedbackDescription.Text.Trim();

            if (string.IsNullOrWhiteSpace(subject) && string.IsNullOrWhiteSpace(desc))
            {
                MessageBox.Show("Lütfen yaşadığınız sorunu kısaca açıklayın.", "Eksik Bilgi", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            AlpAppItem? targetApp = (CmbFeedbackApp.SelectedItem as ComboBoxItem)?.Tag as AlpAppItem;
            string category = (CmbFeedbackCategory.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Bozuk Özellik / Hata";
            string severity = (CmbFeedbackSeverity.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Yüksek";
            string contact = TxtFeedbackContact.Text.Trim();
            bool attachLogs = ChkAttachSystemLogs.IsChecked == true;

            if (string.IsNullOrWhiteSpace(subject))
                subject = $"{targetApp?.Name ?? "AlpTogether"} — {category}";

            TxtFeedbackResultStatus.Text = "Bildiriminiz geliştirici bulutuna gönderiliyor...";

            var (ok, ticketId, msg) = await TelemetryAndHelpService.Instance.SubmitUserFeedbackAsync(
                targetApp, category, severity, contact, subject, desc, attachLogs);

            TxtFeedbackResultStatus.Text = $"✔ {msg}";
            TxtFeedbackSubject.Text = string.Empty;
            TxtFeedbackDescription.Text = string.Empty;

            await RefreshTelemetryInboxAsync();
        }

        private async void BtnRefreshTelemetryInbox_Click(object sender, RoutedEventArgs e)
        {
            await RefreshTelemetryInboxAsync();
        }

        private async Task RefreshTelemetryInboxAsync()
        {
            var tickets = await TelemetryAndHelpService.Instance.FetchRecentTicketsAsync();
            var errors = await TelemetryAndHelpService.Instance.FetchRecentErrorsAsync();

            IcIncomingTickets.ItemsSource = tickets.Take(12).ToList();
            IcIncomingErrors.ItemsSource = errors.Take(12).ToList();
        }

        private void BtnWhatsAppSupport_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string appName = ((CmbFeedbackApp.SelectedItem as ComboBoxItem)?.Tag as AlpAppItem)?.Name ?? "AlpTogether";
                string text = Uri.EscapeDataString($"Merhaba Alphan, {appName} uygulaması hakkında destek/hata bildiriminde bulunmak istiyorum: {TxtFeedbackSubject.Text}");
                string waNum = SettingsService.Instance.Current.SupportWhatsAppNumber;
                Process.Start(new ProcessStartInfo
                {
                    FileName = $"https://wa.me/{waNum}?text={text}",
                    UseShellExecute = true
                });
            }
            catch { }
        }

        private void BtnOpenGitHubIssues_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "https://github.com/alphankilicaslan",
                    UseShellExecute = true
                });
            }
            catch { }
        }

        private void BtnOpenGitHubRepo_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = SettingsService.Instance.Current.GitHubProfileUrl,
                    UseShellExecute = true
                });
            }
            catch { }
        }

        // ==================== DEVELOPER PUBLISHER & SETTINGS ====================
        private void BtnPickLocalExeForPublish_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "Yayınlanacak Uygulamanın .exe veya .zip Dosyasını Seçin",
                Filter = "Uygulama ve Paketler (*.exe;*.zip;*.msi)|*.exe;*.zip;*.msi|Tüm Dosyalar (*.*)|*.*"
            };

            if (dlg.ShowDialog(this) == true && File.Exists(dlg.FileName))
            {
                var fi = new FileInfo(dlg.FileName);
                TxtPubExeName.Text = fi.Name;
                TxtPubAppName.Text = Path.GetFileNameWithoutExtension(fi.Name);
                double mb = fi.Length / (1024.0 * 1024.0);
                TxtPubSizeText.Text = $"{mb:0.1} MB";

                try
                {
                    var fvi = FileVersionInfo.GetVersionInfo(dlg.FileName);
                    if (!string.IsNullOrWhiteSpace(fvi.FileVersion))
                        TxtPubVersion.Text = fvi.FileVersion;
                }
                catch { }
            }
        }

        private async void BtnPublishNewApp_Click(object sender, RoutedEventArgs e)
        {
            string appName = TxtPubAppName.Text.Trim();
            string version = TxtPubVersion.Text.Trim();
            string exeName = TxtPubExeName.Text.Trim();

            if (string.IsNullOrWhiteSpace(appName) || string.IsNullOrWhiteSpace(exeName))
            {
                MessageBox.Show("Lütfen Uygulama Adı ve Çalıştırılabilir .exe adını girin.", "Eksik Bilgi", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string appId = appName.ToLowerInvariant().Replace(" ", "-").Replace(".", "-");
            var notesLines = TxtPubPatchNotes.Text
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList();

            if (notesLines.Count == 0)
                notesLines.Add($"{appName} v{version} sürümü AlpTogether kataloğunda yayınlandı.");

            var newApp = new AlpAppItem
            {
                Id = appId,
                Name = appName,
                Subtitle = $"{TxtPubCategory.Text.Trim()} — AlpTogether Ekosistem Uygulaması",
                Category = TxtPubCategory.Text.Trim(),
                Developer = "Alphan Kılıçaslan",
                Description = TxtPubDescription.Text.Trim(),
                IconPackUri = "pack://application:,,,/Assets/alptogether_logo_pink.png",
                LatestVersion = string.IsNullOrWhiteSpace(version) ? "1.0.0" : version,
                ReleaseDate = DateTime.Now.ToString("dd MMMM yyyy"),
                DownloadSizeText = TxtPubSizeText.Text.Trim(),
                ExecutableName = exeName,
                DefaultInstallSubFolder = appName.Replace(" ", ""),
                DirectDownloadUrl = TxtPubDirectUrl.Text.Trim(),
                InstallerDownloadUrl = TxtPubInstallerUrl.Text.Trim(),
                InstallerFileName = string.IsNullOrWhiteSpace(TxtPubInstallerUrl.Text) ? "" : Path.GetFileName(TxtPubInstallerUrl.Text.Trim()),
                PackageType = string.IsNullOrWhiteSpace(TxtPubInstallerUrl.Text) ? AppPackageType.SingleExecutable : AppPackageType.HybridSetupAndFiles,
                PreferredHybridMode = HybridInstallMode.HybridAuto,
                PatchNotes = new List<PatchNoteEntry>
                {
                    new PatchNoteEntry
                    {
                        AppId = appId,
                        AppName = appName,
                        Version = string.IsNullOrWhiteSpace(version) ? "1.0.0" : version,
                        ReleaseDate = DateTime.Now.ToString("dd MMMM yyyy"),
                        Title = $"{appName} v{version} Güncellemesi",
                        Tag = "YENİ YAYIN",
                        Changes = notesLines
                    }
                }
            };

            TxtPublishResultStatus.Text = "Kataloğa yayınlanıyor...";
            var (ok, msg) = await CatalogAndHybridInstallerService.Instance.PublishOrUpdateAppInCatalogAsync(newApp);
            TxtPublishResultStatus.Text = ok ? $"✔ {msg}" : $"⚠ {msg}";

            if (ok)
            {
                SelectApp(newApp);
            }
        }

        private void LoadSettingsIntoUi()
        {
            var cfg = SettingsService.Instance.Current;
            TxtDefaultRootPath.Text = cfg.DefaultInstallRoot;
            TxtSettingGitHubCatalog.Text = cfg.GitHubCatalogUrl;
            TxtSettingFirebaseUrl.Text = cfg.FirebaseBaseUrl;
            TxtSettingWebhookUrl.Text = cfg.CustomWebhookUrl;
            ChkSettingAutoTelemetry.IsChecked = cfg.AutoSendCrashTelemetry;
            ChkSettingMonitorChildCrashes.IsChecked = cfg.MonitorLaunchedAppsForCrashes;
            ChkSettingDesktopShortcut.IsChecked = cfg.CreateDesktopShortcutOnInstall;

            RefreshCustomSlotUi();
        }

        private void BtnSaveSettings_Click(object sender, RoutedEventArgs e)
        {
            var cfg = SettingsService.Instance.Current;
            cfg.GitHubCatalogUrl = TxtSettingGitHubCatalog.Text.Trim();
            cfg.FirebaseBaseUrl = TxtSettingFirebaseUrl.Text.Trim();
            cfg.CustomWebhookUrl = TxtSettingWebhookUrl.Text.Trim();
            cfg.AutoSendCrashTelemetry = ChkSettingAutoTelemetry.IsChecked == true;
            cfg.MonitorLaunchedAppsForCrashes = ChkSettingMonitorChildCrashes.IsChecked == true;
            cfg.CreateDesktopShortcutOnInstall = ChkSettingDesktopShortcut.IsChecked == true;

            SettingsService.Instance.Save();
            AppLoggerService.Instance.Success("AYARLAR", "Bulut, GitHub ve Telemetri ayarları kaydedildi.");
            MessageBox.Show("İstemci ayarları başarıyla kaydedildi!", "Ayarlar Kaydedildi", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // ==================== DEVELOPER STUDIO SUB-TABS ====================
        private void SwitchDevSubView(UIElement targetView, Button? activeTabBtn)
        {
            DevSubViewUsers.Visibility = ReferenceEquals(DevSubViewUsers, targetView) ? Visibility.Visible : Visibility.Collapsed;
            DevSubViewTelemetry.Visibility = ReferenceEquals(DevSubViewTelemetry, targetView) ? Visibility.Visible : Visibility.Collapsed;
            DevSubViewPublisher.Visibility = ReferenceEquals(DevSubViewPublisher, targetView) ? Visibility.Visible : Visibility.Collapsed;
            DevSubViewKeygen.Visibility = ReferenceEquals(DevSubViewKeygen, targetView) ? Visibility.Visible : Visibility.Collapsed;
            DevSubViewSettings.Visibility = ReferenceEquals(DevSubViewSettings, targetView) ? Visibility.Visible : Visibility.Collapsed;

            BtnDevSubTabUsers.Style = (Style)FindResource("PinkSecondaryButtonStyle");
            BtnDevSubTabTelemetry.Style = (Style)FindResource("PinkSecondaryButtonStyle");
            BtnDevSubTabPublisher.Style = (Style)FindResource("PinkSecondaryButtonStyle");
            BtnDevSubTabSettings.Style = (Style)FindResource("PinkSecondaryButtonStyle");

            if (activeTabBtn != null)
            {
                activeTabBtn.Style = (Style)FindResource("PinkPrimaryButtonStyle");
            }
        }

        private void BtnDevSubTabUsers_Click(object sender, RoutedEventArgs e)
        {
            SwitchDevSubView(DevSubViewUsers, BtnDevSubTabUsers);
            _ = RefreshAdminUsersAsync();
        }

        private void BtnDevSubTabTelemetry_Click(object sender, RoutedEventArgs e)
        {
            SwitchDevSubView(DevSubViewTelemetry, BtnDevSubTabTelemetry);
            _ = RefreshDevTelemetryAsync();
        }

        // ==================== KULLANICI KAYIT MODALI (1 HWID = 1 BENZERSİZ İSİM) ====================
        private async void BtnSaveUsernamePrompt_Click(object sender, RoutedEventArgs e)
        {
            string chosenName = TxtPromptUsernameInput.Text.Trim();
            if (string.IsNullOrWhiteSpace(chosenName))
            {
                TxtPromptUsernameStatus.Text = "Lütfen bir kullanıcı adı girin.";
                return;
            }

            BtnSaveUsernamePrompt.IsEnabled = false;
            BtnSaveUsernamePrompt.Content = "Kontrol Ediliyor ve Kaydediliyor...";
            TxtPromptUsernameStatus.Text = "";

            var (success, message) = await UserAuthAndPermissionService.Instance.TryRegisterUsernameAsync(chosenName);
            if (success)
            {
                ModalUsernamePrompt.Visibility = Visibility.Collapsed;
                MessageBox.Show($"Hoş geldiniz, {chosenName}!\n\nKullanıcı adınız başarıyla kaydedildi ve bu cihazın HWID kimliğine bağlandı.\nUygulama yetkileri için yöneticiye (Alphan) bu adınızı iletebilirsiniz.",
                    "Kayıt Başarılı", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                TxtPromptUsernameStatus.Text = message;
            }

            BtnSaveUsernamePrompt.IsEnabled = true;
            BtnSaveUsernamePrompt.Content = "✓ Kaydet ve AlpTogether'a Başla";
        }

        private void TxtPromptUsernameInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                BtnSaveUsernamePrompt_Click(sender, e);
            }
        }

        // ==================== KULLANICI & WHITELIST YÖNETİM MASASI ====================
        private async Task RefreshAdminUsersAsync()
        {
            BtnDevRefreshUsers.IsEnabled = false;
            BtnDevRefreshUsers.Content = "Yenileniyor...";

            try
            {
                _allAdminUsers = await UserAuthAndPermissionService.Instance.FetchAllUsersAsync();
                TxtDevTotalUsersCount.Text = _allAdminUsers.Count.ToString();
                FilterAdminUsers();

                if (_selectedAdminUser != null)
                {
                    var updated = _allAdminUsers.FirstOrDefault(u => string.Equals(u.Hwid, _selectedAdminUser.Hwid, StringComparison.OrdinalIgnoreCase));
                    if (updated != null)
                    {
                        _selectedAdminUser = updated;
                        UpdateSelectedUserUi(updated);
                    }
                }
            }
            catch (Exception ex)
            {
                AppLoggerService.Instance.Error("ADMİN-USER", "Kullanıcı tablosu yenilenemedi: " + ex.Message);
            }
            finally
            {
                BtnDevRefreshUsers.IsEnabled = true;
                BtnDevRefreshUsers.Content = "🔄 Listeyi Yenile";
            }
        }

        private void FilterAdminUsers()
        {
            string q = (TxtSearchUsers.Text ?? "").Trim();
            if (string.IsNullOrEmpty(q))
            {
                LstAdminUsers.ItemsSource = null;
                LstAdminUsers.ItemsSource = _allAdminUsers;
            }
            else
            {
                var filtered = _allAdminUsers.Where(u =>
                    u.Username.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    u.AdminAlias.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    u.Hwid.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    u.AlpNetHwid.Contains(q, StringComparison.OrdinalIgnoreCase)
                ).ToList();
                LstAdminUsers.ItemsSource = null;
                LstAdminUsers.ItemsSource = filtered;
            }
        }

        private void TxtSearchUsers_TextChanged(object sender, TextChangedEventArgs e)
        {
            FilterAdminUsers();
        }

        private void LstAdminUsers_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (LstAdminUsers.SelectedItem is AlpTogetherUserRecord user)
            {
                _selectedAdminUser = user;
                UpdateSelectedUserUi(user);
            }
        }

        private void UpdateSelectedUserUi(AlpTogetherUserRecord? user)
        {
            if (user == null)
            {
                PnlNoUserSelected.Visibility = Visibility.Visible;
                PnlSelectedUserDetails.Visibility = Visibility.Collapsed;
                return;
            }

            PnlNoUserSelected.Visibility = Visibility.Collapsed;
            PnlSelectedUserDetails.Visibility = Visibility.Visible;

            TxtDevUserAvatar.Text = user.InitialLetter;
            TxtDevUserTitle.Text = user.DisplayName;
            TxtDevUserRegDate.Text = user.RegisteredAtDisplay;
            TxtDevUserLastSeen.Text = user.LastSeenDisplay;
            TxtDevUserHwid.Text = $"HWID: {user.Hwid}" + (string.IsNullOrWhiteSpace(user.AlpNetHwid) ? "" : $" | AlpNet: {user.AlpNetHwid}");
            TxtDevUserAliasInput.Text = user.AdminAlias;

            // AlpLingo Status
            if (user.AppPermissions.TryGetValue("alplingo", out var lingoPerm) && lingoPerm.HasAccess && !lingoPerm.IsExpired)
            {
                TxtAlpLingoStatusBadge.Text = lingoPerm.StatusBadge;
                TxtAlpLingoStatusBadge.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(lingoPerm.StatusColorHex));
                BrdAlpLingoStatus.BorderBrush = TxtAlpLingoStatusBadge.Foreground;
                TxtAlpLingoStatusDesc.Text = lingoPerm.StatusDescription;
            }
            else
            {
                TxtAlpLingoStatusBadge.Text = "⛔ Yetkisiz";
                TxtAlpLingoStatusBadge.Foreground = new SolidColorBrush(Color.FromRgb(150, 150, 150));
                BrdAlpLingoStatus.BorderBrush = TxtAlpLingoStatusBadge.Foreground;
                TxtAlpLingoStatusDesc.Text = "AlpLingo için henüz yetki verilmemiş veya süresi dolmuş.";
            }

            // AlpNet Pulse Status
            if (user.AppPermissions.TryGetValue("alpnet-pulse", out var pulsePerm) && pulsePerm.HasAccess && !pulsePerm.IsExpired)
            {
                TxtAlpNetStatusBadge.Text = pulsePerm.StatusBadge;
                TxtAlpNetStatusBadge.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(pulsePerm.StatusColorHex));
                BrdAlpNetStatus.BorderBrush = TxtAlpNetStatusBadge.Foreground;
                TxtAlpNetStatusDesc.Text = pulsePerm.StatusDescription;
            }
            else
            {
                TxtAlpNetStatusBadge.Text = "⛔ Yetkisiz";
                TxtAlpNetStatusBadge.Foreground = new SolidColorBrush(Color.FromRgb(150, 150, 150));
                BrdAlpNetStatus.BorderBrush = TxtAlpNetStatusBadge.Foreground;
                TxtAlpNetStatusDesc.Text = "AlpNet Pulse için henüz yetki verilmemiş veya süresi dolmuş.";
            }

            // AlpFinans Status
            if (user.AppPermissions.TryGetValue("alpfinans", out var finansPerm) && finansPerm.HasAccess && !finansPerm.IsExpired)
            {
                TxtAlpFinansStatusBadge.Text = finansPerm.StatusBadge;
                TxtAlpFinansStatusBadge.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(finansPerm.StatusColorHex));
                BrdAlpFinansStatus.BorderBrush = TxtAlpFinansStatusBadge.Foreground;
                TxtAlpFinansStatusDesc.Text = finansPerm.StatusDescription;
            }
            else
            {
                TxtAlpFinansStatusBadge.Text = "⛔ Yetkisiz";
                TxtAlpFinansStatusBadge.Foreground = new SolidColorBrush(Color.FromRgb(150, 150, 150));
                BrdAlpFinansStatus.BorderBrush = TxtAlpFinansStatusBadge.Foreground;
                TxtAlpFinansStatusDesc.Text = "AlpFinans için henüz yetki verilmemiş veya süresi dolmuş.";
            }

            // AlphaGravity Status
            if (user.AppPermissions.TryGetValue("alphagravity", out var gravPerm) && gravPerm.HasAccess && !gravPerm.IsExpired)
            {
                TxtAlphaGravityStatusBadge.Text = gravPerm.StatusBadge;
                TxtAlphaGravityStatusBadge.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(gravPerm.StatusColorHex));
                BrdAlphaGravityStatus.BorderBrush = TxtAlphaGravityStatusBadge.Foreground;
                TxtAlphaGravityStatusDesc.Text = gravPerm.StatusDescription;
            }
            else
            {
                TxtAlphaGravityStatusBadge.Text = "⛔ Yetkisiz";
                TxtAlphaGravityStatusBadge.Foreground = new SolidColorBrush(Color.FromRgb(150, 150, 150));
                BrdAlphaGravityStatus.BorderBrush = TxtAlphaGravityStatusBadge.Foreground;
                TxtAlphaGravityStatusDesc.Text = "AlphaGravity için henüz yetki verilmemiş veya süresi dolmuş.";
            }

            // AlpRust+ Status
            if ((user.AppPermissions.TryGetValue("alprust-plus", out var rustPerm) || user.AppPermissions.TryGetValue("alprust", out rustPerm)) && rustPerm.HasAccess && !rustPerm.IsExpired)
            {
                TxtAlpRustStatusBadge.Text = rustPerm.StatusBadge;
                TxtAlpRustStatusBadge.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(rustPerm.StatusColorHex));
                BrdAlpRustStatus.BorderBrush = TxtAlpRustStatusBadge.Foreground;
                TxtAlpRustStatusDesc.Text = rustPerm.StatusDescription;
            }
            else
            {
                TxtAlpRustStatusBadge.Text = "⛔ Yetkisiz";
                TxtAlpRustStatusBadge.Foreground = new SolidColorBrush(Color.FromRgb(150, 150, 150));
                BrdAlpRustStatus.BorderBrush = TxtAlpRustStatusBadge.Foreground;
                TxtAlpRustStatusDesc.Text = "AlpRust+ için henüz yetki verilmemiş veya süresi dolmuş.";
            }

            // Update Admin Role button state
            if (BtnDevToggleAdminRole != null)
            {
                if (user.IsAdmin)
                {
                    BtnDevToggleAdminRole.Content = "👑 Admin Yetkisini Geri Al";
                    BtnDevToggleAdminRole.Style = (Style)FindResource("PinkSecondaryButtonStyle");
                }
                else
                {
                    BtnDevToggleAdminRole.Content = "👑 Yönetici (Admin) Ata";
                    BtnDevToggleAdminRole.Style = (Style)FindResource("PinkPrimaryButtonStyle");
                }
            }
        }

        private void BtnDevRefreshUsers_Click(object sender, RoutedEventArgs e)
        {
            _ = RefreshAdminUsersAsync();
        }

        private async void BtnDevImportLegacyAlpNet_Click(object sender, RoutedEventArgs e)
        {
            var confirm = MessageBox.Show(
                "Firebase /users/ altındaki eski AlpNet Pulse kullanıcılarını aktarıp AlpNet Pulse yetkisi vermek istiyor musunuz?\n\n(Not: Var olan kullanıcılar ve anahtarları asla bozulmaz.)",
                "Eski AlpNet Kullanıcılarını Aktar",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (confirm != MessageBoxResult.Yes) return;

            BtnDevImportLegacyAlpNet.IsEnabled = false;
            BtnDevImportLegacyAlpNet.Content = "Aktarılıyor...";

            var (imported, merged) = await UserAuthAndPermissionService.Instance.ImportLegacyAlpNetUsersAsync();

            MessageBox.Show(
                $"İşlem Başarıyla Tamamlandı!\n\n" +
                $"• Yeni Eklenen AlpNet Kullanıcısı: {imported}\n" +
                $"• Mevcut Hesabıyla Birleştirilen (Örn. Alphan): {merged}\n\n" +
                $"Tüm lisans anahtarları ve süreleri başarıyla çözümlenerek Whitelist Masasına entegre edildi.",
                "AlpNet Kullanıcı ve Lisans Aktarımı", MessageBoxButton.OK, MessageBoxImage.Information);

            _ = RefreshAdminUsersAsync();
            BtnDevImportLegacyAlpNet.IsEnabled = true;
            BtnDevImportLegacyAlpNet.Content = "📥 Eski AlpNet Kullanıcılarını Aktar";
        }

        private async void BtnSaveUserAlias_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedAdminUser == null) return;

            string alias = TxtDevUserAliasInput.Text.Trim();
            BtnSaveUserAlias.IsEnabled = false;
            BtnSaveUserAlias.Content = "Kaydediliyor...";

            bool ok = await UserAuthAndPermissionService.Instance.UpdateUserAliasAsync(_selectedAdminUser.Hwid, alias);
            if (ok)
            {
                _selectedAdminUser.AdminAlias = alias;
                UpdateSelectedUserUi(_selectedAdminUser);
                FilterAdminUsers();
                MessageBox.Show("Kullanıcı takma adı / notu başarıyla güncellendi!", "Kaydedildi", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show("Takma ad kaydedilirken hata oluştu. Bağlantınızı kontrol edin.", "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            BtnSaveUserAlias.IsEnabled = true;
            BtnSaveUserAlias.Content = "💾 Kaydet";
        }

        private int GetDurationDaysFromCombo(ComboBox cmb)
        {
            if (cmb.SelectedItem is ComboBoxItem item && int.TryParse(item.Tag?.ToString(), out int days))
            {
                return days;
            }
            return 30;
        }

        private async void BtnGrantAlpLingo_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedAdminUser == null) return;

            int days = GetDurationDaysFromCombo(CmbDurationAlpLingo);
            BtnGrantAlpLingo.IsEnabled = false;
            BtnGrantAlpLingo.Content = "Yetki Veriliyor...";

            bool ok = await UserAuthAndPermissionService.Instance.GrantAppPermissionAsync(_selectedAdminUser.Hwid, "alplingo", days);
            if (ok)
            {
                string durationText = days >= 9999 ? "Süresiz" : $"{days} Gün";
                MessageBox.Show($"'{_selectedAdminUser.DisplayName}' için AlpLingo yetkisi başarıyla tanımlandı ({durationText})!", "Yetki Tanımlandı", MessageBoxButton.OK, MessageBoxImage.Information);
                await RefreshAdminUsersAsync();
            }
            else
            {
                MessageBox.Show("Yetki tanımlanırken hata oluştu.", "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            BtnGrantAlpLingo.IsEnabled = true;
            BtnGrantAlpLingo.Content = "⚡ Yetki Ver / Uzat";
        }

        private async void BtnRevokeAlpLingo_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedAdminUser == null) return;

            var res = MessageBox.Show(
                $"'{_selectedAdminUser.DisplayName}' kullanıcısının AlpLingo yetkisini kaldırmak istiyor musunuz?\n\n(ÖNEMLİ: Kullanıcı veritabanından silinmeyecektir, sadece AlpLingo erişimi kapatılacaktır.)",
                "Yetkiyi Kaldır",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (res != MessageBoxResult.Yes) return;

            BtnRevokeAlpLingo.IsEnabled = false;
            BtnRevokeAlpLingo.Content = "Kaldırılıyor...";

            bool ok = await UserAuthAndPermissionService.Instance.RevokeAppPermissionAsync(_selectedAdminUser.Hwid, "alplingo");
            if (ok)
            {
                MessageBox.Show($"'{_selectedAdminUser.DisplayName}' için AlpLingo yetkisi kaldırıldı.", "Yetki Kaldırıldı", MessageBoxButton.OK, MessageBoxImage.Information);
                await RefreshAdminUsersAsync();
            }
            else
            {
                MessageBox.Show("Yetki kaldırılırken hata oluştu.", "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            BtnRevokeAlpLingo.IsEnabled = true;
            BtnRevokeAlpLingo.Content = "⛔ Yetkiyi Kaldır";
        }

        private async void BtnGrantAlpNet_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedAdminUser == null) return;

            int days = GetDurationDaysFromCombo(CmbDurationAlpNet);
            BtnGrantAlpNet.IsEnabled = false;
            BtnGrantAlpNet.Content = "Yetki Veriliyor...";

            bool ok = await UserAuthAndPermissionService.Instance.GrantAppPermissionAsync(_selectedAdminUser.Hwid, "alpnet-pulse", days);
            if (ok)
            {
                string durationText = days >= 9999 ? "Süresiz" : $"{days} Gün";
                MessageBox.Show($"'{_selectedAdminUser.DisplayName}' için AlpNet Pulse yetkisi başarıyla tanımlandı ({durationText})!", "Yetki Tanımlandı", MessageBoxButton.OK, MessageBoxImage.Information);
                await RefreshAdminUsersAsync();
            }
            else
            {
                MessageBox.Show("Yetki tanımlanırken hata oluştu.", "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            BtnGrantAlpNet.IsEnabled = true;
            BtnGrantAlpNet.Content = "⚡ Yetki Ver / Uzat";
        }

        private async void BtnRevokeAlpNet_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedAdminUser == null) return;

            var res = MessageBox.Show(
                $"'{_selectedAdminUser.DisplayName}' kullanıcısının AlpNet Pulse yetkisini kaldırmak istiyor musunuz?\n\n(ÖNEMLİ: Kullanıcı veritabanından silinmeyecektir, sadece AlpNet Pulse erişimi kapatılacaktır.)",
                "Yetkiyi Kaldır",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (res != MessageBoxResult.Yes) return;

            BtnRevokeAlpNet.IsEnabled = false;
            BtnRevokeAlpNet.Content = "Kaldırılıyor...";

            bool ok = await UserAuthAndPermissionService.Instance.RevokeAppPermissionAsync(_selectedAdminUser.Hwid, "alpnet-pulse");
            if (ok)
            {
                MessageBox.Show($"'{_selectedAdminUser.DisplayName}' için AlpNet Pulse yetkisi kaldırıldı.", "Yetki Kaldırıldı", MessageBoxButton.OK, MessageBoxImage.Information);
                await RefreshAdminUsersAsync();
            }
            else
            {
                MessageBox.Show("Yetki kaldırılırken hata oluştu.", "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            BtnRevokeAlpNet.IsEnabled = true;
            BtnRevokeAlpNet.Content = "⛔ Yetkiyi Kaldır";
        }

        #region AlpFinans & AlphaGravity & Admin Role Handlers
        private async void BtnDevToggleAdminRole_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedAdminUser == null) return;
            bool newAdminState = !_selectedAdminUser.IsAdmin;
            string actionText = newAdminState ? "Admin olarak atamak" : "Admin yetkisini geri almak";
            var result = MessageBox.Show($"'{_selectedAdminUser.DisplayName}' kullanıcısını {actionText} istediğinize emin misiniz?",
                "Admin Rol Yönetimi", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes) return;

            BtnDevToggleAdminRole.IsEnabled = false;
            bool ok = await UserAuthAndPermissionService.Instance.SetUserAdminRoleAsync(_selectedAdminUser.Hwid, newAdminState);
            if (ok)
            {
                _selectedAdminUser.IsAdmin = newAdminState;
                UpdateSelectedUserUi(_selectedAdminUser);
                MessageBox.Show($"Kullanıcı rolü güncellendi: {(newAdminState ? "👑 Admin" : "Standart Kullanıcı")}", "Başarılı", MessageBoxButton.OK, MessageBoxImage.Information);
                await RefreshAdminUsersAsync();
            }
            else
            {
                MessageBox.Show("Rol güncellenirken hata oluştu.", "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            BtnDevToggleAdminRole.IsEnabled = true;
        }

        private async void BtnGrantAlpFinans_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedAdminUser == null) return;
            int days = GetDurationDaysFromCombo(CmbDurationAlpFinans);
            BtnGrantAlpFinans.IsEnabled = false;
            BtnGrantAlpFinans.Content = "Yetki Veriliyor...";

            bool ok = await UserAuthAndPermissionService.Instance.GrantAppPermissionAsync(_selectedAdminUser.Hwid, "alpfinans", days);
            if (ok)
            {
                string durationText = days >= 9999 ? "Süresiz" : $"{days} Gün";
                MessageBox.Show($"'{_selectedAdminUser.DisplayName}' için AlpFinans yetkisi başarıyla tanımlandı ({durationText})!", "Yetki Tanımlandı", MessageBoxButton.OK, MessageBoxImage.Information);
                await RefreshAdminUsersAsync();
            }
            else
            {
                MessageBox.Show("Yetki tanımlanırken hata oluştu.", "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            BtnGrantAlpFinans.IsEnabled = true;
            BtnGrantAlpFinans.Content = "⚡ Yetki Ver / Uzat";
        }

        private async void BtnRevokeAlpFinans_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedAdminUser == null) return;
            var res = MessageBox.Show(
                $"'{_selectedAdminUser.DisplayName}' kullanıcısının AlpFinans yetkisini kaldırmak istiyor musunuz?",
                "Yetkiyi Kaldır", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (res != MessageBoxResult.Yes) return;

            BtnRevokeAlpFinans.IsEnabled = false;
            BtnRevokeAlpFinans.Content = "Kaldırılıyor...";
            bool ok = await UserAuthAndPermissionService.Instance.RevokeAppPermissionAsync(_selectedAdminUser.Hwid, "alpfinans");
            if (ok)
            {
                MessageBox.Show($"'{_selectedAdminUser.DisplayName}' için AlpFinans yetkisi kaldırıldı.", "Yetki Kaldırıldı", MessageBoxButton.OK, MessageBoxImage.Information);
                await RefreshAdminUsersAsync();
            }
            else
            {
                MessageBox.Show("Yetki kaldırılırken hata oluştu.", "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            BtnRevokeAlpFinans.IsEnabled = true;
            BtnRevokeAlpFinans.Content = "⛔ Yetkiyi Kaldır";
        }

        private async void BtnGrantAlphaGravity_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedAdminUser == null) return;
            int days = GetDurationDaysFromCombo(CmbDurationAlphaGravity);
            BtnGrantAlphaGravity.IsEnabled = false;
            BtnGrantAlphaGravity.Content = "Yetki Veriliyor...";

            bool ok = await UserAuthAndPermissionService.Instance.GrantAppPermissionAsync(_selectedAdminUser.Hwid, "alphagravity", days);
            if (ok)
            {
                string durationText = days >= 9999 ? "Süresiz" : $"{days} Gün";
                MessageBox.Show($"'{_selectedAdminUser.DisplayName}' için AlphaGravity yetkisi başarıyla tanımlandı ({durationText})!", "Yetki Tanımlandı", MessageBoxButton.OK, MessageBoxImage.Information);
                await RefreshAdminUsersAsync();
            }
            else
            {
                MessageBox.Show("Yetki tanımlanırken hata oluştu.", "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            BtnGrantAlphaGravity.IsEnabled = true;
            BtnGrantAlphaGravity.Content = "⚡ Yetki Ver / Uzat";
        }

        private async void BtnRevokeAlphaGravity_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedAdminUser == null) return;
            var res = MessageBox.Show(
                $"'{_selectedAdminUser.DisplayName}' kullanıcısının AlphaGravity yetkisini kaldırmak istiyor musunuz?",
                "Yetkiyi Kaldır", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (res != MessageBoxResult.Yes) return;

            BtnRevokeAlphaGravity.IsEnabled = false;
            BtnRevokeAlphaGravity.Content = "Kaldırılıyor...";
            bool ok = await UserAuthAndPermissionService.Instance.RevokeAppPermissionAsync(_selectedAdminUser.Hwid, "alphagravity");
            if (ok)
            {
                MessageBox.Show($"'{_selectedAdminUser.DisplayName}' için AlphaGravity yetkisi kaldırıldı.", "Yetki Kaldırıldı", MessageBoxButton.OK, MessageBoxImage.Information);
                await RefreshAdminUsersAsync();
            }
            else
            {
                MessageBox.Show("Yetki kaldırılırken hata oluştu.", "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            BtnRevokeAlphaGravity.IsEnabled = true;
            BtnRevokeAlphaGravity.Content = "⛔ Yetkiyi Kaldır";
        }

        private async void BtnGrantAlpRust_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedAdminUser == null) return;
            int days = GetDurationDaysFromCombo(CmbDurationAlpRust);
            BtnGrantAlpRust.IsEnabled = false;
            BtnGrantAlpRust.Content = "Yetki Veriliyor...";

            bool ok = await UserAuthAndPermissionService.Instance.GrantAppPermissionAsync(_selectedAdminUser.Hwid, "alprust-plus", days);
            await UserAuthAndPermissionService.Instance.GrantAppPermissionAsync(_selectedAdminUser.Hwid, "alprust", days);

            if (ok)
            {
                string durationText = days >= 9999 ? "Süresiz" : $"{days} Gün";
                MessageBox.Show($"'{_selectedAdminUser.DisplayName}' için AlpRust+ yetkisi başarıyla tanımlandı ({durationText})!", "Yetki Tanımlandı", MessageBoxButton.OK, MessageBoxImage.Information);
                await RefreshAdminUsersAsync();
            }
            else
            {
                MessageBox.Show("Yetki tanımlanırken hata oluştu.", "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            BtnGrantAlpRust.IsEnabled = true;
            BtnGrantAlpRust.Content = "⚡ Yetki Ver / Uzat";
        }

        private async void BtnRevokeAlpRust_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedAdminUser == null) return;
            var res = MessageBox.Show(
                $"'{_selectedAdminUser.DisplayName}' kullanıcısının AlpRust+ yetkisini kaldırmak istiyor musunuz?",
                "Yetkiyi Kaldır", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (res != MessageBoxResult.Yes) return;

            BtnRevokeAlpRust.IsEnabled = false;
            BtnRevokeAlpRust.Content = "Kaldırılıyor...";
            bool ok = await UserAuthAndPermissionService.Instance.RevokeAppPermissionAsync(_selectedAdminUser.Hwid, "alprust-plus");
            await UserAuthAndPermissionService.Instance.RevokeAppPermissionAsync(_selectedAdminUser.Hwid, "alprust");

            if (ok)
            {
                MessageBox.Show($"'{_selectedAdminUser.DisplayName}' için AlpRust+ yetkisi kaldırıldı.", "Yetki Kaldırıldı", MessageBoxButton.OK, MessageBoxImage.Information);
                await RefreshAdminUsersAsync();
            }
            else
            {
                MessageBox.Show("Yetki kaldırılırken hata oluştu.", "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            BtnRevokeAlpRust.IsEnabled = true;
            BtnRevokeAlpRust.Content = "⛔ Yetkiyi Kaldır";
        }
        #endregion

        private async void BtnDeleteUserRecord_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedAdminUser == null) return;

            var res = MessageBox.Show(
                $"⚠️ TEHLİKELİ İŞLEM!\n\n'{_selectedAdminUser.DisplayName}' ({_selectedAdminUser.Hwid}) kullanıcısını ve tüm verilerini veritabanından kalıcı olarak silmek istediğinize emin misiniz?\n\nBu işlem geri alınamaz!",
                "Kullanıcıyı Tamamen Sil",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (res != MessageBoxResult.Yes) return;

            BtnDeleteUserRecord.IsEnabled = false;
            BtnDeleteUserRecord.Content = "Siliniyor...";

            bool ok = await UserAuthAndPermissionService.Instance.DeleteUserAsync(_selectedAdminUser.Hwid);
            if (ok)
            {
                MessageBox.Show("Kullanıcı veritabanından tamamen silindi.", "Silindi", MessageBoxButton.OK, MessageBoxImage.Information);
                _selectedAdminUser = null;
                UpdateSelectedUserUi(null);
                await RefreshAdminUsersAsync();
            }
            else
            {
                MessageBox.Show("Kullanıcı silinirken hata oluştu.", "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
                BtnDeleteUserRecord.IsEnabled = true;
                BtnDeleteUserRecord.Content = "🗑 Kullanıcıyı Tamamen Sil";
            }
        }

        private void BtnDevSubTabPublisher_Click(object sender, RoutedEventArgs e)
        {
            SwitchDevSubView(DevSubViewPublisher, BtnDevSubTabPublisher);
        }

        private void BtnDevSubTabKeygen_Click(object sender, RoutedEventArgs e)
        {
            SwitchDevSubView(DevSubViewKeygen, null);
            TxtDevThisHwid.Text = DeveloperAuthService.Instance.DeviceHwid;
        }

        private void BtnDevSubTabSettings_Click(object sender, RoutedEventArgs e)
        {
            SwitchDevSubView(DevSubViewSettings, BtnDevSubTabSettings);
            LoadSettingsIntoUi();
        }

        private void BtnDevLockMode_Click(object sender, RoutedEventArgs e)
        {
            var res = MessageBox.Show(
                "Geliştirici modundan çıkıp standart kullanıcı görünümüne geçmek istiyor musunuz?\n\n(Tekrar açmak için Ctrl+Shift+D tuşlayabilir veya sol üst logoya 5 kez tıklayabilirsiniz)",
                "Geliştirici Modunu Kapat",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (res == MessageBoxResult.Yes)
            {
                DeveloperAuthService.Instance.LockAdminMode();
                SwitchToView(ViewStoreLibrary);
            }
        }

        // ==================== CANLI TELEMETRİ & KULLANICI LOGLARI ====================
        private async Task RefreshDevTelemetryAsync()
        {
            BtnRefreshDevEvents.IsEnabled = false;
            BtnRefreshDevEvents.Content = "Yenileniyor...";

            try
            {
                var events = await TelemetryAndHelpService.Instance.FetchRecentEventsAsync(250);
                var errors = await TelemetryAndHelpService.Instance.FetchRecentErrorsAsync();

                // Convert errors into TelemetryEventRecord if missing
                foreach (var err in errors)
                {
                    if (!events.Any(x => x.EventId == err.ReportId))
                    {
                        events.Add(new TelemetryEventRecord
                        {
                            EventId = err.ReportId,
                            Timestamp = err.Timestamp,
                            DeviceId = err.DeviceId,
                            MachineName = err.MachineName,
                            EventType = "ERROR",
                            AppId = err.AppId,
                            AppName = err.AppName,
                            AppVersion = err.AppVersion,
                            Details = err.ErrorMessage,
                            ExtraInfo = err.StackTrace
                        });
                    }
                }

                _allDevEvents = events.OrderByDescending(x => x.Timestamp).ToList();

                // Update Metric Stat Counters
                TxtDevStatTotalEvents.Text = $"{_allDevEvents.Count} Olay";
                TxtDevStatDownloads.Text = $"{_allDevEvents.Count(x => x.EventType == "DOWNLOAD_STARTED" || x.EventType == "INSTALL_SUCCESS")} İşlem";
                TxtDevStatErrors.Text = $"{_allDevEvents.Count(x => x.EventType == "ERROR")} Hata";
                TxtDevStatDevices.Text = $"{_allDevEvents.Select(x => x.DeviceId).Where(d => !string.IsNullOrWhiteSpace(d)).Distinct().Count()} Cihaz";

                // Populate Kişi Bazlı Log Kontrol (CmbDevFilterUser)
                if (CmbDevFilterUser != null)
                {
                    string prevSelected = CmbDevFilterUser.SelectedItem as string ?? "Tümü";
                    var userList = _allDevEvents
                        .Select(e => !string.IsNullOrWhiteSpace(e.UserName)
                            ? $"{e.UserName} ({e.MachineName})"
                            : (!string.IsNullOrWhiteSpace(e.MachineName) ? e.MachineName : e.DeviceId))
                        .Where(s => !string.IsNullOrWhiteSpace(s))
                        .Distinct()
                        .OrderBy(s => s)
                        .ToList();

                    CmbDevFilterUser.Items.Clear();
                    CmbDevFilterUser.Items.Add("Tümü");
                    foreach (var u in userList) CmbDevFilterUser.Items.Add(u);

                    if (CmbDevFilterUser.Items.Contains(prevSelected))
                        CmbDevFilterUser.SelectedItem = prevSelected;
                    else
                        CmbDevFilterUser.SelectedIndex = 0;
                }

                ApplyDevEventFilter();
            }
            catch (Exception ex)
            {
                AppLoggerService.Instance.Error("TELEMETRİ", "Telemetri yüklenirken hata: " + ex.Message);
            }
            finally
            {
                BtnRefreshDevEvents.IsEnabled = true;
                BtnRefreshDevEvents.Content = "🔄 Canlı Verileri Yenile";
            }
        }

        private void ApplyDevEventFilter()
        {
            string search = (TxtDevSearch.Text ?? "").Trim().ToLowerInvariant();

            var filtered = _allDevEvents.Where(ev =>
            {
                if (_devEventFilter == "DOWNLOADS" && ev.EventType != "DOWNLOAD_STARTED") return false;
                if (_devEventFilter == "INSTALLS" && ev.EventType != "INSTALL_SUCCESS") return false;
                if (_devEventFilter == "LAUNCHES" && ev.EventType != "APP_LAUNCHED") return false;
                if (_devEventFilter == "ERRORS" && ev.EventType != "ERROR") return false;

                if (CmbDevFilterUser?.SelectedIndex > 0 && CmbDevFilterUser.SelectedItem is string selUser && selUser != "Tümü")
                {
                    string target = !string.IsNullOrWhiteSpace(ev.UserName)
                        ? $"{ev.UserName} ({ev.MachineName})"
                        : (!string.IsNullOrWhiteSpace(ev.MachineName) ? ev.MachineName : ev.DeviceId);
                    if (!string.Equals(target, selUser, StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(ev.UserName, selUser, StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(ev.MachineName, selUser, StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(ev.DeviceId, selUser, StringComparison.OrdinalIgnoreCase))
                        return false;
                }

                if (!string.IsNullOrWhiteSpace(search))
                {
                    bool match = (ev.AppName ?? "").ToLowerInvariant().Contains(search) ||
                                 (ev.DeviceId ?? "").ToLowerInvariant().Contains(search) ||
                                 (ev.MachineName ?? "").ToLowerInvariant().Contains(search) ||
                                 (ev.Details ?? "").ToLowerInvariant().Contains(search);
                    if (!match) return false;
                }

                return true;
            }).ToList();

            LstDevTelemetryEvents.ItemsSource = filtered;

            if (filtered.Count > 0 && LstDevTelemetryEvents.SelectedItem == null)
            {
                LstDevTelemetryEvents.SelectedIndex = 0;
            }
        }

        private void BtnRefreshDevEvents_Click(object sender, RoutedEventArgs e) => _ = RefreshDevTelemetryAsync();

        private void CmbDevFilterUser_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ApplyDevEventFilter();
        }

        private async void BtnDevClearOldLogs_Click(object sender, RoutedEventArgs e)
        {
            var res = MessageBox.Show(
                "Buluttaki eski telemetri olaylarını ve hata kayıtlarını temizlemek istediğinize emin misiniz?",
                "Eski Logları Temizle",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (res != MessageBoxResult.Yes) return;

            BtnDevClearOldLogs.IsEnabled = false;
            BtnDevClearOldLogs.Content = "Temizleniyor...";
            bool ok = await UserAuthAndPermissionService.Instance.ClearOldTelemetryAsync(true);
            if (ok)
            {
                MessageBox.Show("Buluttaki telemetri ve hata kayıtları başarıyla temizlendi!", "Temizlendi", MessageBoxButton.OK, MessageBoxImage.Information);
                await RefreshDevTelemetryAsync();
            }
            else
            {
                MessageBox.Show("Kayıtlar temizlenirken bir hata oluştu.", "Hata", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            BtnDevClearOldLogs.IsEnabled = true;
            BtnDevClearOldLogs.Content = "🧹 Eskileri Temizle";
        }

        private void BtnDevFilterAll_Click(object sender, RoutedEventArgs e) { _devEventFilter = "ALL"; ApplyDevEventFilter(); }
        private void BtnDevFilterDownloads_Click(object sender, RoutedEventArgs e) { _devEventFilter = "DOWNLOADS"; ApplyDevEventFilter(); }
        private void BtnDevFilterInstalls_Click(object sender, RoutedEventArgs e) { _devEventFilter = "INSTALLS"; ApplyDevEventFilter(); }
        private void BtnDevFilterLaunches_Click(object sender, RoutedEventArgs e) { _devEventFilter = "LAUNCHES"; ApplyDevEventFilter(); }
        private void BtnDevFilterErrors_Click(object sender, RoutedEventArgs e) { _devEventFilter = "ERRORS"; ApplyDevEventFilter(); }

        private void TxtDevSearch_TextChanged(object sender, TextChangedEventArgs e) => ApplyDevEventFilter();

        private void LstDevTelemetryEvents_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (LstDevTelemetryEvents.SelectedItem is TelemetryEventRecord ev)
            {
                TxtDevDetailEventId.Text = ev.EventId;
                TxtDevDetailTime.Text = ev.Timestamp;
                TxtDevDetailType.Text = $"{ev.EventType} ({ev.EventBadgeText})";
                TxtDevDetailApp.Text = ev.AppName;
                TxtDevDetailVersion.Text = ev.AppVersion;
                TxtDevDetailDeviceId.Text = ev.DeviceId;
                TxtDevDetailMachine.Text = ev.MachineName;
                TxtDevDetailUser.Text = ev.UserName;
                TxtDevDetailMessage.Text = ev.Details;
                TxtDevDetailStackTrace.Text = string.IsNullOrWhiteSpace(ev.ExtraInfo) ? "Detaylı hata izi yok." : ev.ExtraInfo;
            }
        }

        private void BtnDevCopySelectedEvent_Click(object sender, RoutedEventArgs e)
        {
            if (LstDevTelemetryEvents.SelectedItem is TelemetryEventRecord ev)
            {
                string text = $"[Olay]: {ev.EventType} | [App]: {ev.AppName} v{ev.AppVersion}\n" +
                              $"[Zaman]: {ev.Timestamp} | [Cihaz]: {ev.DeviceId} ({ev.MachineName} - {ev.UserName})\n" +
                              $"[Detay]: {ev.Details}\n" +
                              (string.IsNullOrWhiteSpace(ev.ExtraInfo) ? "" : $"[Stack]:\n{ev.ExtraInfo}");
                Clipboard.SetText(text);
                MessageBox.Show("Olay detayı panoya kopyalandı!", "Kopyalandı", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        // ==================== ALPTOGETHER ADMIN KEYGEN ====================
        private void BtnDevCopyThisHwid_Click(object sender, RoutedEventArgs e)
        {
            Clipboard.SetText(DeveloperAuthService.Instance.DeviceHwid);
            MessageBox.Show($"Bu cihazın HWID kimliği panoya kopyalandı:\n{DeveloperAuthService.Instance.DeviceHwid}", "Kopyalandı", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void BtnDevSelfActivate_Click(object sender, RoutedEventArgs e)
        {
            string key = DeveloperAuthService.GenerateAdminKey(DeveloperAuthService.Instance.DeviceHwid, new DateTime(2099, 12, 31, 23, 59, 59, DateTimeKind.Utc));
            bool ok = DeveloperAuthService.Instance.TryUnlockAdmin(key, out string msg);
            if (ok)
            {
                MessageBox.Show("Bu cihaz başarıyla süresiz Admin olarak etkinleştirildi!", "Admin Aktif", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void BtnDevGenerateAdminKey_Click(object sender, RoutedEventArgs e)
        {
            string target = TxtDevTargetHwid.Text.Trim();
            DateTime exp = CmbDevKeyExpiry.SelectedIndex switch
            {
                0 => DateTime.UtcNow.AddDays(30),
                1 => DateTime.UtcNow.AddDays(90),
                2 => DateTime.UtcNow.AddDays(365),
                _ => new DateTime(2099, 12, 31, 23, 59, 59, DateTimeKind.Utc)
            };

            string key = DeveloperAuthService.GenerateAdminKey(target, exp);
            TxtDevGeneratedKeyResult.Text = key;
            TxtDevKeygenStatus.Text = $"✔ Lisans üretildi! (Bitiş: {exp.ToLocalTime():dd.MM.yyyy})";
        }

        private void BtnDevCopyGeneratedKey_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(TxtDevGeneratedKeyResult.Text))
            {
                Clipboard.SetText(TxtDevGeneratedKeyResult.Text);
                MessageBox.Show("Üretilen admin anahtarı panoya kopyalandı!", "Kopyalandı", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private async void BtnDevPushKeyToFirebase_Click(object sender, RoutedEventArgs e)
        {
            string targetHwid = TxtDevTargetHwid.Text.Trim();
            string key = TxtDevGeneratedKeyResult.Text.Trim();

            if (string.IsNullOrWhiteSpace(targetHwid) || string.IsNullOrWhiteSpace(key))
            {
                MessageBox.Show("Lütfen önce hedef HWID girip anahtar üretin.", "Eksik Bilgi", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            TxtDevKeygenStatus.Text = "Buluta gönderiliyor...";
            bool ok = await DeveloperAuthService.Instance.PushAdminLicenseToFirebaseAsync(targetHwid, key);
            TxtDevKeygenStatus.Text = ok
                ? "✔ Lisans Firebase bulutuna kaydedildi! Hedef cihaz otomatik yetkilendirilecek."
                : "⚠ Buluta kaydedilemedi. Firebase bağlantısını kontrol edin.";
        }

        // ==================== GİZLİ TETİKLEYİCİLER & GİRİŞ MODAL ====================
        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.D && (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) == (ModifierKeys.Control | ModifierKeys.Shift))
            {
                ShowAdminUnlockModal();
                e.Handled = true;
            }
        }

        private void BrandLogo_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if ((DateTime.Now - _lastLogoClickTime).TotalSeconds > 3)
            {
                _logoClickCount = 0;
            }

            _lastLogoClickTime = DateTime.Now;
            _logoClickCount++;

            if (_logoClickCount >= 5)
            {
                _logoClickCount = 0;
                ShowAdminUnlockModal();
                e.Handled = true;
            }
        }

        private void ShowAdminUnlockModal()
        {
            if (DeveloperAuthService.Instance.IsPrimaryDevMachine || DeveloperAuthService.Instance.IsAdminMode)
            {
                DeveloperAuthService.Instance.UnlockDeveloperModeDirectly();
                AdminUnlockOverlay.Visibility = Visibility.Collapsed;
                SwitchToView(ViewPublisherAndSettings);
                return;
            }

            TxtModalDeviceHwid.Text = DeveloperAuthService.Instance.DeviceHwid;
            TxtModalAdminInput.Text = "";
            TxtModalStatus.Text = "";
            AdminUnlockOverlay.Visibility = Visibility.Visible;
            TxtModalAdminInput.Focus();
        }

        private void BtnModalClose_Click(object sender, RoutedEventArgs e)
        {
            AdminUnlockOverlay.Visibility = Visibility.Collapsed;
        }

        private void BtnModalCopyHwid_Click(object sender, RoutedEventArgs e)
        {
            Clipboard.SetText(DeveloperAuthService.Instance.DeviceHwid);
            TxtModalStatus.Foreground = new SolidColorBrush(Color.FromRgb(0, 229, 255));
            TxtModalStatus.Text = "Cihaz HWID panoya kopyalandı!";
        }

        private void BtnModalUnlock_Click(object sender, RoutedEventArgs e)
        {
            string input = TxtModalAdminInput.Text.Trim();
            if (string.IsNullOrWhiteSpace(input))
            {
                TxtModalStatus.Foreground = new SolidColorBrush(Color.FromRgb(255, 23, 68));
                TxtModalStatus.Text = "Lütfen bir admin anahtarı veya parola girin.";
                return;
            }

            bool success = DeveloperAuthService.Instance.TryUnlockAdmin(input, out string message);
            if (success)
            {
                AdminUnlockOverlay.Visibility = Visibility.Collapsed;
                SwitchToView(ViewPublisherAndSettings);
                MessageBox.Show(message, "Geliştirici Modu Açıldı", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                TxtModalStatus.Foreground = new SolidColorBrush(Color.FromRgb(255, 23, 68));
                TxtModalStatus.Text = "Hata: " + message;
            }
        }
    }
}
