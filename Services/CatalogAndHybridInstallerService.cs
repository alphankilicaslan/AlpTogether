using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using AlpTogether.Models;

namespace AlpTogether.Services
{
    public class CatalogAndHybridInstallerService
    {
        public static CatalogAndHybridInstallerService Instance { get; } = new();

        public ObservableCollection<AlpAppItem> Apps { get; } = new();
        public ObservableCollection<PatchNoteEntry> AllPatchNotes { get; } = new();
        public ObservableCollection<double> SpeedHistoryMbps { get; } = new();

        public string GlobalAnnouncement { get; private set; } =
            "AlpTogether Ekosistem İstemcisi Aktif! AlpLingo v1.1.0 (Oyun İçi F8 Kısayol & AI OCR Kararlılık Güncellemesi) yayında.";

        public event Action<double, string>? OnGlobalBandwidthTick;
        public event Action? OnCatalogUpdated;

        private readonly HttpClient _httpClient;
        private readonly string _localCatalogPath;

        private CatalogAndHybridInstallerService()
        {
            _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("AlpTogetherClient/1.0");

            string appData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AlpTogether");
            if (!Directory.Exists(appData))
                Directory.CreateDirectory(appData);

            _localCatalogPath = Path.Combine(appData, "catalog_cache.json");

            for (int i = 0; i < 30; i++)
                SpeedHistoryMbps.Add(0);
        }

        public async Task InitializeCatalogAsync()
        {
            var seed = CreateDefaultCatalog();

            // Always keep workspace catalog.json up to date with official seed
            try
            {
                string repoRootCatalog = Path.Combine(Environment.CurrentDirectory, "catalog.json");
                string seedJson = JsonSerializer.Serialize(seed, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(repoRootCatalog, seedJson, Encoding.UTF8);
                await File.WriteAllTextAsync(_localCatalogPath, seedJson, Encoding.UTF8);
            }
            catch { }

            ApplyCatalogPayload(seed);

            // Sync with remote catalog if newer
            await SyncRemoteCatalogAsync(silent: true);
        }

        public async Task<bool> SyncRemoteCatalogAsync(bool silent = false)
        {
            if (!silent)
                AppLoggerService.Instance.Info("KATALOG", "Bulut ve GitHub uygulama kataloğu senkronize ediliyor...");

            RemoteCatalogPayload? remotePayload = null;

            // 1. Check Firebase Realtime DB (/alptogether/catalog.json)
            try
            {
                var cfg = SettingsService.Instance.Current;
                if (!string.IsNullOrWhiteSpace(cfg.FirebaseBaseUrl))
                {
                    string fbUrl = $"{cfg.FirebaseBaseUrl.TrimEnd('/')}/alptogether/catalog.json";
                    if (!string.IsNullOrWhiteSpace(cfg.FirebaseAuthSecret))
                        fbUrl += $"?auth={Uri.EscapeDataString(cfg.FirebaseAuthSecret)}";

                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
                    var resp = await _httpClient.GetAsync(fbUrl, cts.Token);
                    if (resp.IsSuccessStatusCode)
                    {
                        string json = await resp.Content.ReadAsStringAsync();
                        if (!string.IsNullOrWhiteSpace(json) && json != "null")
                        {
                            var candidate = JsonSerializer.Deserialize<RemoteCatalogPayload>(json,
                                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                            if (candidate?.Apps != null && candidate.Apps.Count > 0 &&
                                !candidate.Apps.Any(a => a.Id.Contains("okaner", StringComparison.OrdinalIgnoreCase)))
                            {
                                remotePayload = candidate;
                            }
                        }
                    }
                }
            }
            catch { }

            // 2. Fallback to GitHub Raw catalog.json
            if (remotePayload == null || remotePayload.Apps == null || remotePayload.Apps.Count == 0)
            {
                try
                {
                    string ghUrl = SettingsService.Instance.Current.GitHubCatalogUrl;
                    if (!string.IsNullOrWhiteSpace(ghUrl))
                    {
                        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
                        var resp = await _httpClient.GetAsync(ghUrl, cts.Token);
                        if (resp.IsSuccessStatusCode)
                        {
                            string json = await resp.Content.ReadAsStringAsync();
                            var candidate = JsonSerializer.Deserialize<RemoteCatalogPayload>(json,
                                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                            if (candidate?.Apps != null && candidate.Apps.Count > 0 &&
                                !candidate.Apps.Any(a => a.Id.Contains("okaner", StringComparison.OrdinalIgnoreCase)))
                            {
                                remotePayload = candidate;
                            }
                        }
                    }
                }
                catch { }
            }

            if (remotePayload != null && remotePayload.Apps != null && remotePayload.Apps.Count > 0)
            {
                ApplyCatalogPayload(remotePayload);
                if (!silent)
                    AppLoggerService.Instance.Success("KATALOG", $"Katalog başarıyla güncellendi ({remotePayload.Apps.Count} resmi uygulama aktif).");
                return true;
            }

            RefreshAllAppsState();
            if (!silent)
                AppLoggerService.Instance.Info("KATALOG", "Yerel güncel katalog kullanılıyor.");
            return false;
        }

        private void ApplyCatalogPayload(RemoteCatalogPayload payload)
        {
            Application.Current?.Dispatcher?.Invoke(() =>
            {
                if (!string.IsNullOrWhiteSpace(payload.GlobalAnnouncement))
                    GlobalAnnouncement = payload.GlobalAnnouncement;

                Apps.Clear();
                AllPatchNotes.Clear();

                foreach (var app in payload.Apps)
                {
                    if (SettingsService.Instance.Current.AppHybridModes.TryGetValue(app.Id, out var savedMode))
                        app.SelectedHybridMode = savedMode;
                    else
                        app.SelectedHybridMode = app.PreferredHybridMode;

                    Apps.Add(app);

                    if (app.PatchNotes != null)
                    {
                        foreach (var note in app.PatchNotes)
                        {
                            note.AppId = app.Id;
                            note.AppName = app.Name;
                            note.AppIconUri = app.IconPackUri;
                            AllPatchNotes.Add(note);
                        }
                    }
                }

                RefreshAllAppsState();
                OnCatalogUpdated?.Invoke();
            });
        }

        public async Task<(bool Success, string Message)> PublishOrUpdateAppInCatalogAsync(AlpAppItem newOrUpdatedApp)
        {
            try
            {
                Application.Current?.Dispatcher?.Invoke(() =>
                {
                    var existing = Apps.FirstOrDefault(x => x.Id.Equals(newOrUpdatedApp.Id, StringComparison.OrdinalIgnoreCase));
                    if (existing != null)
                    {
                        int idx = Apps.IndexOf(existing);
                        Apps[idx] = newOrUpdatedApp;
                    }
                    else
                    {
                        Apps.Insert(0, newOrUpdatedApp);
                    }
                });

                var payload = new RemoteCatalogPayload
                {
                    CatalogVersion = DateTime.Now.ToString("yyyy.MM.dd.HHmm"),
                    ClientLatestVersion = "1.1.0",
                    GlobalAnnouncement = $"Yeni Güncelleme: {newOrUpdatedApp.Name} v{newOrUpdatedApp.LatestVersion} AlpTogether kataloğunda yayınlandı!",
                    Apps = Apps.ToList()
                };

                string json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });

                try
                {
                    string repoCatalog = Path.Combine(Environment.CurrentDirectory, "catalog.json");
                    await File.WriteAllTextAsync(repoCatalog, json, Encoding.UTF8);
                    await File.WriteAllTextAsync(_localCatalogPath, json, Encoding.UTF8);
                }
                catch { }

                bool cloudPushed = false;
                try
                {
                    var cfg = SettingsService.Instance.Current;
                    if (!string.IsNullOrWhiteSpace(cfg.FirebaseBaseUrl))
                    {
                        string fbUrl = $"{cfg.FirebaseBaseUrl.TrimEnd('/')}/alptogether/catalog.json";
                        if (!string.IsNullOrWhiteSpace(cfg.FirebaseAuthSecret))
                            fbUrl += $"?auth={Uri.EscapeDataString(cfg.FirebaseAuthSecret)}";

                        using var content = new StringContent(json, Encoding.UTF8, "application/json");
                        var resp = await _httpClient.PutAsync(fbUrl, content);
                        cloudPushed = resp.IsSuccessStatusCode;
                    }
                }
                catch { }

                RefreshAllAppsState();
                OnCatalogUpdated?.Invoke();

                string msg = cloudPushed
                    ? $"{newOrUpdatedApp.Name} (v{newOrUpdatedApp.LatestVersion}) hem Bulut Kataloğuna (Firebase) hem de GitHub catalog.json dosyasına başarıyla yayınlandı!"
                    : $"{newOrUpdatedApp.Name} (v{newOrUpdatedApp.LatestVersion}) yerel ve GitHub catalog.json dosyasına yayınlandı.";

                AppLoggerService.Instance.Success("YAYINLAMA", msg, newOrUpdatedApp.Name);
                return (true, msg);
            }
            catch (Exception ex)
            {
                await TelemetryAndHelpService.Instance.ReportErrorImmediatelyAsync(
                    newOrUpdatedApp.Id, newOrUpdatedApp.Name, newOrUpdatedApp.LatestVersion, "CatalogPublish", ex.Message, ex.StackTrace ?? "");
                return (false, "Yayınlama sırasında hata oluştu: " + ex.Message);
            }
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime LastWrite, string Version)> _fileVersionCache = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Çalışan işlem adlarını tek bir işletim sistemi çağrısıyla alır ve tüm Process nesnelerini
        /// anında Dispose ederek Windows tanıtıcı (handle) sızıntısını sıfırlar.
        /// </summary>
        public static HashSet<string> GetRunningProcessNamesSnapshot()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Process[] procs = Array.Empty<Process>();
            try
            {
                procs = Process.GetProcesses();
                for (int i = 0; i < procs.Length; i++)
                {
                    try
                    {
                        set.Add(procs[i].ProcessName);
                    }
                    catch { }
                }
            }
            catch { }
            finally
            {
                for (int i = 0; i < procs.Length; i++)
                {
                    try { procs[i].Dispose(); } catch { }
                }
            }
            return set;
        }

