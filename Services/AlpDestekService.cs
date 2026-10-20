using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using AlpTogether.Models;

namespace AlpTogether.Services
{
    public class AlpDestekService
    {
        public static AlpDestekService Instance { get; } = new();

        private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(10) };
        private DispatcherTimer? _clientSessionListenerTimer;
        private bool _isListenerActive = false;
        private string _activeHostSessionId = string.Empty;
        private CancellationTokenSource? _hostCts;
        private Window? _activeFloatingToolbar;

        private AlpDestekService()
        {
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

        #region Client Background Listener (Checks for Admin Support Requests)
        /// <summary>
        /// Tüm istemcilerde arka planda sessizce çalışır. Admin tarafından bu kullanıcıya bir canlı destek
        /// isteği gelip gelmediğini kontrol eder. Normal kullanıcı arayüzünde hiçbir buton veya iz bırakmaz.
        /// </summary>
        public void StartClientSessionListener()
        {
            if (_isListenerActive) return;
            _isListenerActive = true;

            _clientSessionListenerTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(3)
            };
            _clientSessionListenerTimer.Tick += async (s, e) => await CheckIncomingSupportRequestAsync();
            _clientSessionListenerTimer.Start();
        }

        public void StopClientSessionListener()
        {
            _clientSessionListenerTimer?.Stop();
            _clientSessionListenerTimer = null;
            _isListenerActive = false;
        }

        private async Task CheckIncomingSupportRequestAsync()
        {
            string hwid = UserAuthAndPermissionService.Instance.DeviceHwid;
            if (string.IsNullOrWhiteSpace(hwid) || !string.IsNullOrEmpty(_activeHostSessionId))
                return;

            try
            {
                string url = GetFirebaseUrl($"alptogether/support_sessions/{hwid}");
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                var resp = await _httpClient.GetAsync(url, cts.Token);
                if (resp.IsSuccessStatusCode)
                {
                    string json = await resp.Content.ReadAsStringAsync();
                    if (!string.IsNullOrWhiteSpace(json) && json != "null")
                    {
                        var session = JsonSerializer.Deserialize<SupportSession>(json);
                        if (session != null && session.Status == "requested")
                        {
                            // Admin bu cihaza canlı destek isteği attı!
                            await PromptUserConsentAndStartAsync(session);
                        }
                    }
                }
            }
            catch { }
        }

