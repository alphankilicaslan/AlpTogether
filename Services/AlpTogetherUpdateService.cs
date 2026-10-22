using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using AlpTogether.Models;

namespace AlpTogether.Services
{
    public class UpdateCheckResult
    {
        public bool IsUpdateAvailable { get; set; }
        public string CurrentVersion { get; set; } = string.Empty;
        public string LatestVersion { get; set; } = string.Empty;
        public string ReleaseTitle { get; set; } = string.Empty;
        public string ReleaseNotes { get; set; } = string.Empty;
        public string DownloadUrl { get; set; } = string.Empty;
        public string SetupDownloadUrl { get; set; } = string.Empty;
        public long FileSizeBytes { get; set; }
        public string PublishedAt { get; set; } = string.Empty;
        public string ErrorMessage { get; set; } = string.Empty;
        public bool Success => string.IsNullOrEmpty(ErrorMessage);
    }

    /// <summary>
    /// AlpTogether İstemcisi Kendi Kendini Güncelleme (Self-Update) Motoru.
    /// GitHub Releases API ve Bulut Kataloğu üzerinden yeni sürümleri denetler,
    /// AlpTogether.exe dosyasını arka planda indirir, PE bütünlüğünü doğrular
    /// ve kesintisiz (in-place) yeniden başlatma ile kendini günceller.
    /// </summary>
    public class AlpTogetherUpdateService
    {
        public static AlpTogetherUpdateService Instance { get; } = new();

        public const string GitHubRepoOwner = "alphankilicaslan";
        public const string GitHubRepoName = "AlpTogether";
        public const string GitHubReleasesApiUrl = "https://api.github.com/repos/alphankilicaslan/AlpTogether/releases/latest";
        public const string DefaultClientDownloadUrl = "https://github.com/alphankilicaslan/AlpTogether/releases/latest/download/AlpTogether.exe";
        public const string DefaultSetupDownloadUrl = "https://github.com/alphankilicaslan/AlpTogether/releases/latest/download/AlpTogether.Setup.exe";

        private readonly HttpClient _httpClient;

        public static string CurrentVersion
        {
            get
            {
                try
                {
                    var ver = Assembly.GetExecutingAssembly().GetName().Version;
                    if (ver != null)
                        return $"{ver.Major}.{ver.Minor}.{ver.Build}";
                }
                catch { }
                return "1.1.2";
            }
        }

        public string LatestVersion { get; private set; } = CurrentVersion;
        public string ReleaseTitle { get; private set; } = string.Empty;
        public string ReleaseNotes { get; private set; } = string.Empty;
        public string PublishedAt { get; private set; } = string.Empty;
        public string DownloadUrl { get; private set; } = DefaultClientDownloadUrl;
        public string SetupDownloadUrl { get; private set; } = DefaultSetupDownloadUrl;
        public long FileSizeBytes { get; private set; } = 0;
        public bool IsUpdateAvailable { get; private set; } = false;
        public bool IsChecking { get; private set; } = false;
        public bool IsDownloading { get; private set; } = false;
        public double DownloadProgress { get; private set; } = 0;
        public string StatusMessage { get; private set; } = "Hazır";
        public DateTime? LastCheckTime { get; private set; } = null;

        public event Action<UpdateCheckResult>? OnUpdateCheckCompleted;
        public event Action<double, string>? OnDownloadProgressChanged;

