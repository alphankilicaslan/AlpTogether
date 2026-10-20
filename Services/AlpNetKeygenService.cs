using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace AlpTogether.Services
{
    public class AlpNetKeygenService
    {
        public static AlpNetKeygenService Instance { get; } = new();

        public const string SecretKey = "AlpNetSecret2026!PulseCore";
        private const string FirebaseBaseUrl = "https://alpnet-d0bc4-default-rtdb.europe-west1.firebasedatabase.app";
        private const string FirebaseAuth = "ExNWZwF2CkByKFILhfQ2BbTpuxYFlFAUO8quWwfc";

        private readonly string _historyFile;
        private readonly string _bannedHistoryFile;
        private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(10) };

        public List<KeygenLicenseItem> History { get; private set; } = new();
        public List<KeygenLicenseItem> BannedHistory { get; private set; } = new();
        public List<KeygenActiveDevice> LiveDevices { get; private set; } = new();
        public KeygenAppState CurrentAppState { get; private set; } = new();

        public decimal TotalRevenue => History.Sum(x => x.Price);
        public int ActiveSubs => History.Count(x => !x.IsExpired);
        public int TotalLicenses => History.Count;

        private AlpNetKeygenService()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string alpNetDir = Path.Combine(appData, "AlpNet");
            if (!Directory.Exists(alpNetDir))
            {
                Directory.CreateDirectory(alpNetDir);
            }

            _historyFile = Path.Combine(alpNetDir, "keygen_history.json");
            _bannedHistoryFile = Path.Combine(alpNetDir, "banned_history.json");

            LoadHistory();
            LoadBannedHistory();
        }

        #region Persistence (Local JSON Files)
        public void LoadHistory()
        {
            try
            {
                if (File.Exists(_historyFile))
                {
                    string json = File.ReadAllText(_historyFile);
                    var list = JsonSerializer.Deserialize<List<KeygenLicenseItem>>(json);
                    if (list != null)
                    {
                        History = list;
                    }
                }
            }
            catch { }
        }

        public void SaveHistory()
        {
            try
            {
                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(History, options);
                File.WriteAllText(_historyFile, json);
            }
            catch { }
        }

        public void LoadBannedHistory()
        {
            try
            {
                if (File.Exists(_bannedHistoryFile))
                {
                    string json = File.ReadAllText(_bannedHistoryFile);
                    var list = JsonSerializer.Deserialize<List<KeygenLicenseItem>>(json);
                    if (list != null)
                    {
                        BannedHistory = list;
                    }
                }
            }
            catch { }
        }

        public void SaveBannedHistory()
        {
            try
            {
                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(BannedHistory, options);
                File.WriteAllText(_bannedHistoryFile, json);
            }
            catch { }
        }
        #endregion

        #region License Generation & Validation
        public string GenerateLicense(string hwid, int hours, string durationStr, string note, decimal price)
        {
            hwid = hwid.Trim();
            if (string.IsNullOrEmpty(hwid))
                throw new ArgumentException("Donanım kimliği (HWID) boş olamaz.");

            // Remove expired keys for this HWID
            History.RemoveAll(item =>
            {
                if (item.IsExpired)
                {
                    try
                    {
                        string dec = Encoding.UTF8.GetString(Convert.FromBase64String(item.KeyString));
                        return dec.StartsWith(hwid + "|");
                    }
                    catch { return false; }
                }
                return false;
            });

            DateTime expireDate = hours >= 24 * 3650 ? DateTime.Now.AddYears(100) : DateTime.Now.AddHours(hours);
            long expTicks = expireDate.Ticks;

            string payload = $"{hwid}|{expTicks}";
            string signature = ComputeHmacSha256(payload, SecretKey);
            string finalKey = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{payload}|{signature}"));

            if (string.IsNullOrWhiteSpace(note)) note = "-";

            var newItem = new KeygenLicenseItem
            {
                CreatedAt = DateTime.Now.ToString("dd.MM.yyyy HH:mm"),
                Note = note,
                Duration = durationStr,
                KeyString = finalKey,
                ExpiresAtTicks = expTicks,
                Price = price
            };

            History.Insert(0, newItem);
            SaveHistory();

            // Push to Firebase in background
            _ = PushLicenseToFirebaseAsync(hwid, finalKey);

            return finalKey;
        }

        public async Task PushLicenseToFirebaseAsync(string hwid, string key)
        {
            try
            {
                string url = $"{FirebaseBaseUrl}/users/{hwid}.json?auth={FirebaseAuth}";
                var data = new { license_key = key };
                string json = JsonSerializer.Serialize(data);
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                await _httpClient.PatchAsync(url, content);
            }
            catch { }
        }

        public int DeleteExpiredLicenses()
        {
            long currentTicks = DateTime.Now.Ticks;
            int removed = History.RemoveAll(h => h.ExpiresAtTicks > 0 && h.ExpiresAtTicks < currentTicks);
            if (removed > 0)
            {
                SaveHistory();
            }
            return removed;
        }

        public void DeleteLicense(KeygenLicenseItem item)
        {
            History.Remove(item);
            SaveHistory();
        }

        public void UpdateNote(KeygenLicenseItem item, string newNote)
        {
            item.Note = newNote.Trim();
            SaveHistory();
        }

        public static string ComputeHmacSha256(string data, string key)
        {
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(key));
            byte[] hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
            return Convert.ToBase64String(hash);
        }
        #endregion

        #region Ban & Blacklist
        public async Task ProcessBanAsync(KeygenLicenseItem item, string targetToBan, string banReason)
        {
            if (!string.IsNullOrEmpty(targetToBan))
            {
                try
                {
                    string url = $"{FirebaseBaseUrl}/blacklist.json?auth={FirebaseAuth}";
                    List<string> blacklist = new();

                    var resp = await _httpClient.GetAsync(url);
                    if (resp.IsSuccessStatusCode)
                    {
                        string bJson = await resp.Content.ReadAsStringAsync();
                        if (!string.IsNullOrWhiteSpace(bJson) && bJson != "null")
                        {
                            var list = JsonSerializer.Deserialize<List<string>>(bJson);
                            if (list != null) blacklist = list;
                        }
                    }

                    if (!blacklist.Contains(targetToBan))
                    {
                        blacklist.Add(targetToBan);
                        string json = JsonSerializer.Serialize(blacklist);
                        using var content = new StringContent(json, Encoding.UTF8, "application/json");
                        await _httpClient.PutAsync(url, content);
                    }
                }
                catch { }
            }

            History.Remove(item);
            SaveHistory();

            item.Note = $"{banReason} - {item.Note}";
            BannedHistory.Insert(0, item);
            SaveBannedHistory();
        }

        public async Task RemoveFromBlacklistAsync(KeygenLicenseItem item)
        {
            string targetToUnban = "";
            bool isHwidBan = item.Note.StartsWith("YASAKLI (HWID)");

            if (isHwidBan)
            {
                try
                {
                    string rawKey = item.KeyString.Replace("-", "").Replace("ALPNETPULSE", "");
                    byte[] decodedBytes = Convert.FromBase64String(rawKey);
                    string fullKeyData = Encoding.UTF8.GetString(decodedBytes);
                    string[] parts = fullKeyData.Split('|');
                    if (parts.Length > 0) targetToUnban = parts[0];
                }
                catch { }
                if (string.IsNullOrEmpty(targetToUnban)) targetToUnban = item.KeyString;
            }
            else
            {
                targetToUnban = item.KeyString;
            }

            if (!string.IsNullOrEmpty(targetToUnban))
            {
                try
                {
                    string url = $"{FirebaseBaseUrl}/blacklist.json?auth={FirebaseAuth}";
                    List<string> blacklist = new();

                    var resp = await _httpClient.GetAsync(url);
                    if (resp.IsSuccessStatusCode)
                    {
                        string bJson = await resp.Content.ReadAsStringAsync();
                        if (!string.IsNullOrWhiteSpace(bJson) && bJson != "null")
                        {
                            var blist = JsonSerializer.Deserialize<List<string>>(bJson);
                            if (blist != null) blacklist = blist;
                        }
                    }

                    if (blacklist.Contains(targetToUnban))
                    {
                        blacklist.Remove(targetToUnban);
                        string json = JsonSerializer.Serialize(blacklist);
                        using var content = new StringContent(json, Encoding.UTF8, "application/json");
                        await _httpClient.PutAsync(url, content);
                    }
                }
                catch { }
            }

            BannedHistory.Remove(item);

            if (item.Note.StartsWith("YASAKLI (HWID) - "))
                item.Note = item.Note["YASAKLI (HWID) - ".Length..];
            else if (item.Note.StartsWith("SİLİNDİ (ANAHTAR) - "))
                item.Note = item.Note["SİLİNDİ (ANAHTAR) - ".Length..];

            History.Insert(0, item);

            SaveBannedHistory();
            SaveHistory();
        }
        #endregion

        #region Live Devices & App State
        public async Task<List<KeygenActiveDevice>> FetchLiveDevicesAsync()
        {
            try
            {
                string url = $"{FirebaseBaseUrl}/users.json?auth={FirebaseAuth}";
                var resp = await _httpClient.GetAsync(url);
                if (resp.IsSuccessStatusCode)
                {
                    string json = await resp.Content.ReadAsStringAsync();
                    if (!string.IsNullOrWhiteSpace(json) && json != "null")
                    {
                        var dict = JsonSerializer.Deserialize<Dictionary<string, FirebaseUserDto>>(json);
                        var activeList = new List<KeygenActiveDevice>();

                        if (dict != null)
                        {
                            foreach (var kvp in dict)
                            {
                                var dto = kvp.Value;
                                var device = new KeygenActiveDevice
                                {
                                    Hwid = dto.hwid ?? kvp.Key,
                                    LicenseKey = dto.license_key ?? "-",
                                    Version = dto.version ?? "-"
                                };

                                if (DateTime.TryParse(dto.last_seen, out DateTime lastSeen))
                                {
                                    device.LastSeen = lastSeen;
                                }

                                if (device.LicenseKey.StartsWith("TRIAL-"))
                                {
                                    device.Note = "Ücretsiz Deneme";
                                    device.Duration = "1 Saatlik";
                                }
                                else
                                {
                                    var histItem = History.Find(h => h.KeyString == device.LicenseKey);
                                    if (histItem != null)
                                    {
                                        device.Note = histItem.Note;
                                        device.Duration = histItem.Duration;
                                    }
                                    else
                                    {
                                        device.Note = "-";
                                        device.Duration = "-";
                                    }
                                }

                                activeList.Add(device);
                            }
                        }

                        // Sort by newest online first
                        activeList.Sort((a, b) => b.LastSeen.CompareTo(a.LastSeen));
                        LiveDevices = activeList;
                        return LiveDevices;
                    }
                }
            }
            catch { }

            return LiveDevices;
        }

        public async Task<KeygenAppState> FetchAppStateAsync()
        {
            try
            {
                string url = $"{FirebaseBaseUrl}/appstate.json?auth={FirebaseAuth}";
                var resp = await _httpClient.GetAsync(url);
                if (resp.IsSuccessStatusCode)
                {
                    string json = await resp.Content.ReadAsStringAsync();
                    if (!string.IsNullOrWhiteSpace(json) && json != "null")
                    {
                        var state = JsonSerializer.Deserialize<KeygenAppState>(json);
                        if (state != null)
                        {
                            CurrentAppState = state;
                            return CurrentAppState;
                        }
                    }
                }
            }
            catch { }

            return CurrentAppState;
        }

        public async Task<bool> PatchAppStateAsync(object state)
        {
            try
            {
                string json = JsonSerializer.Serialize(state);
                string url = $"{FirebaseBaseUrl}/appstate.json?auth={FirebaseAuth}";
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                var resp = await _httpClient.PatchAsync(url, content);
                return resp.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        public async Task<bool> SendDirectMessageAsync(string hwid, string message)
        {
            try
            {
                string url = $"{FirebaseBaseUrl}/messages/{hwid}.json?auth={FirebaseAuth}";
                var contentObj = new { message = message.Trim() };
                string json = JsonSerializer.Serialize(contentObj);
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                var resp = await _httpClient.PutAsync(url, content);
                return resp.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }
        #endregion
    }

    public class KeygenLicenseItem
    {
        public string CreatedAt { get; set; } = "";
        public string Note { get; set; } = "";
        public string Duration { get; set; } = "";
        public string KeyString { get; set; } = "";
        public long ExpiresAtTicks { get; set; } = 0;
        public decimal Price { get; set; } = 0;

        [System.Text.Json.Serialization.JsonIgnore]
        public string FormattedPrice => $"₺{Price:0.00}";

        [System.Text.Json.Serialization.JsonIgnore]
        public bool IsExpired => ExpiresAtTicks > 0 && DateTime.Now.Ticks > ExpiresAtTicks;

        [System.Text.Json.Serialization.JsonIgnore]
        public string StatusText => IsExpired ? "Süresi Doldu" : (ExpiresAtTicks == 0 ? "Belirsiz" : "Aktif");

        [System.Text.Json.Serialization.JsonIgnore]
        public string StatusColorHex => IsExpired ? "#FF1744" : (ExpiresAtTicks == 0 ? "#ADB5BD" : "#00E676");
    }

    public class KeygenActiveDevice
    {
        public string Hwid { get; set; } = "";
        public string LicenseKey { get; set; } = "";
        public string Note { get; set; } = "";
        public string Duration { get; set; } = "";
        public string Version { get; set; } = "-";
        public DateTime LastSeen { get; set; } = DateTime.MinValue;

        public bool IsOnline => (DateTime.UtcNow - LastSeen).TotalMinutes <= 3;
        public string Status => IsOnline ? "Çevrimiçi" : "Çevrimdışı";
        public string LastSeenText => LastSeen == DateTime.MinValue ? "Bilinmiyor" : LastSeen.ToLocalTime().ToString("dd.MM.yyyy HH:mm");
        public string StatusColorHex => IsOnline ? "#00E676" : "#ADB5BD";
    }

    public class KeygenAppState
    {
        public bool maintenance { get; set; } = false;
        public string maintenanceMsg { get; set; } = "Sunucularımızda bakım çalışması yapılıyor. Lütfen daha sonra tekrar deneyin.";
        public string broadcastMsg { get; set; } = "";
        public string version { get; set; } = "1.0.0";
        public string updateUrl { get; set; } = "https://alpnet.com/download";
    }

    public class FirebaseUserDto
    {
        public string? hwid { get; set; }
        public string? license_key { get; set; }
        public string? last_seen { get; set; }
        public string? version { get; set; }
    }
}
