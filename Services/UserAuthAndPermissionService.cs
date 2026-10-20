using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AlpTogether.Models;
using Microsoft.Win32;

namespace AlpTogether.Services
{
    public class UserAuthAndPermissionService
    {
        public static UserAuthAndPermissionService Instance { get; } = new();

        private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(10) };
        private readonly System.Windows.Threading.DispatcherTimer _heartbeatTimer;

        [StructLayout(LayoutKind.Sequential)]
        private struct LASTINPUTINFO
        {
            public uint cbSize;
            public uint dwTime;
        }

        [DllImport("user32.dll")]
        private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

        public static (bool IsAfk, int IdleMinutes) GetIdleState()
        {
            try
            {
                LASTINPUTINFO lii = new LASTINPUTINFO();
                lii.cbSize = (uint)Marshal.SizeOf(lii);
                if (GetLastInputInfo(ref lii))
                {
                    uint idleTicks = (uint)Environment.TickCount - lii.dwTime;
                    int idleMins = (int)(idleTicks / 60000);
                    return (idleMins >= 5, idleMins);
                }
            }
            catch { }
            return (false, 0);
        }

        public static (string ActivityText, string ActivityApp) DetectCurrentActivity()
        {
            try
            {
                var procList = new[]
                {
                    (Name: "AlpLingo", App: "AlpLingo", Title: "AlpLingo"),
                    (Name: "AlpNet.Pulse", App: "AlpNet", Title: "AlpNet Pulse"),
                    (Name: "AlpNetPulse", App: "AlpNet", Title: "AlpNet Pulse"),
                    (Name: "AlphaGravity", App: "AlphaGravity", Title: "AlphaGravity"),
                    (Name: "AlpFinans", App: "AlpFinans", Title: "AlpFinans"),
                    (Name: "RustPlusDesk", App: "AlpRust+", Title: "AlpRust+"),
                    (Name: "AlpRustPlus", App: "AlpRust+", Title: "AlpRust+")
                };

                foreach (var pDef in procList)
                {
                    var procs = Process.GetProcessesByName(pDef.Name);
                    if (procs.Length > 0)
                    {
                        var proc = procs[0];
                        TimeSpan duration = TimeSpan.Zero;
                        try
                        {
                            duration = DateTime.Now - proc.StartTime;
                        }
                        catch { }

                        string durationStr = "";
                        if (duration.TotalMinutes < 1) durationStr = "1 dk";
                        else if (duration.TotalHours < 1) durationStr = $"{(int)duration.TotalMinutes} dk";
                        else durationStr = $"{(int)duration.TotalHours} sa {duration.Minutes} dk";

                        return ($"⚡ {pDef.Title} Açık ({durationStr})", pDef.App);
                    }
                }
            }
            catch { }

            return ("💤 AlpTogether Açık (Masaüstünde)", "AlpTogether");
        }

        public string DeviceHwid => DeveloperAuthService.Instance.DeviceHwid;
        public string LocalAlpNetHwid { get; private set; } = string.Empty;

        public string CurrentUsername => SettingsService.Instance.Current.RegisteredUsername;
        public bool IsRegistered => !string.IsNullOrWhiteSpace(CurrentUsername);

        private readonly ConcurrentDictionary<string, UserAppPermission> _cachedPermissions = new(StringComparer.OrdinalIgnoreCase);
        private readonly SemaphoreSlim _syncSemaphore = new(1, 1);

        #region Offline Resiliency & Local Secure Permission Cache
        private static readonly byte[] CacheMagic = Encoding.ASCII.GetBytes("ATPC"); // AlpTogether Permission Cache
        private const byte CacheVersion = 1;
        private const string CacheSecretSalt = "AlpTogether#SecurePermissionCache#Key2026!Salt";
        private const string CacheHmacSalt = "AlpTogether#HMAC#Integrity2026!Salt";

        public string SecureCacheFilePath { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AlpTogether",
            "secure_perm_cache.dat");

        public bool IsOfflineMode { get; private set; }
        public DateTime? LastSyncUtc { get; private set; }
        public TimeSpan OfflineGracePeriod { get; set; } = TimeSpan.FromHours(24);

        public bool IsOfflineGracePeriodValid
        {
            get
            {
                if (!LastSyncUtc.HasValue) return false;
                // Sistem saati LastSyncUtc'den 15 dakikadan fazla geriye alınmışsa saat manipülasyonu engelle
                if (DateTime.UtcNow < LastSyncUtc.Value.AddMinutes(-15)) return false;
                return (DateTime.UtcNow - LastSyncUtc.Value) <= OfflineGracePeriod;
            }
        }

        public double RemainingOfflineGraceHours
        {
            get
            {
                if (!LastSyncUtc.HasValue) return 0;
                var rem = OfflineGracePeriod - (DateTime.UtcNow - LastSyncUtc.Value);
                return rem.TotalHours > 0 ? rem.TotalHours : 0;
            }
        }

        public string NetworkStatusDescription { get; private set; } = "Ağ: Hazır";

        public event Action? OnPermissionsChanged;
        public event Action<string>? OnUsernameRegistered;
        public event Action<bool, string>? OnNetworkStatusChanged;
        #endregion

