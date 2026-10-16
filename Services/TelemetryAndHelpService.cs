using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using AlpTogether.Models;

namespace AlpTogether.Services
{
    public class TelemetryAndHelpService
    {
        public static TelemetryAndHelpService Instance { get; } = new();

        private readonly HttpClient _httpClient;
        private readonly string _crashDir;
        private readonly string _ticketDir;

        private TelemetryAndHelpService()
        {
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };

            string baseDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AlpTogether");

            _crashDir = Path.Combine(baseDir, "crashes");
            _ticketDir = Path.Combine(baseDir, "tickets");

            try
            {
                if (!Directory.Exists(_crashDir)) Directory.CreateDirectory(_crashDir);
                if (!Directory.Exists(_ticketDir)) Directory.CreateDirectory(_ticketDir);
            }
            catch { }
        }

        private static string BuildFirebaseUrl(string relativePath)
        {
            var cfg = SettingsService.Instance.Current;
            string baseUrl = (cfg.FirebaseBaseUrl ?? "").TrimEnd('/');
            if (string.IsNullOrWhiteSpace(baseUrl))
                return string.Empty;

            string auth = cfg.FirebaseAuthSecret;
            return string.IsNullOrWhiteSpace(auth)
                ? $"{baseUrl}/{relativePath.TrimStart('/')}"
                : $"{baseUrl}/{relativePath.TrimStart('/')}?auth={Uri.EscapeDataString(auth)}";
        }

        /// <summary>
        /// Kullanıcı hata aldığı an otomatik olarak buluta (Firebase + Webhook) ve yerel kayıt defterine iletir.
        /// </summary>
        public async Task<bool> ReportErrorImmediatelyAsync(
            string appId,
            string appName,
            string appVersion,
            string errorSource,
            string errorMessage,
            string stackTrace = "")
        {
            var report = new AutoErrorReport
            {
                ReportId = "ERR-" + DateTime.Now.ToString("MMddHHmmss") + "-" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant(),
                Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                DeviceId = SettingsService.Instance.DeviceId,
                MachineName = Environment.MachineName,
                OsVersion = Environment.OSVersion.ToString(),
                AppId = appId,
                AppName = appName,
                AppVersion = appVersion,
                ErrorSource = errorSource,
                ErrorMessage = errorMessage,
                StackTrace = stackTrace,
                RecentLogs = AppLoggerService.Instance.GetRecentLogsText(30)
            };

            // 1. Save locally immediately
            try
            {
                string localPath = Path.Combine(_crashDir, $"{report.ReportId}.json");
                string jsonLocal = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(localPath, jsonLocal, Encoding.UTF8);
            }
            catch { }

            AppLoggerService.Instance.Error(
                "TELEMETRİ",
                $"Anlık hata raporu oluşturuldu ({report.ReportId}): {errorMessage}",
                appName,
                $"Kaynak: {errorSource}");

            if (!SettingsService.Instance.Current.AutoSendCrashTelemetry)
                return true;

            bool sentCloud = false;

            // 2. Push to Firebase Realtime Database
            try
            {
                string fbUrl = BuildFirebaseUrl($"alptogether/telemetry/errors/{report.ReportId}.json");
                if (!string.IsNullOrWhiteSpace(fbUrl))
                {
                    string payload = JsonSerializer.Serialize(report);
                    using var content = new StringContent(payload, Encoding.UTF8, "application/json");
                    var resp = await _httpClient.PutAsync(fbUrl, content);
                    if (resp.IsSuccessStatusCode)
                    {
                        sentCloud = true;
                        AppLoggerService.Instance.Info("TELEMETRİ", $"Hata raporu ({report.ReportId}) anında geliştirici bulutuna iletildi.", appName);
                    }
                }
            }
            catch { }

            // 3. Push to Optional Discord / Custom Webhook
            await TrySendWebhookNotificationAsync(
                title: $"🚨 Anlık Hata Algılandı: {appName} (v{appVersion})",
                description: $"**Kaynak:** `{errorSource}`\n**Cihaz:** `{report.DeviceId}` ({report.MachineName})\n**Hata:** {errorMessage}",
                colorDecimal: 16717636); // Neon Red/Pink

            return sentCloud;
        }

