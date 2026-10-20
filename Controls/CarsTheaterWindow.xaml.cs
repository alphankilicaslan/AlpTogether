using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using AlpTogether.Services;

namespace AlpTogether.Controls
{
    public partial class CarsTheaterWindow : Window
    {
        private readonly DispatcherTimer _progressTimer;
        private readonly DispatcherTimer _autoHideTimer;
        private readonly System.Diagnostics.Stopwatch _seekThrottleStopwatch = new();
        private bool _isUserSeeking = false;
        private bool _isPlaying = true;
        private bool _isLoopEnabled = true;
        private TimeSpan _duration = TimeSpan.Zero;
        private TimeSpan _savedPosition = TimeSpan.Zero;

        public TimeSpan CurrentPosition => _savedPosition;
        public bool IsPlaying => _isPlaying;
        public double FinalVolume => TheaterMedia.Volume;
        public bool FinalIsMuted => TheaterMedia.IsMuted;
        public bool FinalIsLoopEnabled => _isLoopEnabled;

        public CarsTheaterWindow(CarsVideoSourceInfo sourceInfo, TimeSpan startPosition, double volume, bool isMuted, bool isLoopEnabled)
        {
            _progressTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(200)
            };
            _progressTimer.Tick += ProgressTimer_Tick;

            _autoHideTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(3)
            };
            _autoHideTimer.Tick += AutoHideTimer_Tick;

            InitializeComponent();

            _isLoopEnabled = isLoopEnabled;
            BtnLoop.Content = _isLoopEnabled ? "🔁 Döngü: Açık" : "🔁 Döngü: Kapalı";

            TxtHeaderSource.Text = sourceInfo.BadgeText;
            if (sourceInfo.IsLocal)
            {
                HeaderSourceBadge.BorderBrush = Brushes.MediumSpringGreen;
                TxtHeaderSource.Foreground = Brushes.MediumSpringGreen;
            }
            else
            {
                var hotBrush = TryFindResource("PinkHotBrush") as Brush ?? new SolidColorBrush(Color.FromRgb(0xFF, 0x5C, 0xAA));
                HeaderSourceBadge.BorderBrush = hotBrush;
                TxtHeaderSource.Foreground = hotBrush;
            }

            TheaterMedia.Volume = volume;
            TheaterMedia.IsMuted = isMuted;
            VolumeSlider.Value = volume;
            UpdateVolumeDisplay();

            _savedPosition = startPosition;
            TheaterMedia.Source = sourceInfo.SourceUri;
            TheaterMedia.Position = startPosition;

            Loaded += (s, e) =>
            {
                RenderOptions.SetBitmapScalingMode(TheaterMedia, BitmapScalingMode.Linear);
                RenderOptions.SetEdgeMode(TheaterMedia, EdgeMode.Aliased);

                TheaterMedia.Play();
                _isPlaying = true;
                _progressTimer.Start();
                _autoHideTimer.Start();
                UpdatePlayPauseButton();
            };