        private AlpTogetherUpdateService()
        {
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(15)
            };
            _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("AlpTogether-Client", CurrentVersion));
            _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.v3+json"));
        }

        /// <summary>
        /// GitHub Releases API ve/veya Bulut Kataloğu üzerinden güncellemeleri kontrol eder.
        /// </summary>
        public async Task<UpdateCheckResult> CheckForUpdatesAsync(bool silent = false)
        {
            if (IsChecking)
            {
                return new UpdateCheckResult
                {
                    CurrentVersion = CurrentVersion,
                    LatestVersion = LatestVersion,
                    IsUpdateAvailable = IsUpdateAvailable,
                    DownloadUrl = DownloadUrl,
                    ReleaseNotes = ReleaseNotes
                };
            }

            IsChecking = true;
            StatusMessage = "Güncellemeler denetleniyor...";
            if (!silent)
                AppLoggerService.Instance.Info("GÜNCELLEME", $"AlpTogether istemci güncellemesi GitHub üzerinden denetleniyor (Mevcut: v{CurrentVersion})...", "AlpTogether");

            var result = new UpdateCheckResult
            {
                CurrentVersion = CurrentVersion,
                LatestVersion = CurrentVersion,
                DownloadUrl = DefaultClientDownloadUrl,
                SetupDownloadUrl = DefaultSetupDownloadUrl
            };

            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                using var request = new HttpRequestMessage(HttpMethod.Get, GitHubReleasesApiUrl);
                request.Headers.UserAgent.ParseAdd($"AlpTogether-Client/{CurrentVersion}");
                request.Headers.Accept.ParseAdd("application/vnd.github.v3+json");

                using var response = await _httpClient.SendAsync(request, cts.Token);
                if (response.IsSuccessStatusCode)
                {
                    string json = await response.Content.ReadAsStringAsync(cts.Token);
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    string tag = root.TryGetProperty("tag_name", out var tagProp) ? tagProp.GetString() ?? "" : "";
                    string name = root.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? "" : "";
                    string body = root.TryGetProperty("body", out var bodyProp) ? bodyProp.GetString() ?? "" : "";
                    string pubAt = root.TryGetProperty("published_at", out var pubProp) ? pubProp.GetString() ?? "" : "";

                    string normLatest = NormalizeVersion(tag);
                    result.LatestVersion = normLatest;
                    result.ReleaseTitle = string.IsNullOrWhiteSpace(name) ? $"AlpTogether v{normLatest}" : name;
                    result.ReleaseNotes = body;
                    result.PublishedAt = pubAt;

                    // Parse release assets
                    if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var asset in assets.EnumerateArray())
                        {
                            string assetName = asset.TryGetProperty("name", out var anProp) ? anProp.GetString() ?? "" : "";
                            string assetUrl = asset.TryGetProperty("browser_download_url", out var dlProp) ? dlProp.GetString() ?? "" : "";
                            long assetSize = asset.TryGetProperty("size", out var szProp) ? szProp.GetInt64() : 0;

                            if (assetName.Equals("AlpTogether.exe", StringComparison.OrdinalIgnoreCase))
                            {
                                result.DownloadUrl = assetUrl;
                                result.FileSizeBytes = assetSize;
                            }
                            else if (assetName.Equals("AlpTogether.Setup.exe", StringComparison.OrdinalIgnoreCase))
                            {
                                result.SetupDownloadUrl = assetUrl;
                            }
                        }
                    }

                    result.IsUpdateAvailable = IsNewerVersion(result.LatestVersion, CurrentVersion);
                }
                else
                {
                    // Fallback to catalog.json / settings fallback
                    CheckFromCatalogFallback(result);
                }
            }
            catch (Exception ex)
            {
                // Fallback to catalog.json / cached catalog
                CheckFromCatalogFallback(result);
                if (!result.IsUpdateAvailable)
                {
                    result.ErrorMessage = ex.Message;
                }
            }
            finally
            {
                IsChecking = false;
                LastCheckTime = DateTime.Now;

                LatestVersion = result.LatestVersion;
                ReleaseTitle = result.ReleaseTitle;
                ReleaseNotes = result.ReleaseNotes;
                DownloadUrl = string.IsNullOrWhiteSpace(result.DownloadUrl) ? DefaultClientDownloadUrl : result.DownloadUrl;
                SetupDownloadUrl = string.IsNullOrWhiteSpace(result.SetupDownloadUrl) ? DefaultSetupDownloadUrl : result.SetupDownloadUrl;
                FileSizeBytes = result.FileSizeBytes;
                IsUpdateAvailable = result.IsUpdateAvailable;

                if (IsUpdateAvailable)
                {
                    StatusMessage = $"Yeni sürüm mevcut: v{LatestVersion}";
                    AppLoggerService.Instance.Success("GÜNCELLEME", $"Yeni AlpTogether sürümü bulundu: v{LatestVersion}! (Mevcut: v{CurrentVersion})", "AlpTogether");
                }
                else
                {
                    StatusMessage = $"AlpTogether güncel (v{CurrentVersion})";
                    if (!silent)
                        AppLoggerService.Instance.Info("GÜNCELLEME", $"AlpTogether en güncel sürümde (v{CurrentVersion}).", "AlpTogether");
                }

                Application.Current?.Dispatcher?.BeginInvoke(new Action(() =>
                {
                    OnUpdateCheckCompleted?.Invoke(result);
                }));
            }

            return result;
        }

        private void CheckFromCatalogFallback(UpdateCheckResult result)
        {
            try
            {
                // Fallback: check CatalogAndHybridInstallerService if it has ClientLatestVersion
                // or read local/remote catalog.json
                string catalogPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "catalog.json");
                if (File.Exists(catalogPath))
                {
                    string json = File.ReadAllText(catalogPath);
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("ClientLatestVersion", out var verProp))
                    {
                        string cVer = verProp.GetString() ?? "";
                        if (!string.IsNullOrWhiteSpace(cVer))
                        {
                            result.LatestVersion = NormalizeVersion(cVer);
                            result.IsUpdateAvailable = IsNewerVersion(result.LatestVersion, CurrentVersion);
                        }
                    }
                    if (doc.RootElement.TryGetProperty("ClientDownloadUrl", out var dlProp))
                    {
                        string dl = dlProp.GetString() ?? "";
                        if (!string.IsNullOrWhiteSpace(dl)) result.DownloadUrl = dl;
                    }
                }
            }
            catch { }
        }

        /// <summary>
        /// Katalog senkronizasyonundan gelen ClientLatestVersion bilgisini sisteme işler.
        /// </summary>
        public void NotifyCatalogVersion(string remoteVersion, string? downloadUrl)
        {
            if (string.IsNullOrWhiteSpace(remoteVersion)) return;

            string norm = NormalizeVersion(remoteVersion);
            if (IsNewerVersion(norm, CurrentVersion))
            {
                LatestVersion = norm;
                IsUpdateAvailable = true;
                if (!string.IsNullOrWhiteSpace(downloadUrl))
                    DownloadUrl = downloadUrl;

                StatusMessage = $"Yeni sürüm mevcut: v{LatestVersion}";
                AppLoggerService.Instance.Success("GÜNCELLEME", $"Bulut kataloğu üzerinden yeni AlpTogether sürümü bildirildi: v{LatestVersion}!", "AlpTogether");

                var res = new UpdateCheckResult
                {
                    CurrentVersion = CurrentVersion,
                    LatestVersion = LatestVersion,
                    IsUpdateAvailable = true,
                    DownloadUrl = DownloadUrl,
                    ReleaseTitle = $"AlpTogether v{LatestVersion}",
                    ReleaseNotes = "Bulut kataloğu üzerinden en güncel AlpTogether kararlı sürümü yayınlandı."
                };

                Application.Current?.Dispatcher?.BeginInvoke(new Action(() =>
                {
                    OnUpdateCheckCompleted?.Invoke(res);
                }));
            }
        }

        /// <summary>
        /// Yeni AlpTogether.exe dosyasını indirir, PE doğrulaması yapar ve ilerleme bildirir.
        /// </summary>
        public async Task<string> DownloadUpdateAsync(IProgress<double>? progress = null, CancellationToken ct = default)
        {
            IsDownloading = true;
            DownloadProgress = 0;
            StatusMessage = "Güncelleme paketi indiriliyor...";

            string updateDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AlpTogether", "Update");

            if (!Directory.Exists(updateDir))
                Directory.CreateDirectory(updateDir);

            string targetFile = Path.Combine(updateDir, "AlpTogether.new.exe");
            if (File.Exists(targetFile))
            {
                try { File.Delete(targetFile); } catch { }
            }

            string downloadUrl = string.IsNullOrWhiteSpace(DownloadUrl) ? DefaultClientDownloadUrl : DownloadUrl;

            try
            {
                using var response = await _httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();

                long totalBytes = response.Content.Headers.ContentLength ?? (FileSizeBytes > 0 ? FileSizeBytes : -1L);
                using var stream = await response.Content.ReadAsStreamAsync(ct);
                using var fs = new FileStream(targetFile, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

                byte[] buffer = new byte[81920];
                long totalRead = 0;
                int read;

                while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
                {
                    await fs.WriteAsync(buffer, 0, read, ct);
                    totalRead += read;

                    if (totalBytes > 0)
                    {
                        double pct = Math.Min(100.0, (double)totalRead / totalBytes * 100.0);
                        DownloadProgress = pct;
                        progress?.Report(pct);
                        OnDownloadProgressChanged?.Invoke(pct, $"İndiriliyor: %{pct:0.0} ({(totalRead / 1048576.0):0.0} MB / {(totalBytes / 1048576.0):0.0} MB)");
                    }
                    else
                    {
                        OnDownloadProgressChanged?.Invoke(50, $"İndiriliyor: {(totalRead / 1048576.0):0.0} MB...");
                    }
                }

                await fs.FlushAsync(ct);
                fs.Close();

                // PE Doğrulama (Windows Portable Executable format kontrolü)
                var fi = new FileInfo(targetFile);
                if (!fi.Exists || fi.Length < 500_000)
                {
                    throw new InvalidOperationException($"İndirilen güncelleme dosyası eksik veya geçersiz boyutta ({fi.Length} bayt).");
                }

                using (var checkFs = File.OpenRead(targetFile))
                {
                    byte[] header = new byte[2];
                    int readH = checkFs.Read(header, 0, 2);
                    if (readH < 2 || header[0] != 0x4D || header[1] != 0x5A) // 'M', 'Z'
                    {
                        throw new InvalidOperationException("İndirilen dosya geçerli bir Windows çalıştırılabilir dosyası (PE/MZ) değil.");
                    }
                }

                DownloadProgress = 100;
                StatusMessage = "Güncelleme hazır, yeniden başlatılıyor...";
                OnDownloadProgressChanged?.Invoke(100, "Doğrulandı. AlpTogether güncelleniyor ve yeniden başlatılıyor...");
                AppLoggerService.Instance.Success("GÜNCELLEME", $"AlpTogether v{LatestVersion} dosyası başarıyla indirildi ve doğrulandı ({fi.Length / 1048576.0:0.0} MB).", "AlpTogether");

                return targetFile;
            }
            finally
            {
                IsDownloading = false;
            }
        }

        /// <summary>
        /// İndirilen yeni sürümü mevcut AlpTogether.exe üzerine yazar ve uygulamayı kesintisiz yeniden başlatır.
        /// </summary>
        public bool ApplyUpdateAndRestart(string downloadedExePath)
        {
            if (!File.Exists(downloadedExePath))
                throw new FileNotFoundException("Güncelleme dosyası bulunamadı.", downloadedExePath);

            string currentExe = Environment.ProcessPath ?? "";
            if (string.IsNullOrWhiteSpace(currentExe) || !File.Exists(currentExe))
            {
                currentExe = Process.GetCurrentProcess().MainModule?.FileName ?? "";
            }

            if (string.IsNullOrWhiteSpace(currentExe) || !File.Exists(currentExe))
            {
                // Fallback to LocalAppData default install path
                currentExe = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "AlpTogether", "AlpTogether.exe");
            }

            int currentPid = Environment.ProcessId;
            string updateDir = Path.GetDirectoryName(downloadedExePath) ?? Path.GetTempPath();
            string updaterCmd = Path.Combine(updateDir, "alptogether_self_updater.cmd");
            string workDir = Path.GetDirectoryName(currentExe) ?? updateDir;

            // CMD tabanlı atomik yer değiştirme ve yeniden başlatma betiği
            // 1. Mevcut PID'nin kapanmasını bekler (15 saniyeye kadar).
            // 2. Yeni dosyayı ana .exe üzerine kopyalar.
            // 3. Güncellenen AlpTogether'ı başlatır.
            // 4. Geçici betik ve indirme dosyasını temizler.
            string cmdContent = $@"@echo off
chcp 65001 >nul
set PID={currentPid}
set SRC=""{downloadedExePath}""
set DST=""{currentExe}""
set WORKDIR=""{workDir}""

:wait_loop
tasklist /fi ""PID eq %PID%"" 2>nul | find ""%PID%"" >nul
if not errorlevel 1 (
    timeout /t 1 /nobreak >nul
    goto wait_loop
)
timeout /t 1 /nobreak >nul

set RETRIES=0
:copy_loop
copy /y %SRC% %DST% >nul 2>&1
if errorlevel 1 (
    set /a RETRIES+=1
    if %RETRIES% leq 15 (
        timeout /t 1 /nobreak >nul
        goto copy_loop
    )
)

del /f /q %SRC% >nul 2>&1
cd /d %WORKDIR%
start """" %DST%
(goto) 2>nul & del ""%~f0""
";

            File.WriteAllText(updaterCmd, cmdContent, Encoding.GetEncoding(65001));

            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"\"{updaterCmd}\"\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                WorkingDirectory = updateDir
            };

            Process.Start(psi);

            AppLoggerService.Instance.Success("GÜNCELLEME", $"AlpTogether kendini v{LatestVersion} sürümüne güncelliyor...", "AlpTogether");

            Application.Current?.Dispatcher?.Invoke(() =>
            {
                Application.Current.Shutdown();
            });

            return true;
        }

        /// <summary>
        /// İndirme ve yeniden başlatma işlemini baştan sona otomatik çalıştırır.
        /// </summary>
        public async Task<bool> PerformFullSelfUpdateAsync(IProgress<double>? progress = null, Action<string>? statusCallback = null)
        {
            try
            {
                statusCallback?.Invoke("Yeni sürüm indiriliyor...");
                string downloadedPath = await DownloadUpdateAsync(progress);
                statusCallback?.Invoke("AlpTogether güncelleniyor ve yeniden başlatılıyor...");
                await Task.Delay(400);
                return ApplyUpdateAndRestart(downloadedPath);
            }
            catch (Exception ex)
            {
                AppLoggerService.Instance.Error("GÜNCELLEME", $"Kendini güncelleme başarısız: {ex.Message}", "AlpTogether", ex.StackTrace ?? "");
                statusCallback?.Invoke($"Güncelleme hatası: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Sürümleri karşılaştırır (latest > current ise true döner).
        /// </summary>
        public static bool IsNewerVersion(string latest, string current)
        {
            if (string.IsNullOrWhiteSpace(latest) || string.IsNullOrWhiteSpace(current) || current == "-")
                return false;

            try
            {
                string cleanLatest = NormalizeVersion(latest);
                string cleanCurrent = NormalizeVersion(current);

                if (Version.TryParse(cleanLatest, out var vLatest) && Version.TryParse(cleanCurrent, out var vCurr))
                {
                    return vLatest > vCurr;
                }

                return string.Compare(cleanLatest, cleanCurrent, StringComparison.OrdinalIgnoreCase) > 0;
            }
            catch
            {
                return false;
            }
        }

        public static string NormalizeVersion(string ver)
        {
            if (string.IsNullOrWhiteSpace(ver)) return "0.0.0";
            string s = ver.Trim().TrimStart('v', 'V').Split('-')[0].Split('+')[0];
            var parts = s.Split('.');
            if (parts.Length == 1) return $"{parts[0]}.0.0";
            if (parts.Length == 2) return $"{parts[0]}.{parts[1]}.0";
            if (parts.Length >= 3) return $"{parts[0]}.{parts[1]}.{parts[2]}";
            return s;
        }
    }
}