        /// <summary>
        /// Kullanıcının "Bu bozuk / Şunda sıkıntı var" diyerek gönderdiği geri bildirim ve arıza kaydını iletir.
        /// </summary>
        public async Task<(bool Success, string TicketId, string Message)> SubmitUserFeedbackAsync(
            AlpAppItem? targetApp,
            string category,
            string severity,
            string userContact,
            string subject,
            string description,
            bool attachLogs)
        {
            var ticket = new UserFeedbackTicket
            {
                TicketId = "TKT-" + DateTime.Now.ToString("MMddHHmm") + "-" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant(),
                Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                DeviceId = SettingsService.Instance.DeviceId,
                MachineName = Environment.MachineName,
                TargetAppId = targetApp?.Id ?? "AlpTogether",
                TargetAppName = targetApp?.Name ?? "AlpTogether Genel",
                TargetAppVersion = targetApp != null ? (targetApp.IsInstalled ? targetApp.InstalledVersion : targetApp.LatestVersion) : "1.0.0",
                IssueCategory = category,
                Severity = severity,
                UserContact = string.IsNullOrWhiteSpace(userContact) ? "Belirtilmedi" : userContact.Trim(),
                Subject = subject.Trim(),
                Description = description.Trim(),
                AttachedSystemLogs = attachLogs ? AppLoggerService.Instance.GetRecentLogsText(45) : "Kullanıcı tarafından eklenmedi.",
                Status = "Açık (Yeni)"
            };

            // 1. Save locally so user & dev always have a copy
            try
            {
                string localFile = Path.Combine(_ticketDir, $"{ticket.TicketId}.json");
                string localJson = JsonSerializer.Serialize(ticket, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(localFile, localJson, Encoding.UTF8);
            }
            catch { }

            bool cloudSuccess = false;

            // 2. Push to Firebase RTDB
            try
            {
                string fbUrl = BuildFirebaseUrl($"alptogether/tickets/{ticket.TicketId}.json");
                if (!string.IsNullOrWhiteSpace(fbUrl))
                {
                    string json = JsonSerializer.Serialize(ticket);
                    using var content = new StringContent(json, Encoding.UTF8, "application/json");
                    var response = await _httpClient.PutAsync(fbUrl, content);
                    cloudSuccess = response.IsSuccessStatusCode;
                }
            }
            catch (Exception ex)
            {
                AppLoggerService.Instance.Warn("YARDIM", "Bulut sunucusuna erişilemedi, bildirim yerel kuyruğa kaydedildi.", ticket.TargetAppName, ex.Message);
            }

            // 3. Push to Discord / Custom Webhook if configured
            bool webhookSuccess = await TrySendWebhookNotificationAsync(
                title: $"📩 Yeni Kullanıcı Bildirimi [{ticket.TicketId}] - {ticket.TargetAppName}",
                description: $"**Kategori:** {ticket.IssueCategory} ({ticket.Severity})\n" +
                             $"**Konu:** {ticket.Subject}\n" +
                             $"**Açıklama:** {ticket.Description}\n" +
                             $"**İletişim:** {ticket.UserContact} | **Cihaz:** `{ticket.DeviceId}`",
                colorDecimal: 16723603); // Neon Pink (#FF2E93)

            if (cloudSuccess || webhookSuccess)
            {
                AppLoggerService.Instance.Success(
                    "YARDIM",
                    $"Bildiriminiz ({ticket.TicketId}) anında geliştirici ekibe iletildi!",
                    ticket.TargetAppName,
                    ticket.Subject);
                return (true, ticket.TicketId, $"Bildiriminiz ({ticket.TicketId}) başarıyla bize ulaştı! En kısa sürede incelenecektir.");
            }
            else
            {
                AppLoggerService.Instance.Info(
                    "YARDIM",
                    $"Bildiriminiz ({ticket.TicketId}) yerel olarak kaydedildi.",
                    ticket.TargetAppName,
                    ticket.Subject);
                return (true, ticket.TicketId, $"Bildiriminiz ({ticket.TicketId}) kaydedildi (Çevrimdışı kuyruğa alındı).");
            }
        }

        private async Task<bool> TrySendWebhookNotificationAsync(string title, string description, int colorDecimal)
        {
            string webhook = SettingsService.Instance.Current.CustomWebhookUrl;
            if (string.IsNullOrWhiteSpace(webhook))
                return false;

            try
            {
                var payload = new
                {
                    username = "AlpTogether Telemetry & Help",
                    embeds = new[]
                    {
                        new
                        {
                            title = title,
                            description = description.Length > 1800 ? description[..1800] + "..." : description,
                            color = colorDecimal,
                            footer = new { text = $"AlpTogether v1.0 • {DateTime.Now:dd.MM.yyyy HH:mm:ss}" }
                        }
                    }
                };
                string json = JsonSerializer.Serialize(payload);
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                var resp = await _httpClient.PostAsync(webhook, content);
                return resp.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        public async Task<List<UserFeedbackTicket>> FetchRecentTicketsAsync()
        {
            var result = new List<UserFeedbackTicket>();

            // 1. Try Firebase RTDB
            try
            {
                string fbUrl = BuildFirebaseUrl("alptogether/tickets.json");
                if (!string.IsNullOrWhiteSpace(fbUrl))
                {
                    var resp = await _httpClient.GetAsync(fbUrl);
                    if (resp.IsSuccessStatusCode)
                    {
                        string json = await resp.Content.ReadAsStringAsync();
                        if (!string.IsNullOrWhiteSpace(json) && json != "null")
                        {
                            var dict = JsonSerializer.Deserialize<Dictionary<string, UserFeedbackTicket>>(json,
                                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                            if (dict != null)
                                result.AddRange(dict.Values);
                        }
                    }
                }
            }
            catch { }

            // 2. Merge local tickets
            try
            {
                if (Directory.Exists(_ticketDir))
                {
                    foreach (var file in Directory.GetFiles(_ticketDir, "*.json"))
                    {
                        try
                        {
                            var local = JsonSerializer.Deserialize<UserFeedbackTicket>(File.ReadAllText(file));
                            if (local != null && !result.Any(x => x.TicketId == local.TicketId))
                                result.Add(local);
                        }
                        catch { }
                    }
                }
            }
            catch { }

            return result.OrderByDescending(x => x.Timestamp).ToList();
        }

        public async Task<List<AutoErrorReport>> FetchRecentErrorsAsync()
        {
            var result = new List<AutoErrorReport>();

            try
            {
                string fbUrl = BuildFirebaseUrl("alptogether/telemetry/errors.json");
                if (!string.IsNullOrWhiteSpace(fbUrl))
                {
                    var resp = await _httpClient.GetAsync(fbUrl);
                    if (resp.IsSuccessStatusCode)
                    {
                        string json = await resp.Content.ReadAsStringAsync();
                        if (!string.IsNullOrWhiteSpace(json) && json != "null")
                        {
                            var dict = JsonSerializer.Deserialize<Dictionary<string, AutoErrorReport>>(json,
                                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                            if (dict != null)
                                result.AddRange(dict.Values);
                        }
                    }
                }
            }
            catch { }

            try
            {
                if (Directory.Exists(_crashDir))
                {
                    foreach (var file in Directory.GetFiles(_crashDir, "*.json"))
                    {
                        try
                        {
                            var local = JsonSerializer.Deserialize<AutoErrorReport>(File.ReadAllText(file));
                            if (local != null && !result.Any(x => x.ReportId == local.ReportId))
                                result.Add(local);
                        }
                        catch { }
                    }
                }
            }
            catch { }

            return result.OrderByDescending(x => x.Timestamp).ToList();
        }
    }
}
