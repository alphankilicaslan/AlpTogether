using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AlpTogether.Models;

namespace AlpTogether.Services
{
    public class SettingsService
    {
        private static readonly object _fileLock = new();
        public static SettingsService Instance { get; } = new();

        public ClientSettings Current { get; private set; } = new();
        public string DeviceId { get; private set; } = string.Empty;

        private readonly string _appDataDir;
        private readonly string _settingsFilePath;

        private SettingsService()
        {
            _appDataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AlpTogether");

            if (!Directory.Exists(_appDataDir))
                Directory.CreateDirectory(_appDataDir);

            _settingsFilePath = Path.Combine(_appDataDir, "settings.json");
            DeviceId = ComputeStableDeviceId();
            Load();
        }

        private static string ComputeStableDeviceId()
        {
            try
            {
                string raw = $"{Environment.MachineName}_{Environment.UserName}_{Environment.ProcessorCount}_{Environment.OSVersion.Version}";
                byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
                return "ALP-" + Convert.ToHexString(bytes)[..12];
            }
            catch
            {
                return "ALP-" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
            }
        }

        public void Load()
        {
            lock (_fileLock)
            {
                try
                {
                    if (File.Exists(_settingsFilePath))
                    {
                        string json = File.ReadAllText(_settingsFilePath, Encoding.UTF8);
                        if (!string.IsNullOrWhiteSpace(json))
                        {
                            var loaded = JsonSerializer.Deserialize<ClientSettings>(json, new JsonSerializerOptions
                            {
                                PropertyNameCaseInsensitive = true
                            });
                            if (loaded != null)
                                Current = loaded;
                        }
                    }
                    else
                    {
                        string bakPath = _settingsFilePath + ".bak";
                        if (File.Exists(bakPath))
                        {
                            string json = File.ReadAllText(bakPath, Encoding.UTF8);
                            var loaded = JsonSerializer.Deserialize<ClientSettings>(json, new JsonSerializerOptions
                            {
                                PropertyNameCaseInsensitive = true
                            });
                            if (loaded != null)
                                Current = loaded;
                        }
                    }
                }
                catch (Exception ex)
                {
                    AppLoggerService.Instance.Warn("AYARLAR", "Ayarlar dosyası okunamadı, yedek kontrol ediliyor.", "AlpTogether", ex.Message);
                    try
                    {
                        string bakPath = _settingsFilePath + ".bak";
                        if (File.Exists(bakPath))
                        {
                            string json = File.ReadAllText(bakPath, Encoding.UTF8);
                            var loaded = JsonSerializer.Deserialize<ClientSettings>(json, new JsonSerializerOptions
                            {
                                PropertyNameCaseInsensitive = true
                            });
                            if (loaded != null)
                                Current = loaded;
                        }
                    }
                    catch { }
                }

                if (string.IsNullOrWhiteSpace(Current.DefaultInstallRoot))
                {
                    Current.DefaultInstallRoot = Path.Combine(_appDataDir, "Apps");
                }

                try
                {
                    if (!Directory.Exists(Current.DefaultInstallRoot))
                        Directory.CreateDirectory(Current.DefaultInstallRoot);
                }
                catch { }

                if (Current.CustomSlotItems == null || Current.CustomSlotItems.Count == 0)
                {
                    Current.CustomSlotItems = new()
                    {
                        new CustomWorkspaceSlotItem
                        {
                            Title = "Hibrit Güncelleme & Setup Notu",
                            Tag = "MİMARİ",
                            Content = "Kendi kurulum arayüzü olan uygulamalar (Örn: AlpNet.Setup.exe) hem Birleşik (/auto) hem de Ayrı Pencere modunda çalıştırılabilir.",
                            ActionCommand = "mode:hybrid"
                        },
                        new CustomWorkspaceSlotItem
                        {
                            Title = "Anlık Hata Telemetrisi Aktif",
                            Tag = "CANLI İZLEME",
                            Content = "Kullanıcılar herhangi bir uygulamada çökme veya indirme hatası aldığında Firebase ve Webhook üzerinden anında rapor düşer.",
                            ActionCommand = "view:help"
                        },
                        new CustomWorkspaceSlotItem
                        {
                            Title = "Özel Modül / Eklenti Yuvası #1",
                            Tag = "SONRADAN DEĞİŞTİRİLEBİLİR",
                            Content = "Bu kart ve sağdaki serbest log/not alanı ileride eklenecek yeni duyuru, sunucu durumu veya özel araçlar için hazır bırakılmıştır.",
                            ActionCommand = "custom:slot"
                        }
                    };
                }
            }
        }

        public void Save()
        {
            lock (_fileLock)
            {
                try
                {
                    string json = JsonSerializer.Serialize(Current, new JsonSerializerOptions
                    {
                        WriteIndented = true
                    });

                    string tempPath = _settingsFilePath + ".tmp";
                    string bakPath = _settingsFilePath + ".bak";

                    File.WriteAllText(tempPath, json, Encoding.UTF8);

                    if (File.Exists(_settingsFilePath))
                    {
                        try { File.Copy(_settingsFilePath, bakPath, true); } catch { }
                    }

                    File.Move(tempPath, _settingsFilePath, true);
                }
                catch (Exception ex)
                {
                    AppLoggerService.Instance.Error("AYARLAR", "Ayarlar kaydedilirken hata oluştu.", "AlpTogether", ex.Message);
                }
            }
        }
    }
}
