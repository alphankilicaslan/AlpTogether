using System;
using System.IO;
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
        private bool _isExpandedTheater = false;
        private bool _wasPlayingBeforeHidden = false;
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
            // Ayarları yükle
            try
            {
                var settings = SettingsService.Instance.Current;
                _isLoopEnabled = settings.CarsVideoIsLoopEnabled;
                _isExpandedTheater = settings.CarsVideoIsExpandedTheater;
                VolumeSlider.Value = settings.CarsVideoVolume > 0 ? settings.CarsVideoVolume : 0.50;
                CarsMedia.Volume = VolumeSlider.Value;
                CarsMedia.IsMuted = settings.CarsVideoIsMuted;

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

            RenderOptions.SetBitmapScalingMode(CarsMedia, BitmapScalingMode.Linear);
            RenderOptions.SetEdgeMode(CarsMedia, EdgeMode.Aliased);

            ApplyTheaterHeight();
            BtnLoop.Content = _isLoopEnabled ? "🔁 Döngü: Açık" : "🔁 Döngü: Kapalı";
            UpdateVolumeDisplay();

            // Hibrit video kaynağını çözümle ve başlat
            InitializeVideoSource();
        }

        public void InitializeVideoSource(string? overridePathOrUrl = null, bool forceCloud = false)
        {
            try
            {
                _currentSource = CarsVideoService.Instance.ResolveSource(overridePathOrUrl, forceCloud);
                ApplySourceInfo(_currentSource);

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

                ShowToast(_currentSource.IsLocal ? "⚡ Yerel NVMe Diskten Oynatılıyor" : "☁️ GitHub CDN Bulut Akışına Bağlandı");
            }
            catch (Exception ex)
            {
                ShowError($"Kaynak yükleme hatası: {ex.Message}");
            }
        }

        private void ApplySourceInfo(CarsVideoSourceInfo source)
        {
            TxtSourceBadge.Text = source.BadgeText;
            TxtFileSize.Text = source.FileSizeFormatted;

            if (source.IsLocal)
            {
                SourceBadgeBorder.BorderBrush = Brushes.MediumSpringGreen;
                TxtSourceBadge.Foreground = Brushes.MediumSpringGreen;
            }
            else
            {
                SourceBadgeBorder.BorderBrush = (Brush)FindResource("PinkHotBrush");
                TxtSourceBadge.Foreground = (Brush)FindResource("PinkHotBrush");
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

            string currentTimeStr = FormatTime(CarsMedia.Position);
            string durationStr = FormatTime(_duration);
            TxtTimeDisplay.Text = $"{currentTimeStr} / {durationStr}";
            TxtMiniTime.Text = $" ({currentTimeStr})";
        }

        private void CarsMedia_MediaOpened(object sender, RoutedEventArgs e)
        {
            BufferingIndicator.Visibility = Visibility.Collapsed;
            ErrorFallbackPanel.Visibility = Visibility.Collapsed;
            PosterBackdrop.Visibility = Visibility.Collapsed;

            if (CarsMedia.NaturalDuration.HasTimeSpan)
            {
                _duration = CarsMedia.NaturalDuration.TimeSpan;
                TimelineSlider.Maximum = _duration.TotalSeconds;
            }

            // Önceki kayıtlı konuma devam et (varsa)
            try
            {
                double lastPos = SettingsService.Instance.Current.CarsVideoLastPositionSeconds;
                if (lastPos > 5 && lastPos < _duration.TotalSeconds - 10)
                {
                    CarsMedia.Position = TimeSpan.FromSeconds(lastPos);
                }
            }
            catch { }

            TxtTimeDisplay.Text = $"{FormatTime(CarsMedia.Position)} / {FormatTime(_duration)}";
        }

        private void CarsMedia_MediaEnded(object sender, RoutedEventArgs e)
        {
            if (_isLoopEnabled)
            {
                CarsMedia.Position = TimeSpan.Zero;
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
            ShowError(msg);

            // Eğer yerel dosya başarısız olduysa otomatik olarak bulut fallback dene
            if (_currentSource.IsLocal)
            {
                ShowToast("⚠️ Yerel dosya okunamadı, bulut akışına geçiliyor...");
                InitializeVideoSource(forceCloud: true);
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
                ShowToast("⏸ Duraklatıldı");
            }
            else
            {
                CarsMedia.Play();
                _isPlaying = true;
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
        }

        private void TimelineSlider_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            _isUserSeeking = false;
            CarsMedia.Position = TimeSpan.FromSeconds(TimelineSlider.Value);
        }

        private void TimelineSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isUserSeeking)
            {
                CarsMedia.Position = TimeSpan.FromSeconds(e.NewValue);
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

        private void BtnToggleTheater_Click(object sender, RoutedEventArgs e)
        {
            _isExpandedTheater = !_isExpandedTheater;
            ApplyTheaterHeight();
            ShowToast(_isExpandedTheater ? "📐 Sinema Genişletilmiş Boyut" : "📐 Standart Kompakt Boyut");

            try
            {
                SettingsService.Instance.Current.CarsVideoIsExpandedTheater = _isExpandedTheater;
                SettingsService.Instance.Save();
            }
            catch { }
        }

        private void ApplyTheaterHeight()
        {
            VideoViewportContainer.Height = _isExpandedTheater ? 460 : 270;
            BtnToggleTheater.Content = _isExpandedTheater ? "📐 Normal Boyut" : "📐 Sinema Boyutu";
        }

        private void BtnFullscreen_Click(object sender, RoutedEventArgs e)
        {
            bool wasPlaying = _isPlaying;
            CarsMedia.Pause();

            var theater = new CarsTheaterWindow(
                _currentSource,
                CarsMedia.Position,
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

        private void BtnCollapseBanner_Click(object sender, RoutedEventArgs e)
        {
            ExpandedPlayerCard.Visibility = Visibility.Collapsed;
            CollapsedMiniStrip.Visibility = Visibility.Visible;

            try
            {
                SettingsService.Instance.Current.CarsVideoBannerCollapsed = true;
                SettingsService.Instance.Save();
            }
            catch { }
        }

        private void BtnExpandBanner_Click(object sender, RoutedEventArgs e)
        {
            CollapsedMiniStrip.Visibility = Visibility.Collapsed;
            ExpandedPlayerCard.Visibility = Visibility.Visible;

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
                if (_isPlaying)
                {
                    _wasPlayingBeforeHidden = true;
                    CarsMedia.Pause();
                }
            }
            else
            {
                if (_wasPlayingBeforeHidden)
                {
                    _wasPlayingBeforeHidden = false;
                    CarsMedia.Play();
                }
            }
        }

        private void UserControl_Unloaded(object sender, RoutedEventArgs e)
        {
            _progressTimer.Stop();
            _toastTimer.Stop();

            try
            {
                if (CarsMedia.Position.TotalSeconds > 0)
                {
                    SettingsService.Instance.Current.CarsVideoLastPositionSeconds = CarsMedia.Position.TotalSeconds;
                    SettingsService.Instance.Save();
                }
            }
            catch { }

            CarsMedia.Stop();
            CarsMedia.Close();
        }

        public void PausePlayback()
        {
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
