using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using AlpTogether.Models;

namespace AlpTogether.Services
{
    public class DeveloperAuthService
    {
        public static DeveloperAuthService Instance { get; } = new();

        // 100% Segregated from AlpNet SecretKey ("AlpNetSecret2026!PulseCore")
        private const string DevSecretKey = "AlpTogetherMaster2026!PulseDevStudioSecretKey#CoreAuth";
        private const string KeyHeader = "ATDEV";

        public string DeviceHwid { get; }
        public bool IsPrimaryDevMachine { get; }

        private bool _sessionAdminUnlocked;
        private bool _temporaryUserView;
        public bool IsAdminMode => !_temporaryUserView && (IsPrimaryDevMachine || _sessionAdminUnlocked || SettingsService.Instance.Current.IsAdminModeUnlocked);

        public event Action<bool>? OnAdminModeChanged;

        private DeveloperAuthService()
        {
            DeviceHwid = ComputeAlpTogetherHwid();
            IsPrimaryDevMachine = DetectPrimaryDevMachine();
            if (IsPrimaryDevMachine)
            {
                _sessionAdminUnlocked = true;
                SettingsService.Instance.Current.IsAdminModeUnlocked = true;
                try { SettingsService.Instance.Save(); } catch { }
            }
        }

        /// <summary>
        /// AlpTogether'a özel HWID algoritması. AlpNet HWID formatından tamamen ayrıdır:
        /// - Farklı salt ve SHA-256 hash
        /// - 'AT-' öneki ve 4'lü blok formatı: AT-XXXX-XXXX-XXXX
        /// - Kesinlikle AlpNet ile çakışmaz!
        /// </summary>
        public static string ComputeAlpTogetherHwid()
        {
            try
            {
                string raw = $"ALPTOGETHER_SALT_2026_DEV_{Environment.MachineName.ToUpperInvariant()}_{Environment.UserName.ToUpperInvariant()}";
                byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
                string hex = Convert.ToHexString(hash);
                return $"AT-{hex[..4]}-{hex[4..8]}-{hex[8..12]}-{hex[12..16]}";
            }
            catch
            {
                return "AT-DEV-UNKNOWN-0000";
            }
        }

