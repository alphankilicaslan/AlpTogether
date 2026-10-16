using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AlpTogether.Models
{
    public enum HybridInstallMode
    {
        HybridAuto = 0,          // Birleşik Hibrit: Otomatik/Sessiz kurulum + Doğrudan dosya doğrulama
        StandaloneInstaller = 1, // Ayrı Installer: Uygulamanın kendi kurulum arayüzünü (Setup.exe) açar
        DirectFiles = 2          // Doğrudan Dosya: Kurulum aracını atlayıp doğrudan .exe/.zip senkronize eder
    }

    public enum AppPackageType
    {
        HybridSetupAndFiles = 0, // Hem kendi Setup.exe aracı var hem doğrudan .exe/ek dosyaları var (Örn: AlpNet Pulse)
        SingleExecutable = 1,    // Doğrudan tek/çoklu çalıştırılabilir dosya
        ZipArchive = 2,          // taşınabilir/paket .zip arşivi
        ExternalInstallerOnly = 3 // Sadece harici kurulum paketi (.exe / .msi)
    }

    public class CompanionFileItem
    {
        public string FileName { get; set; } = string.Empty;
        public string DownloadUrl { get; set; } = string.Empty;
        public string RelativeSubDir { get; set; } = string.Empty;
        public string ExpectedSha256 { get; set; } = string.Empty;
        public long ApproximateSizeBytes { get; set; }
    }

    public class PatchNoteEntry
    {
        public string AppName { get; set; } = string.Empty;
        public string AppId { get; set; } = string.Empty;
        public string Version { get; set; } = "1.0.0";
        public string ReleaseDate { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Tag { get; set; } = "GÜNCELLEME";
        public List<string> Changes { get; set; } = new();
    }

    public class AlpAppItem : INotifyPropertyChanged
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Subtitle { get; set; } = string.Empty;
        public string Category { get; set; } = "Sistem & Araçlar";
        public string Developer { get; set; } = "Alphan Kılıçaslan";
        public string Description { get; set; } = string.Empty;
        public string HighlightText { get; set; } = string.Empty;
        public string AccentColorHex { get; set; } = "#FF2E93";
        public string IconPackUri { get; set; } = "pack://application:,,,/Assets/alptogether_logo_pink.png";

        private string _latestVersion = "1.0.0";
        public string LatestVersion
        {
            get => _latestVersion;
            set { _latestVersion = value; OnPropertyChanged(); OnPropertyChanged(nameof(VersionDisplay)); }
        }

        public string ReleaseDate { get; set; } = "Ekim 2026";
        public string DownloadSizeText { get; set; } = "45 MB";
        public long SizeBytes { get; set; } = 45 * 1024 * 1024;

        public AppPackageType PackageType { get; set; } = AppPackageType.HybridSetupAndFiles;
        public HybridInstallMode PreferredHybridMode { get; set; } = HybridInstallMode.HybridAuto;

        public string DirectDownloadUrl { get; set; } = string.Empty;
        public string InstallerDownloadUrl { get; set; } = string.Empty;
        public string InstallerSilentArgs { get; set; } = "/auto";
        public string FirebaseStateUrl { get; set; } = string.Empty;
        public string GitHubRepoUrl { get; set; } = "https://github.com/alphankilicaslan/AlpTogether";

        public string ExecutableName { get; set; } = string.Empty;
        public string InstallerFileName { get; set; } = string.Empty;
        public string DefaultInstallSubFolder { get; set; } = string.Empty;
        public List<string> CandidateExistingPaths { get; set; } = new();
        public List<string> ProcessNamesToKill { get; set; } = new();
        public List<CompanionFileItem> CompanionFiles { get; set; } = new();
        public bool RequiresDotNet9 { get; set; } = true;
        public bool HasCustomInstaller => !string.IsNullOrWhiteSpace(InstallerDownloadUrl) || !string.IsNullOrWhiteSpace(InstallerFileName);
        public string HybridBadgeText => HasCustomInstaller ? "HİBRİT KURULUM (Setup + Doğrudan)" : "DOĞRUDAN PAKET";

        public List<PatchNoteEntry> PatchNotes { get; set; } = new();

        // Runtime Reactive Properties
        private bool _isInstalled;
        public bool IsInstalled
        {
            get => _isInstalled;
            set
            {
                if (_isInstalled != value)
                {
                    _isInstalled = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(PrimaryButtonText));
                    OnPropertyChanged(nameof(StateBadgeText));
                    OnPropertyChanged(nameof(VersionDisplay));
                }
            }
        }

        private string _installedVersion = "-";
        public string InstalledVersion
        {
            get => _installedVersion;
            set
            {
                if (_installedVersion != value)
                {
                    _installedVersion = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(VersionDisplay));
                }
            }
        }

        private string _installPath = string.Empty;
        public string InstallPath
        {
            get => _installPath;
            set { _installPath = value; OnPropertyChanged(); }
        }

        private bool _updateAvailable;
        public bool UpdateAvailable
        {
            get => _updateAvailable;
            set
            {
                if (_updateAvailable != value)
                {
                    _updateAvailable = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(PrimaryButtonText));
                    OnPropertyChanged(nameof(StateBadgeText));
                }
            }
        }

        private bool _isRunning;
        public bool IsRunning
        {
            get => _isRunning;
            set
            {
                if (_isRunning != value)
                {
                    _isRunning = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(PrimaryButtonText));
                    OnPropertyChanged(nameof(StateBadgeText));
                }
            }
        }

        private bool _isBusy;
        public bool IsBusy
        {
            get => _isBusy;
            set
            {
                if (_isBusy != value)
                {
                    _isBusy = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(PrimaryButtonText));
                    OnPropertyChanged(nameof(StateBadgeText));
                }
            }
        }

        private double _progressPercent;
        public double ProgressPercent
        {
            get => _progressPercent;
            set { _progressPercent = value; OnPropertyChanged(); }
        }

        private string _statusText = "Hazır";
        public string StatusText
        {
            get => _statusText;
            set { _statusText = value; OnPropertyChanged(); }
        }

        private string _speedText = string.Empty;
        public string SpeedText
        {
            get => _speedText;
            set { _speedText = value; OnPropertyChanged(); }
        }

        private string _healthStatus = "Doğrulanmadı";
        public string HealthStatus
        {
            get => _healthStatus;
            set { _healthStatus = value; OnPropertyChanged(); }
        }

        private HybridInstallMode _selectedHybridMode = HybridInstallMode.HybridAuto;
        public HybridInstallMode SelectedHybridMode
        {
            get => _selectedHybridMode;
            set
            {
                if (_selectedHybridMode != value)
                {
                    _selectedHybridMode = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(SelectedHybridModeDescription));
                }
            }
        }

        public string SelectedHybridModeDescription => SelectedHybridMode switch
        {
            HybridInstallMode.HybridAuto => "Birleşik Hibrit Mod: Setup ve uygulama dosyalarını AlpTogether içinde otomatik kurar ve doğrular.",
            HybridInstallMode.StandaloneInstaller => "Ayrı Kurulum Arayüzü: Uygulamanın kendi Setup.exe kurulum penceresini bağımsız olarak açar.",
            HybridInstallMode.DirectFiles => "Doğrudan Dosya Modu: Kurulum sihirbazını atlayarak doğrudan yürütülebilir dosyaları klasöre senkronize eder.",
            _ => ""
        };

        public string PrimaryButtonText
        {
            get
            {
                if (IsBusy) return $"İŞLENİYOR... %{ProgressPercent:0}";
                if (IsRunning) return "ÇALIŞIYOR (DURDUR)";
                if (!IsInstalled) return "HEMEN YÜKLE";
                if (UpdateAvailable) return $"GÜNCELLE (v{LatestVersion})";
                return "BAŞLAT";
            }
        }

        public string StateBadgeText
        {
            get
            {
                if (IsBusy) return "İNDİRİLİYOR / ONARILIYOR";
                if (IsRunning) return "AKTİF ÇALIŞIYOR";
                if (UpdateAvailable) return "GÜNCELLEME MEVCUT";
                if (IsInstalled) return "KURULU & HAZIR";
                return "BULUTTA MEVCUT";
            }
        }

        public string VersionDisplay => IsInstalled
            ? $"Kurulu: v{InstalledVersion}  •  Güncel: v{LatestVersion}"
            : $"Son Sürüm: v{LatestVersion}  •  {DownloadSizeText}";

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public class RemoteCatalogPayload
    {
        public string CatalogVersion { get; set; } = "2026.10.16";
        public string ClientLatestVersion { get; set; } = "1.0.0";
        public string ClientDownloadUrl { get; set; } = "https://github.com/alphankilicaslan/AlpTogether/releases/latest/download/AlpTogether.Setup.exe";
        public string GlobalAnnouncement { get; set; } = "AlpTogether v1.0 Yayında! Tüm Alp uygulamalarını tek merkezden indirin, hibrit güncelleyin ve tek tıkla onarın.";
        public List<AlpAppItem> Apps { get; set; } = new();
    }

    public class AutoErrorReport
    {
        public string ReportId { get; set; } = Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
        public string Timestamp { get; set; } = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        public string DeviceId { get; set; } = string.Empty;
        public string MachineName { get; set; } = Environment.MachineName;
        public string OsVersion { get; set; } = Environment.OSVersion.ToString();
        public string AppId { get; set; } = "AlpTogether";
        public string AppName { get; set; } = "AlpTogether Client";
        public string AppVersion { get; set; } = "1.0.0";
        public string ErrorSource { get; set; } = "Runtime"; // Runtime, Install, Update, Repair, ChildProcessCrash
        public string ErrorMessage { get; set; } = string.Empty;
        public string StackTrace { get; set; } = string.Empty;
        public string RecentLogs { get; set; } = string.Empty;
    }

    public class UserFeedbackTicket
    {
        public string TicketId { get; set; } = "TKT-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        public string Timestamp { get; set; } = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        public string DeviceId { get; set; } = string.Empty;
        public string MachineName { get; set; } = Environment.MachineName;
        public string TargetAppId { get; set; } = string.Empty;
        public string TargetAppName { get; set; } = string.Empty;
        public string TargetAppVersion { get; set; } = string.Empty;
        public string IssueCategory { get; set; } = "Bozuk Özellik / Hata";
        public string Severity { get; set; } = "Yüksek";
        public string UserContact { get; set; } = string.Empty;
        public string Subject { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string AttachedSystemLogs { get; set; } = string.Empty;
        public string Status { get; set; } = "Açık (Yeni)";
    }

    public class LogEntryItem
    {
        public string TimeText { get; set; } = DateTime.Now.ToString("HH:mm:ss");
        public string Category { get; set; } = "SİSTEM";
        public string Level { get; set; } = "INFO"; // INFO, SUCCESS, WARN, ERROR
        public string AppName { get; set; } = "AlpTogether";
        public string Message { get; set; } = string.Empty;
        public string Details { get; set; } = string.Empty;

        public string BadgeColorHex => Level switch
        {
            "SUCCESS" => "#00E676",
            "WARN" => "#FFAB00",
            "ERROR" => "#FF1744",
            _ => "#FF2E93"
        };
    }

    public class CustomWorkspaceSlotItem
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N")[..6];
        public string Title { get; set; } = string.Empty;
        public string Tag { get; set; } = "NOT / MODÜL";
        public string Content { get; set; } = string.Empty;
        public string ActionCommand { get; set; } = string.Empty;
        public string CreatedAt { get; set; } = DateTime.Now.ToString("dd.MM.yyyy HH:mm");
    }

    public class ClientSettings
    {
        public string DefaultInstallRoot { get; set; } = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AlpTogether", "Apps");

        public Dictionary<string, string> CustomAppPaths { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> InstalledAppVersions { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, HybridInstallMode> AppHybridModes { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        public string GitHubCatalogUrl { get; set; } = "https://raw.githubusercontent.com/alphankilicaslan/AlpTogether/main/catalog.json";
        public string FirebaseBaseUrl { get; set; } = "https://alpnet-d0bc4-default-rtdb.europe-west1.firebasedatabase.app";
        public string FirebaseAuthSecret { get; set; } = "ExNWZwF2CkByKFILhfQ2BbTpuxYFlFAUO8quWwfc";
        public string CustomWebhookUrl { get; set; } = string.Empty; // Discord / Slack / Custom Webhook
        public string SupportWhatsAppNumber { get; set; } = "905059092062";
        public string GitHubProfileUrl { get; set; } = "https://github.com/alphankilicaslan";

        public bool AutoSendCrashTelemetry { get; set; } = true;
        public bool MonitorLaunchedAppsForCrashes { get; set; } = true;
        public bool CreateDesktopShortcutOnInstall { get; set; } = true;
        public bool EnableAmbientPinkAnimations { get; set; } = true;

        // "Sonradan değiştirilebilir boşluk / log gibi bir yer" özelleştirme verileri
        public string CustomSlotHeader { get; set; } = "Özel Geliştirici & Sistem Boşluğu (Dinamik Alan)";
        public string CustomSlotScratchpad { get; set; } =
            "• Bu alan sonradan eklenecek modüller, hızlı komutlar, canlı duyurular veya özel sistem logları için ayrılmış esnek boşluktur.\n" +
            "• Buraya özel notlar, test parametreleri veya hızlı başlatma komutları ekleyebilirsiniz.";
        public List<CustomWorkspaceSlotItem> CustomSlotItems { get; set; } = new();
    }
}