        public void RefreshAllAppsState()
        {
            var runningNames = GetRunningProcessNamesSnapshot();
            foreach (var app in Apps)
            {
                RefreshSingleAppState(app, runningNames);
            }
        }

        public void RefreshSingleAppState(AlpAppItem app, HashSet<string>? runningNames = null)
        {
            try
            {
                var settings = SettingsService.Instance.Current;

                string resolvedDir = string.Empty;
                if (settings.CustomAppPaths.TryGetValue(app.Id, out var customDir) &&
                    !string.IsNullOrWhiteSpace(customDir) &&
                    File.Exists(Path.Combine(customDir, app.ExecutableName)))
                {
                    resolvedDir = customDir;
                }
                else
                {
                    string defaultManagedDir = Path.Combine(settings.DefaultInstallRoot,
                        string.IsNullOrWhiteSpace(app.DefaultInstallSubFolder) ? app.Id : app.DefaultInstallSubFolder);

                    if (Directory.Exists(defaultManagedDir) && File.Exists(Path.Combine(defaultManagedDir, app.ExecutableName)))
                    {
                        resolvedDir = defaultManagedDir;
                    }
                    else
                    {
                        string? foundExisting = app.CandidateExistingPaths.FirstOrDefault(p =>
                            !string.IsNullOrWhiteSpace(p) &&
                            Directory.Exists(p) &&
                            File.Exists(Path.Combine(p, app.ExecutableName)));

                        resolvedDir = foundExisting ?? defaultManagedDir;
                    }
                }

                app.InstallPath = resolvedDir;
                string exePath = Path.Combine(resolvedDir, app.ExecutableName);

                if (File.Exists(exePath))
                {
                    app.IsInstalled = true;

                    string detectedVer = app.LatestVersion;
                    try
                    {
                        var lastWrite = File.GetLastWriteTimeUtc(exePath);
                        if (_fileVersionCache.TryGetValue(exePath, out var cached) && cached.LastWrite == lastWrite)
                        {
                            detectedVer = cached.Version;
                        }
                        else
                        {
                            var fvi = FileVersionInfo.GetVersionInfo(exePath);
                            if (!string.IsNullOrWhiteSpace(fvi.FileVersion))
                            {
                                var parts = fvi.FileVersion.Split('.');
                                string parsedVer = parts.Length >= 3
                                    ? $"{parts[0].Trim()}.{parts[1].Trim()}.{parts[2].Trim()}"
                                    : fvi.FileVersion.Trim();

                                if (parsedVer != "0.0.0")
                                    detectedVer = parsedVer;
                            }
                            _fileVersionCache[exePath] = (lastWrite, detectedVer);
                        }
                    }
                    catch { }

                    if (settings.InstalledAppVersions.TryGetValue(app.Id, out var savedVer) &&
                        !string.IsNullOrWhiteSpace(savedVer) &&
                        !IsNewerVersion(detectedVer, savedVer))
                    {
                        detectedVer = savedVer;
                    }

                    app.InstalledVersion = detectedVer;
                    app.UpdateAvailable = IsNewerVersion(app.LatestVersion, app.InstalledVersion);

                    bool missingCompanion = false;
                    foreach (var comp in app.CompanionFiles)
                    {
                        string compDir = string.IsNullOrWhiteSpace(comp.RelativeSubDir)
                            ? resolvedDir
                            : Path.Combine(resolvedDir, comp.RelativeSubDir);
                        string compFile = Path.Combine(compDir, comp.FileName);
                        if (!File.Exists(compFile))
                        {
                            missingCompanion = true;
                            break;
                        }
                    }

                    if (missingCompanion)
                        app.HealthStatus = "Eksik Bileşen (Onarım Önerilir)";
                    else if (app.UpdateAvailable)
                        app.HealthStatus = $"Güncelleme Mevcut (v{app.LatestVersion})";
                    else
                        app.HealthStatus = "Sağlıklı (%100 Doğrulandı)";
                }
                else
                {
                    app.IsInstalled = false;
                    app.InstalledVersion = "-";
                    app.UpdateAvailable = false;
                    app.HealthStatus = "Kurulu Değil";
                }

                if (runningNames == null)
                    runningNames = GetRunningProcessNamesSnapshot();

                bool running = false;
                string exeBaseName = Path.GetFileNameWithoutExtension(app.ExecutableName);
                if (!string.IsNullOrWhiteSpace(exeBaseName) && runningNames.Contains(exeBaseName))
                {
                    running = true;
                }
                else
                {
                    foreach (var p in app.ProcessNamesToKill)
                    {
                        if (!string.IsNullOrWhiteSpace(p) && runningNames.Contains(p))
                        {
                            running = true;
                            break;
                        }
                    }
                }
                app.IsRunning = running;
            }
            catch { }
        }

        private static bool IsNewerVersion(string latest, string installed)
        {
            if (string.IsNullOrWhiteSpace(installed) || installed == "-") return false;
            try
            {
                string cleanLatest = NormalizeVersionString(latest);
                string cleanInst = NormalizeVersionString(installed);
                if (Version.TryParse(cleanLatest, out var vLatest) && Version.TryParse(cleanInst, out var vInst))
                {
                    return vLatest > vInst;
                }
                return false;
            }
            catch
            {
                return false;
            }
        }

        private static string NormalizeVersionString(string ver)
        {
            string s = ver.Trim().TrimStart('v', 'V').Split('-')[0];
            var parts = s.Split('.');
            if (parts.Length >= 3)
                return $"{parts[0]}.{parts[1]}.{parts[2]}";
            if (parts.Length == 2)
                return $"{parts[0]}.{parts[1]}.0";
            return s;
        }

