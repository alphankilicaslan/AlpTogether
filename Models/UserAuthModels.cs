using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace AlpTogether.Models
{
    public class UserAppPermission : INotifyPropertyChanged
    {
        private bool _hasAccess;
        [JsonPropertyName("hasAccess")]
        public bool HasAccess
        {
            get => _hasAccess;
            set
            {
                if (_hasAccess != value)
                {
                    _hasAccess = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(StatusBadge));
                    OnPropertyChanged(nameof(StatusColorHex));
                    OnPropertyChanged(nameof(StatusDescription));
                }
            }
        }

        [JsonPropertyName("grantedAt")]
        public string GrantedAt { get; set; } = string.Empty;

        [JsonPropertyName("expiresAt")]
        public string ExpiresAt { get; set; } = string.Empty;

        [JsonPropertyName("durationDays")]
        public int DurationDays { get; set; } = 30; // 99999 for Süresiz

        [JsonPropertyName("grantedBy")]
        public string GrantedBy { get; set; } = "Alphan";

        [JsonPropertyName("notes")]
        public string? Notes { get; set; }

        [JsonIgnore]
        public bool IsIndefinite => DurationDays >= 9999 || ExpiresAt.StartsWith("9999");

        [JsonIgnore]
        public bool IsExpired
        {
            get
            {
                if (!HasAccess) return true;
                if (IsIndefinite) return false;
                if (DateTime.TryParse(ExpiresAt, out var dt))
                {
                    return DateTime.Now > dt;
                }
                return false;
            }
        }

        [JsonIgnore]
        public int DaysRemaining
        {
            get
            {
                if (IsIndefinite) return 9999;
                if (DateTime.TryParse(ExpiresAt, out var dt))
                {
                    var rem = (dt - DateTime.Now).TotalDays;
                    return rem > 0 ? (int)Math.Ceiling(rem) : 0;
                }
                return 0;
            }
        }

        [JsonIgnore]
        public string StatusBadge
        {
            get
            {
                if (!HasAccess) return "⛔ Yetkisiz";
                if (IsIndefinite) return "✔ Süresiz";
                if (IsExpired) return "⏳ Süresi Doldu";
                return $"✔ {DaysRemaining}G Kaldı";
            }
        }

        [JsonIgnore]
        public string StatusColorHex
        {
            get
            {
                if (!HasAccess) return "#757575";
                if (IsExpired) return "#FF1744";
                return "#00E676";
            }
        }

        [JsonIgnore]
        public string StatusDescription
        {
            get
            {
                if (!HasAccess) return "Kullanım yetkisi verilmemiş.";
                if (IsIndefinite) return $"Süresiz aktif yetki ({GrantedBy})";
                if (IsExpired) return $"Süre doldu (Bitiş: {ExpiresAt})";
                return $"Aktif: {DaysRemaining} gün kaldı (Bitiş: {ExpiresAt})";
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? prop = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }

    public class AlpTogetherUserRecord : INotifyPropertyChanged
    {
        [JsonPropertyName("hwid")]
        public string Hwid { get; set; } = string.Empty;

        [JsonPropertyName("username")]
        public string Username { get; set; } = string.Empty;

        private string _adminAlias = string.Empty;
        [JsonPropertyName("adminAlias")]
        public string AdminAlias
        {
            get => _adminAlias;
            set
            {
                if (_adminAlias != value)
                {
                    _adminAlias = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(DisplayName));
                }
            }
        }

        [JsonPropertyName("machineName")]
        public string MachineName { get; set; } = string.Empty;

        [JsonPropertyName("pcUser")]
        public string PcUser { get; set; } = string.Empty;

        [JsonPropertyName("isAdmin")]
        public bool IsAdmin { get; set; }

        [JsonPropertyName("registeredAt")]
        public string RegisteredAt { get; set; } = string.Empty;

        private string _lastSeen = string.Empty;
        [JsonPropertyName("lastSeen")]
        public string LastSeen
        {
            get => _lastSeen;
            set
            {
                if (_lastSeen != value)
                {
                    _lastSeen = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(LastSeenDisplay));
                    OnPropertyChanged(nameof(SortTime));
                }
            }
        }

        [JsonPropertyName("alpnetHwid")]
        public string AlpNetHwid { get; set; } = string.Empty;

        [JsonPropertyName("clientVersion")]
        public string ClientVersion { get; set; } = "1.0.0";

        [JsonPropertyName("isAfk")]
        public bool IsAfk { get; set; }

        [JsonPropertyName("afkMinutes")]
        public int AfkMinutes { get; set; }

        [JsonPropertyName("currentActivity")]
        public string CurrentActivity { get; set; } = string.Empty;

        [JsonPropertyName("activityStartedAt")]
        public string ActivityStartedAt { get; set; } = string.Empty;

        [JsonPropertyName("warpActive")]
        public bool WarpActive { get; set; }

        [JsonPropertyName("warpStartedAt")]
        public string WarpStartedAt { get; set; } = string.Empty;

        [JsonPropertyName("warpStoppedAt")]
        public string WarpStoppedAt { get; set; } = string.Empty;

        [JsonPropertyName("warpTotalHoursToday")]
        public double WarpTotalHoursToday { get; set; }

        [JsonPropertyName("appPermissions")]
        public Dictionary<string, UserAppPermission> AppPermissions { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        [JsonIgnore]
        public string DisplayName
        {
            get
            {
                string bestName = !string.IsNullOrWhiteSpace(AdminAlias) ? AdminAlias : Username;
                if (bestName.StartsWith("AlpNet_", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(AdminAlias))
                    bestName = AdminAlias;

                if (!string.IsNullOrWhiteSpace(MachineName) && MachineName != "AlpNet-PC")
                {
                    if (!string.IsNullOrWhiteSpace(PcUser) && !bestName.Equals(PcUser, StringComparison.OrdinalIgnoreCase))
                        return $"{bestName} — {MachineName} ({PcUser})";
                    return $"{bestName} — {MachineName}";
                }

                if (!string.IsNullOrWhiteSpace(AdminAlias) && !string.IsNullOrWhiteSpace(Username) &&
                    !Username.Equals(AdminAlias, StringComparison.OrdinalIgnoreCase) &&
                    !Username.StartsWith("AlpNet_", StringComparison.OrdinalIgnoreCase))
                {
                    return $"{Username} ({AdminAlias})";
                }

                return bestName;
            }
        }

        [JsonIgnore]
        public string InitialLetter => string.IsNullOrWhiteSpace(Username) ? "?" : Username[..1].ToUpperInvariant();

        [JsonIgnore]
        public string RegisteredAtDisplay
        {
            get
            {
                if (DateTime.TryParse(RegisteredAt, out var dt))
                    return dt.ToString("dd.MM.yyyy HH:mm");
                return RegisteredAt;
            }
        }

        [JsonIgnore]
        public string LastSeenDisplay
        {
            get
            {
                if (DateTime.TryParse(LastSeen, out var dt))
                {
                    var span = DateTime.Now - dt;
                    if (span.TotalMinutes < 1) return "Şimdi aktif";
                    if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes} dakika önce ({dt:HH:mm})";
                    if (span.TotalHours < 24) return $"{(int)span.TotalHours} saat önce ({dt:HH:mm})";
                    return dt.ToString("dd.MM.yyyy HH:mm");
                }
                return LastSeen;
            }
        }

        [JsonIgnore]
        public DateTime SortTime
        {
            get
            {
                if (DateTime.TryParse(LastSeen, out var ls)) return ls;
                if (DateTime.TryParse(RegisteredAt, out var reg)) return reg;
                return DateTime.MinValue;
            }
        }

        [JsonIgnore]
        public string AlpLingoSummary => GetAppSummary("alplingo", "AlpLingo");

        [JsonIgnore]
        public string AlpNetSummary => GetAppSummary("alpnet-pulse", "AlpNet");

        [JsonIgnore]
        public string AlpFinansSummary => GetAppSummary("alpfinans", "AlpFinans");

        [JsonIgnore]
        public string AlphaGravitySummary => GetAppSummary("alphagravity", "AlphaGravity");

        [JsonIgnore]
        public string AlpRustSummary
        {
            get
            {
                if (AppPermissions.TryGetValue("alprust-plus", out var perm) && perm.HasAccess && !perm.IsExpired)
                    return $"AlpRust+: {perm.StatusBadge}";
                if (AppPermissions.TryGetValue("alprust", out var p2) && p2.HasAccess && !p2.IsExpired)
                    return $"AlpRust+: {p2.StatusBadge}";
                return "AlpRust+: ⛔ Yok";
            }
        }

        public string GetAppSummary(string appId, string shortName)
        {
            if (AppPermissions.TryGetValue(appId, out var perm) && perm.HasAccess && !perm.IsExpired)
            {
                return $"{shortName}: {perm.StatusBadge}";
            }
            return $"{shortName}: ⛔ Yok";
        }

        public bool HasActivePermission(string appId)
        {
            if (AppPermissions.TryGetValue(appId, out var perm))
            {
                return perm.HasAccess && !perm.IsExpired;
            }
            return false;
        }

        [JsonIgnore]
        public bool IsOnline
        {
            get
            {
                if (DateTime.TryParse(LastSeen, out var dt))
                {
                    var spanLocal = Math.Abs((DateTime.Now - dt).TotalMinutes);
                    var spanUtc = Math.Abs((DateTime.UtcNow - dt).TotalMinutes);
                    return Math.Min(spanLocal, spanUtc) <= 3.0;
                }
                return false;
            }
        }

        [JsonIgnore]
        public string PresenceStatusText
        {
            get
            {
                if (!IsOnline) return "Çevrimdışı";
                if (IsAfk) return $"Boşta / AFK ({AfkMinutes} dk)";
                return "Çevrimiçi";
            }
        }

        [JsonIgnore]
        public string PresenceDotColorHex
        {
            get
            {
                if (!IsOnline) return "#757575"; // Grey
                if (IsAfk) return "#FFB300"; // Yellow / Orange
                return "#00E676"; // Green
            }
        }

        [JsonIgnore]
        public string ActivityDisplayText
        {
            get
            {
                if (!IsOnline) return "💤 Çevrimdışı";
                if (!string.IsNullOrWhiteSpace(CurrentActivity))
                    return CurrentActivity;
                if (IsAfk) return $"💤 Boşta ({AfkMinutes} dk)";
                return "⚡ AlpTogether Açık (Masaüstünde)";
            }
        }

        [JsonIgnore]
        public string WarpStatusBadge
        {
            get
            {
                if (WarpActive) return "WARP: 🟢 AÇIK";
                return "WARP: ⚪ KAPALI";
            }
        }

        [JsonIgnore]
        public string WarpStatusBadgeColorHex => WarpActive ? "#00E5FF" : "#7E5676";

        [JsonIgnore]
        public string WarpDetailText
        {
            get
            {
                string usageStr = WarpTotalHoursToday > 0 ? $"Bugün: {WarpTotalHoursToday:0.1} sa" : "";
                if (WarpActive)
                {
                    string startStr = "";
                    if (DateTime.TryParse(WarpStartedAt, out var dt))
                        startStr = $"Açılış: {dt.ToLocalTime():HH:mm}";
                    return string.Join(" • ", new[] { startStr, usageStr }.Where(s => !string.IsNullOrEmpty(s)));
                }
                else
                {
                    string stopStr = "";
                    if (DateTime.TryParse(WarpStoppedAt, out var dt))
                        stopStr = $"Son Kapanış: {dt.ToLocalTime():HH:mm}";
                    return string.Join(" • ", new[] { stopStr, usageStr }.Where(s => !string.IsNullOrEmpty(s)));
                }
            }
        }

        [JsonIgnore]
        public string WarpFullDisplayStatus
        {
            get
            {
                string detail = WarpDetailText;
                if (WarpActive)
                {
                    return string.IsNullOrEmpty(detail) ? "WARP: 🟢 AÇIK" : $"WARP: 🟢 AÇIK ({detail})";
                }
                else
                {
                    return string.IsNullOrEmpty(detail) ? "WARP: ⚪ KAPALI" : $"WARP: ⚪ KAPALI ({detail})";
                }
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? prop = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }

    public class AppPermissionOption
    {
        public string AppId { get; set; } = string.Empty;
        public string AppName { get; set; } = string.Empty;
        public string IconText { get; set; } = "✦";
    }

    public class DurationOption
    {
        public string Title { get; set; } = string.Empty;
        public int Days { get; set; }
    }

    public class SupportSession
    {
        [JsonPropertyName("sessionId")]
        public string SessionId { get; set; } = string.Empty;

        [JsonPropertyName("adminHwid")]
        public string AdminHwid { get; set; } = string.Empty;

        [JsonPropertyName("adminName")]
        public string AdminName { get; set; } = "Alphan (Admin)";

        [JsonPropertyName("targetHwid")]
        public string TargetHwid { get; set; } = string.Empty;

        [JsonPropertyName("targetUsername")]
        public string TargetUsername { get; set; } = string.Empty;

        [JsonPropertyName("status")]
        public string Status { get; set; } = "requested"; // requested, accepted, rejected, active, terminated

        [JsonPropertyName("requestedAt")]
        public string RequestedAt { get; set; } = string.Empty;

        [JsonPropertyName("startedAt")]
        public string StartedAt { get; set; } = string.Empty;

        [JsonPropertyName("endedAt")]
        public string EndedAt { get; set; } = string.Empty;

        [JsonPropertyName("directEndpoint")]
        public string DirectEndpoint { get; set; } = string.Empty;

        [JsonPropertyName("relayFrameKey")]
        public string RelayFrameKey { get; set; } = string.Empty;
    }

    public class SecurePermissionCachePayload
    {
        [JsonPropertyName("hwid")]
        public string Hwid { get; set; } = string.Empty;

        [JsonPropertyName("username")]
        public string Username { get; set; } = string.Empty;

        [JsonPropertyName("lastSyncUtc")]
        public DateTime LastSyncUtc { get; set; }

        [JsonPropertyName("permissions")]
        public Dictionary<string, UserAppPermission> Permissions { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        [JsonPropertyName("isAdmin")]
        public bool IsAdmin { get; set; }

        [JsonIgnore]
        public TimeSpan Age => DateTime.UtcNow - LastSyncUtc;
    }

    public class UserAppWhitelistItem : INotifyPropertyChanged
    {
        public string AppId { get; set; } = string.Empty;
        public string AppName { get; set; } = string.Empty;
        public string Subtitle { get; set; } = string.Empty;
        public string IconPackUri { get; set; } = "pack://application:,,,/Assets/alptogether_logo_pink.png";
        public string AccentColorHex { get; set; } = "#FF2E93";

        private bool _hasAccess;
        public bool HasAccess
        {
            get => _hasAccess;
            set { if (_hasAccess != value) { _hasAccess = value; OnPropertyChanged(); } }
        }

        private string _statusBadge = "⛔ Yetkisiz";
        public string StatusBadge
        {
            get => _statusBadge;
            set { if (_statusBadge != value) { _statusBadge = value; OnPropertyChanged(); } }
        }

        private string _statusDescription = "Kullanım yetkisi verilmemiş.";
        public string StatusDescription
        {
            get => _statusDescription;
            set { if (_statusDescription != value) { _statusDescription = value; OnPropertyChanged(); } }
        }

        private string _statusColorHex = "#757575";
        public string StatusColorHex
        {
            get => _statusColorHex;
            set { if (_statusColorHex != value) { _statusColorHex = value; OnPropertyChanged(); } }
        }

        private int _selectedDurationIndex = 2; // 30 Gün (1 Ay)
        public int SelectedDurationIndex
        {
            get => _selectedDurationIndex;
            set { if (_selectedDurationIndex != value) { _selectedDurationIndex = value; OnPropertyChanged(); } }
        }

        public int GetDurationDays()
        {
            return SelectedDurationIndex switch
            {
                0 => 1,
                1 => 7,
                2 => 30,
                3 => 90,
                4 => 365,
                5 => 99999,
                _ => 30
            };
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? prop = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }
}
