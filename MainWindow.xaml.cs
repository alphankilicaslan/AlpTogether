using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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

        public MainWindow()
        {
            InitializeComponent();

            _processMonitorTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(3)
            };
            _processMonitorTimer.Tick += ProcessMonitorTimer_Tick;

            Loaded += MainWindow_Loaded;
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

            // Load incoming tickets & error telemetry in background
            _ = RefreshTelemetryInboxAsync();
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
        private void BtnNavPublisher_Click(object sender, RoutedEventArgs e) => SwitchToView(ViewPublisherAndSettings);

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
            if (WindowState == WindowState.Maximized)
            {
                WindowState = WindowState.Normal;
                OuterWindowBorder.CornerRadius = new CornerRadius(16);
            }
            else
            {
                WindowState = WindowState.Maximized;
                OuterWindowBorder.CornerRadius = new CornerRadius(0);
            }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Application.Current.Shutdown();

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
    }
}