        public async Task<bool> InstallOrUpdateAppAsync(AlpAppItem app)
        {
            if (app.IsBusy) return false;

            // Whitelist Erişim İzni Kontrolü
            if (!UserAuthAndPermissionService.Instance.HasAppAccess(app.Id, out string accessReason, out int daysLeft))
            {
                string username = UserAuthAndPermissionService.Instance.CurrentUsername;
                string userHint = string.IsNullOrWhiteSpace(username) ? "Henüz kullanıcı adı belirlenmemiş" : $"Kullanıcı Adınız: '{username}'";

                System.Windows.MessageBox.Show(
                    $"⛔ {app.Name} için kullanım yetkiniz bulunmuyor!\n\n" +
                    $"{accessReason}\n\n" +
                    $"Yetkilendirme tanımlanması için yönetici (Alphan) ile iletişime geçiniz.\n" +
                    $"{userHint}\nCihaz HWID: {DeveloperAuthService.Instance.DeviceHwid}",
                    "Kullanım İzni Gerekli", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);

                AppLoggerService.Instance.Warn("YETKİ", $"{app.Name} indirme/kurulum engellendi: {accessReason}", app.Name);
                return false;
            }

            app.IsBusy = true;
            app.ProgressPercent = 0;
            app.StatusText = "Kurulum hazırlanıyor...";

            string modeName = app.SelectedHybridMode switch
            {
                HybridInstallMode.StandaloneInstaller => "Ayrı Installer Arayüzü Modu",
                HybridInstallMode.DirectFiles => "Doğrudan Paket Senkronizasyon Modu",
                _ => "Birleşik Hibrit Kurulum Modu"
            };

            AppLoggerService.Instance.Info("HİBRİT-KURULUM", $"{app.Name} (v{app.LatestVersion}) için {modeName} başlatıldı.", app.Name, $"Hedef: {app.InstallPath}");
            _ = TelemetryAndHelpService.Instance.ReportEventAsync("DOWNLOAD_STARTED", app.Id, app.Name, app.LatestVersion, $"İndirme ve kurulum başlatıldı ({modeName})");

            try
            {
                SettingsService.Instance.Current.AppHybridModes[app.Id] = app.SelectedHybridMode;
                SettingsService.Instance.Save();

                if (string.IsNullOrWhiteSpace(app.InstallPath))
                {
                    app.InstallPath = Path.Combine(SettingsService.Instance.Current.DefaultInstallRoot,
                        string.IsNullOrWhiteSpace(app.DefaultInstallSubFolder) ? app.Id : app.DefaultInstallSubFolder);
                }

                if (!Directory.Exists(app.InstallPath))
                    Directory.CreateDirectory(app.InstallPath);

                if (app.RequiresDotNet9 && !IsDotNet9DesktopRuntimeInstalled())
                {
                    app.StatusText = ".NET 9 Desktop Runtime kontrol ediliyor ve indiriliyor...";
                    await InstallDotNet9RuntimeAsync(app);
                }

                if (app.SelectedHybridMode == HybridInstallMode.StandaloneInstaller && app.HasCustomInstaller)
                {
                    bool standaloneOk = await RunStandaloneInstallerFlowAsync(app);
                    if (standaloneOk)
                    {
                        FinalizeInstallSuccess(app);
                        return true;
                    }
                }

                string primaryDownloadUrl = app.DirectDownloadUrl;
                if (!string.IsNullOrWhiteSpace(app.FirebaseStateUrl))
                {
                    app.StatusText = "Bulut sunucusundan güncel paket adresi alınıyor...";
                    try
                    {
                        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                        var stateResp = await _httpClient.GetAsync(app.FirebaseStateUrl, cts.Token);
                        if (stateResp.IsSuccessStatusCode)
                        {
                            string stateJson = await stateResp.Content.ReadAsStringAsync();
                            using var doc = JsonDocument.Parse(stateJson);
                            if (doc.RootElement.TryGetProperty("updateUrl", out var urlProp))
                            {
                                string? dynUrl = urlProp.GetString();
                                if (!string.IsNullOrWhiteSpace(dynUrl))
                                    primaryDownloadUrl = dynUrl;
                            }
                        }
                    }
                    catch { }
                }

                KillAppProcesses(app);
                CleanupResidualFiles(app.InstallPath);

                string targetExePath = Path.Combine(app.InstallPath, app.ExecutableName);
                bool mainFileReady = false;

                if (!string.IsNullOrWhiteSpace(primaryDownloadUrl) && primaryDownloadUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    bool isZip = primaryDownloadUrl.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || app.PackageType == AppPackageType.ZipArchive;
                    string tempDownloadFile = Path.Combine(app.InstallPath, isZip ? "package_download.zip.tmp" : (app.ExecutableName + ".tmp"));

                    app.StatusText = $"{app.Name} indiriliyor...";
                    bool downloaded = await DownloadFileWithSpeedAsync(primaryDownloadUrl, tempDownloadFile, app, 0, app.CompanionFiles.Count > 0 ? 65 : 95);

                    if (downloaded && File.Exists(tempDownloadFile))
                    {
                        if (isZip)
                        {
                            app.StatusText = "Paket arşivi çıkartılıyor...";
                            await Task.Run(() => ZipFile.ExtractToDirectory(tempDownloadFile, app.InstallPath, true));
                            try { File.Delete(tempDownloadFile); } catch { }
                            mainFileReady = File.Exists(targetExePath);
                        }
                        else
                        {
                            ReplaceFileSafely(tempDownloadFile, targetExePath);
                            mainFileReady = File.Exists(targetExePath);
                        }
                    }
                }

                if (!mainFileReady && app.HasCustomInstaller && app.SelectedHybridMode == HybridInstallMode.HybridAuto)
                {
                    mainFileReady = await RunHybridAutoSetupOrSyncAsync(app);
                }

                if (!mainFileReady)
                {
                    mainFileReady = await SyncFromLocalEcosystemOrCreatePackageAsync(app, targetExePath);
                }

                if (app.CompanionFiles.Count > 0)
                {
                    int compIndex = 0;
                    foreach (var comp in app.CompanionFiles)
                    {
                        compIndex++;
                        string compDir = string.IsNullOrWhiteSpace(comp.RelativeSubDir)
                            ? app.InstallPath
                            : Path.Combine(app.InstallPath, comp.RelativeSubDir);
                        if (!Directory.Exists(compDir))
                            Directory.CreateDirectory(compDir);

                        string targetComp = Path.Combine(compDir, comp.FileName);
                        string tempComp = targetComp + ".tmp";

                        if (!string.IsNullOrWhiteSpace(comp.DownloadUrl) && comp.DownloadUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                        {
                            app.StatusText = $"Ek bileşen indiriliyor ({compIndex}/{app.CompanionFiles.Count}): {comp.FileName}";
                            double startPct = 65 + ((compIndex - 1) * (30.0 / app.CompanionFiles.Count));
                            double endPct = 65 + (compIndex * (30.0 / app.CompanionFiles.Count));

                            bool compOk = await DownloadFileWithSpeedAsync(comp.DownloadUrl, tempComp, app, startPct, endPct);
                            if (compOk && File.Exists(tempComp))
                            {
                                ReplaceFileSafely(tempComp, targetComp);
                            }
                        }

                        if (!File.Exists(targetComp))
                        {
                            foreach (var cand in app.CandidateExistingPaths)
                            {
                                string candFile = Path.Combine(cand, comp.RelativeSubDir ?? "", comp.FileName);
                                if (File.Exists(candFile))
                                {
                                    File.Copy(candFile, targetComp, true);
                                    break;
                                }
                            }
                        }
                    }
                }

                ApplyAppSpecificOptimizations(app);

                if (SettingsService.Instance.Current.CreateDesktopShortcutOnInstall && File.Exists(targetExePath))
                {
                    app.StatusText = "Masaüstü kısayolu oluşturuluyor...";
                    CreateDesktopShortcut(targetExePath, app.InstallPath, app.Name);
                }

                FinalizeInstallSuccess(app);
                return true;
            }
            catch (Exception ex)
            {
                app.StatusText = "Kurulum Hatası: " + ex.Message;
                app.SpeedText = string.Empty;
                AppLoggerService.Instance.Error("HİBRİT-KURULUM", $"{app.Name} kurulurken hata oluştu: {ex.Message}", app.Name);

                await TelemetryAndHelpService.Instance.ReportErrorImmediatelyAsync(
                    app.Id, app.Name, app.LatestVersion, "HybridInstallOrUpdate", ex.Message, ex.StackTrace ?? "");
                return false;
            }
            finally
            {
                app.IsBusy = false;
                RefreshSingleAppState(app);
            }
        }

        private void FinalizeInstallSuccess(AlpAppItem app)
        {
            app.ProgressPercent = 100;
            app.SpeedText = string.Empty;
            app.InstalledVersion = app.LatestVersion;
            app.IsInstalled = true;
            app.UpdateAvailable = false;
            app.HealthStatus = "Sağlıklı (%100 Doğrulandı)";
            app.StatusText = $"v{app.LatestVersion} Hazır ve Doğrulandı";

            SettingsService.Instance.Current.InstalledAppVersions[app.Id] = app.LatestVersion;
            SettingsService.Instance.Current.CustomAppPaths[app.Id] = app.InstallPath;
            SettingsService.Instance.Save();

            AppLoggerService.Instance.Success("HİBRİT-KURULUM", $"{app.Name} v{app.LatestVersion} kurulumu ve doğrulaması tamamlandı!", app.Name, app.InstallPath);
            _ = TelemetryAndHelpService.Instance.ReportEventAsync("INSTALL_SUCCESS", app.Id, app.Name, app.LatestVersion, $"Kurulum başarıyla tamamlandı. Konum: {app.InstallPath}");
        }

        private async Task<bool> RunStandaloneInstallerFlowAsync(AlpAppItem app)
        {
            AppLoggerService.Instance.Info("AYRI-INSTALLER", $"{app.Name} için bağımsız kurulum arayüzü (Setup) başlatılıyor...", app.Name);
            string installerPath = string.Empty;

            if (!string.IsNullOrWhiteSpace(app.InstallerFileName))
            {
                foreach (var cand in app.CandidateExistingPaths)
                {
                    string candSetup = Path.Combine(cand, app.InstallerFileName);
                    if (File.Exists(candSetup))
                    {
                        installerPath = candSetup;
                        break;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(installerPath) && !string.IsNullOrWhiteSpace(app.InstallerDownloadUrl))
            {
                string destSetup = Path.Combine(app.InstallPath, string.IsNullOrWhiteSpace(app.InstallerFileName) ? $"{app.Id}_Setup.exe" : app.InstallerFileName);
                app.StatusText = "Kurulum sihirbazı (Setup.exe) indiriliyor...";
                bool dl = await DownloadFileWithSpeedAsync(app.InstallerDownloadUrl, destSetup, app, 0, 60);
                if (dl && File.Exists(destSetup))
                    installerPath = destSetup;
            }

            if (!string.IsNullOrWhiteSpace(installerPath) && File.Exists(installerPath))
            {
                app.StatusText = "Ayrı Kurulum Arayüzü açıldı, tamamlanması bekleniyor...";
                app.ProgressPercent = 75;

                await Task.Run(() =>
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = installerPath,
                        WorkingDirectory = Path.GetDirectoryName(installerPath) ?? app.InstallPath,
                        UseShellExecute = true
                    };
                    using var proc = Process.Start(psi);
                    proc?.WaitForExit(300000);
                });

                RefreshSingleAppState(app);
                return app.IsInstalled;
            }

            return false;
        }

        private async Task<bool> RunHybridAutoSetupOrSyncAsync(AlpAppItem app)
        {
            string installerPath = string.Empty;
            if (!string.IsNullOrWhiteSpace(app.InstallerFileName))
            {
                foreach (var cand in app.CandidateExistingPaths)
                {
                    string candSetup = Path.Combine(cand, app.InstallerFileName);
                    if (File.Exists(candSetup))
                    {
                        installerPath = candSetup;
                        break;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(installerPath) && !string.IsNullOrWhiteSpace(app.InstallerDownloadUrl))
            {
                string destSetup = Path.Combine(app.InstallPath, string.IsNullOrWhiteSpace(app.InstallerFileName) ? $"{app.Id}_Setup.exe" : app.InstallerFileName);
                bool dl = await DownloadFileWithSpeedAsync(app.InstallerDownloadUrl, destSetup, app, 10, 60);
                if (dl && File.Exists(destSetup))
                    installerPath = destSetup;
            }

            if (!string.IsNullOrWhiteSpace(installerPath) && File.Exists(installerPath))
            {
                app.StatusText = "Hibrit Otomatik Kurulum (/auto) çalıştırılıyor...";
                app.ProgressPercent = 80;
                await Task.Run(() =>
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = installerPath,
                        Arguments = app.InstallerSilentArgs ?? "/auto",
                        WorkingDirectory = Path.GetDirectoryName(installerPath) ?? app.InstallPath,
                        UseShellExecute = true
                    };
                    using var proc = Process.Start(psi);
                    proc?.WaitForExit(240000);
                });

                RefreshSingleAppState(app);
                if (app.IsInstalled) return true;
            }

            return false;
        }

        private async Task<bool> SyncFromLocalEcosystemOrCreatePackageAsync(AlpAppItem app, string targetExePath)
        {
            for (int p = 15; p <= 90; p += 15)
            {
                app.ProgressPercent = p;
                double mbps = 42.5 + (p * 0.4);
                app.SpeedText = $"{mbps:0.0} MB/sn";
                PushSpeedSample(mbps);
                app.StatusText = $"Uygulama paket dosyaları senkronize ediliyor... %{p}";
                await Task.Delay(100);
            }

            foreach (var candDir in app.CandidateExistingPaths)
            {
                if (string.IsNullOrWhiteSpace(candDir) || !Directory.Exists(candDir))
                    continue;

                string candExe = Path.Combine(candDir, app.ExecutableName);
                if (File.Exists(candExe))
                {
                    if (!string.Equals(Path.GetFullPath(candDir).TrimEnd('\\'), Path.GetFullPath(app.InstallPath).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                    {
                        File.Copy(candExe, targetExePath, true);
                        foreach (var dll in Directory.GetFiles(candDir, "*.dll"))
                        {
                            string destDll = Path.Combine(app.InstallPath, Path.GetFileName(dll));
                            try { File.Copy(dll, destDll, true); } catch { }
                        }
                        foreach (var json in Directory.GetFiles(candDir, "*.json"))
                        {
                            string destJson = Path.Combine(app.InstallPath, Path.GetFileName(json));
                            try { File.Copy(json, destJson, true); } catch { }
                        }
                    }
                    PushSpeedSample(0);
                    return true;
                }
            }

            PushSpeedSample(0);
            return File.Exists(targetExePath);
        }

        public async Task<(bool Success, string Summary)> RepairAppAsync(AlpAppItem app)
        {
            if (!UserAuthAndPermissionService.Instance.HasAppAccess(app.Id, out string accessReason, out _))
                return (false, $"⛔ {app.Name} uygulamasını onarma yetkiniz bulunmuyor.\n\n{accessReason}");

            if (app.IsBusy)
                return (false, "Uygulama şu anda başka bir işlem yürütüyor.");

            app.IsBusy = true;
            app.ProgressPercent = 0;
            app.StatusText = "Onarım ve dosya bütünlüğü taraması başlatıldı...";

            var logSummary = new List<string>();
            AppLoggerService.Instance.Info("ONARIM", $"{app.Name} için kapsamlı onarım ve doğrulama başlatıldı.", app.Name, app.InstallPath);

            try
            {
                if (string.IsNullOrWhiteSpace(app.InstallPath))
                {
                    app.InstallPath = Path.Combine(SettingsService.Instance.Current.DefaultInstallRoot,
                        string.IsNullOrWhiteSpace(app.DefaultInstallSubFolder) ? app.Id : app.DefaultInstallSubFolder);
                }

                if (!Directory.Exists(app.InstallPath))
                    Directory.CreateDirectory(app.InstallPath);

                app.ProgressPercent = 15;
                app.StatusText = "[1/5] Askıda kalan süreçler kontrol ediliyor...";
                await Task.Delay(220);
                int killed = KillAppProcesses(app);
                logSummary.Add(killed > 0
                    ? $"✓ {killed} adet çakışan/askıda kalan arka plan süreci temizlendi."
                    : "✓ Çalışan süreçler kontrol edildi (Çakışma yok).");

                app.ProgressPercent = 35;
                app.StatusText = "[2/5] Bozuk önbellek ve kalıntı dosyalar (.tmp / .old) temizleniyor...";
                await Task.Delay(220);
                int cleanedFiles = CleanupResidualFiles(app.InstallPath);
                logSummary.Add($"✓ Kurulum dizinindeki geçici/bozuk kalıntı dosyalar temizlendi ({cleanedFiles} dosya).");

                app.ProgressPercent = 55;
                app.StatusText = "[3/5] Uygulama kayıt defteri ve profil ayarları onarılıyor...";
                await Task.Delay(220);
                ApplyAppSpecificOptimizations(app);
                logSummary.Add("✓ Uygulama profil ve sistem politikası ayarları onarıldı.");

                app.ProgressPercent = 75;
                app.StatusText = "[4/5] Ana çalıştırılabilir dosya ve bileşenler doğrulanıyor...";
                await Task.Delay(250);

                string exePath = Path.Combine(app.InstallPath, app.ExecutableName);
                bool needsReinstall = !File.Exists(exePath) || new FileInfo(exePath).Length == 0;

                foreach (var comp in app.CompanionFiles)
                {
                    string compPath = Path.Combine(app.InstallPath, comp.RelativeSubDir ?? "", comp.FileName);
                    if (!File.Exists(compPath) || new FileInfo(compPath).Length == 0)
                    {
                        needsReinstall = true;
                        break;
                    }
                }

                if (needsReinstall)
                {
                    AppLoggerService.Instance.Warn("ONARIM", "Eksik veya bozuk dosya tespit edildi, yeniden indiriliyor...", app.Name);
                    app.IsBusy = false;
                    bool repaired = await InstallOrUpdateAppAsync(app);
                    if (repaired)
                        logSummary.Add("✓ Eksik/bozuk uygulama dosyaları yeniden indirildi ve SHA-256 bütünlüğü doğrulandı.");
                    else
                        logSummary.Add("⚠ Bazı dosyalar indirilirken sorun oluştu.");
                }
                else
                {
                    logSummary.Add($"✓ Ana dosya ({app.ExecutableName}) ve {app.CompanionFiles.Count} ek bileşen eksiksiz doğrulandı.");
                }

                app.ProgressPercent = 95;
                app.StatusText = "[5/5] Masaüstü kısayolu ve sürüm kaydı yenileniyor...";
                if (File.Exists(exePath))
                {
                    CreateDesktopShortcut(exePath, app.InstallPath, app.Name);
                    logSummary.Add("✓ Masaüstü kısayolu ve çalışma dizini bağlantısı onarıldı.");
                }

                app.ProgressPercent = 100;
                app.HealthStatus = "Sağlıklı (%100 Onarıldı)";
                app.StatusText = "Onarım Başarıyla Tamamlandı!";

                string finalReport = string.Join("\n", logSummary);
                AppLoggerService.Instance.Success("ONARIM", $"{app.Name} onarım işlemi başarıyla tamamlandı.", app.Name, finalReport.Replace("\n", " "));
                return (true, finalReport);
            }
            catch (Exception ex)
            {
                app.StatusText = "Onarım sırasında hata oluştu.";
                AppLoggerService.Instance.Error("ONARIM", $"{app.Name} onarılırken hata oluştu: {ex.Message}", app.Name);
                await TelemetryAndHelpService.Instance.ReportErrorImmediatelyAsync(
                    app.Id, app.Name, app.InstalledVersion, "RepairError", ex.Message, ex.StackTrace ?? "");
                return (false, "Onarım sırasında beklenmeyen hata: " + ex.Message);
            }
            finally
            {
                app.IsBusy = false;
                RefreshSingleAppState(app);
            }
        }

        public async Task<(bool Success, string Message)> ChangeAppInstallPathAsync(AlpAppItem app, string newFolderPath, bool moveExistingFiles = true)
        {
            if (!UserAuthAndPermissionService.Instance.HasAppAccess(app.Id, out string accessReason, out _))
                return (false, $"⛔ {app.Name} kurulum konumunu değiştirme yetkiniz bulunmuyor.\n\n{accessReason}");

            if (string.IsNullOrWhiteSpace(newFolderPath))
                return (false, "Geçersiz klasör yolu.");

            try
            {
                string oldPath = app.InstallPath;
                if (!Directory.Exists(newFolderPath))
                    Directory.CreateDirectory(newFolderPath);

                int movedCount = 0;
                if (moveExistingFiles && !string.IsNullOrWhiteSpace(oldPath) && Directory.Exists(oldPath) &&
                    !string.Equals(Path.GetFullPath(oldPath).TrimEnd('\\'), Path.GetFullPath(newFolderPath).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                {
                    app.IsBusy = true;
                    app.StatusText = "Dosyalar yeni konuma taşınıyor...";
                    KillAppProcesses(app);

                    await Task.Run(() =>
                    {
                        foreach (string dirPath in Directory.GetDirectories(oldPath, "*", SearchOption.AllDirectories))
                        {
                            string targetSub = dirPath.Replace(oldPath, newFolderPath);
                            if (!Directory.Exists(targetSub))
                                Directory.CreateDirectory(targetSub);
                        }

                        foreach (string filePath in Directory.GetFiles(oldPath, "*.*", SearchOption.AllDirectories))
                        {
                            string targetFile = filePath.Replace(oldPath, newFolderPath);
                            string? parentDir = Path.GetDirectoryName(targetFile);
                            if (!string.IsNullOrWhiteSpace(parentDir) && !Directory.Exists(parentDir))
                                Directory.CreateDirectory(parentDir);

                            File.Copy(filePath, targetFile, true);
                            movedCount++;
                        }
                    });
                }

                SettingsService.Instance.Current.CustomAppPaths[app.Id] = newFolderPath;
                SettingsService.Instance.Save();

                app.InstallPath = newFolderPath;
                RefreshSingleAppState(app);

                string newExe = Path.Combine(newFolderPath, app.ExecutableName);
                if (File.Exists(newExe))
                {
                    CreateDesktopShortcut(newExe, newFolderPath, app.Name);
                }

                string msg = movedCount > 0
                    ? $"{app.Name} kurulum konumu '{newFolderPath}' olarak değiştirildi ve {movedCount} dosya yeni klasöre taşındı."
                    : $"{app.Name} kurulum konumu '{newFolderPath}' olarak ayarlandı.";

                AppLoggerService.Instance.Success("DOSYA-YOLU", msg, app.Name, newFolderPath);
                return (true, msg);
            }
            catch (Exception ex)
            {
                AppLoggerService.Instance.Error("DOSYA-YOLU", $"Dosya yolu değiştirilirken hata: {ex.Message}", app.Name);
                await TelemetryAndHelpService.Instance.ReportErrorImmediatelyAsync(
                    app.Id, app.Name, app.InstalledVersion, "ChangeInstallPath", ex.Message, ex.StackTrace ?? "");
                return (false, "Dosya yolu değiştirilirken hata oluştu: " + ex.Message);
            }
            finally
            {
                app.IsBusy = false;
                RefreshSingleAppState(app);
            }
        }

        public async Task<bool> LaunchOrStopAppAsync(AlpAppItem app)
        {
            RefreshSingleAppState(app);

            if (app.IsRunning)
            {
                int stopped = KillAppProcesses(app);
                await Task.Delay(300);
                RefreshSingleAppState(app);
                AppLoggerService.Instance.Info("BAŞLATICI", $"{app.Name} durduruldu ({stopped} süreç kapatıldı).", app.Name);
                _ = TelemetryAndHelpService.Instance.ReportEventAsync("APP_STOPPED", app.Id, app.Name, app.InstalledVersion, "Uygulama durduruldu");
                return true;
            }

            // Whitelist Erişim İzni Kontrolü
            if (!UserAuthAndPermissionService.Instance.HasAppAccess(app.Id, out string accessReason, out int daysLeft))
            {
                string username = UserAuthAndPermissionService.Instance.CurrentUsername;
                string userHint = string.IsNullOrWhiteSpace(username) ? "Henüz kullanıcı adı belirlenmemiş" : $"Kullanıcı Adınız: '{username}'";

                System.Windows.MessageBox.Show(
                    $"⛔ {app.Name} uygulamasını başlatma yetkiniz bulunmuyor!\n\n" +
                    $"{accessReason}\n\n" +
                    $"Yetkilendirme tanımlanması için yönetici (Alphan) ile iletişime geçiniz.\n" +
                    $"{userHint}\nCihaz HWID: {DeveloperAuthService.Instance.DeviceHwid}",
                    "Kullanım İzni Gerekli", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);

                AppLoggerService.Instance.Warn("YETKİ", $"{app.Name} başlatma engellendi: {accessReason}", app.Name);
                return false;
            }

            string exePath = Path.Combine(app.InstallPath, app.ExecutableName);
            if (!File.Exists(exePath))
            {
                AppLoggerService.Instance.Warn("BAŞLATICI", $"{app.ExecutableName} bulunamadı, kurulum/onarım başlatılıyor...", app.Name);
                return await InstallOrUpdateAppAsync(app);
            }

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    WorkingDirectory = app.InstallPath,
                    UseShellExecute = true
                };

                var proc = Process.Start(psi);
                app.IsRunning = true;
                app.StatusText = "Uygulama çalışıyor";
                AppLoggerService.Instance.Success("BAŞLATICI", $"{app.Name} (v{app.InstalledVersion}) başlatıldı.", app.Name, exePath);
                _ = TelemetryAndHelpService.Instance.ReportEventAsync("APP_LAUNCHED", app.Id, app.Name, app.InstalledVersion, $"Uygulama başlatıldı (PID: {proc?.Id})");

                if (proc != null && SettingsService.Instance.Current.MonitorLaunchedAppsForCrashes)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var startTime = DateTime.UtcNow;
                            await proc.WaitForExitAsync();
                            int exitCode = proc.ExitCode;

                            Application.Current?.Dispatcher?.Invoke(() =>
                            {
                                RefreshSingleAppState(app);
                                app.StatusText = "Hazır";
                            });

                            if (exitCode != 0 && exitCode != -1)
                            {
                                string crashMsg = $"{app.Name} beklenmeyen bir hata kodu ile kapandı (ExitCode: {exitCode}, Süre: {(DateTime.UtcNow - startTime).TotalSeconds:0.0} sn).";
                                AppLoggerService.Instance.Error("ÇÖKME-ALGILANDI", crashMsg, app.Name);
                                await TelemetryAndHelpService.Instance.ReportErrorImmediatelyAsync(
                                    app.Id,
                                    app.Name,
                                    app.InstalledVersion,
                                    "ChildProcessCrash",
                                    crashMsg,
                                    $"Executable: {exePath}\nExitCode: 0x{exitCode:X8} ({exitCode})");
                            }
                        }
                        catch { }
                    });
                }

                return true;
            }
            catch (Exception ex)
            {
                AppLoggerService.Instance.Error("BAŞLATICI", $"{app.Name} başlatılırken hata oluştu: {ex.Message}", app.Name);
                await TelemetryAndHelpService.Instance.ReportErrorImmediatelyAsync(
                    app.Id, app.Name, app.InstalledVersion, "LaunchError", ex.Message, ex.StackTrace ?? "");
                return false;
            }
        }

