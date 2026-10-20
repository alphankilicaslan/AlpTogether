using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace AlpTogether.Services
{
    public class PublisherPreset
    {
        public string Tag { get; set; } = string.Empty;
        public string AppName { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public string ExeName { get; set; } = string.Empty;
        public string LocalPath { get; set; } = string.Empty;
        public string SizeText { get; set; } = string.Empty;
        public string DirectUrl { get; set; } = string.Empty;
        public string InstallerUrl { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string PatchNotes { get; set; } = string.Empty;
        public string LastPublishedAt { get; set; } = string.Empty;
    }

    public class PublisherPresetService
    {
        public static PublisherPresetService Instance { get; } = new();

        private readonly string _presetsFilePath;
        private Dictionary<string, PublisherPreset> _presets = new(StringComparer.OrdinalIgnoreCase);

        private PublisherPresetService()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string alpTogetherDir = Path.Combine(appData, "AlpTogether");
            if (!Directory.Exists(alpTogetherDir))
            {
                Directory.CreateDirectory(alpTogetherDir);
            }
            _presetsFilePath = Path.Combine(alpTogetherDir, "publisher_presets.json");
            LoadPresets();
        }

        public void LoadPresets()
        {
            try
            {
                if (File.Exists(_presetsFilePath))
                {
                    string json = File.ReadAllText(_presetsFilePath);
                    var dict = JsonSerializer.Deserialize<Dictionary<string, PublisherPreset>>(json);
                    if (dict != null)
                    {
                        _presets = new Dictionary<string, PublisherPreset>(dict, StringComparer.OrdinalIgnoreCase);
                    }
                }
            }
            catch { }
        }

        public void SavePresets()
        {
            try
            {
                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(_presets, options);
                File.WriteAllText(_presetsFilePath, json);
            }
            catch { }
        }

        public void SavePreset(PublisherPreset preset)
        {
            if (string.IsNullOrWhiteSpace(preset.Tag)) return;
            _presets[preset.Tag] = preset;
            SavePresets();
        }

        public PublisherPreset GetPreset(string tag)
        {
            if (_presets.TryGetValue(tag, out var p))
            {
                ProbeAndRefreshFromDisk(p);
                return p;
            }

            var defaultPreset = CreateDefaultPreset(tag);
            ProbeAndRefreshFromDisk(defaultPreset);
            _presets[tag] = defaultPreset;
            return defaultPreset;
        }

        public static void ProbeAndRefreshFromDisk(PublisherPreset preset)
        {
            try
            {
                string? validPath = null;
                if (!string.IsNullOrWhiteSpace(preset.LocalPath) && File.Exists(preset.LocalPath))
                {
                    validPath = preset.LocalPath;
                }
                else
                {
                    var candidates = GetCandidatePaths(preset.Tag);
                    validPath = candidates.FirstOrDefault(File.Exists);
                }

                if (!string.IsNullOrEmpty(validPath))
                {
                    preset.LocalPath = validPath;
                    var fi = new FileInfo(validPath);
                    double mb = fi.Length / (1024.0 * 1024.0);
                    preset.SizeText = $"{mb:0.1} MB";

                    try
                    {
                        var fvi = FileVersionInfo.GetVersionInfo(validPath);
                        if (!string.IsNullOrWhiteSpace(fvi.FileVersion))
                        {
                            preset.Version = fvi.FileVersion.Trim();
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        public static List<string> GetCandidatePaths(string tag)
        {
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            return tag.ToLowerInvariant() switch
            {
                "alpnet" => new List<string>
                {
                    Path.Combine(desktop, "AlpNet", "SetupProject", "AlpNet.Pulse.exe"),
                    Path.Combine(desktop, "AlpNet", "AlpNetPulse_Release_New", "AlpNetPulse.exe"),
                    Path.Combine(desktop, "AlpNet", "publish_pulse", "AlpNetPulse.exe"),
                    Path.Combine(desktop, "AlpNet", "AlpNetPulse", "bin", "Release", "net9.0-windows", "AlpNetPulse.exe")
                },
                "alplingo" => new List<string>
                {
                    Path.Combine(desktop, "AlpLingo", "AlpLingo.exe")
                },
                "alphagravity" => new List<string>
                {
                    Path.Combine(desktop, "AlphaGravity", "publish", "AlphaGravity.exe"),
                    Path.Combine(desktop, "AlphaGravity", "AlphaGravity.exe"),
                    Path.Combine(desktop, "AlphaGravity", "publish_single", "AlphaGravity.exe"),
                    Path.Combine(desktop, "AlphaGravity", "bin", "Release", "net9.0-windows", "AlphaGravity.exe")
                },
                "alpfinans" => new List<string>
                {
                    Path.Combine(desktop, "AlpFinans", "AlpFinans.exe"),
                    Path.Combine(desktop, "AlpFinans", "bin_release", "AlpFinans.exe"),
                    Path.Combine(desktop, "AlpFinans", "AlpFinans_CS", "bin", "Release", "net9.0-windows", "AlpFinans.exe")
                },
                "alprust-plus" => new List<string>
                {
                    Path.Combine(desktop, "AlpRust+", "RustPlusDesktop", "bin", "Installer", "publish", "RustPlusDesk.exe"),
                    Path.Combine(desktop, "AlpRust+", "RustPlusDesktop", "bin", "Release", "net8.0-windows10.0.17763.0", "RustPlusDesk.exe")
                },
                _ => new List<string>()
            };
        }

        private static PublisherPreset CreateDefaultPreset(string tag)
        {
            return tag.ToLowerInvariant() switch
            {
                "alpnet" => new PublisherPreset
                {
                    Tag = "alpnet",
                    AppName = "AlpNet Pulse",
                    Version = "2.2.5",
                    Category = "Ağ, WARP & Çeviri",
                    ExeName = "AlpNet.Pulse.exe",
                    SizeText = "28.5 MB",
                    DirectUrl = "https://github.com/alphankilicaslan/AlpNet/releases/download/v2.2.5/AlpNet.Pulse.exe",
                    InstallerUrl = "",
                    Description = "Cloudflare WARP tüneli, ultra düşük gecikmeli VPN ve gerçek zamanlı sesli diyalog çevirmeni.",
                    PatchNotes = "Wiresock hızlı başlatma entegrasyonu (hizmet tekrar kurulmadan anında başlar).\n25 saniyelik otomatik watchdog heartbeat ve tünel onarımı eklendi.\nAlternatif port geçişleri (500 -> 2408 -> 4500 -> 1701) devrede."
                },
                "alplingo" => new PublisherPreset
                {
                    Tag = "alplingo",
                    AppName = "AlpLingo",
                    Version = "1.4.0",
                    Category = "Oyun & AI Çeviri",
                    ExeName = "AlpLingo.exe",
                    SizeText = "42.0 MB",
                    DirectUrl = "https://github.com/alphankilicaslan/AlpLingo/releases/download/v1.4.0/AlpLingo.exe",
                    InstallerUrl = "",
                    Description = "Yapay zeka ve OCR destekli anlık oyun içi ekran ve ses çevirmeni.",
                    PatchNotes = "Yüksek FPS oyunlar için OCR performans iyileştirmesi.\nBellek tüketimi düşürüldü ve pencere yakalama hızlandırıldı."
                },
                "alphagravity" => new PublisherPreset
                {
                    Tag = "alphagravity",
                    AppName = "AlphaGravity",
                    Version = "1.1.0",
                    Category = "Yapay Zeka & Asistan",
                    ExeName = "AlphaGravity.exe",
                    SizeText = "35.0 MB",
                    DirectUrl = "https://github.com/alphankilicaslan/AlphaGravity/releases/download/v1.1.0/AlphaGravity.exe",
                    InstallerUrl = "",
                    Description = "Gelişmiş çoklu yapay zeka modeli entegrasyonu ve kodlama asistanı.",
                    PatchNotes = "Çoklu model prompt şablonları eklendi.\nAPI yanıt süreleri optimize edildi."
                },
                "alpfinans" => new PublisherPreset
                {
                    Tag = "alpfinans",
                    AppName = "AlpFinans",
                    Version = "1.0.5",
                    Category = "Borsa & Finans",
                    ExeName = "AlpFinans.exe",
                    SizeText = "22.0 MB",
                    DirectUrl = "https://github.com/alphankilicaslan/AlpFinans/releases/download/v1.0.5/AlpFinans.exe",
                    InstallerUrl = "",
                    Description = "Canlı borsa, kripto para ve portföy takip istasyonu.",
                    PatchNotes = "Gerçek zamanlı fiyat uyarıları ve portföy grafikleri güncellendi."
                },
                "alprust-plus" => new PublisherPreset
                {
                    Tag = "alprust-plus",
                    AppName = "AlpRust+",
                    Version = "1.0.2",
                    Category = "Oyun & Güvenlik",
                    ExeName = "RustPlusDesk.exe",
                    SizeText = "19.5 MB",
                    DirectUrl = "https://github.com/alphankilicaslan/AlpRustPlus/releases/download/v1.0.2/RustPlusDesk.exe",
                    InstallerUrl = "",
                    Description = "Rust+ masaüstü kamera izleme, akıllı anahtarlar ve anlık raid alarmı.",
                    PatchNotes = "Baskın (raid) sesli alarm motoru güçlendirildi.\nKamera kare yenileme hızı artırıldı."
                },
                _ => new PublisherPreset
                {
                    Tag = tag,
                    AppName = "Yeni Uygulama",
                    Version = "1.0.0",
                    Category = "Araçlar",
                    ExeName = "App.exe",
                    SizeText = "20.0 MB",
                    DirectUrl = "",
                    InstallerUrl = "",
                    Description = "AlpTogether ekosistemi için yeni nesil masaüstü uygulaması.",
                    PatchNotes = "İlk kararlı sürüm yayınlandı."
                }
            };
        }
    }
}