        private async Task PromptUserConsentAndStartAsync(SupportSession session)
        {
            _activeHostSessionId = session.SessionId;

            bool userAccepted = false;
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                userAccepted = ShowConsentDialog(session);
            });

            if (!userAccepted)
            {
                // Reddedildi veya zaman aşımı
                await UpdateSessionStatusAsync(session.TargetHwid, "rejected");
                _activeHostSessionId = string.Empty;
                return;
            }

            // Verify session was not cancelled while dialog was up
            string checkUrl = GetFirebaseUrl($"alptogether/support_sessions/{session.TargetHwid}/status");
            try
            {
                var stResp = await _httpClient.GetAsync(checkUrl);
                if (stResp.IsSuccessStatusCode)
                {
                    string curSt = (await stResp.Content.ReadAsStringAsync()).Trim('"');
                    if (curSt == "terminated" || curSt == "rejected")
                    {
                        _activeHostSessionId = string.Empty;
                        return;
                    }
                }
            }
            catch { }

            // Kabul edildi
            await UpdateSessionStatusAsync(session.TargetHwid, "accepted", startedAt: DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

            // Üstte yüzen minik gizli cyber-pink çubuğu aç
            Application.Current.Dispatcher.Invoke(() =>
            {
                ShowFloatingToolbar(session);
            });

            // Ekran yayını ve fare/klavye dinleyicisini başlat
            _hostCts = new CancellationTokenSource();
            _ = Task.Run(() => RunHostStreamLoopAsync(session, _hostCts.Token));
            _ = Task.Run(() => RunHostInputLoopAsync(session, _hostCts.Token));
        }

        private bool ShowConsentDialog(SupportSession session)
        {
            bool accepted = false;
            var dialog = new Window
            {
                Title = "💻 AlpDestek • Canlı Destek Talebi",
                Width = 470,
                Height = 270,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Topmost = true,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Background = System.Windows.Media.Brushes.Transparent,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = true
            };

            var border = new System.Windows.Controls.Border
            {
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(18, 7, 28)),
                BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 46, 147)),
                BorderThickness = new Thickness(1.8),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(22, 20, 22, 18)
            };

            var root = new System.Windows.Controls.Grid();
            root.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto }); // Header
            root.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // Content
            root.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto }); // Buttons

            // Header
            var headerStack = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
            var badge = new System.Windows.Controls.Border
            {
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 46, 147)),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 3, 8, 3),
                Margin = new Thickness(0, 0, 10, 0)
            };
            badge.Child = new System.Windows.Controls.TextBlock
            {
                Text = "ALPDESKEK CANLI YARDIM",
                Foreground = System.Windows.Media.Brushes.White,
                FontSize = 10,
                FontWeight = FontWeights.ExtraBold
            };
            headerStack.Children.Add(badge);

            var titleTxt = new System.Windows.Controls.TextBlock
            {
                Text = "Uzaktan Canlı Destek Talebi",
                Foreground = System.Windows.Media.Brushes.White,
                FontSize = 15,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center
            };
            headerStack.Children.Add(titleTxt);
            System.Windows.Controls.Grid.SetRow(headerStack, 0);
            root.Children.Add(headerStack);

            // Body
            var bodyStack = new System.Windows.Controls.StackPanel { Margin = new Thickness(0, 0, 0, 14) };
            var adminTxt = new System.Windows.Controls.TextBlock
            {
                Text = $"{session.AdminName} bilgisayarınıza bağlanarak uzaktan canlı teknik yardım sağlamak istiyor.",
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 234, 245)),
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8)
            };
            bodyStack.Children.Add(adminTxt);

            var descTxt = new System.Windows.Controls.TextBlock
            {
                Text = "• Ekranınız canlı olarak görüntülenecek ve fare/klavye ile geçici müdahale edilecektir.\n• İstediğiniz an ekranın üstündeki kırmızı 'Bağlantıyı Kes' butonuna basarak oturumu derhal durdurabilirsiniz.",
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(188, 164, 183)),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 16
            };
            bodyStack.Children.Add(descTxt);

            int remainingSeconds = 60;
            var timerTxt = new System.Windows.Controls.TextBlock
            {
                Text = $"⏳ Otomatik yanıt süresi: {remainingSeconds} sn",
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 229, 255)),
                FontSize = 10.5,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 8, 0, 0)
            };
            bodyStack.Children.Add(timerTxt);
            System.Windows.Controls.Grid.SetRow(bodyStack, 1);
            root.Children.Add(bodyStack);

            // Buttons
            var btnDock = new System.Windows.Controls.DockPanel { LastChildFill = false };
            var allowBtn = new System.Windows.Controls.Button
            {
                Content = "✅ İzin Ver",
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 46, 147)),
                Foreground = System.Windows.Media.Brushes.White,
                FontWeight = FontWeights.Bold,
                FontSize = 12,
                Padding = new Thickness(22, 9, 22, 9),
                Cursor = System.Windows.Input.Cursors.Hand,
                BorderThickness = new Thickness(0),
                Margin = new Thickness(0, 0, 10, 0)
            };
            allowBtn.Click += (s, e) => { accepted = true; dialog.Close(); };
            System.Windows.Controls.DockPanel.SetDock(allowBtn, System.Windows.Controls.Dock.Right);
            btnDock.Children.Add(allowBtn);

            var rejectBtn = new System.Windows.Controls.Button
            {
                Content = "❌ Reddet",
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(40, 18, 55)),
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 133, 192)),
                FontWeight = FontWeights.Bold,
                FontSize = 12,
                Padding = new Thickness(18, 9, 18, 9),
                Cursor = System.Windows.Input.Cursors.Hand,
                BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(85, 36, 109)),
                BorderThickness = new Thickness(1)
            };
            rejectBtn.Click += (s, e) => { accepted = false; dialog.Close(); };
            System.Windows.Controls.DockPanel.SetDock(rejectBtn, System.Windows.Controls.Dock.Right);
            btnDock.Children.Add(rejectBtn);

            System.Windows.Controls.Grid.SetRow(btnDock, 2);
            root.Children.Add(btnDock);

            border.Child = root;
            dialog.Content = border;

            // Countdown timer
            var cdTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            cdTimer.Tick += (s, e) =>
            {
                remainingSeconds--;
                if (remainingSeconds <= 0)
                {
                    cdTimer.Stop();
                    dialog.Close();
                }
                else
                {
                    timerTxt.Text = $"⏳ Otomatik yanıt süresi: {remainingSeconds} sn";
                }
            };
            cdTimer.Start();

            dialog.Closed += (s, e) => cdTimer.Stop();
            dialog.ShowDialog();

            return accepted;
        }

        private void ShowFloatingToolbar(SupportSession session)
        {
            try
            {
                _activeFloatingToolbar?.Close();

                int screenW = GetSystemMetrics(SM_CXSCREEN);
                if (screenW <= 0) screenW = (int)SystemParameters.PrimaryScreenWidth;

                var win = new Window
                {
                    Title = "AlpDestek Canlı Oturum",
                    Width = 430,
                    Height = 44,
                    WindowStyle = WindowStyle.None,
                    AllowsTransparency = true,
                    Topmost = true,
                    ShowInTaskbar = false,
                    ResizeMode = ResizeMode.NoResize,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = Math.Max(0, (screenW - 430) / 2),
                    Top = 10,
                    Background = System.Windows.Media.Brushes.Transparent
                };

                var border = new System.Windows.Controls.Border
                {
                    Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(235, 18, 7, 28)),
                    BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 46, 147)),
                    BorderThickness = new Thickness(1.5),
                    CornerRadius = new CornerRadius(10),
                    Padding = new Thickness(14, 6, 14, 6)
                };

                var dock = new System.Windows.Controls.DockPanel { LastChildFill = true };

                var disconnectBtn = new System.Windows.Controls.Button
                {
                    Content = "🔴 Bağlantıyı Kes",
                    Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 23, 68)),
                    Foreground = System.Windows.Media.Brushes.White,
                    FontWeight = FontWeights.Bold,
                    FontSize = 11,
                    Padding = new Thickness(12, 4, 12, 4),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    BorderThickness = new Thickness(0)
                };
                disconnectBtn.Click += async (s, e) =>
                {
                    await TerminateActiveSessionAsync(session.TargetHwid);
                };
                System.Windows.Controls.DockPanel.SetDock(disconnectBtn, System.Windows.Controls.Dock.Right);
                dock.Children.Add(disconnectBtn);

                var stack = new System.Windows.Controls.StackPanel
                {
                    Orientation = System.Windows.Controls.Orientation.Horizontal,
                    VerticalAlignment = VerticalAlignment.Center
                };

                var liveDot = new System.Windows.Shapes.Ellipse
                {
                    Width = 8,
                    Height = 8,
                    Fill = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 230, 118)),
                    Margin = new Thickness(0, 0, 8, 0)
                };
                stack.Children.Add(liveDot);

                var statusTxt = new System.Windows.Controls.TextBlock
                {
                    Text = $"Canlı Destek Aktif ({session.AdminName})",
                    Foreground = System.Windows.Media.Brushes.White,
                    FontWeight = FontWeights.Bold,
                    FontSize = 11.5,
                    VerticalAlignment = VerticalAlignment.Center
                };
                stack.Children.Add(statusTxt);
                dock.Children.Add(stack);

                border.Child = dock;
                win.Content = border;
                win.Show();
                _activeFloatingToolbar = win;
            }
            catch { }
        }

        public async Task TerminateActiveSessionAsync(string targetHwid)
        {
            try
            {
                _hostCts?.Cancel();
                _activeHostSessionId = string.Empty;

                Application.Current.Dispatcher.Invoke(() =>
                {
                    _activeFloatingToolbar?.Close();
                    _activeFloatingToolbar = null;
                });

                await UpdateSessionStatusAsync(targetHwid, "terminated", endedAt: DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

                // Clear stream node and inputs
                string streamUrl = GetFirebaseUrl($"alptogether/support_stream/{targetHwid}");
                await _httpClient.DeleteAsync(streamUrl);
            }
            catch { }
        }
        #endregion

        #region Screen Streaming & Remote Input Execution (User Host Loop)
        private async Task RunHostStreamLoopAsync(SupportSession session, CancellationToken ct)
        {
            string frameUrl = GetFirebaseUrl($"alptogether/support_stream/{session.TargetHwid}/frame");

            int screenW = GetSystemMetrics(SM_CXSCREEN);
            int screenH = GetSystemMetrics(SM_CYSCREEN);
            if (screenW <= 0) screenW = (int)SystemParameters.PrimaryScreenWidth;
            if (screenH <= 0) screenH = (int)SystemParameters.PrimaryScreenHeight;

            // Maintain exact aspect ratio without distortion (max bounds 1280x720)
            double scale = Math.Min(1280.0 / Math.Max(1, screenW), 720.0 / Math.Max(1, screenH));
            if (scale > 1.0) scale = 1.0;
            int targetWidth = (int)(screenW * scale);
            int targetHeight = (int)(screenH * scale);

            int staticCounter = 0;
            int lastLength = 0;

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    // Check if session status changed to terminated
                    string checkUrl = GetFirebaseUrl($"alptogether/support_sessions/{session.TargetHwid}/status");
                    var statusResp = await _httpClient.GetAsync(checkUrl, ct);
                    if (statusResp.IsSuccessStatusCode)
                    {
                        string s = (await statusResp.Content.ReadAsStringAsync(ct)).Trim('"');
                        if (s == "terminated" || s == "rejected")
                        {
                            _hostCts?.Cancel(); // FIX: terminate input loop as well
                            break;
                        }
                    }

                    // Capture primary screen using true physical pixel coordinates
                    byte[]? jpegBytes = CaptureScreenJpeg(screenW, screenH, targetWidth, targetHeight, 48L);
                    if (jpegBytes != null && jpegBytes.Length > 0)
                    {
                        // Lightweight frame delta check: if identical length and static, only send heartbeat every 15 frames (~2s)
                        bool isStatic = Math.Abs(jpegBytes.Length - lastLength) < 16;
                        lastLength = jpegBytes.Length;

                        if (isStatic) staticCounter++;
                        else staticCounter = 0;

                        if (!isStatic || staticCounter % 15 == 0)
                        {
                            string base64 = Convert.ToBase64String(jpegBytes);
                            var payload = new
                            {
                                t = DateTime.UtcNow.Ticks,
                                w = targetWidth,
                                h = targetHeight,
                                screenW = screenW,
                                screenH = screenH,
                                data = base64
                            };

                            string json = JsonSerializer.Serialize(payload);
                            using var content = new StringContent(json, Encoding.UTF8, "application/json");
                            await _httpClient.PutAsync(frameUrl, content, ct);
                        }
                    }

                    await Task.Delay(140, ct); // ~7-10 FPS adaptive frame relay
                }
                catch (OperationCanceledException) { break; }
                catch { await Task.Delay(400, ct); }
            }

            Application.Current.Dispatcher.Invoke(() =>
            {
                _activeFloatingToolbar?.Close();
                _activeFloatingToolbar = null;
            });
            _activeHostSessionId = string.Empty;
        }

        private async Task RunHostInputLoopAsync(SupportSession session, CancellationToken ct)
        {
            string inputsUrl = GetFirebaseUrl($"alptogether/support_stream/{session.TargetHwid}/inputs");
            long lastProcessedId = 0;

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var resp = await _httpClient.GetAsync(inputsUrl, ct);
                    if (resp.IsSuccessStatusCode)
                    {
                        string json = await resp.Content.ReadAsStringAsync(ct);
                        if (!string.IsNullOrWhiteSpace(json) && json != "null")
                        {
                            using var doc = JsonDocument.Parse(json);
                            if (doc.RootElement.ValueKind == JsonValueKind.Object)
                            {
                                var sortedEvents = new List<(long Id, JsonElement Elem)>();
                                foreach (var prop in doc.RootElement.EnumerateObject())
                                {
                                    if (long.TryParse(prop.Name, out long id) && id > lastProcessedId)
                                    {
                                        sortedEvents.Add((id, prop.Value));
                                    }
                                }

                                if (sortedEvents.Count > 0)
                                {
                                    sortedEvents.Sort((a, b) => a.Id.CompareTo(b.Id));
                                    foreach (var item in sortedEvents)
                                    {
                                        lastProcessedId = item.Id;
                                        ExecuteRemoteInput(item.Elem);
                                    }

                                    // Asynchronously clean up processed events from queue
                                    _ = Task.Run(async () =>
                                    {
                                        try
                                        {
                                            foreach (var item in sortedEvents)
                                            {
                                                string delUrl = GetFirebaseUrl($"alptogether/support_stream/{session.TargetHwid}/inputs/{item.Id}");
                                                await _httpClient.DeleteAsync(delUrl);
                                            }
                                        }
                                        catch { }
                                    });
                                }
                            }
                        }
                    }
                    await Task.Delay(50, ct); // Fast input queue polling
                }
                catch (OperationCanceledException) { break; }
                catch { await Task.Delay(200, ct); }
            }
        }

        public static byte[]? CaptureScreenJpeg(int screenW, int screenH, int targetW, int targetH, long quality)
        {
            try
            {
                using var screenBmp = new Bitmap(screenW, screenH, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(screenBmp))
                {
                    g.CopyFromScreen(0, 0, 0, 0, new System.Drawing.Size(screenW, screenH), CopyPixelOperation.SourceCopy);
                }

                using var resized = new Bitmap(targetW, targetH);
                using (var gResized = Graphics.FromImage(resized))
                {
                    gResized.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
                    gResized.DrawImage(screenBmp, 0, 0, targetW, targetH);
                }

                using var ms = new MemoryStream();
                var encoder = ImageCodecInfo.GetImageEncoders().FirstOrDefault(c => c.FormatID == ImageFormat.Jpeg.Guid);
                if (encoder != null)
                {
                    var encParams = new EncoderParameters(1);
                    encParams.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);
                    resized.Save(ms, encoder, encParams);
                    return ms.ToArray();
                }
                else
                {
                    resized.Save(ms, ImageFormat.Jpeg);
                    return ms.ToArray();
                }
            }
            catch { return null; }
        }

        private static void ExecuteRemoteInput(JsonElement elem)
        {
            try
            {
                string type = elem.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
                int x = elem.TryGetProperty("x", out var xElem) ? xElem.GetInt32() : 0;
                int y = elem.TryGetProperty("y", out var yElem) ? yElem.GetInt32() : 0;

                switch (type)
                {
                    case "mouse_move":
                        SetCursorPos(x, y);
                        break;
                    case "mouse_down":
                        SetCursorPos(x, y);
                        mouse_event(MOUSEEVENTF_LEFTDOWN, (uint)x, (uint)y, 0, 0);
                        break;
                    case "mouse_up":
                        SetCursorPos(x, y);
                        mouse_event(MOUSEEVENTF_LEFTUP, (uint)x, (uint)y, 0, 0);
                        break;
                    case "right_down":
                        SetCursorPos(x, y);
                        mouse_event(MOUSEEVENTF_RIGHTDOWN, (uint)x, (uint)y, 0, 0);
                        break;
                    case "right_up":
                        SetCursorPos(x, y);
                        mouse_event(MOUSEEVENTF_RIGHTUP, (uint)x, (uint)y, 0, 0);
                        break;
                    case "scroll":
                        int delta = elem.TryGetProperty("delta", out var dElem) ? dElem.GetInt32() : 0;
                        mouse_event(MOUSEEVENTF_WHEEL, (uint)x, (uint)y, (uint)delta, 0);
                        break;
                    case "key_down":
                        int vkDown = elem.TryGetProperty("vk", out var vk1) ? vk1.GetInt32() : 0;
                        keybd_event((byte)vkDown, 0, 0, 0);
                        break;
                    case "key_up":
                        int vkUp = elem.TryGetProperty("vk", out var vk2) ? vk2.GetInt32() : 0;
                        keybd_event((byte)vkUp, 0, KEYEVENTF_KEYUP, 0);
                        break;
                }
            }
            catch { }
        }

        [DllImport("user32.dll")]
        public static extern int GetSystemMetrics(int nIndex);
        public const int SM_CXSCREEN = 0;
        public const int SM_CYSCREEN = 1;

        [DllImport("user32.dll")]
        private static extern bool SetCursorPos(int X, int Y);

        [DllImport("user32.dll")]
        private static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, int dwExtraInfo);

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, int dwExtraInfo);

        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        private const uint MOUSEEVENTF_LEFTUP = 0x0004;
        private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
        private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
        private const uint MOUSEEVENTF_WHEEL = 0x0800;
        private const uint KEYEVENTF_KEYUP = 0x0002;
        #endregion

        #region Admin Operations (Request Session & Push Input)
        public async Task<SupportSession> RequestSupportSessionAsync(string targetHwid, string targetUsername)
        {
            var session = new SupportSession
            {
                SessionId = "SES-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(),
                AdminHwid = UserAuthAndPermissionService.Instance.DeviceHwid,
                AdminName = "Alphan (Admin)",
                TargetHwid = targetHwid.Trim(),
                TargetUsername = targetUsername.Trim(),
                Status = "requested",
                RequestedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            };

            string url = GetFirebaseUrl($"alptogether/support_sessions/{session.TargetHwid}");
            string json = JsonSerializer.Serialize(session);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            await _httpClient.PutAsync(url, content);

            return session;
        }

        public async Task SendAdminInputEventAsync(string targetHwid, object inputEvent)
        {
            try
            {
                long eventId = DateTime.UtcNow.Ticks;
                string url = GetFirebaseUrl($"alptogether/support_stream/{targetHwid}/inputs/{eventId}");
                string json = JsonSerializer.Serialize(inputEvent);
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                await _httpClient.PutAsync(url, content);
            }
            catch { }
        }

        public async Task UpdateSessionStatusAsync(string targetHwid, string status, string? startedAt = null, string? endedAt = null)
        {
            try
            {
                string url = GetFirebaseUrl($"alptogether/support_sessions/{targetHwid}");
                var dict = new Dictionary<string, object> { ["status"] = status };
                if (!string.IsNullOrEmpty(startedAt)) dict["startedAt"] = startedAt;
                if (!string.IsNullOrEmpty(endedAt)) dict["endedAt"] = endedAt;

                string json = JsonSerializer.Serialize(dict);
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                await _httpClient.PatchAsync(url, content);
            }
            catch { }
        }
        #endregion
    }
}
