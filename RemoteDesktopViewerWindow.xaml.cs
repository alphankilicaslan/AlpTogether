using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Net.Http;
using AlpTogether.Services;

namespace AlpTogether
{
    public partial class RemoteDesktopViewerWindow : Window
    {
        private readonly string _targetHwid;
        private readonly string _targetUsername;
        private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(8) };
        private CancellationTokenSource? _sessionCts;
        private long _lastFrameTimestamp = 0;
        private int _targetScreenW = 1920;
        private int _targetScreenH = 1080;
        private DateTime _lastMouseMoveSent = DateTime.MinValue;
        private int _frameCount = 0;
        private DateTime _fpsStartTime = DateTime.UtcNow;

        public RemoteDesktopViewerWindow(string targetHwid, string targetUsername)
        {
            InitializeComponent();
            _targetHwid = targetHwid.Trim();
            _targetUsername = string.IsNullOrWhiteSpace(targetUsername) ? "Bilinmeyen Kullanıcı" : targetUsername.Trim();

            TxtTargetUserTitle.Text = $"Hedef Kullanıcı: {_targetUsername}";
            TxtTargetHwid.Text = $"HWID: {_targetHwid}";

            Loaded += RemoteDesktopViewerWindow_Loaded;
        }

        private string GetFirebaseUrl(string path)
        {
            var cfg = SettingsService.Instance.Current;
            string baseUrl = cfg.FirebaseBaseUrl.TrimEnd('/');
            string url = $"{baseUrl}/{path.TrimStart('/')}.json";
            if (!string.IsNullOrWhiteSpace(cfg.FirebaseAuthSecret))
                url += $"?auth={Uri.EscapeDataString(cfg.FirebaseAuthSecret)}";
            return url;
        }

        private async void RemoteDesktopViewerWindow_Loaded(object sender, RoutedEventArgs e)
        {
            _sessionCts = new CancellationTokenSource();
            try
            {
                // Send request to target client
                await AlpDestekService.Instance.RequestSupportSessionAsync(_targetHwid, _targetUsername);

                // Start status & frame loop
                _ = Task.Run(() => RunViewerLoopAsync(_sessionCts.Token));
            }
            catch (Exception ex)
            {
                TxtSessionStatus.Text = $"Hata: {ex.Message}";
                DotSessionStatus.Fill = new SolidColorBrush(Color.FromRgb(255, 23, 68));
            }
        }