        private static bool DetectPrimaryDevMachine()
        {
            try
            {
                string machine = Environment.MachineName;
                string user = Environment.UserName;

                if (machine.Contains("MONSTER", StringComparison.OrdinalIgnoreCase) ||
                    machine.Contains("ALPHAN", StringComparison.OrdinalIgnoreCase))
                    return true;

                if (user.Contains("Alphan", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch { }

            return false;
        }

        public static string ComputeHmacSha256(string data, string secret = DevSecretKey)
        {
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
            byte[] hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
            return Convert.ToBase64String(hash);
        }

        /// <summary>
        /// AlpTogether Admin / Geliştirici Lisans Anahtarı üretir.
        /// Format: Base64(ATDEV|{targetHwid}|{expTicks}|{hmac})
        /// AlpNet'in KeyManager'ı bu anahtarı asla tanımaz ve kabul etmez.
        /// </summary>
        public static string GenerateAdminKey(string targetHwid, DateTime expirationDate)
        {
            string hwid = string.IsNullOrWhiteSpace(targetHwid) ? "AT-GLOBAL-DEV" : targetHwid.Trim().ToUpperInvariant();
            long ticks = expirationDate.Ticks;
            string payload = $"{KeyHeader}|{hwid}|{ticks}";
            string signature = ComputeHmacSha256(payload, DevSecretKey);

            string rawKey = $"{KeyHeader}|{hwid}|{ticks}|{signature}";
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(rawKey));
        }

        /// <summary>
        /// Verilen anahtarı doğrular.
        /// </summary>
        public static bool ValidateAdminKey(string base64Key, string expectedHwid, out DateTime expirationDate, out string reason)
        {
            expirationDate = DateTime.MinValue;
            reason = string.Empty;

            if (string.IsNullOrWhiteSpace(base64Key))
            {
                reason = "Anahtar boş olamaz.";
                return false;
            }

            try
            {
                byte[] bytes = Convert.FromBase64String(base64Key.Trim());
                string decoded = Encoding.UTF8.GetString(bytes);
                string[] parts = decoded.Split('|');

                if (parts.Length != 4 || parts[0] != KeyHeader)
                {
                    reason = "Geçersiz AlpTogether lisans formatı (AlpNet veya farklı anahtar tespit edildi).";
                    return false;
                }

                string targetHwid = parts[1];
                string expTicksStr = parts[2];
                string signature = parts[3];

                // Cihaz eşleşmesi (GLOBAL admin anahtarı değilse bu cihaza ait olmalı)
                if (targetHwid != "AT-GLOBAL-DEV" && !string.Equals(targetHwid, expectedHwid.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    reason = $"Bu anahtar bu cihaza ait değil! (Beklenen: {expectedHwid}, Anahtar: {targetHwid})";
                    return false;
                }

                string payload = $"{KeyHeader}|{targetHwid}|{expTicksStr}";
                string expectedSig = ComputeHmacSha256(payload, DevSecretKey);

                if (signature != expectedSig)
                {
                    reason = "Anahtar dijital imzası doğrulanamadı (Geçersiz veya bozuk imza).";
                    return false;
                }

                if (long.TryParse(expTicksStr, out long ticks))
                {
                    expirationDate = new DateTime(ticks, DateTimeKind.Utc);
                    if (DateTime.UtcNow <= expirationDate)
                    {
                        reason = "Lisans geçerli!";
                        return true;
                    }
                    else
                    {
                        reason = $"Lisans süresi dolmuş! (Bitiş Tarihi: {expirationDate.ToLocalTime():dd.MM.yyyy HH:mm})";
                        return false;
                    }
                }

                reason = "Lisans zaman damgası çözülemedi.";
                return false;
            }
            catch (Exception ex)
            {
                reason = "Anahtar çözülürken hata: " + ex.Message;
                return false;
            }
        }

        /// <summary>
        /// Parola veya Lisans Anahtarı ile Admin modunu açar.
        /// </summary>
        public bool TryUnlockAdmin(string input, out string message)
        {
            string clean = (input ?? "").Trim();

            // 1. Master Passcode Bypass (Alphan Geliştirici Acil Parolası)
            if (clean.Equals("alpdev2026", StringComparison.OrdinalIgnoreCase) ||
                clean.Equals("AlpTogether2026!", StringComparison.Ordinal) ||
                clean.Equals("alphanmaster", StringComparison.OrdinalIgnoreCase))
            {
                _sessionAdminUnlocked = true;
                SettingsService.Instance.Current.IsAdminModeUnlocked = true;
                SettingsService.Instance.Save();
                message = "Geliştirici Master Parolası ile Admin Modu Aktif Edildi!";
                OnAdminModeChanged?.Invoke(true);
                AppLoggerService.Instance.Success("ADMİN-GİRİŞ", "Admin / Geliştirici modu başarıyla açıldı.", "AlpTogether");
                return true;
            }

            // 2. AlpTogether Admin Key Validation
            if (ValidateAdminKey(clean, DeviceHwid, out DateTime exp, out string reason))
            {
                _sessionAdminUnlocked = true;
                SettingsService.Instance.Current.IsAdminModeUnlocked = true;
                SettingsService.Instance.Current.AdminLicenseKey = clean;
                SettingsService.Instance.Save();
                message = $"Admin Lisansı Doğrulandı! (Bitiş: {exp.ToLocalTime():dd.MM.yyyy})";
                OnAdminModeChanged?.Invoke(true);
                AppLoggerService.Instance.Success("ADMİN-GİRİŞ", $"Lisans ile Admin modu açıldı ({exp.ToLocalTime():dd.MM.yyyy}).", "AlpTogether");
                return true;
            }

            message = reason;
            return false;
        }

        public void UnlockDeveloperModeDirectly()
        {
            _temporaryUserView = false;
            _sessionAdminUnlocked = true;
            SettingsService.Instance.Current.IsAdminModeUnlocked = true;
            SettingsService.Instance.Save();
            OnAdminModeChanged?.Invoke(true);
            AppLoggerService.Instance.Success("ADMİN-GİRİŞ", "Geliştirici modu doğrudan açıldı.", "AlpTogether");
        }

        public void LockAdminMode()
        {
            _temporaryUserView = true;
            OnAdminModeChanged?.Invoke(false);
            AppLoggerService.Instance.Info("ADMİN-GÖRÜNÜM", "Standart kullanıcı görünümüne geçildi.", "AlpTogether");
        }

        public async Task<bool> PushAdminLicenseToFirebaseAsync(string hwid, string key)
        {
            try
            {
                var cfg = SettingsService.Instance.Current;
                if (string.IsNullOrWhiteSpace(cfg.FirebaseBaseUrl)) return false;

                string url = $"{cfg.FirebaseBaseUrl.TrimEnd('/')}/alptogether/dev_licenses/{hwid.Trim()}.json";
                if (!string.IsNullOrWhiteSpace(cfg.FirebaseAuthSecret))
                    url += $"?auth={Uri.EscapeDataString(cfg.FirebaseAuthSecret)}";

                var payload = new
                {
                    hwid = hwid,
                    license_key = key,
                    created_at = DateTime.UtcNow.ToString("o"),
                    created_by = Environment.MachineName
                };

                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
                string json = JsonSerializer.Serialize(payload);
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                var resp = await client.PutAsync(url, content);
                return resp.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }
    }
}