        private UserAuthAndPermissionService()
        {
            LocalAlpNetHwid = DetectLocalAlpNetHwid();

            // 1. Yerel güvenli önbelleği anında yükle (uygulama açılır açılmaz izinleri hazır et)
            bool cacheLoaded = LoadSecurePermissionCache();
            if (cacheLoaded)
            {
                // Canlı ağ doğrulaması tamamlanana kadar yerel önbellek durumunu aktif çevrimdışı mod olarak işaretle
                IsOfflineMode = true;
                NetworkStatusDescription = IsOfflineGracePeriodValid
                    ? "Çevrimdışı Mod - Yerel Önbellek Aktif"
                    : "Çevrimdışı - Doğrulama Süresi Doldu";
            }
            else
            {
                NetworkStatusDescription = "Ağ: Hazır";
            }

            // 2. Ağ arayüzü değişikliklerini dinle (internet geri geldiğinde anında senkronize ol)
            try
            {
                NetworkChange.NetworkAvailabilityChanged += (s, e) =>
                {
                    if (e.IsAvailable)
                    {
                        _ = Task.Run(async () => await SyncSelfAndHeartbeatAsync());
                    }
                };
                NetworkChange.NetworkAddressChanged += (s, e) =>
                {
                    _ = Task.Run(async () => await SyncSelfAndHeartbeatAsync());
                };
            }
            catch { }

            _heartbeatTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(30)
            };
            _heartbeatTimer.Tick += async (s, e) => await SyncSelfAndHeartbeatAsync();
            _heartbeatTimer.Start();
        }

        #region Local AlpNet HWID & License Matcher
        /// <summary>
        private static string? _cachedDetectedLocalAlpNetHwid;

        /// <summary>
        /// Bilgisayarda kurulu AlpNet Pulse varsa yerel lisans dosyasındaki gerçek DeviceId'yi çeker.
        /// Bulunamazsa MD5 HWID algoritmasıyla AlpNet Pulse ile %100 birebir aynı değeri hesaplar.
        /// </summary>
        public static string DetectLocalAlpNetHwid()
        {
            if (!string.IsNullOrEmpty(_cachedDetectedLocalAlpNetHwid))
                return _cachedDetectedLocalAlpNetHwid;

            try
            {
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string configPath = Path.Combine(localAppData, "AlpNet", "license_config.json");
                if (File.Exists(configPath))
                {
                    string json = File.ReadAllText(configPath);
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("deviceId", out var dElem))
                    {
                        string? devId = dElem.GetString();
                        if (!string.IsNullOrWhiteSpace(devId))
                        {
                            _cachedDetectedLocalAlpNetHwid = devId.Trim().ToUpperInvariant();
                            return _cachedDetectedLocalAlpNetHwid;
                        }
                    }
                }
            }
            catch { }

            _cachedDetectedLocalAlpNetHwid = ComputeLocalAlpNetHwid();
            return _cachedDetectedLocalAlpNetHwid;
        }

        /// <summary>
        /// Base64 ile şifrelenmiş AlpNet Pulse lisans anahtarını tam olarak çözer.
        /// Format: targetDeviceId|expTicks|signature (veya TRIAL-{deviceId})
        /// </summary>
        public static bool TryDecodeAlpNetKey(string? keyStr, out string targetDeviceId, out DateTime expDate, out bool isIndefinite)
        {
            targetDeviceId = string.Empty;
            expDate = DateTime.MinValue;
            isIndefinite = false;

            if (string.IsNullOrWhiteSpace(keyStr))
                return false;

            if (keyStr.StartsWith("TRIAL-", StringComparison.OrdinalIgnoreCase))
            {
                targetDeviceId = keyStr[6..].Trim().ToUpperInvariant();
                expDate = DateTime.UtcNow.AddHours(1);
                isIndefinite = false;
                return true;
            }

            try
            {
                string rawKey = keyStr.Replace("-", "").Replace("ALPNETPULSE", "").Trim();
                byte[] decodedBytes = Convert.FromBase64String(rawKey);
                string fullKeyData = Encoding.UTF8.GetString(decodedBytes);

                string[] parts = fullKeyData.Split('|');
                if (parts.Length < 2) return false;

                if (parts.Length >= 3)
                {
                    string payload = $"{parts[0]}|{parts[1]}";
                    string expectedSig = AlpNetKeygenService.ComputeHmacSha256(payload, AlpNetKeygenService.SecretKey);
                    if (!string.Equals(parts[2].Trim(), expectedSig.Trim(), StringComparison.Ordinal)) return false;
                }

                targetDeviceId = parts[0].Trim().ToUpperInvariant();
                if (long.TryParse(parts[1].Trim(), out long ticks))
                {
                    expDate = new DateTime(ticks, DateTimeKind.Utc);
                    if (expDate.Year >= 2100)
                    {
                        isIndefinite = true;
                    }
                    return true;
                }
            }
            catch { }

            return false;
        }

        /// <summary>
        /// Bu bilgisayardaki mevcut AlpNet Pulse lisansını (yerel diskten veya Firebase'den) otomatik algılar.
        /// Eğer aktif bir AlpNet lisansı varsa, kullanıcının AlpTogether hesabına alpnet-pulse yetkisi olarak anında bağlar.
        /// </summary>
        public async Task<bool> AutoDetectAndLinkLocalAlpNetLicenseAsync(AlpTogetherUserRecord? targetRecord = null)
        {
            try
            {
                string foundKey = string.Empty;
                string foundDeviceId = LocalAlpNetHwid;

                // 1. Yerel AlpNet lisans dosyasını kontrol et (%LocalAppData%\AlpNet\license_config.json)
                try
                {
                    string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    string configPath = Path.Combine(localAppData, "AlpNet", "license_config.json");
                    if (File.Exists(configPath))
                    {
                        string json = await File.ReadAllTextAsync(configPath);
                        using var doc = JsonDocument.Parse(json);
                        if (doc.RootElement.TryGetProperty("licenseKey", out var kElem))
                            foundKey = kElem.GetString() ?? "";
                        if (doc.RootElement.TryGetProperty("deviceId", out var dElem))
                        {
                            string? dStr = dElem.GetString();
                            if (!string.IsNullOrWhiteSpace(dStr))
                                foundDeviceId = dStr.Trim().ToUpperInvariant();
                        }
                    }
                }
                catch { }

                // 2. Eğer yerelde anahtar yoksa Firebase /users/{LocalAlpNetHwid}.json'a bak
                if (string.IsNullOrWhiteSpace(foundKey) && !string.IsNullOrWhiteSpace(foundDeviceId))
                {
                    try
                    {
                        string remoteUserUrl = GetFirebaseUrl($"users/{foundDeviceId}");
                        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        var resp = await _httpClient.GetAsync(remoteUserUrl, cts.Token);
                        if (resp.IsSuccessStatusCode)
                        {
                            string json = await resp.Content.ReadAsStringAsync();
                            if (!string.IsNullOrWhiteSpace(json) && json != "null")
                            {
                                using var doc = JsonDocument.Parse(json);
                                if (doc.RootElement.TryGetProperty("license_key", out var kElem))
                                    foundKey = kElem.GetString() ?? "";
                            }
                        }
                    }
                    catch { }
                }

                if (string.IsNullOrWhiteSpace(foundKey))
                    return false;

                // 3. Lisans anahtarını çözümle
                if (TryDecodeAlpNetKey(foundKey, out string targetDevId, out DateTime expDate, out bool isIndefinite))
                {
                    if (!string.Equals(targetDevId, LocalAlpNetHwid, StringComparison.OrdinalIgnoreCase)) return false;
                    DateTime now = DateTime.UtcNow;
                    bool isActive = isIndefinite || (expDate > now);

                    if (isActive)
                    {
                        int durationDays = isIndefinite ? 99999 : (int)Math.Max(1, Math.Ceiling((expDate - now).TotalDays));
                        string expAtStr = isIndefinite ? "9999-12-31 23:59:59" : expDate.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

                        var pulsePerm = new UserAppPermission
                        {
                            HasAccess = true,
                            DurationDays = durationDays,
                            GrantedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                            ExpiresAt = expAtStr,
                            GrantedBy = "AlpNet Otomatik Eşleştirme",
                            Notes = $"Cihazdaki AlpNet Pulse lisansı ({targetDevId}) başarıyla bağlandı."
                        };

                        // Önbelleğe al
                        _cachedPermissions["alpnet-pulse"] = pulsePerm;
                        if (targetRecord != null)
                        {
                            targetRecord.AlpNetHwid = foundDeviceId;
                            targetRecord.AppPermissions["alpnet-pulse"] = pulsePerm;
                        }

                        // Firebase'e bildir
                        if (IsRegistered || !string.IsNullOrWhiteSpace(DeviceHwid))
                        {
                            string permUrl = GetFirebaseUrl($"alptogether/users/{DeviceHwid}/appPermissions/alpnet-pulse");
                            string permJson = JsonSerializer.Serialize(pulsePerm);
                            using var permContent = new StringContent(permJson, Encoding.UTF8, "application/json");
                            await _httpClient.PutAsync(permUrl, permContent);

                            string hwidPatchUrl = GetFirebaseUrl($"alptogether/users/{DeviceHwid}");
                            var patchObj = new { alpnetHwid = foundDeviceId };
                            string patchJson = JsonSerializer.Serialize(patchObj);
                            using var patchContent = new StringContent(patchJson, Encoding.UTF8, "application/json");
                            await _httpClient.PatchAsync(hwidPatchUrl, patchContent);
                        }

                        AppLoggerService.Instance.Success("ALPNET-LİSANS", $"Cihazdaki AlpNet Pulse lisansı tespit edildi ve AlpTogether hesabıyla birleştirildi ({pulsePerm.StatusBadge}).", "AlpNet");
                        OnPermissionsChanged?.Invoke();
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                AppLoggerService.Instance.Error("ALPNET-EŞLEŞTİRME", "AlpNet lisansı eşleştirilirken hata: " + ex.Message, "AlpTogether");
            }

            return false;
        }

        /// <summary>
        /// AlpNet Pulse'ın kullandığı 32 karakterlik MD5 HWID algoritması ile %100 birebir aynıdır.
        /// Bu sayede AlpTogether bu cihazdaki AlpNet Pulse kimliğini de otomatik tanır ve eşleştirir.
        /// </summary>
        public static string ComputeLocalAlpNetHwid()
        {
            try
            {
                // 1. Registry MachineGuid
                using (var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                    .OpenSubKey(@"SOFTWARE\Microsoft\Cryptography"))
                {
                    if (key != null)
                    {
                        var guid = key.GetValue("MachineGuid")?.ToString();
                        if (!string.IsNullOrEmpty(guid))
                        {
                            return Md5Hash(guid.Trim());
                        }
                    }
                }
            }
            catch { }

            try
            {
                // Fallback MAC Address + Hostname
                var interfaces = NetworkInterface.GetAllNetworkInterfaces();
                var macs = new List<string>();
                foreach (var ni in interfaces)
                {
                    if (ni.NetworkInterfaceType != NetworkInterfaceType.Loopback && ni.OperationalStatus == OperationalStatus.Up)
                    {
                        var mac = ni.GetPhysicalAddress().ToString();
                        if (!string.IsNullOrEmpty(mac) && mac != "000000000000")
                        {
                            macs.Add(mac);
                        }
                    }
                }
                if (macs.Count > 0)
                {
                    macs.Sort();
                    string combined = string.Join("-", macs) + "-" + Environment.MachineName;
                    return Md5Hash(combined);
                }
            }
            catch { }

            return Md5Hash(Environment.MachineName + Environment.UserName);
        }

        private static string Md5Hash(string input)
        {
            using var md5 = MD5.Create();
            byte[] bytes = md5.ComputeHash(Encoding.UTF8.GetBytes(input));
            return BitConverter.ToString(bytes).Replace("-", "").ToUpperInvariant();
        }
        #endregion

        #region User Registration & Name Uniqueness (1 HWID = 1 Unique Name)
        private string GetFirebaseUrl(string path)
        {
            var cfg = SettingsService.Instance.Current;
            string baseUrl = cfg.FirebaseBaseUrl.TrimEnd('/');
            string url = $"{baseUrl}/{path.TrimStart('/')}.json";
            if (!string.IsNullOrWhiteSpace(cfg.FirebaseAuthSecret))
                url += $"?auth={Uri.EscapeDataString(cfg.FirebaseAuthSecret)}";
            return url;
        }

        /// <summary>
        /// Kullanıcının girdiği rumuzu / takma adı Firebase üzerinden kontrol eder.
        /// Benzersizse (başka bir HWID tarafından alınmamışsa) bu cihaza kalıcı olarak bağlar.
        /// </summary>
        public async Task<(bool Success, string Message)> TryRegisterUsernameAsync(string chosenUsername)
        {
            string clean = (chosenUsername ?? "").Trim();

            if (clean.Length < 2)
                return (false, "Kullanıcı adı en az 2 karakter olmalıdır.");

            if (clean.Length > 24)
                return (false, "Kullanıcı adı en fazla 24 karakter olabilir.");

            if (!Regex.IsMatch(clean, @"^[a-zA-Z0-9_\-\. ]+$"))
                return (false, "Kullanıcı adı sadece harf, rakam, alt çizgi, tire ve nokta içerebilir.");

            try
            {
                // 1. Firebase /alptogether/users.json listesini çek ve benzersizlik kontrolü yap
                string checkUrl = GetFirebaseUrl("alptogether/users");
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                var resp = await _httpClient.GetAsync(checkUrl, cts.Token);

                if (resp.IsSuccessStatusCode)
                {
                    string json = await resp.Content.ReadAsStringAsync();
                    if (!string.IsNullOrWhiteSpace(json) && json != "null")
                    {
                        var usersDict = JsonSerializer.Deserialize<Dictionary<string, AlpTogetherUserRecord>>(json,
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                        if (usersDict != null)
                        {
                            foreach (var kvp in usersDict)
                            {
                                var existing = kvp.Value;
                                if (existing != null &&
                                    string.Equals(existing.Username?.Trim(), clean, StringComparison.OrdinalIgnoreCase))
                                {
                                    // Eğer bu isim zaten başka bir HWID tarafından alınmışsa engelle!
                                    if (!string.Equals(kvp.Key, DeviceHwid, StringComparison.OrdinalIgnoreCase) &&
                                        !string.Equals(existing.Hwid, DeviceHwid, StringComparison.OrdinalIgnoreCase))
                                    {
                                        return (false, $"'{clean}' kullanıcı adı zaten başka bir kullanıcı/cihaz tarafından alınmış! Lütfen kendinize özel farklı bir rumuz belirleyin.");
                                    }
                                }
                            }
                        }
                    }
                }

                // 2. Kullanıcı kaydını oluştur / güncelle
                string userUrl = GetFirebaseUrl($"alptogether/users/{DeviceHwid}");
                string nowStr = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

                // Mevcut kaydı varsa izinlerini ve adminAlias'ını koru
                AlpTogetherUserRecord record = new()
                {
                    Hwid = DeviceHwid,
                    Username = clean,
                    MachineName = Environment.MachineName,
                    RegisteredAt = string.IsNullOrWhiteSpace(SettingsService.Instance.Current.UserRegistrationDate) ? nowStr : SettingsService.Instance.Current.UserRegistrationDate,
                    LastSeen = nowStr,
                    AlpNetHwid = LocalAlpNetHwid,
                    ClientVersion = "1.0.0"
                };

                // Eğer Firebase'de önceden kayıtlıysa var olan izinleri koru
                try
                {
                    var existingResp = await _httpClient.GetAsync(userUrl);
                    if (existingResp.IsSuccessStatusCode)
                    {
                        string existJson = await existingResp.Content.ReadAsStringAsync();
                        if (!string.IsNullOrWhiteSpace(existJson) && existJson != "null")
                        {
                            var oldRec = JsonSerializer.Deserialize<AlpTogetherUserRecord>(existJson,
                                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                            if (oldRec != null)
                            {
                                record.AdminAlias = oldRec.AdminAlias ?? "";
                                if (!string.IsNullOrWhiteSpace(oldRec.RegisteredAt))
                                    record.RegisteredAt = oldRec.RegisteredAt;
                                if (oldRec.AppPermissions != null)
                                    record.AppPermissions = oldRec.AppPermissions;
                            }
                        }
                    }
                }
                catch { }

                // Eğer bu cihazda AlpNet lisansı varsa ve henüz yetki verilmemişse otomatik bağla!
                if (!record.AppPermissions.ContainsKey("alpnet-pulse"))
                {
                    await AutoDetectAndLinkLocalAlpNetLicenseAsync(record);
                }

                string putJson = JsonSerializer.Serialize(record);
                using var putContent = new StringContent(putJson, Encoding.UTF8, "application/json");
                var putResp = await _httpClient.PutAsync(userUrl, putContent);

                if (!putResp.IsSuccessStatusCode)
                {
                    return (false, $"Sunucuya kaydedilemedi (HTTP {(int)putResp.StatusCode}). Lütfen internet bağlantınızı kontrol edin.");
                }

                // Yerel ayarlara kaydet
                SettingsService.Instance.Current.RegisteredUsername = clean;
                SettingsService.Instance.Current.UserRegistrationDate = record.RegisteredAt;
                SettingsService.Instance.Save();

                AppLoggerService.Instance.Success("KULLANICI", $"Kullanıcı adı '{clean}' başarıyla kaydedildi ve cihaz HWID'sine bağlandı.", "AlpTogether");
                OnUsernameRegistered?.Invoke(clean);
                await SyncSelfAndHeartbeatAsync();

                return (true, $"Kullanıcı adınız '{clean}' başarıyla kaydedildi!");
            }
            catch (Exception ex)
            {
                return (false, "İşlem sırasında hata oluştu: " + ex.Message);
            }
        }
        #endregion

        #region Secure Permission Cache Implementation (AES-256-CBC + HMAC-SHA256)
        public static byte[] EncryptCachePayload(SecurePermissionCachePayload payload, string hwid)
        {
            string json = JsonSerializer.Serialize(payload);
            byte[] plainBytes = Encoding.UTF8.GetBytes(json);

            string machineSalt = Environment.MachineName.ToUpperInvariant();
            byte[] aesKey = SHA256.HashData(Encoding.UTF8.GetBytes($"{hwid}|KEY|{machineSalt}|{CacheSecretSalt}"));
            byte[] hmacKey = SHA256.HashData(Encoding.UTF8.GetBytes($"{hwid}|HMAC|{machineSalt}|{CacheHmacSalt}"));

            byte[] iv = RandomNumberGenerator.GetBytes(16);
            byte[] cipherBytes;

            using (var aes = Aes.Create())
            {
                aes.Key = aesKey;
                aes.IV = iv;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                using var encryptor = aes.CreateEncryptor();
                cipherBytes = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);
            }

            int payloadLen = 4 + 1 + 16 + cipherBytes.Length;
            byte[] toSign = new byte[payloadLen];
            Buffer.BlockCopy(CacheMagic, 0, toSign, 0, 4);
            toSign[4] = CacheVersion;
            Buffer.BlockCopy(iv, 0, toSign, 5, 16);
            Buffer.BlockCopy(cipherBytes, 0, toSign, 21, cipherBytes.Length);

            using var hmac = new HMACSHA256(hmacKey);
            byte[] sig = hmac.ComputeHash(toSign);

            byte[] finalData = new byte[toSign.Length + sig.Length];
            Buffer.BlockCopy(toSign, 0, finalData, 0, toSign.Length);
            Buffer.BlockCopy(sig, 0, finalData, toSign.Length, sig.Length);

            return finalData;
        }

        public static SecurePermissionCachePayload? DecryptCachePayload(byte[] fileData, string hwid)
        {
            if (fileData == null || fileData.Length < (4 + 1 + 16 + 16 + 32))
                return null;

            if (fileData[0] != CacheMagic[0] || fileData[1] != CacheMagic[1] ||
                fileData[2] != CacheMagic[2] || fileData[3] != CacheMagic[3] ||
                fileData[4] != CacheVersion)
            {
                return null;
            }

            int sigLen = 32;
            int payloadLen = fileData.Length - sigLen;
            string machineSalt = Environment.MachineName.ToUpperInvariant();
            byte[] hmacKey = SHA256.HashData(Encoding.UTF8.GetBytes($"{hwid}|HMAC|{machineSalt}|{CacheHmacSalt}"));

            using var hmac = new HMACSHA256(hmacKey);
            byte[] computedSig = hmac.ComputeHash(fileData, 0, payloadLen);

            if (!CryptographicOperations.FixedTimeEquals(computedSig, fileData.AsSpan(payloadLen, sigLen)))
            {
                return null;
            }

            byte[] aesKey = SHA256.HashData(Encoding.UTF8.GetBytes($"{hwid}|KEY|{machineSalt}|{CacheSecretSalt}"));
            byte[] iv = new byte[16];
            Buffer.BlockCopy(fileData, 5, iv, 0, 16);

            int cipherLen = payloadLen - 21;
            byte[] plainBytes;

            using (var aes = Aes.Create())
            {
                aes.Key = aesKey;
                aes.IV = iv;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                using var decryptor = aes.CreateDecryptor();
                plainBytes = decryptor.TransformFinalBlock(fileData, 21, cipherLen);
            }

            string json = Encoding.UTF8.GetString(plainBytes);
            var payload = JsonSerializer.Deserialize<SecurePermissionCachePayload>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (payload != null && string.Equals(payload.Hwid, hwid, StringComparison.OrdinalIgnoreCase))
            {
                return payload;
            }

            return null;
        }

        public bool LoadSecurePermissionCache()
        {
            try
            {
                if (!File.Exists(SecureCacheFilePath))
                    return false;

                byte[] bytes = File.ReadAllBytes(SecureCacheFilePath);
                var payload = DecryptCachePayload(bytes, DeviceHwid);
                if (payload == null)
                {
                    AppLoggerService.Instance.Warn("GÜVENLİ-ÖNBELLEK", "Yerel yetki önbelleği doğrulanamadı veya bozulmuş.", "AlpTogether");
                    return false;
                }

                LastSyncUtc = payload.LastSyncUtc.Kind == DateTimeKind.Utc
                    ? payload.LastSyncUtc
                    : payload.LastSyncUtc.ToUniversalTime();

                if (!string.IsNullOrWhiteSpace(payload.Username) &&
                    string.IsNullOrWhiteSpace(SettingsService.Instance.Current.RegisteredUsername))
                {
                    SettingsService.Instance.Current.RegisteredUsername = payload.Username;
                }

                _cachedPermissions.Clear();
                if (payload.Permissions != null && payload.Permissions.Count > 0)
                {
                    foreach (var kvp in payload.Permissions)
                    {
                        _cachedPermissions[kvp.Key] = kvp.Value;
                    }
                }

                if (payload.IsAdmin && IsOfflineGracePeriodValid)
                {
                    DeveloperAuthService.Instance.UnlockDeveloperModeDirectly();
                }

                AppLoggerService.Instance.Info("GÜVENLİ-ÖNBELLEK", $"Yerel güvenli yetki önbelleği yüklendi ({_cachedPermissions.Count} izin, Son Senkron: {payload.LastSyncUtc.ToLocalTime():yyyy-MM-dd HH:mm}).", "AlpTogether");
                return true;
            }
            catch (Exception ex)
            {
                AppLoggerService.Instance.Warn("GÜVENLİ-ÖNBELLEK", "Yerel yetki önbelleği yüklenirken hata: " + ex.Message, "AlpTogether");
                return false;
            }
        }

        public void SaveSecurePermissionCache(bool isAdmin)
        {
            try
            {
                string dir = Path.GetDirectoryName(SecureCacheFilePath)!;
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var payload = new SecurePermissionCachePayload
                {
                    Hwid = DeviceHwid,
                    Username = CurrentUsername,
                    LastSyncUtc = LastSyncUtc ?? DateTime.UtcNow,
                    Permissions = new Dictionary<string, UserAppPermission>(_cachedPermissions, StringComparer.OrdinalIgnoreCase),
                    IsAdmin = isAdmin
                };

                byte[] encrypted = EncryptCachePayload(payload, DeviceHwid);
                string tmpPath = SecureCacheFilePath + ".tmp";
                File.WriteAllBytes(tmpPath, encrypted);
                File.Move(tmpPath, SecureCacheFilePath, overwrite: true);
            }
            catch (Exception ex)
            {
                AppLoggerService.Instance.Warn("GÜVENLİ-ÖNBELLEK", "Yerel güvenli önbellek diske kaydedilemedi: " + ex.Message, "AlpTogether");
            }
        }

        #if DEBUG || true
        public void SetOfflineModeForTesting(bool isOffline, DateTime? simulatedLastSyncUtc = null)
        {
            IsOfflineMode = isOffline;
            if (simulatedLastSyncUtc.HasValue)
                LastSyncUtc = simulatedLastSyncUtc.Value.Kind == DateTimeKind.Utc
                    ? simulatedLastSyncUtc.Value
                    : simulatedLastSyncUtc.Value.ToUniversalTime();

            NetworkStatusDescription = isOffline
                ? (IsOfflineGracePeriodValid ? "Çevrimdışı Mod - Yerel Önbellek Aktif" : "Çevrimdışı - Doğrulama Süresi Doldu")
                : "Ağ: Hazır";
            OnNetworkStatusChanged?.Invoke(IsOfflineMode, NetworkStatusDescription);
        }

        public void SetCachedPermissionForTesting(string appId, UserAppPermission perm)
        {
            _cachedPermissions[appId] = perm;
        }

        public void ClearCachedPermissionsForTesting()
        {
            _cachedPermissions.Clear();
        }
        #endif
        #endregion

        #region Presence Heartbeat & Whitelist Verification
        public async Task SyncSelfAndHeartbeatAsync()
        {
            if (!await _syncSemaphore.WaitAsync(0))
            {
                // Halihazırda senkronizasyon çalışıyor, paralel mükerrer çağrıyı atla
                return;
            }

            bool networkSuccess = false;
            AlpTogetherUserRecord? userRec = null;
            bool wasOffline = IsOfflineMode;

            try
            {
                LocalAlpNetHwid = DetectLocalAlpNetHwid();

                string userUrl = GetFirebaseUrl($"alptogether/users/{DeviceHwid}");
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
                var resp = await _httpClient.GetAsync(userUrl, cts.Token);

                if (resp.IsSuccessStatusCode)
                {
                    networkSuccess = true;
                    string json = await resp.Content.ReadAsStringAsync();
                    if (!string.IsNullOrWhiteSpace(json) && json != "null")
                    {
                        userRec = JsonSerializer.Deserialize<AlpTogetherUserRecord>(json,
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                        if (userRec != null)
                        {
                            if (!string.IsNullOrWhiteSpace(userRec.Username) &&
                                string.IsNullOrWhiteSpace(SettingsService.Instance.Current.RegisteredUsername))
                            {
                                SettingsService.Instance.Current.RegisteredUsername = userRec.Username;
                                SettingsService.Instance.Save();
                            }

                            _cachedPermissions.Clear();
                            if (userRec.AppPermissions != null)
                            {
                                foreach (var kvp in userRec.AppPermissions)
                                {
                                    _cachedPermissions[kvp.Key] = kvp.Value;
                                }
                            }

                            // Buluttan gelen admin rolüne göre geliştirici modunu senkronize et
                            if (userRec.IsAdmin)
                            {
                                DeveloperAuthService.Instance.UnlockDeveloperModeDirectly();
                            }
                            else if (!DeveloperAuthService.Instance.IsPrimaryDevMachine)
                            {
                                DeveloperAuthService.Instance.LockAdminMode();
                                SettingsService.Instance.Current.IsAdminModeUnlocked = false;
                                try { SettingsService.Instance.Save(); } catch { }
                            }

                            LastSyncUtc = DateTime.UtcNow;
                            SaveSecurePermissionCache(userRec.IsAdmin);
                        }
                    }
                    else
                    {
                        // Firebase sunucusu 200 OK yanıt verdi ancak kayıt null (kullanıcı silinmiş veya kayıt açılmamış)
                        _cachedPermissions.Clear();
                        try
                        {
                            if (File.Exists(SecureCacheFilePath))
                                File.Delete(SecureCacheFilePath);
                        }
                        catch { }
                    }
                }
                else
                {
                    // HTTP 5xx veya 4xx -> Ağ veya sunucu yanıt vermiyor
                    networkSuccess = false;
                }

                // Otomatik AlpNet Pulse lisansı tespit ve eşleştirme
                if (networkSuccess && userRec != null &&
                    (!_cachedPermissions.TryGetValue("alpnet-pulse", out var existingPulse) || !existingPulse.HasAccess || existingPulse.IsExpired))
                {
                    await AutoDetectAndLinkLocalAlpNetLicenseAsync(userRec);
                }

                // Anlık durum (LastSeen, MachineName, AFK & Aktivite) heartbeat güncellemesi gönder
                if (networkSuccess && IsRegistered && resp.IsSuccessStatusCode)
                {
                    var (isAfk, afkMinutes) = GetIdleState();
                    var (actText, actApp) = DetectCurrentActivity();
                    if (isAfk)
                    {
                        actText = actText.Replace("⚡", "💤") + $" [Boşta - {afkMinutes} dk]";
                    }

                    var heartbeatData = new
                    {
                        lastSeen = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                        machineName = Environment.MachineName,
                        alpnetHwid = LocalAlpNetHwid,
                        clientVersion = "1.0.0",
                        isAfk = isAfk,
                        afkMinutes = afkMinutes,
                        currentActivity = actText
                    };
                    string patchJson = JsonSerializer.Serialize(heartbeatData);
                    using var patchContent = new StringContent(patchJson, Encoding.UTF8, "application/json");
                    await _httpClient.PatchAsync(userUrl, patchContent);
                }
            }
            catch (Exception ex)
            {
                networkSuccess = false;
                AppLoggerService.Instance.Info("AĞ-BİLGİ", "Firebase senkronizasyonu tamamlanamadı (Çevrimdışı tolerans devrede): " + ex.Message, "AlpTogether");
            }
            finally
            {
                _syncSemaphore.Release();
            }

            if (networkSuccess)
            {
                IsOfflineMode = false;
                NetworkStatusDescription = "Ağ: Hazır";
                if (wasOffline)
                {
                    AppLoggerService.Instance.Success("AĞ-BAĞLANTI", "Firebase bağlantısı yeniden sağlandı. Çevrimdışı moddan çıkıldı.", "AlpTogether");
                }
                OnNetworkStatusChanged?.Invoke(false, NetworkStatusDescription);
                OnPermissionsChanged?.Invoke();
            }
            else
            {
                IsOfflineMode = true;
                if (_cachedPermissions.IsEmpty)
                {
                    LoadSecurePermissionCache();
                }

                if (IsOfflineGracePeriodValid)
                {
                    NetworkStatusDescription = "Çevrimdışı Mod - Yerel Önbellek Aktif";
                }
                else
                {
                    NetworkStatusDescription = "Çevrimdışı - Doğrulama Süresi Doldu";
                }

                OnNetworkStatusChanged?.Invoke(true, NetworkStatusDescription);
                OnPermissionsChanged?.Invoke();
            }
        }

        /// <summary>
        /// İstemcinin ilgili uygulamayı kurma veya başlatma yetkisi olup olmadığını denetler.
        /// Firebase RTDB bağlantısı koptuğunda veya sunucu yanıt vermediğinde 24 saatlik
        /// yerel şifreli tolerans (Offline Grace Period) üzerinden kullanıcının kilitlenmesini engeller.
        /// </summary>
        public bool HasAppAccess(string appId, out string reason, out int daysLeft)
        {
            reason = string.Empty;
            daysLeft = 0;

            // 1. Kullanıcı adı henüz tanımlanmamışsa
            if (!IsRegistered)
            {
                reason = "Önce AlpTogether için bir kullanıcı adı belirlemeniz gerekmektedir.";
                daysLeft = 0;
                return false;
            }

            // 2. Geliştirici / Yönetici modu denetimi
            if (DeveloperAuthService.Instance.IsAdminMode)
            {
                reason = "Geliştirici / Yönetici tam erişim modu aktif.";
                daysLeft = 99999;
                return true;
            }

            // 3. Buluttan veya yerel güvenli önbellekten çekilen yetkiyi denetle
            if (_cachedPermissions.TryGetValue(appId, out var perm) ||
                (appId == "alprust-plus" && _cachedPermissions.TryGetValue("alprust", out perm)) ||
                (appId == "alprust" && _cachedPermissions.TryGetValue("alprust-plus", out perm)))
            {
                if (!perm.HasAccess)
                {
                    reason = "Yönetici (Alphan) bu uygulama için yetkinizi kaldırmış.";
                    daysLeft = 0;
                    return false;
                }

                if (perm.IsExpired)
                {
                    reason = $"Bu uygulamanın kullanım süresi {perm.ExpiresAt} tarihinde dolmuştur.";
                    daysLeft = 0;
                    return false;
                }

                // Çevrimdışı Mod Tolerans Denetimi (Offline Grace Period)
                if (IsOfflineMode)
                {
                    // Saat manipülasyonu kontrolü (saat geri alınmış mı?)
                    if (LastSyncUtc.HasValue && DateTime.UtcNow < LastSyncUtc.Value.AddMinutes(-15))
                    {
                        reason = "Sistem saati geriye alınmış tespit edildi! Güvenlik nedeniyle çevrimdışı kullanım kilitlendi. Lütfen saat ayarlarınızı doğrulayın ve internete bağlanın.";
                        daysLeft = 0;
                        return false;
                    }

                    if (!IsOfflineGracePeriodValid)
                    {
                        double passedHours = LastSyncUtc.HasValue ? (DateTime.UtcNow - LastSyncUtc.Value).TotalHours : 0;
                        reason = $"Çevrimdışı kullanım süresi ({OfflineGracePeriod.TotalHours:0} saat) doldu ({passedHours:0.0} saat geçti). Yetkileri doğrulamak için lütfen internete bağlanın.";
                        daysLeft = 0;
                        return false;
                    }

                    int hoursLeft = (int)Math.Max(1, Math.Ceiling(RemainingOfflineGraceHours));
                    daysLeft = perm.IsIndefinite ? 99999 : perm.DaysRemaining;
                    reason = perm.IsIndefinite
                        ? $"Çevrimdışı Mod: Süresiz yetki yerel güvenli önbellekte doğrulandı ({hoursLeft} sa çevrimdışı tolerans kaldı)."
                        : $"Çevrimdışı Mod: {daysLeft} gün yetki aktif ({hoursLeft} sa çevrimdışı tolerans kaldı).";
                    return true;
                }

                daysLeft = perm.IsIndefinite ? 99999 : perm.DaysRemaining;
                reason = perm.IsIndefinite ? "Süresiz kullanım yetkisi aktif." : $"Yetkiniz aktif ({daysLeft} gün kaldı).";
                return true;
            }

            if (IsOfflineMode)
            {
                reason = "Çevrimdışı Mod: Bu uygulama için yerel doğrulanmış yetki kaydı bulunamadı. Lütfen ilk yetkilendirme için internete bağlanın.";
                daysLeft = 0;
                return false;
            }

            reason = "Bu uygulama için yönetici (Alphan) tarafından henüz kullanım izni verilmemiş.";
            daysLeft = 0;
            return false;
        }
        #endregion

        #region Admin Whitelist & User Management Operations
        /// <summary>
        /// %LOCALAPPDATA%\AlpNet\keygen_history.json dosyasından tüm lisans anahtarı ve kullanıcı notlarını
        /// (Barış, Ferdicik, yiğit11, Arda vb.) çözer ve HWID -> Not sözlüğü döndürür.
        /// </summary>
        public static Dictionary<string, string> GetAlpNetKeygenNotes()
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            var paths = new List<string>
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AlpNet", "keygen_history.json"),
                @"C:\Users\Alphan.MonsterAlphan\Desktop\AlpNet\keygen_history.json",
                @"C:\Users\Alphan.MonsterAlphan\Desktop\AlpNet\AlpNetPulse\keygen_history.json"
            };

            foreach (var path in paths)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        string json = File.ReadAllText(path);
                        using var doc = JsonDocument.Parse(json);
                        if (doc.RootElement.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var item in doc.RootElement.EnumerateArray())
                            {
                                string note = item.TryGetProperty("Note", out var n) ? n.GetString() ?? "" : "";
                                string keyStr = item.TryGetProperty("KeyString", out var k) ? k.GetString() ?? "" : "";

                                if (string.IsNullOrWhiteSpace(note)) continue;

                                if (TryDecodeAlpNetKey(keyStr, out string targetDevId, out _, out _))
                                {
                                    if (!string.IsNullOrWhiteSpace(targetDevId))
                                    {
                                        string cleanId = targetDevId.Trim().ToUpperInvariant();
                                        dict[cleanId] = note.Trim();
                                        if (cleanId.Length >= 6)
                                        {
                                            dict[cleanId[..6]] = note.Trim();
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                catch { }
            }

            return dict;
        }

        /// <summary>
        /// Tüm kayıtlı kullanıcıları Firebase'den çeker ve son görülme zamanına göre yeniden eskiye sıralar.
        /// keygen_history.json ve telemetri olaylarıyla eşleştirerek gerçek kişi isimlerini (Barış, Ferdicik vb.) ve bilgisayar adlarını çözer.
        /// </summary>
        public async Task<List<AlpTogetherUserRecord>> FetchAllUsersAsync()
        {
            var list = new List<AlpTogetherUserRecord>();
            try
            {
                string url = GetFirebaseUrl("alptogether/users");
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var resp = await _httpClient.GetAsync(url, cts.Token);

                // 1. Resolve keygen notes mapping (Barış, Ferdicik, Arda, yiğit11, etc.)
                var keygenNotes = GetAlpNetKeygenNotes();

                // 2. Fetch telemetry events to resolve real PC machine names and usernames
                Dictionary<string, (string Machine, string User)> deviceMeta = new(StringComparer.OrdinalIgnoreCase);
                try
                {
                    string evtUrl = GetFirebaseUrl("alptogether/telemetry/events");
                    using var evtCts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
                    var evtResp = await _httpClient.GetAsync(evtUrl, evtCts.Token);
                    if (evtResp.IsSuccessStatusCode)
                    {
                        string evtJson = await evtResp.Content.ReadAsStringAsync();
                        if (!string.IsNullOrWhiteSpace(evtJson) && evtJson != "null")
                        {
                            var evtDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(evtJson);
                            if (evtDict != null)
                            {
                                foreach (var item in evtDict.Values)
                                {
                                    string devId = item.TryGetProperty("DeviceId", out var d) ? d.GetString() ?? "" : "";
                                    string mach = item.TryGetProperty("MachineName", out var m) ? m.GetString() ?? "" : "";
                                    string usr = item.TryGetProperty("UserName", out var u) ? u.GetString() ?? "" : "";
                                    if (!string.IsNullOrWhiteSpace(devId) && (!string.IsNullOrWhiteSpace(mach) || !string.IsNullOrWhiteSpace(usr)))
                                    {
                                        deviceMeta[devId] = (mach, usr);
                                    }
                                }
                            }
                        }
                    }
                }
                catch { }

                // 3. Fetch AlpNet legacy users node to resolve real-time WARP active state & VPN timestamps
                Dictionary<string, JsonElement> alpnetUsers = new(StringComparer.OrdinalIgnoreCase);
                try
                {
                    string alpnetUrl = GetFirebaseUrl("users");
                    using var alpCts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
                    var alpResp = await _httpClient.GetAsync(alpnetUrl, alpCts.Token);
                    if (alpResp.IsSuccessStatusCode)
                    {
                        string alpJson = await alpResp.Content.ReadAsStringAsync();
                        if (!string.IsNullOrWhiteSpace(alpJson) && alpJson != "null")
                        {
                            var aDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(alpJson);
                            if (aDict != null) alpnetUsers = new(aDict, StringComparer.OrdinalIgnoreCase);
                        }
                    }
                }
                catch { }

                if (resp.IsSuccessStatusCode)
                {
                    string json = await resp.Content.ReadAsStringAsync();
                    if (!string.IsNullOrWhiteSpace(json) && json != "null")
                    {
                        var dict = JsonSerializer.Deserialize<Dictionary<string, AlpTogetherUserRecord>>(json,
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                        if (dict != null)
                        {
                            foreach (var kvp in dict)
                            {
                                var user = kvp.Value;
                                if (user != null)
                                {
                                    if (string.IsNullOrWhiteSpace(user.Hwid))
                                        user.Hwid = kvp.Key;

                                    // Resolve from telemetry events if machine/pc user is unknown
                                    if ((string.IsNullOrWhiteSpace(user.MachineName) || user.MachineName == "AlpNet-PC") &&
                                        (deviceMeta.TryGetValue(user.Hwid, out var meta) ||
                                         (!string.IsNullOrWhiteSpace(user.AlpNetHwid) && deviceMeta.TryGetValue(user.AlpNetHwid, out meta))))
                                    {
                                        if (!string.IsNullOrWhiteSpace(meta.Machine)) user.MachineName = meta.Machine;
                                        if (!string.IsNullOrWhiteSpace(meta.User)) user.PcUser = meta.User;
                                    }

                                    // Map AlpNet WARP status & timestamps
                                    JsonElement alpElem = default;
                                    bool hasAlp = (!string.IsNullOrWhiteSpace(user.AlpNetHwid) && alpnetUsers.TryGetValue(user.AlpNetHwid, out alpElem)) ||
                                                  (!string.IsNullOrWhiteSpace(user.Hwid) && alpnetUsers.TryGetValue(user.Hwid, out alpElem));

                                    if (hasAlp && alpElem.ValueKind == JsonValueKind.Object)
                                    {
                                        if (alpElem.TryGetProperty("warp_active", out var waElem))
                                            user.WarpActive = waElem.GetBoolean();
                                        if (alpElem.TryGetProperty("warp_started_at", out var wsElem))
                                            user.WarpStartedAt = wsElem.GetString() ?? "";
                                        if (alpElem.TryGetProperty("warp_stopped_at", out var wstElem))
                                            user.WarpStoppedAt = wstElem.GetString() ?? "";
                                        if (alpElem.TryGetProperty("warp_total_hours_today", out var wtElem) && wtElem.TryGetDouble(out double th))
                                            user.WarpTotalHoursToday = th;
                                    }

                                    // Resolve friendly user identity from keygen history (Barış, Ferdicik, etc.)
                                    string? matchedNote = null;
                                    if (!string.IsNullOrWhiteSpace(user.AlpNetHwid))
                                    {
                                        if (keygenNotes.TryGetValue(user.AlpNetHwid, out var n1)) matchedNote = n1;
                                        else if (user.AlpNetHwid.Length >= 6 && keygenNotes.TryGetValue(user.AlpNetHwid[..6], out var n2)) matchedNote = n2;
                                    }
                                    if (string.IsNullOrWhiteSpace(matchedNote) && !string.IsNullOrWhiteSpace(user.Hwid))
                                    {
                                        if (keygenNotes.TryGetValue(user.Hwid, out var n3)) matchedNote = n3;
                                        else if (user.Hwid.Length >= 6 && keygenNotes.TryGetValue(user.Hwid[..6], out var n4)) matchedNote = n4;
                                    }

                                    if (!string.IsNullOrWhiteSpace(matchedNote))
                                    {
                                        bool needPatch = false;
                                        if (string.IsNullOrWhiteSpace(user.AdminAlias) || user.AdminAlias.StartsWith("AlpNet", StringComparison.OrdinalIgnoreCase))
                                        {
                                            user.AdminAlias = matchedNote;
                                            needPatch = true;
                                        }
                                        if (string.IsNullOrWhiteSpace(user.Username) || user.Username.StartsWith("AlpNet_", StringComparison.OrdinalIgnoreCase))
                                        {
                                            user.Username = matchedNote;
                                            needPatch = true;
                                        }

                                        if (needPatch)
                                        {
                                            string uHwid = user.Hwid;
                                            string uName = user.Username;
                                            string uAlias = user.AdminAlias;
                                            _ = Task.Run(async () =>
                                            {
                                                try
                                                {
                                                    string patchUrl = GetFirebaseUrl($"alptogether/users/{uHwid}");
                                                    var patchObj = new { username = uName, adminAlias = uAlias };
                                                    string pJson = JsonSerializer.Serialize(patchObj);
                                                    using var pContent = new StringContent(pJson, Encoding.UTF8, "application/json");
                                                    await _httpClient.PatchAsync(patchUrl, pContent);
                                                }
                                                catch { }
                                            });
                                        }
                                    }

                                    list.Add(user);
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AppLoggerService.Instance.Error("ADMİN-USER", "Kullanıcı listesi alınamadı.", "AlpTogether", ex.Message);
            }

            // Son görülme / kayıt tarihine göre yeniden eskiye sırala (Alphan en son giren kişiyi en üstte görsün)
            return list.OrderByDescending(u => u.SortTime).ToList();
        }

        /// <summary>
        /// Seçili kullanıcıyı Yönetici (Admin) olarak atar veya yöneticilik yetkisini geri alır.
        /// </summary>
        public async Task<bool> SetUserAdminRoleAsync(string hwid, bool isAdmin)
        {
            try
            {
                string cleanHwid = hwid.Trim();
                string patchUrl = GetFirebaseUrl($"alptogether/users/{cleanHwid}");
                var patchObj = new { isAdmin = isAdmin };
                string json = JsonSerializer.Serialize(patchObj);
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                await _httpClient.PatchAsync(patchUrl, content);

                var allApps = new List<string> { "alpnet-pulse", "alplingo", "alpfinans", "alphagravity", "alprust-plus", "alprust" };
                try
                {
                    var catalogApps = CatalogAndHybridInstallerService.Instance.Apps;
                    if (catalogApps != null)
                    {
                        foreach (var a in catalogApps)
                        {
                            if (!allApps.Contains(a.Id, StringComparer.OrdinalIgnoreCase))
                                allApps.Add(a.Id);
                        }
                    }
                }
                catch { }

                if (isAdmin)
                {
                    string adminKey = DeveloperAuthService.GenerateAdminKey(cleanHwid, new DateTime(2099, 12, 31, 23, 59, 59, DateTimeKind.Utc));
                    await DeveloperAuthService.Instance.PushAdminLicenseToFirebaseAsync(cleanHwid, adminKey);

                    foreach (var appId in allApps)
                    {
                        await GrantAppPermissionAsync(cleanHwid, appId, 99999, "👑 Yönetici (Admin) Süresiz Tam Yetki");
                    }
                    AppLoggerService.Instance.Success("ADMIN-ATAMA", $"'{cleanHwid}' başarıyla YÖNETİCİ (Admin) yapıldı ve tüm uygulamalara süresiz whitelist yetkisi tanımlandı.", "AlpTogether");
                }
                else
                {
                    string delUrl = GetFirebaseUrl($"alptogether/dev_licenses/{cleanHwid}");
                    await _httpClient.DeleteAsync(delUrl);

                    foreach (var appId in allApps)
                    {
                        await RevokeAppPermissionAsync(cleanHwid, appId);
                    }
                    AppLoggerService.Instance.Warn("ADMIN-KALDIR", $"'{cleanHwid}' yönetici yetkisi ve süresiz erişimleri geri alındı.", "AlpTogether");
                }

                return true;
            }
            catch (Exception ex)
            {
                AppLoggerService.Instance.Error("ADMIN-HATA", "Admin rolü değiştirilemedi: " + ex.Message, "AlpTogether");
                return false;
            }
        }

        /// <summary>
        /// Buluttaki ve yerel diskteki eski telemetri olaylarını ve hata loglarını temizler.
        /// </summary>
        public async Task<bool> ClearOldTelemetryAsync(bool allLogs = true)
        {
            try
            {
                // 1. Bulut (Firebase RTDB) telemetri kayıtlarını sil
                string eventsUrl = GetFirebaseUrl("alptogether/telemetry/events");
                string errorsUrl = GetFirebaseUrl("alptogether/telemetry/errors");

                await _httpClient.DeleteAsync(eventsUrl);
                await _httpClient.DeleteAsync(errorsUrl);

                // 2. Yerel disk önbelleğini temizle (tekrar birleşip listeye girmesinler)
                TelemetryAndHelpService.Instance.ClearLocalTelemetryCache();

                AppLoggerService.Instance.Success("LOG-TEMİZLE", "Buluttaki ve yereldeki telemetri ve hata kayıtları başarıyla temizlendi.", "AlpTogether");
                return true;
            }
            catch (Exception ex)
            {
                AppLoggerService.Instance.Error("LOG-TEMİZLE-HATA", "Loglar temizlenirken hata: " + ex.Message, "AlpTogether");
                return false;
            }
        }

        /// <summary>
        /// Kullanıcının Alphan tarafından görünen takma adını (alias) günceller.
        /// </summary>
        public async Task<bool> UpdateUserAliasAsync(string hwid, string newAlias)
        {
            try
            {
                string url = GetFirebaseUrl($"alptogether/users/{hwid.Trim()}");
                var data = new { adminAlias = (newAlias ?? "").Trim() };
                string json = JsonSerializer.Serialize(data);
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                var resp = await _httpClient.PatchAsync(url, content);
                return resp.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Kullanıcıya belirli bir uygulama için belirli süreli veya süresiz yetki tanımlar.
        /// </summary>
        public async Task<bool> GrantAppPermissionAsync(string hwid, string appId, int durationDays, string? notes = null)
        {
            try
            {
                string url = GetFirebaseUrl($"alptogether/users/{hwid.Trim()}/appPermissions/{appId.Trim()}");
                DateTime now = DateTime.Now;
                string expiresAt = durationDays >= 9999 ? "9999-12-31 23:59:59" : now.AddDays(durationDays).ToString("yyyy-MM-dd HH:mm:ss");

                var perm = new UserAppPermission
                {
                    HasAccess = true,
                    DurationDays = durationDays,
                    GrantedAt = now.ToString("yyyy-MM-dd HH:mm:ss"),
                    ExpiresAt = expiresAt,
                    GrantedBy = "Alphan (Admin)",
                    Notes = notes
                };

                string json = JsonSerializer.Serialize(perm);
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                var resp = await _httpClient.PutAsync(url, content);

                if (resp.IsSuccessStatusCode)
                {
                    if (string.Equals(hwid.Trim(), DeviceHwid, StringComparison.OrdinalIgnoreCase))
                    {
                        _cachedPermissions[appId.Trim()] = perm;
                        SaveSecurePermissionCache(DeveloperAuthService.Instance.IsAdminMode);
                        OnPermissionsChanged?.Invoke();
                    }
                    AppLoggerService.Instance.Success("YETKİ-TANIM", $"'{hwid}' için {appId} yetkisi verildi ({durationDays} gün).", appId);
                    return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                AppLoggerService.Instance.Error("YETKİ-HATA", $"Yetki verilemedi: {ex.Message}", appId);
                return false;
            }
        }

        /// <summary>
        /// Kullanıcının belirli bir uygulamadaki yetkisini kaldırır.
        /// DİKKAT: KULLANICIYI VERİTABANINDAN SİLMEZ! Sadece o uygulama için hasAccess = false yapar.
        /// </summary>
        public async Task<bool> RevokeAppPermissionAsync(string hwid, string appId)
        {
            try
            {
                string url = GetFirebaseUrl($"alptogether/users/{hwid.Trim()}/appPermissions/{appId.Trim()}");
                var patchData = new
                {
                    hasAccess = false,
                    revokedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                };

                string json = JsonSerializer.Serialize(patchData);
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                var resp = await _httpClient.PatchAsync(url, content);

                if (resp.IsSuccessStatusCode)
                {
                    if (string.Equals(hwid.Trim(), DeviceHwid, StringComparison.OrdinalIgnoreCase))
                    {
                        if (_cachedPermissions.TryGetValue(appId.Trim(), out var existing))
                        {
                            existing.HasAccess = false;
                        }
                        else
                        {
                            _cachedPermissions[appId.Trim()] = new UserAppPermission { HasAccess = false };
                        }
                        SaveSecurePermissionCache(DeveloperAuthService.Instance.IsAdminMode);
                        OnPermissionsChanged?.Invoke();
                    }
                    AppLoggerService.Instance.Warn("YETKİ-KALDIR", $"'{hwid}' için {appId} yetkisi kaldırıldı (Kullanıcı korundu).", appId);
                    return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                AppLoggerService.Instance.Error("YETKİ-HATA", $"Yetki kaldırılamadı: {ex.Message}", appId);
                return false;
            }
        }

        /// <summary>
        /// Kullanıcıyı ve tüm verilerini Firebase'den tamamen siler.
        /// </summary>
        public async Task<bool> DeleteUserAsync(string hwid)
        {
            try
            {
                string url = GetFirebaseUrl($"alptogether/users/{hwid.Trim()}");
                var resp = await _httpClient.DeleteAsync(url);
                if (resp.IsSuccessStatusCode)
                {
                    AppLoggerService.Instance.Info("KULLANICI-SİL", $"Kullanıcı tamamen silindi: {hwid}", "AlpTogether");
                    return true;
                }
                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Eski AlpNet Pulse kullanıcılarını (/users/ altındaki kayıtları) bozmadan yeni
        /// AlpTogether /alptogether/users/ sistemine aktarır ve AlpNet yetkisi tanımlar.
        /// </summary>
        /// <summary>
        /// Eski AlpNet Pulse kullanıcılarını (/users/ altındaki kayıtları) ve lisans anahtarlarını bozmadan yeni
        /// AlpTogether /alptogether/users/ sistemine aktarır, anahtarların bitiş sürelerini tam çözer ve 
        /// var olan AlpTogether hesaplarıyla (ör. Alphan) tek potada birleştirir.
        /// </summary>
        public async Task<(int ImportedCount, int MergedCount)> ImportLegacyAlpNetUsersAsync()
        {
            int imported = 0;
            int merged = 0;

            try
            {
                // 1. Eski AlpNet kullanıcılarını çek
                string legacyUrl = GetFirebaseUrl("users");
                var resp = await _httpClient.GetAsync(legacyUrl);
                if (!resp.IsSuccessStatusCode) return (0, 0);

                string json = await resp.Content.ReadAsStringAsync();
                if (string.IsNullOrWhiteSpace(json) || json == "null") return (0, 0);

                var legacyDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
                if (legacyDict == null) return (0, 0);

                // 2. Mevcut AlpTogether kullanıcılarını çek
                string currentUrl = GetFirebaseUrl("alptogether/users");
                var curResp = await _httpClient.GetAsync(currentUrl);
                Dictionary<string, AlpTogetherUserRecord>? curDict = null;
                if (curResp.IsSuccessStatusCode)
                {
                    string curJson = await curResp.Content.ReadAsStringAsync();
                    if (!string.IsNullOrWhiteSpace(curJson) && curJson != "null")
                    {
                        curDict = JsonSerializer.Deserialize<Dictionary<string, AlpTogetherUserRecord>>(curJson,
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    }
                }
                curDict ??= new();

                var keygenNotes = GetAlpNetKeygenNotes();
                DateTime nowUtc = DateTime.UtcNow;

                foreach (var kvp in legacyDict)
                {
                    string legacyHwid = kvp.Key.Trim().ToUpperInvariant();
                    var elem = kvp.Value;

                    string licenseKey = string.Empty;
                    if (elem.TryGetProperty("license_key", out var kElem))
                        licenseKey = kElem.GetString() ?? "";

                    string lastSeen = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                    if (elem.TryGetProperty("last_seen", out var lsElem))
                    {
                        string? lsRaw = lsElem.GetString();
                        if (DateTime.TryParse(lsRaw, out var lsParsed))
                            lastSeen = lsParsed.ToString("yyyy-MM-dd HH:mm:ss");
                    }

                    string version = "2.2.5";
                    if (elem.TryGetProperty("version", out var vElem))
                        version = vElem.GetString() ?? version;

                    // Lisans anahtarını çözümle
                    bool hasAccess = false;
                    int durationDays = 0;
                    string expiresAt = "Süresi Dolmuş";
                    bool isIndefinite = false;

                    if (TryDecodeAlpNetKey(licenseKey, out string decTargetId, out DateTime expDateUtc, out isIndefinite))
                    {
                        if (isIndefinite)
                        {
                            hasAccess = true;
                            durationDays = 99999;
                            expiresAt = "9999-12-31 23:59:59";
                        }
                        else if (expDateUtc > nowUtc)
                        {
                            hasAccess = true;
                            durationDays = (int)Math.Max(1, Math.Ceiling((expDateUtc - nowUtc).TotalDays));
                            expiresAt = expDateUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
                        }
                        else
                        {
                            hasAccess = false;
                            durationDays = 0;
                            expiresAt = expDateUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
                        }
                    }
                    else if (!string.IsNullOrWhiteSpace(licenseKey))
                    {
                        // Anahtar formatı bilinemiyorsa ama anahtarı varsa koruma amaçlı süresiz ver
                        hasAccess = true;
                        durationDays = 99999;
                        expiresAt = "9999-12-31 23:59:59";
                        isIndefinite = true;
                    }

                    var pulsePerm = new UserAppPermission
                    {
                        HasAccess = hasAccess,
                        DurationDays = durationDays,
                        GrantedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                        ExpiresAt = expiresAt,
                        GrantedBy = "AlpNet Lisans Aktarımı",
                        Notes = isIndefinite ? "Süresiz AlpNet lisansı aktarıldı." : $"AlpNet lisansı aktarıldı (Bitiş: {expiresAt})."
                    };

                    // Mevcut bir AlpTogether kullanıcısı ile eşleşiyor mu?
                    var existingUserKvp = curDict.FirstOrDefault(u =>
                        string.Equals(u.Value.AlpNetHwid, legacyHwid, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(u.Value.Hwid, legacyHwid, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(u.Key, legacyHwid, StringComparison.OrdinalIgnoreCase));

                    if (existingUserKvp.Value != null)
                    {
                        // MEVCUT HESAPLA BİRLEŞTİR (ÖRN: ALPHAN!)
                        var existingUser = existingUserKvp.Value;
                        string userHwid = string.IsNullOrWhiteSpace(existingUser.Hwid) ? existingUserKvp.Key : existingUser.Hwid;

                        existingUser.AlpNetHwid = legacyHwid;
                        existingUser.AppPermissions["alpnet-pulse"] = pulsePerm;

                        // Firebase'e sadece yetki ve alpnetHwid güncellemesi gönder (kullanıcının adı/şifresi/özel ayarı bozulmaz!)
                        string permUrl = GetFirebaseUrl($"alptogether/users/{userHwid}/appPermissions/alpnet-pulse");
                        string permJson = JsonSerializer.Serialize(pulsePerm);
                        using var permContent = new StringContent(permJson, Encoding.UTF8, "application/json");
                        await _httpClient.PutAsync(permUrl, permContent);

                        string patchUrl = GetFirebaseUrl($"alptogether/users/{userHwid}");
                        var patchObj = new { alpnetHwid = legacyHwid };
                        string patchJson = JsonSerializer.Serialize(patchObj);
                        using var patchContent = new StringContent(patchJson, Encoding.UTF8, "application/json");
                        await _httpClient.PatchAsync(patchUrl, patchContent);

                        merged++;
                    }
                    else
                    {
                        // YENİ KULLANICI OLARAK EKLE
                        string shortId = legacyHwid.Length >= 6 ? legacyHwid[..6] : legacyHwid;

                        string machineName = "AlpNet-PC";
                        if (elem.TryGetProperty("machine_name", out var mnElem) || elem.TryGetProperty("machineName", out mnElem))
                            machineName = mnElem.GetString() ?? machineName;

                        string pcUser = "";
                        if (elem.TryGetProperty("pc_user", out var puElem) || elem.TryGetProperty("pcUser", out puElem) || elem.TryGetProperty("username", out puElem))
                            pcUser = puElem.GetString() ?? "";

                        string resolvedNote = "";
                        if (keygenNotes.TryGetValue(legacyHwid, out var kn1)) resolvedNote = kn1;
                        else if (legacyHwid.Length >= 6 && keygenNotes.TryGetValue(legacyHwid[..6], out var kn2)) resolvedNote = kn2;

                        string username = !string.IsNullOrWhiteSpace(resolvedNote)
                            ? resolvedNote
                            : (!string.IsNullOrWhiteSpace(pcUser) ? pcUser : (!string.IsNullOrWhiteSpace(machineName) && machineName != "AlpNet-PC" ? machineName : $"AlpNet_{shortId}"));

                        string alias = !string.IsNullOrWhiteSpace(resolvedNote)
                            ? resolvedNote
                            : (isIndefinite 
                                ? "AlpNet Süresiz Lisans" 
                                : (hasAccess ? $"AlpNet Lisanslı ({durationDays} Gün)" : "AlpNet (Süresi Dolmuş)"));

                        var newRecord = new AlpTogetherUserRecord
                        {
                            Hwid = legacyHwid,
                            Username = username,
                            AdminAlias = alias,
                            MachineName = machineName,
                            PcUser = pcUser,
                            RegisteredAt = lastSeen,
                            LastSeen = lastSeen,
                            AlpNetHwid = legacyHwid,
                            ClientVersion = version,
                            AppPermissions = new Dictionary<string, UserAppPermission>(StringComparer.OrdinalIgnoreCase)
                            {
                                ["alpnet-pulse"] = pulsePerm
                            }
                        };

                        string putUrl = GetFirebaseUrl($"alptogether/users/{legacyHwid}");
                        string putJson = JsonSerializer.Serialize(newRecord);
                        using var content = new StringContent(putJson, Encoding.UTF8, "application/json");
                        var putResp = await _httpClient.PutAsync(putUrl, content);

                        if (putResp.IsSuccessStatusCode)
                        {
                            imported++;
                            curDict[legacyHwid] = newRecord;
                        }
                    }
                }

                AppLoggerService.Instance.Success("AKTARIM-BİRLEŞTİRME", 
                    $"{imported} yeni kullanıcı aktarıldı, {merged} mevcut kullanıcıya AlpNet Pulse yetkisi bağlandı.", "AlpTogether");
            }
            catch (Exception ex)
            {
                AppLoggerService.Instance.Error("AKTARIM-HATA", "AlpNet kullanıcıları aktarılırken hata: " + ex.Message, "AlpTogether");
            }

            return (imported, merged);
        }
        #endregion
    }
}