        private async Task RunViewerLoopAsync(CancellationToken ct)
        {
            bool sessionAccepted = false;
            string statusUrl = GetFirebaseUrl($"alptogether/support_sessions/{_targetHwid}/status");
            string frameUrl = GetFirebaseUrl($"alptogether/support_stream/{_targetHwid}/frame");

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (!sessionAccepted)
                    {
                        // Check if client accepted
                        var resp = await _httpClient.GetAsync(statusUrl, ct);
                        if (resp.IsSuccessStatusCode)
                        {
                            string raw = (await resp.Content.ReadAsStringAsync(ct)).Trim('"');
                            if (raw == "accepted")
                            {
                                sessionAccepted = true;
                                await Dispatcher.InvokeAsync(() =>
                                {
                                    PnlWaitingConsent.Visibility = Visibility.Collapsed;
                                    ViewboxScreen.Visibility = Visibility.Visible;
                                    DotSessionStatus.Fill = new SolidColorBrush(Color.FromRgb(0, 230, 118)); // Green
                                    TxtSessionStatus.Text = "Canlı Yayın Aktif";
                                    GridCanvas.Focus();
                                });
                            }
                            else if (raw == "rejected")
                            {
                                await Dispatcher.InvokeAsync(() =>
                                {
                                    DotSessionStatus.Fill = new SolidColorBrush(Color.FromRgb(255, 23, 68));
                                    TxtSessionStatus.Text = "Kullanıcı isteği reddetti.";
                                    MessageBox.Show("Kullanıcı canlı teknik destek isteğini onaylamadı.", "İstek Reddedildi", MessageBoxButton.OK, MessageBoxImage.Information);
                                    Close();
                                });
                                break;
                            }
                            else if (raw == "terminated")
                            {
                                await Dispatcher.InvokeAsync(() =>
                                {
                                    DotSessionStatus.Fill = new SolidColorBrush(Color.FromRgb(255, 23, 68));
                                    TxtSessionStatus.Text = "Oturum sonlandırıldı.";
                                    Close();
                                });
                                break;
                            }
                        }
                        await Task.Delay(1000, ct);
                        continue;
                    }

                    // Fetch frame
                    var frameResp = await _httpClient.GetAsync(frameUrl, ct);
                    if (frameResp.IsSuccessStatusCode)
                    {
                        string json = await frameResp.Content.ReadAsStringAsync(ct);
                        if (!string.IsNullOrWhiteSpace(json) && json != "null")
                        {
                            using var doc = JsonDocument.Parse(json);
                            var root = doc.RootElement;
                            if (root.TryGetProperty("t", out var tElem))
                            {
                                long timestamp = tElem.GetInt64();
                                if (timestamp > _lastFrameTimestamp)
                                {
                                    _lastFrameTimestamp = timestamp;

                                    if (root.TryGetProperty("screenW", out var swElem))
                                        _targetScreenW = Math.Max(800, swElem.GetInt32());
                                    if (root.TryGetProperty("screenH", out var shElem))
                                        _targetScreenH = Math.Max(600, shElem.GetInt32());

                                    if (root.TryGetProperty("data", out var dataElem))
                                    {
                                        string b64 = dataElem.GetString() ?? "";
                                        if (!string.IsNullOrEmpty(b64))
                                        {
                                            byte[] bytes = Convert.FromBase64String(b64);
                                            await RenderFrameAsync(bytes, _targetScreenW, _targetScreenH);
                                        }
                                    }
                                }
                            }
                        }
                    }

                    await Task.Delay(120, ct); // Fast polling for frames
                }
                catch (OperationCanceledException) { break; }
                catch { await Task.Delay(500, ct); }
            }
        }

        private async Task RenderFrameAsync(byte[] jpegBytes, int scrW, int scrH)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    using var ms = new MemoryStream(jpegBytes);
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.StreamSource = ms;
                    bmp.EndInit();
                    bmp.Freeze();

                    ImgRemoteScreen.Source = bmp;

                    // Update FPS & resolution
                    _frameCount++;
                    var elapsed = (DateTime.UtcNow - _fpsStartTime).TotalSeconds;
                    if (elapsed >= 1.0)
                    {
                        double fps = _frameCount / elapsed;
                        TxtFpsCounter.Text = $"Akış: {fps:0.0} FPS | Çözünürlük: {scrW}x{scrH}";
                        _frameCount = 0;
                        _fpsStartTime = DateTime.UtcNow;
                    }
                }
                catch { }
            });
        }

        #region Remote Input Capture & Forwarding
        private (int x, int y)? MapCanvasToRemote(Point clickPoint)
        {
            if (ImgRemoteScreen.ActualWidth <= 0 || ImgRemoteScreen.ActualHeight <= 0)
                return null;

            double ratioX = clickPoint.X / ImgRemoteScreen.ActualWidth;
            double ratioY = clickPoint.Y / ImgRemoteScreen.ActualHeight;

            ratioX = Math.Clamp(ratioX, 0.0, 1.0);
            ratioY = Math.Clamp(ratioY, 0.0, 1.0);

            int mappedX = (int)(ratioX * _targetScreenW);
            int mappedY = (int)(ratioY * _targetScreenH);
            return (mappedX, mappedY);
        }

        private void GridCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (ChkControlEnabled.IsChecked != true) return;

            // Throttle mouse moves to ~50ms
            if ((DateTime.UtcNow - _lastMouseMoveSent).TotalMilliseconds < 50)
                return;

            _lastMouseMoveSent = DateTime.UtcNow;
            Point p = e.GetPosition(ImgRemoteScreen);
            var mapped = MapCanvasToRemote(p);
            if (mapped == null) return;

            _ = AlpDestekService.Instance.SendAdminInputEventAsync(_targetHwid, new
            {
                id = DateTime.UtcNow.Ticks,
                type = "mouse_move",
                x = mapped.Value.x,
                y = mapped.Value.y
            });
        }

        private void GridCanvas_MouseDown(object sender, MouseButtonEventArgs e)
        {
            GridCanvas.Focus();
            if (ChkControlEnabled.IsChecked != true) return;

            Point p = e.GetPosition(ImgRemoteScreen);
            var mapped = MapCanvasToRemote(p);
            if (mapped == null) return;

            string type = e.ChangedButton == MouseButton.Right ? "right_down" : "mouse_down";
            _ = AlpDestekService.Instance.SendAdminInputEventAsync(_targetHwid, new
            {
                id = DateTime.UtcNow.Ticks,
                type,
                x = mapped.Value.x,
                y = mapped.Value.y
            });
        }

        private void GridCanvas_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (ChkControlEnabled.IsChecked != true) return;

            Point p = e.GetPosition(ImgRemoteScreen);
            var mapped = MapCanvasToRemote(p);
            if (mapped == null) return;

            string type = e.ChangedButton == MouseButton.Right ? "right_up" : "mouse_up";
            _ = AlpDestekService.Instance.SendAdminInputEventAsync(_targetHwid, new
            {
                id = DateTime.UtcNow.Ticks,
                type,
                x = mapped.Value.x,
                y = mapped.Value.y
            });
        }

        private void GridCanvas_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (ChkControlEnabled.IsChecked != true) return;

            Point p = e.GetPosition(ImgRemoteScreen);
            var mapped = MapCanvasToRemote(p);
            int x = mapped?.x ?? 0;
            int y = mapped?.y ?? 0;

            _ = AlpDestekService.Instance.SendAdminInputEventAsync(_targetHwid, new
            {
                id = DateTime.UtcNow.Ticks,
                type = "scroll",
                delta = e.Delta,
                x,
                y
            });
        }

        private void GridCanvas_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (ChkControlEnabled.IsChecked != true) return;

            int vk = KeyInterop.VirtualKeyFromKey(e.Key);
            _ = AlpDestekService.Instance.SendAdminInputEventAsync(_targetHwid, new
            {
                id = DateTime.UtcNow.Ticks,
                type = "key_down",
                vk
            });

            e.Handled = true;
        }

        private void GridCanvas_PreviewKeyUp(object sender, KeyEventArgs e)
        {
            if (ChkControlEnabled.IsChecked != true) return;

            int vk = KeyInterop.VirtualKeyFromKey(e.Key);
            _ = AlpDestekService.Instance.SendAdminInputEventAsync(_targetHwid, new
            {
                id = DateTime.UtcNow.Ticks,
                type = "key_up",
                vk
            });

            e.Handled = true;
        }
        #endregion

        private bool _isTerminated = false;

        private async void BtnEndSession_Click(object sender, RoutedEventArgs e)
        {
            await TerminateAndCloseAsync();
        }

        private async void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_isTerminated) return;
            _isTerminated = true;
            _sessionCts?.Cancel();
            try
            {
                await AlpDestekService.Instance.TerminateActiveSessionAsync(_targetHwid);
            }
            catch { }
        }

        private async Task TerminateAndCloseAsync()
        {
            if (_isTerminated) return;
            _isTerminated = true;
            try
            {
                _sessionCts?.Cancel();
                await AlpDestekService.Instance.TerminateActiveSessionAsync(_targetHwid);
            }
            catch { }
            Close();
        }
    }
}
