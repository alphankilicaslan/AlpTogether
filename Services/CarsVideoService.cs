using System;
using System.IO;
using AlpTogether.Models;

namespace AlpTogether.Services
{
    public enum CarsVideoSourceType
    {
        LocalDisk,
        GitHubRelease,
        CustomStream
    }

    public class CarsVideoSourceInfo
    {
        public CarsVideoSourceType SourceType { get; set; }
        public Uri SourceUri { get; set; } = null!;
        public string? LocalFilePath { get; set; }
        public string DisplayTitle { get; set; } = string.Empty;
        public string BadgeText { get; set; } = string.Empty;
        public string FileSizeFormatted { get; set; } = "1.18 GB";
        public bool IsLocal => SourceType == CarsVideoSourceType.LocalDisk;
    }

    public class CarsVideoService
    {
        public static CarsVideoService Instance { get; } = new();

        public const string CanonicalLocalPath = @"C:\Users\Alphan.MonsterAlphan\Downloads\Cars 2006 UBluray 1080p DUAL.mp4";
        public const string CanonicalLocalPathDotted = @"C:\Users\Alphan.MonsterAlphan\Downloads\Cars.2006.UBluray.1080p.DUAL.mp4";
        public const string GitHubReleaseAssetUrl = "https://github.com/alphankilicaslan/AlpTogether/releases/download/v1.1.0/Cars.2006.UBluray.1080p.DUAL.mp4";
        public const string FallbackStreamUrl = "https://raw.githubusercontent.com/alphankilicaslan/AlpTogether/main/Assets/CarsTrailerPreview.mp4";

        private CarsVideoService() { }

        /// <summary>
        /// Hibrit kaynak çözümleyici:
        /// 1. Kullanıcı tarafından seçilen özel yerel dosya varsa onu kullanır.
        /// 2. Canonical indirme yolunda (C:\Users\Alphan.MonsterAlphan\Downloads\...) varsa diskten oynatır.
        /// 3. Kullanıcının genel Downloads klasöründe veya uygulama dizininde varsa onu kullanır.
        /// 4. Yerel dosya bulunamazsa GitHub Release v1.1.0 CDN akış URL'sine sorunsuz fallback yapar.
        /// </summary>
        public CarsVideoSourceInfo ResolveSource(string? overridePathOrUrl = null, bool forceCloud = false)
        {
            if (!forceCloud)
            {
                // 1. Override edilmiş path kontrolü
                if (!string.IsNullOrWhiteSpace(overridePathOrUrl) && File.Exists(overridePathOrUrl))
                {
                    return CreateLocalSource(overridePathOrUrl, "Özel Seçilen Yerel Dosya");
                }

                // 2. Settings içinde kayıtlı özel yol kontrolü
                try
                {
                    string savedCustom = SettingsService.Instance.Current.CarsVideoCustomPath;
                    if (!string.IsNullOrWhiteSpace(savedCustom) && File.Exists(savedCustom))
                    {
                        return CreateLocalSource(savedCustom, "Kayıtlı Yerel Dosya");
                    }
                }
                catch { }

                // 3. Birincil kanonik yol kontrolü (C:\Users\Alphan.MonsterAlphan\Downloads\...)
                if (File.Exists(CanonicalLocalPath))
                {
                    return CreateLocalSource(CanonicalLocalPath, "Yerel Disk (NVMe Hızlı Oynatma)");
                }
                if (File.Exists(CanonicalLocalPathDotted))
                {
                    return CreateLocalSource(CanonicalLocalPathDotted, "Yerel Disk (NVMe Hızlı Oynatma)");
                }

                // 4. Dinamik profil Downloads klasörü kontrolü
                string dynamicUserDownloads = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Downloads",
                    "Cars 2006 UBluray 1080p DUAL.mp4");
                string dynamicUserDownloadsDotted = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Downloads",
                    "Cars.2006.UBluray.1080p.DUAL.mp4");

                if (File.Exists(dynamicUserDownloads))
                {
                    return CreateLocalSource(dynamicUserDownloads, "Kullanıcı İndirilenler (Disk)");
                }
                if (File.Exists(dynamicUserDownloadsDotted))
                {
                    return CreateLocalSource(dynamicUserDownloadsDotted, "Kullanıcı İndirilenler (Disk)");
                }

                // 5. Uygulama çalışma dizini kontrolü
                string baseDirCandidate = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Cars 2006 UBluray 1080p DUAL.mp4");
                string baseDirCandidateDotted = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Cars.2006.UBluray.1080p.DUAL.mp4");
                if (File.Exists(baseDirCandidate))
                {
                    return CreateLocalSource(baseDirCandidate, "Uygulama Dizini (Disk)");
                }
                if (File.Exists(baseDirCandidateDotted))
                {
                    return CreateLocalSource(baseDirCandidateDotted, "Uygulama Dizini (Disk)");
                }
            }

            // 6. Bulut Fallback (GitHub Release CDN)
            string cloudUrl = !string.IsNullOrWhiteSpace(overridePathOrUrl) && Uri.IsWellFormedUriString(overridePathOrUrl, UriKind.Absolute)
                ? overridePathOrUrl
                : GitHubReleaseAssetUrl;

            return new CarsVideoSourceInfo
            {
                SourceType = CarsVideoSourceType.GitHubRelease,
                SourceUri = new Uri(cloudUrl, UriKind.Absolute),
                LocalFilePath = null,
                DisplayTitle = "Bulut Akışı (GitHub Release v1.1.0)",
                BadgeText = "☁️ BULUT AKIŞI (CDN)",
                FileSizeFormatted = "1.18 GB (Akış)"
            };
        }

        private static CarsVideoSourceInfo CreateLocalSource(string filePath, string label)
        {
            string sizeStr = "1.18 GB";
            try
            {
                var fi = new FileInfo(filePath);
                if (fi.Exists)
                {
                    double mb = fi.Length / (1024.0 * 1024.0);
                    if (mb >= 1000)
                        sizeStr = $"{(mb / 1024.0).ToString("F2", System.Globalization.CultureInfo.InvariantCulture)} GB";
                    else
                        sizeStr = $"{mb.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)} MB";
                }
            }
            catch { }

            return new CarsVideoSourceInfo
            {
                SourceType = CarsVideoSourceType.LocalDisk,
                SourceUri = new Uri(filePath, UriKind.Absolute),
                LocalFilePath = filePath,
                DisplayTitle = $"{label} — {Path.GetFileName(filePath)}",
                BadgeText = "⚡ YEREL DİSK (1080p)",
                FileSizeFormatted = sizeStr
            };
        }
    }
}