            StateChanged += TheaterWindow_StateChanged;
        }

        private bool _wasPlayingBeforeMinimized = false;

        private void TheaterWindow_StateChanged(object? sender, EventArgs e)
        {
            if (WindowState == WindowState.Minimized)
            {
                if (_isPlaying)
                {
                    _wasPlayingBeforeMinimized = true;
                    TheaterMedia.Pause();
                    _isPlaying = false;
                    _progressTimer.Stop();
                    _autoHideTimer.Stop();
                    UpdatePlayPauseButton();
                }
            }
            else
            {
                if (_wasPlayingBeforeMinimized)
                {
                    _wasPlayingBeforeMinimized = false;
                    TheaterMedia.Play();
                    _isPlaying = true;
                    _progressTimer.Start();
                    _autoHideTimer.Start();
                    UpdatePlayPauseButton();
                }
            }
        }

        private void ProgressTimer_Tick(object? sender, EventArgs e)
        {
            if (!_isPlaying)
            {
                _progressTimer.Stop();
                return;
            }

            if (!_isUserSeeking && _duration.TotalSeconds > 0)
            {
                TimelineSlider.Value = TheaterMedia.Position.TotalSeconds;
            }
            // Mouse release safeguard when dragging
            if (_isUserSeeking && Mouse.LeftButton == MouseButtonState.Released)
            {
                _isUserSeeking = false;
                TheaterMedia.Position = TimeSpan.FromSeconds(TimelineSlider.Value);
                _savedPosition = TheaterMedia.Position;
                _seekThrottleStopwatch.Reset();
            }
            if (!_isUserSeeking)
            {
                _savedPosition = TheaterMedia.Position;
            }
            TxtTimeDisplay.Text = $"{FormatTime(TheaterMedia.Position)} / {FormatTime(_duration)}";
        }

        private void AutoHideTimer_Tick(object? sender, EventArgs e)
        {
            if (TopHeaderBar.Opacity > 0)
            {
                var fadeOut = new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(400));
                TopHeaderBar.BeginAnimation(UIElement.OpacityProperty, fadeOut);
                BottomControlBar.BeginAnimation(UIElement.OpacityProperty, fadeOut);
                Cursor = Cursors.None;
            }
        }

        private void ResetAutoHideTimer()
        {
            if (TopHeaderBar == null || BottomControlBar == null || _autoHideTimer == null)
                return;

            if (TopHeaderBar.Opacity < 1.0)
            {
                var fadeIn = new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(200));
                TopHeaderBar.BeginAnimation(UIElement.OpacityProperty, fadeIn);
                BottomControlBar.BeginAnimation(UIElement.OpacityProperty, fadeIn);
            }
            Cursor = Cursors.Arrow;
            _autoHideTimer.Stop();
            _autoHideTimer.Start();
        }

        private void Window_MouseMove(object sender, MouseEventArgs e)
        {
            ResetAutoHideTimer();
        }

        private void TheaterMedia_MediaOpened(object sender, RoutedEventArgs e)
        {
            if (TheaterMedia.NaturalDuration.HasTimeSpan)
            {
                _duration = TheaterMedia.NaturalDuration.TimeSpan;
                TimelineSlider.Maximum = _duration.TotalSeconds;
            }
            TxtTimeDisplay.Text = $"{FormatTime(TheaterMedia.Position)} / {FormatTime(_duration)}";
        }

        private void TheaterMedia_MediaEnded(object sender, RoutedEventArgs e)
        {
            if (_isLoopEnabled)
            {
                TheaterMedia.Stop();
                TheaterMedia.Position = TimeSpan.Zero;
                TheaterMedia.Play();
                _isPlaying = true;
                _savedPosition = TimeSpan.Zero;
            }
            else
            {
                _isPlaying = false;
                TheaterMedia.Pause();
            }
            UpdatePlayPauseButton();
        }

        private void TheaterMedia_MediaFailed(object? sender, ExceptionRoutedEventArgs e)
        {
            MessageBox.Show($"Video oynatma hatası: {e.ErrorException?.Message}", "Arabalar 1 Sinema Modu", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private void TheaterMedia_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                Close();
                return;
            }
            TogglePlayPause();
        }

        private void BtnPlayPause_Click(object sender, RoutedEventArgs e)
        {
            TogglePlayPause();
        }

        private void TogglePlayPause()
        {
            if (_isPlaying)
            {
                TheaterMedia.Pause();
                _isPlaying = false;
                _progressTimer.Stop();
                ShowCenterFlash("⏸");
            }
            else
            {
                TheaterMedia.Play();
                _isPlaying = true;
                _progressTimer.Start();
                ShowCenterFlash("▶");
            }
            UpdatePlayPauseButton();
            ResetAutoHideTimer();
        }

        private void UpdatePlayPauseButton()
        {
            BtnPlayPause.Content = _isPlaying ? "⏸ Duraklat" : "▶ Oynat";
        }

        private void ShowCenterFlash(string icon)
        {
            TxtCenterActionIcon.Text = icon;
            var sb = new Storyboard();
            var anim = new DoubleAnimationUsingKeyFrames();
            anim.KeyFrames.Add(new DiscreteDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            anim.KeyFrames.Add(new LinearDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(700))));
            Storyboard.SetTarget(anim, CenterActionIndicator);
            Storyboard.SetTargetProperty(anim, new PropertyPath(UIElement.OpacityProperty));
            sb.Children.Add(anim);
            sb.Begin();
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
            var target = TheaterMedia.Position.Add(TimeSpan.FromSeconds(seconds));
            if (target < TimeSpan.Zero) target = TimeSpan.Zero;
            if (_duration > TimeSpan.Zero && target > _duration) target = _duration;
            TheaterMedia.Position = target;
            _savedPosition = target;
            TimelineSlider.Value = target.TotalSeconds;
            ShowCenterFlash(seconds > 0 ? "⏩" : "⏪");
            ResetAutoHideTimer();
        }

        private void TimelineSlider_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            _isUserSeeking = true;
            _seekThrottleStopwatch.Restart();
            ResetAutoHideTimer();
        }

        private void TimelineSlider_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            _isUserSeeking = false;
            TheaterMedia.Position = TimeSpan.FromSeconds(TimelineSlider.Value);
            _savedPosition = TheaterMedia.Position;
            _seekThrottleStopwatch.Reset();
            ResetAutoHideTimer();
        }

        private void TimelineSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isUserSeeking)
            {
                TxtTimeDisplay.Text = $"{FormatTime(TimeSpan.FromSeconds(e.NewValue))} / {FormatTime(_duration)}";
                if (!_seekThrottleStopwatch.IsRunning || _seekThrottleStopwatch.ElapsedMilliseconds >= 150)
                {
                    TheaterMedia.Position = TimeSpan.FromSeconds(e.NewValue);
                    _savedPosition = TheaterMedia.Position;
                    _seekThrottleStopwatch.Restart();
                }
            }
            ResetAutoHideTimer();
        }

        private void BtnLoop_Click(object sender, RoutedEventArgs e)
        {
            _isLoopEnabled = !_isLoopEnabled;
            BtnLoop.Content = _isLoopEnabled ? "🔁 Döngü: Açık" : "🔁 Döngü: Kapalı";
            ResetAutoHideTimer();
        }

        private void BtnMute_Click(object sender, RoutedEventArgs e)
        {
            TheaterMedia.IsMuted = !TheaterMedia.IsMuted;
            UpdateVolumeDisplay();
            ResetAutoHideTimer();
        }

        private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (TheaterMedia != null)
            {
                TheaterMedia.Volume = e.NewValue;
                if (TheaterMedia.IsMuted && e.NewValue > 0)
                {
                    TheaterMedia.IsMuted = false;
                }
                UpdateVolumeDisplay();
            }
            ResetAutoHideTimer();
        }

        private void UpdateVolumeDisplay()
        {
            if (TheaterMedia == null || BtnMute == null || TxtVolumePercent == null)
                return;

            if (TheaterMedia.IsMuted || TheaterMedia.Volume == 0)
            {
                BtnMute.Content = "🔇";
                TxtVolumePercent.Text = "Sessiz";
            }
            else
            {
                BtnMute.Content = TheaterMedia.Volume > 0.5 ? "🔊" : "🔉";
                TxtVolumePercent.Text = $"{(int)(TheaterMedia.Volume * 100)}%";
            }
        }

        private void BtnExitFullscreen_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            ResetAutoHideTimer();
            switch (e.Key)
            {
                case Key.Escape:
                case Key.F:
                case Key.F11:
                    Close();
                    e.Handled = true;
                    break;
                case Key.Space:
                    TogglePlayPause();
                    e.Handled = true;
                    break;
                case Key.Left:
                    SeekRelative(-10);
                    e.Handled = true;
                    break;
                case Key.Right:
                    SeekRelative(10);
                    e.Handled = true;
                    break;
                case Key.Up:
                    VolumeSlider.Value = Math.Min(1.0, VolumeSlider.Value + 0.05);
                    e.Handled = true;
                    break;
                case Key.Down:
                    VolumeSlider.Value = Math.Max(0.0, VolumeSlider.Value - 0.05);
                    e.Handled = true;
                    break;
                case Key.M:
                    BtnMute_Click(this, new RoutedEventArgs());
                    e.Handled = true;
                    break;
            }
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            try
            {
                if (TheaterMedia.Position.TotalSeconds > 0)
                {
                    _savedPosition = TheaterMedia.Position;
                }
            }
            catch { }
            base.OnClosing(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            StateChanged -= TheaterWindow_StateChanged;
            _progressTimer.Stop();
            _autoHideTimer.Stop();
            TheaterMedia.Stop();
            TheaterMedia.Close();
            TheaterMedia.Source = null;
            _isPlaying = false;
            base.OnClosed(e);
        }

        private static string FormatTime(TimeSpan t)
        {
            return t.TotalHours >= 1
                ? t.ToString(@"hh\:mm\:ss")
                : t.ToString(@"mm\:ss");
        }
    }
}
