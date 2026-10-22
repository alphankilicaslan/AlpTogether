using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using AlpTogether.Services;
using Microsoft.Win32;

namespace AlpTogether.Controls
{
    public partial class CarsVideoPlayerControl : UserControl
    {
        private readonly DispatcherTimer _progressTimer;
        private readonly DispatcherTimer _toastTimer;
        private CarsVideoSourceInfo _currentSource = null!;
        private TimeSpan _duration = TimeSpan.Zero;
        private bool _isUserSeeking = false;
        private bool _isPlaying = false;
        private bool _isLoopEnabled = true;
        private bool _wasPlayingBeforeHidden = false;
        private bool _wasPlayingBeforeMinimized = false;
        private bool _isInitializing = false;
        private Window? _parentWindow;
        private readonly System.Diagnostics.Stopwatch _seekThrottleStopwatch = new();

        public CarsVideoPlayerControl()
        {
            InitializeComponent();

            _progressTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(250)
            };
            _progressTimer.Tick += ProgressTimer_Tick;

            _toastTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(2.5)
            };
            _toastTimer.Tick += (s, e) =>
            {
                _toastTimer.Stop();
                var fadeOut = new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(400));
                ToastNotificationBorder.BeginAnimation(UIElement.OpacityProperty, fadeOut);
            };
        }

        private void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            _isInitializing = true;
            try
            {
                var settings = SettingsService.Instance.Current;
                _isLoopEnabled = settings.CarsVideoIsLoopEnabled;
                VolumeSlider.Value = settings.CarsVideoVolume > 0 ? settings.CarsVideoVolume : 0.50;
                CarsMedia.Volume = VolumeSlider.Value;
                CarsMedia.IsMuted = true; // Arka plan ses patlamalarını önlemek için varsayılan olarak sessiz başlatılır

                if (settings.CarsVideoBannerCollapsed)
                {
                    ExpandedPlayerCard.Visibility = Visibility.Collapsed;
                    CollapsedMiniStrip.Visibility = Visibility.Visible;
                }
            }
            catch
            {
                VolumeSlider.Value = 0.50;
                CarsMedia.Volume = 0.50;
                CarsMedia.IsMuted = true;
            }
            finally
            {
                _isInitializing = false;
            }

            _parentWindow = Window.GetWindow(this);
            if (_parentWindow != null)
            {
                _parentWindow.StateChanged -= ParentWindow_StateChanged;
                _parentWindow.StateChanged += ParentWindow_StateChanged;
            }

            RenderOptions.SetBitmapScalingMode(CarsMedia, BitmapScalingMode.Linear);
            RenderOptions.SetEdgeMode(CarsMedia, EdgeMode.Aliased);

            BtnLoop.Content = _isLoopEnabled ? "🔁 Döngü: Açık" : "🔁 Döngü: Kapalı";
            UpdateVolumeDisplay();

            // Hibrit video kaynağını çözümle ve doğrudan akışla başlat
            InitializeVideoSource();
        }

        private void VideoViewportContainer_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (e.WidthChanged && e.NewSize.Width > 100)
            {
                // 2.3875 : 1 CinemaScope En-Boy Oranı (1146 x 480)
                // Siyah ekran / siyah şerit kalmadan kartın içine tam oturmasını sağlar!
                double targetHeight = Math.Round(e.NewSize.Width * (480.0 / 1146.0));
                if (targetHeight < 360) targetHeight = 360;
                if (targetHeight > 620) targetHeight = 620;

                if (Math.Abs(VideoViewportContainer.Height - targetHeight) > 1)
                {
                    VideoViewportContainer.Height = targetHeight;
                }
            }
        }

        private void ParentWindow_StateChanged(object? sender, EventArgs e)
        {
            if (_parentWindow == null) return;

            if (_parentWindow.WindowState == WindowState.Minimized)
            {
                if (_isPlaying)
                {
                    _wasPlayingBeforeMinimized = true;
                    CarsMedia.Pause();
                    _isPlaying = false;
                    _progressTimer.Stop();
                    UpdatePlayPauseButton();
                }
            }
            else
            {
                if (_wasPlayingBeforeMinimized)
                {
                    _wasPlayingBeforeMinimized = false;
                    CarsMedia.Play();
                    _isPlaying = true;
                    _progressTimer.Start();
                    UpdatePlayPauseButton();
                }
            }
        }

        public void InitializeVideoSource(string? overridePathOrUrl = null, bool forceCloud = false)
        {
            try
            {
                _currentSource = CarsVideoService.Instance.ResolveSource(overridePathOrUrl, forceCloud);
                ApplySourceInfo(_currentSource);

                CloudDownloadOverlay.Visibility = Visibility.Collapsed;
                CarsMedia.Visibility = Visibility.Visible;
                CarsMedia.Source = _currentSource.SourceUri;

                if (IsVisible && ExpandedPlayerCard.Visibility == Visibility.Visible)
                {
                    CarsMedia.Play();
                    _isPlaying = true;
                    _progressTimer.Start();
                }
                else
                {
                    CarsMedia.Pause();
                    _isPlaying = false;
                    _progressTimer.Stop();
                }
                UpdatePlayPauseButton();

                ShowToast(_currentSource.IsLocal ? "⚡ Yerel Diskten Oynatılıyor" : "☁️ GitHub CDN Bulut Akışı (Doğrudan)");
            }
            catch (Exception ex)
            {
                ShowError($"Kaynak yükleme hatası: {ex.Message}");
            }
        }

        private void ApplySourceInfo(CarsVideoSourceInfo source)
        {
            if (BtnChangeSource != null)
            {
                BtnChangeSource.Content = source.IsLocal ? "⚡ Kaynak" : "☁️ Kaynak";
                BtnChangeSource.ToolTip = $"Kaynak: {source.BadgeText} ({source.FileSizeFormatted})\n{source.DisplayTitle}\nDeğiştirmek için tıklayın.";
            }

            ErrorFallbackPanel.Visibility = Visibility.Collapsed;
            PosterBackdrop.Visibility = Visibility.Collapsed;
        }

        private void ProgressTimer_Tick(object? sender, EventArgs e)
        {
            if (!_isPlaying || !IsVisible)
            {
                _progressTimer.Stop();
                return;
            }

            if (!_isUserSeeking && _duration.TotalSeconds > 0)
            {
                TimelineSlider.Value = CarsMedia.Position.TotalSeconds;
            }

            if (_isUserSeeking && Mouse.LeftButton == MouseButtonState.Released)
            {
                _isUserSeeking = false;
                CarsMedia.Position = TimeSpan.FromSeconds(TimelineSlider.Value);
            }

            string currentTimeStr = FormatTime(CarsMedia.Position);
            string durationStr = FormatTime(_duration);
            TxtTimeDisplay.Text = $"{currentTimeStr} / {durationStr}";
            TxtMiniTime.Text = $" ({currentTimeStr})";
        }

        private void CarsMedia_MediaOpened(object sender, RoutedEventArgs e)
        {
            BufferingIndicator.Visibility = Visibility.Collapsed;
            ErrorFallbackPanel.Visibility = Visibility.Collapsed;
            CloudDownloadOverlay.Visibility = Visibility.Collapsed;
            PosterBackdrop.Visibility = Visibility.Collapsed;

            if (CarsMedia.NaturalDuration.HasTimeSpan)
            {
                _duration = CarsMedia.NaturalDuration.TimeSpan;
                TimelineSlider.Maximum = _duration.TotalSeconds;
            }

            // 33. saniyeden başlasın (intro logoları ve siyah ekranı atlayarak doğrudan aksiyona girer)
            double startPos = 33.0;
            try
            {
                double lastPos = SettingsService.Instance.Current.CarsVideoLastPositionSeconds;
                if (lastPos > 33.0 && lastPos < _duration.TotalSeconds - 10)
                {
                    startPos = lastPos;
                }
            }
            catch { }

            CarsMedia.Position = TimeSpan.FromSeconds(startPos);
            TimelineSlider.Value = startPos;
            TxtTimeDisplay.Text = $"{FormatTime(CarsMedia.Position)} / {FormatTime(_duration)}";
        }

        private void CarsMedia_MediaEnded(object sender, RoutedEventArgs e)
        {
            if (_isLoopEnabled)
            {
                CarsMedia.Stop();
                CarsMedia.Position = TimeSpan.FromSeconds(33.0);
                CarsMedia.Play();
                _isPlaying = true;
                _progressTimer.Start();
            }
            else
            {
                _isPlaying = false;
                CarsMedia.Pause();
                _progressTimer.Stop();
            }
            UpdatePlayPauseButton();
        }

        private void CarsMedia_MediaFailed(object? sender, ExceptionRoutedEventArgs e)
        {
            BufferingIndicator.Visibility = Visibility.Collapsed;
            _progressTimer.Stop();
            _isPlaying = false;
            UpdatePlayPauseButton();
            string msg = e.ErrorException?.Message ?? "Video akışı açılamadı.";

            if (_currentSource.IsLocal)
            {
                ShowToast("⚠️ Yerel dosya okunamadı, bulut akışına geçiliyor...");
                InitializeVideoSource(forceCloud: true);
            }
            else
            {
                ShowError($"Video açılamadı: {msg}\nLütfen internet bağlantınızı kontrol edin veya '📁 Kaynak' menüsünden yerel bir dosya seçin.");
            }
        }

        private void ShowError(string msg)
        {
            TxtErrorMessage.Text = msg;
            ErrorFallbackPanel.Visibility = Visibility.Visible;
            PosterBackdrop.Visibility = Visibility.Visible;
        }

        private void CarsMedia_BufferingStarted(object sender, RoutedEventArgs e)
        {
            BufferingIndicator.Visibility = Visibility.Visible;
        }

        private void CarsMedia_BufferingEnded(object sender, RoutedEventArgs e)
        {
            BufferingIndicator.Visibility = Visibility.Collapsed;
        }

        private void VideoViewport_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                BtnFullscreen_Click(sender, e);
                return;
            }
            TogglePlayPause();
        }

        private void CenterBigPlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            TogglePlayPause();
        }

        private void BtnPlayPause_Click(object sender, RoutedEventArgs e)
        {
            TogglePlayPause();
        }

        public void TogglePlayPause()
        {
            if (_isPlaying)
            {
                CarsMedia.Pause();
                _isPlaying = false;
                _progressTimer.Stop();
                ShowToast("⏸ Duraklatıldı");
            }
            else
            {
                CarsMedia.Play();
                _isPlaying = true;
                _progressTimer.Start();
                ShowToast("▶ Oynatılıyor");
            }
            UpdatePlayPauseButton();
        }

        private void UpdatePlayPauseButton()
        {
            BtnPlayPause.Content = _isPlaying ? "⏸ Duraklat" : "▶ Oynat";
            BtnMiniPlayPause.Content = _isPlaying ? "⏸ Duraklat" : "▶ Oynat";
            CenterBigPlayOverlay.Visibility = _isPlaying ? Visibility.Collapsed : Visibility.Visible;
        }

        private void BtnRewind10_Click(object sender, RoutedEventArgs e)
        {
            SeekRelative(-10);
        }

        private void BtnForward10_Click(object sender, RoutedEventArgs e)
        {
            SeekRelative(10);
        }

        private void SeekRelative(double seconds)
        {
            var target = CarsMedia.Position.Add(TimeSpan.FromSeconds(seconds));
            if (target < TimeSpan.Zero) target = TimeSpan.Zero;
            if (_duration > TimeSpan.Zero && target > _duration) target = _duration;
            CarsMedia.Position = target;
            TimelineSlider.Value = target.TotalSeconds;
            ShowToast(seconds > 0 ? "⏩ +10sn İleri" : "⏪ -10sn Geri");
        }

        private void TimelineSlider_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            _isUserSeeking = true;
            _seekThrottleStopwatch.Restart();
        }

        private void TimelineSlider_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            _isUserSeeking = false;
            CarsMedia.Position = TimeSpan.FromSeconds(TimelineSlider.Value);
            _seekThrottleStopwatch.Reset();
        }

        private void TimelineSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isUserSeeking)
            {
                string currentTimeStr = FormatTime(TimeSpan.FromSeconds(e.NewValue));
                string durationStr = FormatTime(_duration);
                TxtTimeDisplay.Text = $"{currentTimeStr} / {durationStr}";

                if (!_seekThrottleStopwatch.IsRunning || _seekThrottleStopwatch.ElapsedMilliseconds >= 150)
                {
                    CarsMedia.Position = TimeSpan.FromSeconds(e.NewValue);
                    _seekThrottleStopwatch.Restart();
                }
            }
        }

        private void BtnLoop_Click(object sender, RoutedEventArgs e)
        {
            _isLoopEnabled = !_isLoopEnabled;
            BtnLoop.Content = _isLoopEnabled ? "🔁 Döngü: Açık" : "🔁 Döngü: Kapalı";
            ShowToast(_isLoopEnabled ? "🔁 Otomatik Döngü Açık" : "⏹ Döngü Kapatıldı");

            try
            {
                SettingsService.Instance.Current.CarsVideoIsLoopEnabled = _isLoopEnabled;
                SettingsService.Instance.Save();
            }
            catch { }
        }

        private void BtnMute_Click(object sender, RoutedEventArgs e)
        {
            CarsMedia.IsMuted = !CarsMedia.IsMuted;
            UpdateVolumeDisplay();
            ShowToast(CarsMedia.IsMuted ? "🔇 Ses Kapatıldı (Sessiz)" : $"🔊 Ses Açık ({(int)(CarsMedia.Volume * 100)}%)");

            try
            {
                SettingsService.Instance.Current.CarsVideoIsMuted = CarsMedia.IsMuted;
                SettingsService.Instance.Save();
            }
            catch { }
        }

        private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isInitializing) return;

            if (CarsMedia != null)
            {
                CarsMedia.Volume = e.NewValue;
                if (CarsMedia.IsMuted && e.NewValue > 0)
                {
                    CarsMedia.IsMuted = false;
                }
                UpdateVolumeDisplay();

                try
                {
                    SettingsService.Instance.Current.CarsVideoVolume = e.NewValue;
                    SettingsService.Instance.Current.CarsVideoIsMuted = CarsMedia.IsMuted;
                    SettingsService.Instance.Save();
                }
                catch { }
            }
        }

        private void UpdateVolumeDisplay()
        {
            if (CarsMedia == null || BtnMute == null || TxtVolumePercent == null)
                return;

            if (CarsMedia.IsMuted || CarsMedia.Volume == 0)
            {
                BtnMute.Content = "🔇";
                TxtVolumePercent.Text = "Sessiz";
            }
            else
            {
                BtnMute.Content = CarsMedia.Volume > 0.5 ? "🔊" : "🔉";
                TxtVolumePercent.Text = $"{(int)(CarsMedia.Volume * 100)}%";
            }
        }

        private void BtnFullscreen_Click(object sender, RoutedEventArgs e)
        {
            bool wasPlaying = _isPlaying;
            CarsMedia.Pause();

            var startPos = CarsMedia.Position.TotalSeconds < 33.0
                ? TimeSpan.FromSeconds(33.0)
                : CarsMedia.Position;

            var theater = new CarsTheaterWindow(
                _currentSource,
                startPos,
                CarsMedia.Volume,
                CarsMedia.IsMuted,
                _isLoopEnabled)
            {
                Owner = Window.GetWindow(this)
            };

            theater.ShowDialog();

            // Tam ekrandan dönüldüğünde değerleri eşle
            CarsMedia.Position = theater.CurrentPosition;
            CarsMedia.Volume = theater.FinalVolume;
            CarsMedia.IsMuted = theater.FinalIsMuted;
            _isLoopEnabled = theater.FinalIsLoopEnabled;
            BtnLoop.Content = _isLoopEnabled ? "🔁 Döngü: Açık" : "🔁 Döngü: Kapalı";
            VolumeSlider.Value = theater.FinalVolume;
            UpdateVolumeDisplay();

            if (theater.IsPlaying || wasPlaying)
            {
                CarsMedia.Play();
                _isPlaying = true;
            }
            else
            {
                _isPlaying = false;
            }
            UpdatePlayPauseButton();
        }

        private void BtnChangeSource_Click(object sender, RoutedEventArgs e)
        {
            if (BtnChangeSource.ContextMenu != null)
            {
                BtnChangeSource.ContextMenu.PlacementTarget = BtnChangeSource;
                BtnChangeSource.ContextMenu.IsOpen = true;
            }
        }

        private void MenuSourceAuto_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                SettingsService.Instance.Current.CarsVideoCustomPath = string.Empty;
                SettingsService.Instance.Save();
            }
            catch { }
            InitializeVideoSource();
        }

        private void MenuSourceCloud_Click(object sender, RoutedEventArgs e)
        {
            InitializeVideoSource(forceCloud: true);
        }

        private void MenuSourceBrowse_Click(object sender, RoutedEventArgs e)
        {
            var ofd = new OpenFileDialog
            {
                Title = "Arabalar 1 (Cars 2006) Video Dosyasını Seçin",
                Filter = "MP4 Video Dosyaları (*.mp4)|*.mp4|Tüm Medya Dosyaları (*.*)|*.*",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            };

            if (ofd.ShowDialog() == true && File.Exists(ofd.FileName))
            {
                try
                {
                    SettingsService.Instance.Current.CarsVideoCustomPath = ofd.FileName;
                    SettingsService.Instance.Save();
                }
                catch { }

                InitializeVideoSource(ofd.FileName);
            }
        }

        private void BtnExpandBanner_Click(object sender, RoutedEventArgs e)
        {
            CollapsedMiniStrip.Visibility = Visibility.Collapsed;
            ExpandedPlayerCard.Visibility = Visibility.Visible;

            if (_wasPlayingBeforeHidden)
            {
                _wasPlayingBeforeHidden = false;
                CarsMedia.Play();
                _isPlaying = true;
                _progressTimer.Start();
                UpdatePlayPauseButton();
            }

            try
            {
                SettingsService.Instance.Current.CarsVideoBannerCollapsed = false;
                SettingsService.Instance.Save();
            }
            catch { }
        }

        private void ShowToast(string message)
        {
            TxtToastMessage.Text = message;
            _toastTimer.Stop();
            var fadeIn = new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(200));
            ToastNotificationBorder.BeginAnimation(UIElement.OpacityProperty, fadeIn);
            _toastTimer.Start();
        }

        private void UserControl_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (!IsVisible)
            {
                _progressTimer.Stop();
                if (_isPlaying)
                {
                    _wasPlayingBeforeHidden = true;
                    CarsMedia.Pause();
                    _isPlaying = false;
                    UpdatePlayPauseButton();
                }
            }
            else
            {
                if (_wasPlayingBeforeHidden && ExpandedPlayerCard.Visibility == Visibility.Visible)
                {
                    _wasPlayingBeforeHidden = false;
                    CarsMedia.Play();
                    _isPlaying = true;
                    _progressTimer.Start();
                    UpdatePlayPauseButton();
                }
            }
        }

        private void UserControl_Unloaded(object sender, RoutedEventArgs e)
        {
            if (_parentWindow != null)
            {
                _parentWindow.StateChanged -= ParentWindow_StateChanged;
            }

            _progressTimer.Stop();
            _toastTimer.Stop();

            try
            {
                if (CarsMedia.Position.TotalSeconds >= 33)
                {
                    SettingsService.Instance.Current.CarsVideoLastPositionSeconds = CarsMedia.Position.TotalSeconds;
                    SettingsService.Instance.Save();
                }
            }
            catch { }

            // EVR / DirectShow COM grafiklerini ve dosya handle'ını serbest bırak
            CarsMedia.Stop();
            CarsMedia.Close();
            CarsMedia.Source = null;
            _isPlaying = false;
        }

        public void ReleaseResources()
        {
            UserControl_Unloaded(this, new RoutedEventArgs());
        }

        public void PausePlayback()
        {
            _progressTimer.Stop();
            if (_isPlaying)
            {
                CarsMedia.Pause();
                _isPlaying = false;
                UpdatePlayPauseButton();
            }
        }

        private static string FormatTime(TimeSpan t)
        {
            return t.TotalHours >= 1
                ? t.ToString(@"hh\:mm\:ss")
                : t.ToString(@"mm\:ss");
        }
    }
}