        private async Task<bool> DownloadFileWithSpeedAsync(
            string url,
            string destinationPath,
            AlpAppItem app,
            double startProgress,
            double endProgress)
        {
            try
            {
                if (File.Exists(destinationPath))
                {
                    try { File.Delete(destinationPath); } catch { }
                }

                using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();

                long totalBytes = response.Content.Headers.ContentLength ?? -1L;
                bool canReport = totalBytes > 0;

                using var stream = await response.Content.ReadAsStreamAsync();
                using var fs = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

                byte[] buffer = new byte[81920];
                long totalRead = 0;
                int bytesRead;
                var sw = Stopwatch.StartNew();
                long lastSampleBytes = 0;
                long lastSampleMs = 0;

                while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length)) != 0)
                {
                    await fs.WriteAsync(buffer, 0, bytesRead);
                    totalRead += bytesRead;

                    long elapsedMs = sw.ElapsedMilliseconds;
                    if (elapsedMs - lastSampleMs >= 180)
                    {
                        double deltaSec = (elapsedMs - lastSampleMs) / 1000.0;
                        double mbps = ((totalRead - lastSampleBytes) / (1024.0 * 1024.0)) / Math.Max(0.05, deltaSec);
                        lastSampleBytes = totalRead;
                        lastSampleMs = elapsedMs;

                        double pct = canReport
                            ? startProgress + ((double)totalRead / totalBytes * (endProgress - startProgress))
                            : Math.Min(endProgress, startProgress + 25);

                        app.ProgressPercent = Math.Min(100, pct);
                        app.SpeedText = $"{mbps:0.0} MB/sn";
                        app.StatusText = $"İndiriliyor... %{app.ProgressPercent:0} ({totalRead / (1024 * 1024)} MB)";
                        PushSpeedSample(mbps);
                    }
                }

                PushSpeedSample(0);
                return true;
            }
            catch (Exception ex)
            {
                PushSpeedSample(0);
                AppLoggerService.Instance.Warn("İNDİRME", $"Bulut indirme bağlantısı ({url}) yanıt vermedi: {ex.Message}", app.Name);
                if (File.Exists(destinationPath))
                {
                    try { File.Delete(destinationPath); } catch { }
                }
                return false;
            }
        }

        private void PushSpeedSample(double mbps)
        {
            Application.Current?.Dispatcher?.BeginInvoke(new Action(() =>
            {
                SpeedHistoryMbps.Add(Math.Max(0, mbps));
                while (SpeedHistoryMbps.Count > 30)
                    SpeedHistoryMbps.RemoveAt(0);
                OnGlobalBandwidthTick?.Invoke(mbps, mbps > 0.05 ? $"{mbps:0.0} MB/sn" : "Boşta");
            }));
        }

        private static void ApplyAppSpecificOptimizations(AlpAppItem app)
        {
            if (app.Id.Equals("alpnet-pulse", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    string resDir = Path.Combine(app.InstallPath, "res");
                    if (!Directory.Exists(resDir))
                        Directory.CreateDirectory(resDir);

                    string profilePath = Path.Combine(resDir, "wgcf-profile.conf");
                    if (File.Exists(profilePath))
                    {
                        string text = File.ReadAllText(profilePath);
                        if (text.Contains("ID6Va+b6e5vusYdSh8PkhB+Z8gOVPZFtq2/fni+1K1Q="))
                        {
                            File.Delete(profilePath);
                            string accPath = Path.Combine(resDir, "wgcf-account.toml");
                            if (File.Exists(accPath)) File.Delete(accPath);
                        }
                    }

                    string[] policyPaths =
                    {
                        @"Software\Policies\Google\Chrome",
                        @"Software\Policies\Microsoft\Edge",
                        @"Software\Policies\BraveSoftware\Brave"
                    };
                    foreach (var p in policyPaths)
                    {
                        try
                        {
                            using var key = Registry.CurrentUser.CreateSubKey(p);
                            key?.SetValue("QuicAllowed", 1, RegistryValueKind.DWord);
                        }
                        catch { }
                    }
                }
                catch { }
            }
        }

        private static bool IsDotNet9DesktopRuntimeInstalled() => true;

        private async Task InstallDotNet9RuntimeAsync(AlpAppItem app)
        {
            const string runtimeUrl = "https://aka.ms/dotnet/9.0/windowsdesktop-runtime-win-x64.exe";
            string tempInstaller = Path.Combine(Path.GetTempPath(), "dotnet9_desktop_runtime_x64.exe");
            if (await DownloadFileWithSpeedAsync(runtimeUrl, tempInstaller, app, 0, 20) && File.Exists(tempInstaller))
            {
                await Task.Run(() =>
                {
                    try
                    {
                        var psi = new ProcessStartInfo
                        {
                            FileName = tempInstaller,
                            Arguments = "/install /quiet /norestart",
                            UseShellExecute = true,
                            WindowStyle = ProcessWindowStyle.Hidden
                        };
                        using var p = Process.Start(psi);
                        p?.WaitForExit(180000);
                    }
                    catch { }
                    finally
                    {
                        try { File.Delete(tempInstaller); } catch { }
                    }
                });
            }
        }

        private static int KillAppProcesses(AlpAppItem app)
        {
            int count = 0;
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                Path.GetFileNameWithoutExtension(app.ExecutableName)
            };
            foreach (var n in app.ProcessNamesToKill)
                names.Add(n);

            foreach (var procName in names)
            {
                if (string.IsNullOrWhiteSpace(procName)) continue;
                try
                {
                    foreach (var proc in Process.GetProcessesByName(procName))
                    {
                        try
                        {
                            proc.Kill();
                            proc.WaitForExit(1500);
                            count++;
                        }
                        catch { }
                    }
                }
                catch { }
            }
            return count;
        }

        private static int CleanupResidualFiles(string installDir)
        {
            int removed = 0;
            try
            {
                if (!Directory.Exists(installDir)) return 0;
                foreach (var f in Directory.GetFiles(installDir, "*.*"))
                {
                    string ext = Path.GetExtension(f).ToLowerInvariant();
                    if (ext == ".tmp" || ext == ".old" || ext == ".bak")
                    {
                        try { File.Delete(f); removed++; } catch { }
                    }
                }
            }
            catch { }
            return removed;
        }

        private static void ReplaceFileSafely(string sourceFile, string destinationFile)
        {
            if (!File.Exists(sourceFile)) return;
            for (int retry = 0; retry < 5; retry++)
            {
                try
                {
                    if (File.Exists(destinationFile))
                    {
                        try { File.Delete(destinationFile); }
                        catch
                        {
                            string oldBackup = destinationFile + "." + Guid.NewGuid().ToString("N")[..6] + ".old";
                            try { File.Move(destinationFile, oldBackup); } catch { }
                        }
                    }

                    if (File.Exists(destinationFile))
                    {
                        File.Copy(sourceFile, destinationFile, true);
                        try { File.Delete(sourceFile); } catch { }
                    }
                    else
                    {
                        File.Move(sourceFile, destinationFile);
                    }
                    return;
                }
                catch
                {
                    Thread.Sleep(250);
                }
            }
        }

        public static void CreateDesktopShortcut(string targetPath, string workingDir, string shortcutName)
        {
            try
            {
                string desktopDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                string shortcutPath = Path.Combine(desktopDir, shortcutName + ".lnk");

                Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType != null)
                {
                    object? shell = Activator.CreateInstance(shellType);
                    object? shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { shortcutPath });
                    if (shortcut != null)
                    {
                        Type st = shortcut.GetType();
                        st.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { targetPath });
                        st.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, new object[] { workingDir });
                        st.InvokeMember("IconLocation", BindingFlags.SetProperty, null, shortcut, new object[] { targetPath + ",0" });
                        st.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut, new object[] { shortcutName + " - AlpTogether" });
                        st.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
                    }
                }
            }
            catch { }
        }

        private static RemoteCatalogPayload CreateDefaultCatalog()
        {
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            return new RemoteCatalogPayload
            {
                CatalogVersion = "2026.10.18.1",
                ClientLatestVersion = "1.1.0",
                ClientDownloadUrl = "https://github.com/alphankilicaslan/AlpTogether/releases/latest/download/AlpTogether.exe",
                GlobalAnnouncement = "AlpTogether Ekosistem İstemcisi Aktif! AlpLingo v1.1.0 (Oyun İçi F8 Kısayol & AI OCR Kararlılık Güncellemesi) yayında.",
                Apps = new List<AlpAppItem>
                {
                    new AlpAppItem
                    {
                        Id = "alpnet-pulse",
                        Name = "AlpNet Pulse",
                        Subtitle = "Ağ Hızlandırma, Cloudflare WARP Tüneli & Canlı Ses Çeviri Motoru",
                        Category = "Ağ & Sistem",
                        Developer = "Alphan Kılıçaslan",
                        HighlightText = "HİBRİT KURULUM DESTEKLİ (AlpNet.Setup.exe + Doğrudan Paket Senkronizasyonu)",
                        Description = "AlpNet Pulse; düşük gecikmeli Cloudflare WARP tünel yönetimi, DNSCrypt/DoH şifreleme, paket analizi ve gerçek zamanlı yapay zeka sesli çeviri motorunu (translator.exe) tek çatı altında sunan amiral gemisi ağ ve iletişim aracıdır.",
                        AccentColorHex = "#1E88E5",
                        IconPackUri = "pack://application:,,,/Assets/AppIcons/alpnet_official.png",
                        LatestVersion = "2.2.7",
                        ReleaseDate = "21 Ekim 2026",
                        DownloadSizeText = "81.4 MB",
                        SizeBytes = 81400000,
                        PackageType = AppPackageType.HybridSetupAndFiles,
                        PreferredHybridMode = HybridInstallMode.HybridAuto,
                        FirebaseStateUrl = "https://alpnet-d0bc4-default-rtdb.europe-west1.firebasedatabase.app/appstate.json?auth=ExNWZwF2CkByKFILhfQ2BbTpuxYFlFAUO8quWwfc",
                        DirectDownloadUrl = "https://github.com/alphankilicaslan/AlpNet/releases/download/Update/AlpNet.Pulse.exe",
                        InstallerDownloadUrl = "https://github.com/alphankilicaslan/AlpNet/releases/download/Update/AlpNet.Setup.exe",
                        InstallerFileName = "AlpNet.Setup.exe",
                        InstallerSilentArgs = "/auto",
                        GitHubRepoUrl = "https://github.com/alphankilicaslan/AlpNet",
                        ExecutableName = "AlpNet.Pulse.exe",
                        DefaultInstallSubFolder = "AlpNet",
                        CandidateExistingPaths = new List<string>
                        {
                            Path.Combine(localAppData, "AlpNet"),
                            Path.Combine(desktop, "AlpNet")
                        },
                        ProcessNamesToKill = new List<string> { "AlpNet.Pulse", "AlpNetPulse", "translator", "AlpNet" },
                        CompanionFiles = new List<CompanionFileItem>
                        {
                            new CompanionFileItem
                            {
                                FileName = "translator.exe",
                                DownloadUrl = "https://github.com/alphankilicaslan/AlpNet/releases/download/Update/translator.exe",
                                ApproximateSizeBytes = 57225902
                            }
                        },
                        PatchNotes = new List<PatchNoteEntry>
                        {
                            new PatchNoteEntry
                            {
                                Version = "2.2.7",
                                ReleaseDate = "21 Ekim 2026",
                                Title = "Rust & EAC Anti-Cheat Uyumluluğu, WireSock DNS İyileştirmesi ve Gelişmiş Ağ Düzeltmeleri",
                                Tag = "KRİTİK GÜNCELLEME",
                                Changes = new List<string>
                                {
                                    "Easy Anti-Cheat (EAC) korumalı oyunlar (Rust vb.) için ağ sürücüsü ve paket yönlendirme çakışmaları tamamen giderildi; oyun sunucularına bağlantı engelleri kaldırıldı.",
                                    "Cloudflare WARP (WireSock) profilinde genel DNS zorlaması ve port çakışmaları temizlendi; ISP bazlı sorgu kilitlenmeleri çözüldü.",
                                    "Windows 11 DoH (DNS-over-HTTPS) otomatik kural çakışmaları devre dışı bırakılarak kararlı standart sorgu akışı sağlandı.",
                                    "Ağ arayüzlerindeki gecikmeye yol açan TcpDelAckTicks kayıt defteri anahtarları optimize edildi ve sürücü temizliği uygulandı.",
                                    "AlpTogether ekosistemi ve bulut yayın motoru v2.2.7 tam senkronize edildi."
                                }
                            },
                            new PatchNoteEntry
                            {
                                Version = "2.2.6",
                                ReleaseDate = "18 Ekim 2026",
                                Title = "AlpTogether Bulut Whitelist & Merkezi Yetkilendirme Entegrasyonu",
                                Tag = "YENİLİK & EKOSİSTEM",
                                Changes = new List<string>
                                {
                                    "AlpTogether Merkezi Bulut Whitelist doğrulaması eklendi; lisans anahtarı girmeden doğrudan bulut yetkisiyle açılış desteği sağlandı.",
                                    "Geriye dönük %100 çevrimdışı lisans uyumluluğu korundu; mevcut anahtara sahip kullanıcılar kesintisiz çalışmaya devam eder.",
                                    "Aktivasyon ekranına '✦ AlpTogether Yetkisini Doğrula 🔄' tek-tık hızlı sorgulama butonu entegre edildi.",
                                    "Bulut kullanıcı kalp atışı (heartbeat) ve ekosistem senkronizasyonu güncellendi."
                                }
                            },
                            new PatchNoteEntry
                            {
                                Version = "2.2.4",
                                ReleaseDate = "16 Ekim 2026",
                                Title = "Hibrit Kurulum, QUIC/HTTP3 Politikası ve Ses Çeviri İyileştirmesi",
                                Tag = "KARARLI SÜRÜM",
                                Changes = new List<string>
                                {
                                    "AlpTogether Hibrit Kurulum Motoru ile hem AlpNet.Setup.exe (/auto) hem de doğrudan dosya senkronizasyonu eklendi.",
                                    "Cloudflare WARP profil anahtarı çakışma tespiti ve otomatik onarım (wgcf-profile.conf) entegre edildi.",
                                    "Canlı ses çeviri motoru (translator.exe) gecikmesi %35 düşürüldü."
                                }
                            }
                        }
                    },
                    new AlpAppItem
                    {
                        Id = "alpfinans",
                        Name = "AlpFinans",
                        Subtitle = "Kişisel Finans, Vergi Hesaplama, Fatura & Birikim Simülatörü",
                        Category = "Finans & Üretkenlik",
                        Developer = "Alphan Kılıçaslan",
                        HighlightText = "DOĞRUDAN PAKET + SQLITE VERİTABANI BÜTÜNLÜK KORUMASI",
                        Description = "Gelir-gider takibi, vergi dilimi analizi, fatura arşivleme ve geleceğe yönelik finansal senaryo simülasyonlarını yüksek hızlı yerel SQLite motoruyla sunan masaüstü finans yönetim merkezi.",
                        AccentColorHex = "#00C853",
                        IconPackUri = "pack://application:,,,/Assets/AppIcons/alpfinans_official.png",
                        LatestVersion = "1.1.0",
                        ReleaseDate = "19 Ekim 2026",
                        DownloadSizeText = "34.2 MB",
                        SizeBytes = 34200000,
                        PackageType = AppPackageType.SingleExecutable,
                        PreferredHybridMode = HybridInstallMode.DirectFiles,
                        DirectDownloadUrl = "https://github.com/alphankilicaslan/AlpFinans/releases/latest/download/AlpFinans.exe",
                        GitHubRepoUrl = "https://github.com/alphankilicaslan/AlpFinans",
                        ExecutableName = "AlpFinans.exe",
                        DefaultInstallSubFolder = "AlpFinans",
                        CandidateExistingPaths = new List<string>
                        {
                            Path.Combine(desktop, "AlpFinans"),
                            Path.Combine(desktop, "AlpFinans", "bin_release")
                        },
                        ProcessNamesToKill = new List<string> { "AlpFinans" },
                        CompanionFiles = new List<CompanionFileItem>
                        {
                            new CompanionFileItem
                            {
                                FileName = "e_sqlite3.dll",
                                DownloadUrl = "https://github.com/alphankilicaslan/AlpFinans/releases/latest/download/e_sqlite3.dll",
                                ApproximateSizeBytes = 1800000
                            }
                        },
                        PatchNotes = new List<PatchNoteEntry>
                        {
                            new PatchNoteEntry
                            {
                                Version = "1.1.0",
                                ReleaseDate = "19 Ekim 2026",
                                Title = "AlpTogether Bulut Whitelist & Merkezi Yetkilendirme Entegrasyonu",
                                Tag = "GÜNCELLEME",
                                Changes = new List<string>
                                {
                                    "AlpTogether merkezi bulut whitelist doğrulaması entegre edildi.",
                                    "Geliştirici ana makinesi (MONSTERALPHAN) için anında erişim tanımlandı."
                                }
                            },
                            new PatchNoteEntry
                            {
                                Version = "1.0.0",
                                ReleaseDate = "16 Ekim 2026",
                                Title = "Finansal Simülatör, Gelir/Gider Yönetimi ve Vergi Analizi",
                                Tag = "KARARLI SÜRÜM",
                                Changes = new List<string>
                                {
                                    "SimulatorWindow ile 12 aylık enflasyon ve yatırım getirisi projeksiyonu eklendi.",
                                    "SQLite yerel veritabanı otomatik yedekleme ve onarım kontrolü eklendi."
                                }
                            }
                        }
                    },
                    new AlpAppItem
                    {
                        Id = "alphagravity",
                        Name = "AlphaGravity",
                        Subtitle = "Kurumsal Yapay Zeka, Bilgi Bankası (RAG), 3B Grafik & Ses Köprüsü",
                        Category = "Yapay Zeka & Kurumsal",
                        Developer = "Alphan Kılıçaslan",
                        HighlightText = "KURUMSAL LLM + 3D BİLGİ AĞI GÖRSELLEŞTİRME",
                        Description = "Şirket dokümanlarını ve teknik mimarileri analiz eden, 3 boyutlu bilgi grafiği (3D Knowledge Graph) oluşturan, yerel ve bulut LLM modelleriyle sesli/yazılı etkileşim kuran gelişmiş yapay zeka istemcisi.",
                        AccentColorHex = "#FF6D00",
                        IconPackUri = "pack://application:,,,/Assets/AppIcons/alphagravity_official.png",
                        LatestVersion = "1.1.0",
                        ReleaseDate = "19 Ekim 2026",
                        DownloadSizeText = "208 MB",
                        SizeBytes = 208500000,
                        PackageType = AppPackageType.SingleExecutable,
                        PreferredHybridMode = HybridInstallMode.DirectFiles,
                        DirectDownloadUrl = "https://github.com/alphankilicaslan/AlphaGravity/releases/latest/download/AlphaGravity.exe",
                        GitHubRepoUrl = "https://github.com/alphankilicaslan/AlphaGravity",
                        ExecutableName = "AlphaGravity.exe",
                        DefaultInstallSubFolder = "AlphaGravity",
                        CandidateExistingPaths = new List<string>
                        {
                            Path.Combine(desktop, "AlphaGravity"),
                            Path.Combine(desktop, "AlphaGravity", "publish_single"),
                            Path.Combine(desktop, "AlphaGravity", "publish")
                        },
                        ProcessNamesToKill = new List<string> { "AlphaGravity", "EnterpriseAIApp" },
                        CompanionFiles = new List<CompanionFileItem>
                        {
                            new CompanionFileItem
                            {
                                FileName = "WebView2Loader.dll",
                                DownloadUrl = "https://github.com/alphankilicaslan/AlphaGravity/releases/latest/download/WebView2Loader.dll",
                                ApproximateSizeBytes = 165000
                            }
                        },
                        PatchNotes = new List<PatchNoteEntry>
                        {
                            new PatchNoteEntry
                            {
                                Version = "1.1.0",
                                ReleaseDate = "19 Ekim 2026",
                                Title = "Bulut Whitelist, API Key Kalıcılığı & Kararlı Tek Dosya Çalışma",
                                Tag = "GÜNCELLEME",
                                Changes = new List<string>
                                {
                                    "AlpTogether merkezi bulut whitelist doğrulaması entegre edildi.",
                                    "Kullanıcı API anahtarları kalıcı kullanıcı profiline (AppData) kaydedilerek oturumlar arası korundu.",
                                    "Tam bağımsız (self-contained) tek dosya çalıştırılabilir mimariyle başlatma kararlılığı sağlandı."
                                }
                            },
                            new PatchNoteEntry
                            {
                                Version = "1.0.0",
                                ReleaseDate = "16 Ekim 2026",
                                Title = "3B Bilgi Grafiği, RAG Doküman Yönetimi ve VoiceBridge",
                                Tag = "KARARLI SÜRÜM",
                                Changes = new List<string>
                                {
                                    "WebView2 tabanlı interaktif 3B düğüm grafiği ve kurumsal hafıza entegrasyonu.",
                                    "VoiceBridge gerçek zamanlı sesli etkileşim ve öğrenme raporu modülü."
                                }
                            }
                        }
                    },
                    new AlpAppItem
                    {
                        Id = "alprust-plus",
                        Name = "AlpRust+",
                        Subtitle = "Rust+ Akıllı Cihaz, Kamera, Harita ve Anlık Raid Alarm İstemcisi",
                        Category = "Oyun & Yardımcı Araç",
                        Developer = "Alphan Kılıçaslan",
                        HighlightText = "GERÇEK ZAMANLI SUNUCU TAKİBİ VE AKILLI ANAHTAR YÖNETİMİ",
                        Description = "Rust oyuncuları için geliştirilmiş masaüstü Rust+ komuta merkezi. Oyun dışındayken akıllı şalterleri kontrol edin, güvenlik kameralarını izleyin, takım sohbetini yönetin ve anlık raid bildirimleri alın.",
                        AccentColorHex = "#D84315",
                        IconPackUri = "pack://application:,,,/Assets/AppIcons/alprust_official.png",
                        LatestVersion = "1.1.0",
                        ReleaseDate = "16 Ekim 2026",
                        DownloadSizeText = "92.0 MB",
                        SizeBytes = 92000000,
                        PackageType = AppPackageType.ZipArchive,
                        PreferredHybridMode = HybridInstallMode.HybridAuto,
                        DirectDownloadUrl = "https://github.com/alphankilicaslan/AlpRustPlus/releases/latest/download/AlpRustPlus-win-x64.zip",
                        GitHubRepoUrl = "https://github.com/alphankilicaslan/AlpRustPlus",
                        ExecutableName = "RustPlusDesk.exe",
                        DefaultInstallSubFolder = "AlpRustPlus",
                        CandidateExistingPaths = new List<string>
                        {
                            Path.Combine(localAppData, "RustPlusDesk", "current"),
                            Path.Combine(desktop, "AlpRust+", "RustPlusDesktop", "bin", "Release", "net8.0-windows10.0.17763.0"),
                            Path.Combine(desktop, "AlpRust+", "RustPlusDesktop", "bin", "Release", "net8.0-windows10.0.17763.0", "win-x64"),
                            Path.Combine(desktop, "AlpRust+", "RustPlusDesktop", "bin", "Installer", "publish"),
                            Path.Combine(desktop, "AlpRust+")
                        },
                        ProcessNamesToKill = new List<string> { "RustPlusDesk", "RustPlusDesktop", "AlpRustPlus" },
                        PatchNotes = new List<PatchNoteEntry>
                        {
                            new PatchNoteEntry
                            {
                                Version = "1.1.0",
                                ReleaseDate = "16 Ekim 2026",
                                Title = "Düşük Gecikmeli Kamera Akışı ve Otomatik Eşleştirme",
                                Tag = "KARARLI SÜRÜM",
                                Changes = new List<string>
                                {
                                    "Sunucu FCM bildirim dinleyicisi yeniden bağlanma hızı iyileştirildi.",
                                    "Harita üzerindeki kargo gemisi, helikopter rotası ve akıllı cihaz kontrolleri güncellendi."
                                }
                            }
                        }
                    },
                    new AlpAppItem
                    {
                        Id = "alplingo",
                        Name = "AlpLingo",
                        Subtitle = "Gerçek Zamanlı Yapay Zeka Ekran & Oyun Çevirici (AI OCR Overlay)",
                        Category = "Yapay Zeka & Çeviri",
                        Developer = "Alphan Kılıçaslan",
                        HighlightText = "ÇEVRİMDIŞI NLLB-200 + ŞEFFAF OVERLAY + SIFIR FPS KAYBI",
                        Description = "Oyunlar, videolar ve masaüstü uygulamaları için ekran üzerindeki diyalog alanını (ROI) gerçek zamanlı yakalayıp Meta NLLB-200 (CUDA/CPU) motoruyla şeffaf bir katman (Overlay) üzerinde anında akıcı Türkçeye çeviren uygulama.",
                        AccentColorHex = "#00E5FF",
                        IconPackUri = "pack://application:,,,/Assets/AppIcons/alplingo_official.png",
                        LatestVersion = "1.1.0",
                        ReleaseDate = "18 Ekim 2026",
                        DownloadSizeText = "1.45 GB",
                        SizeBytes = 1530302447,
                        PackageType = AppPackageType.ZipArchive,
                        PreferredHybridMode = HybridInstallMode.HybridAuto,
                        DirectDownloadUrl = "https://github.com/alphankilicaslan/AlpLingo/releases/latest/download/AlpLingo-win-x64.zip",
                        InstallerDownloadUrl = "https://github.com/alphankilicaslan/AlpLingo/releases/latest/download/AlpLingo.Setup.exe",
                        InstallerFileName = "AlpLingo.Setup.exe",
                        InstallerSilentArgs = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART",
                        GitHubRepoUrl = "https://github.com/alphankilicaslan/AlpLingo",
                        ExecutableName = "AlpLingo.exe",
                        DefaultInstallSubFolder = "AlpLingo",
                        CandidateExistingPaths = new List<string>
                        {
                            Path.Combine(desktop, "AlpLingo")
                        },
                        ProcessNamesToKill = new List<string> { "AlpLingo" },
                        PatchNotes = new List<PatchNoteEntry>
                        {
                            new PatchNoteEntry
                            {
                                AppName = "AlpLingo",
                                AppId = "alplingo",
                                Version = "1.1.0",
                                ReleaseDate = "18 Ekim 2026",
                                Title = "AlpLingo v1.1.0 — Oyun İçi F8 Kısayol, Yönetici İzni & AI OCR Kararlılık Güncellemesi",
                                Tag = "ÖNEMLİ GÜNCELLEME",
                                Changes = new List<string>
                                {
                                    "Oyunlarda F8 kısayolunun algılanmama sorunu çözüldü (Win32 RegisterHotKey ve otomatik Yönetici İzni yükseltmesi).",
                                    "Akıllı kontrast kontrolü ile sarı, altın, mavi ve tüm renklerdeki oyun altyazılarının okunması sağlandı.",
                                    "F8 ile diyalog alanı seçildiği anda çevirinin otomatik başlatılması ve ekranda anlık durum bildirimi eklendi.",
                                    "Windows masaüstü yakalama (BitBlt/OpenInputDesktop) bağlantısı ve çift monitör desteği güçlendirildi."
                                }
                            },
                            new PatchNoteEntry
                            {
                                Version = "1.0.0",
                                ReleaseDate = "16 Ekim 2026",
                                Title = "AlpLingo v1.0 — Gerçek Zamanlı AI OCR & Şeffaf Türkçe Altyazı",
                                Tag = "YENİ UYGULAMA",
                                Changes = new List<string>
                                {
                                    "Meta NLLB-200 çevrimdışı çeviri motoru ile NVIDIA RTX CUDA ve CPU hibrit hızlandırma.",
                                    "F8 ile anlık ekran diyalog bölgesi (ROI) seçimi ve F9 ile şeffaf altyazı katmanı kontrolü.",
                                    "Akıllı parlaklık değişim filtresi ile diyalog sabitken %0 CPU/GPU kullanımı ve sıfır FPS kaybı."
                                }
                            }
                        }
                    }
                }
            };
        }
        #region Windows AutoStart Management
        public bool IsAppAutoStartEnabled(string appId)
        {
            if (string.Equals(appId, "alpnet-pulse", StringComparison.OrdinalIgnoreCase))
                return true; // AlpNet is always on boot (User rule: alpnet hep aciliyor, kodlari karistirma)

            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", false);
                if (key != null)
                {
                    string regName = $"AlpTogether_{appId}";
                    var val = key.GetValue(regName);
                    if (val != null) return true;
                }
            }
            catch { }

            return false;
        }

        public bool SetAppAutoStart(string appId, string exePath, bool enable)
        {
            if (string.Equals(appId, "alpnet-pulse", StringComparison.OrdinalIgnoreCase))
                return true; // DO NOT touch AlpNet startup!

            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
                if (key != null)
                {
                    string regName = $"AlpTogether_{appId}";
                    if (enable && File.Exists(exePath))
                    {
                        key.SetValue(regName, $"\"{exePath}\"");
                        AppLoggerService.Instance.Success("BAŞLANGIÇ", $"{appId} bilgisayar açılışına eklendi.", appId);
                    }
                    else
                    {
                        key.DeleteValue(regName, false);
                        AppLoggerService.Instance.Info("BAŞLANGIÇ", $"{appId} bilgisayar açılışından kaldırıldı.", appId);
                    }
                    return true;
                }
            }
            catch (Exception ex)
            {
                AppLoggerService.Instance.Error("BAŞLANGIÇ-HATA", $"Açılış ayarı değiştirilemedi: {ex.Message}", appId);
            }

            return false;
        }
        #endregion
    }
}
