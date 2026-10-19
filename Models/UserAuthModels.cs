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
}
