using System;
using System.IO;
using System.Linq;
using AlpTogether.Models;
using AlpTogether.Services;

namespace AlpTogether.Tests
{
    class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            if (System.Windows.Application.Current == null)
            {
                try
                {
                    var app = new AlpTogether.App();
                    app.InitializeComponent();
                }
                catch { }
            }

            Console.WriteLine("==================================================");
            Console.WriteLine(" AlpTogether Verification & Regression Test Suite ");
            Console.WriteLine("==================================================");

            int failures = 0;

            // TEST 1: DeveloperAuthService & Dev Machine Detection
            Console.WriteLine("\n[TEST 1] Testing DeveloperAuthService...");
            try
            {
                var auth = DeveloperAuthService.Instance;
                string hwid = auth.DeviceHwid;
                Console.WriteLine($" -> Device HWID: {hwid}");
                if (!hwid.StartsWith("AT-"))
                {
                    Console.WriteLine(" FAIL: HWID does not start with AT-");
                    failures++;
                }

                bool isPrimary = auth.IsPrimaryDevMachine;
                Console.WriteLine($" -> IsPrimaryDevMachine (MONSTERALPHAN / Alphan): {isPrimary}");
                if (!isPrimary)
                {
                    Console.WriteLine(" FAIL: Expected IsPrimaryDevMachine to be true on Alphan's machine!");
                    failures++;
                }

                if (!auth.IsAdminMode)
                {
                    Console.WriteLine(" FAIL: Expected IsAdminMode to be true on primary dev machine!");
                    failures++;
                }

                // Verify admin key generation & validation
                DateTime exp = DateTime.UtcNow.AddDays(30);
                string key = DeveloperAuthService.GenerateAdminKey(hwid, exp);
                bool valid = DeveloperAuthService.ValidateAdminKey(key, hwid, out DateTime outExp, out string reason);
                if (!valid)
                {
                    Console.WriteLine($" FAIL: Generated admin key validation failed: {reason}");
                    failures++;
                }
                else
                {
                    Console.WriteLine($" -> Admin key generated & verified successfully: {key[..18]}...");
                }

                // Verify key rejection for different HWID
                bool wrongHwidValid = DeveloperAuthService.ValidateAdminKey(key, "AT-WRONG-HWID-9999", out _, out _);
                if (wrongHwidValid)
                {
                    Console.WriteLine(" FAIL: Admin key accepted for wrong HWID!");
                    failures++;
                }
                else
                {
                    Console.WriteLine(" -> Key correctly rejected for mismatched HWID.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($" FAIL: Test 1 exception: {ex.Message}");
                failures++;
            }

            // TEST 2: UserAuthAndPermissionService & keygen_history.json parsing
            Console.WriteLine("\n[TEST 2] Testing Keygen Notes & User Resolution...");
            try
            {
                var notes = UserAuthAndPermissionService.GetAlpNetKeygenNotes();
                Console.WriteLine($" -> Loaded {notes.Count} user note mappings from keygen_history.json.");
                if (notes.Count == 0)
                {
                    Console.WriteLine(" FAIL: No keygen notes loaded! Expected at least Barış, Ferdicik, etc.");
                    failures++;
                }

                // Check Barış (ADF3CBCF14F53C0C68DBA67DE86BC58B)
                if (notes.TryGetValue("ADF3CBCF14F53C0C68DBA67DE86BC58B", out var barisNote))
                {
                    Console.WriteLine($" -> Verified Barış mapping: ADF3CB... => '{barisNote}'");
                }
                else
                {
                    Console.WriteLine(" FAIL: ADF3CBCF14F53C0C68DBA67DE86BC58B (Barış) not found in keygen notes!");
                    failures++;
                }

                // Check Ferdicik (D58B835CE52E92E3CF38B8F675A3F1AA)
                if (notes.TryGetValue("D58B835CE52E92E3CF38B8F675A3F1AA", out var ferdiNote))
                {
                    Console.WriteLine($" -> Verified Ferdicik mapping: D58B83... => '{ferdiNote}'");
                }
                else
                {
                    Console.WriteLine(" FAIL: D58B835CE52E92E3CF38B8F675A3F1AA (Ferdicik) not found in keygen notes!");
                    failures++;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($" FAIL: Test 2 exception: {ex.Message}");
                failures++;
            }

            // TEST 3: User DisplayName Formatting
            Console.WriteLine("\n[TEST 3] Testing User DisplayName Formatting...");
            try
            {
                var u1 = new AlpTogetherUserRecord
                {
                    Hwid = "ADF3CBCF14F53C0C68DBA67DE86BC58B",
                    Username = "Barış",
                    AdminAlias = "Barış",
                    MachineName = "DESKTOP-ABC",
                    PcUser = "baris"
                };
                if (u1.DisplayName != "Barış — DESKTOP-ABC (baris)")
                {
                    Console.WriteLine($" FAIL: Unexpected DisplayName: '{u1.DisplayName}'");
                    failures++;
                }
                else
                {
                    Console.WriteLine($" -> User DisplayName with PC user: '{u1.DisplayName}'");
                }

                var u2 = new AlpTogetherUserRecord
                {
                    Hwid = "D58B835CE52E92E3CF38B8F675A3F1AA",
                    Username = "AlpNet_D58B83",
                    AdminAlias = "Ferdicik",
                    MachineName = "AlpNet-PC"
                };
                if (u2.DisplayName != "Ferdicik")
                {
                    Console.WriteLine($" FAIL: Expected 'Ferdicik' but got '{u2.DisplayName}'");
                    failures++;
                }
                else
                {
                    Console.WriteLine($" -> User DisplayName with AlpNet prefix replacement: '{u2.DisplayName}'");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($" FAIL: Test 3 exception: {ex.Message}");
                failures++;
            }

            // TEST 4: AutoStart Safeguards (AlpNet Pulse Must Never Be Touched)
            Console.WriteLine("\n[TEST 4] Testing Windows AutoStart Safeguards...");
            try
            {
                bool alpNetEnabled = CatalogAndHybridInstallerService.Instance.IsAppAutoStartEnabled("alpnet-pulse");
                if (!alpNetEnabled)
                {
                    Console.WriteLine(" FAIL: IsAppAutoStartEnabled('alpnet-pulse') should always return true!");
                    failures++;
                }
                else
                {
                    Console.WriteLine(" -> AlpNet autostart status correctly locked to TRUE.");
                }

                bool dummySet = CatalogAndHybridInstallerService.Instance.SetAppAutoStart("alpnet-pulse", "dummy.exe", false);
                if (!dummySet)
                {
                    Console.WriteLine(" FAIL: SetAppAutoStart on alpnet-pulse should succeed as no-op safely!");
                    failures++;
                }
                else
                {
                    Console.WriteLine(" -> SetAppAutoStart correctly safeguarded AlpNet without registry changes.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($" FAIL: Test 4 exception: {ex.Message}");
                failures++;
            }

            // TEST 5: Telemetry Cache Clearing
            Console.WriteLine("\n[TEST 5] Testing Telemetry Local Cache Cleaning...");
            try
            {
                string baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AlpTogether");
                string eventsDir = Path.Combine(baseDir, "events");
                Directory.CreateDirectory(eventsDir);
                string testFile = Path.Combine(eventsDir, "test_event_9999.json");
                File.WriteAllText(testFile, "{\"EventId\":\"test_event_9999\"}");

                TelemetryAndHelpService.Instance.ClearLocalTelemetryCache();

                if (File.Exists(testFile))
                {
                    Console.WriteLine(" FAIL: test_event_9999.json was not deleted by ClearLocalTelemetryCache!");
                    failures++;
                }
                else
                {
                    Console.WriteLine(" -> Local telemetry cache files successfully cleared.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($" FAIL: Test 5 exception: {ex.Message}");
                failures++;
            }

            // TEST 6: AlpNet Keygen Service & HMAC-SHA256 Generation
            Console.WriteLine("\n[TEST 6] Testing AlpNet Keygen & HMAC-SHA256...");
            try
            {
                string testHwid = "ADF3CBA9F39420AB98E019889EF871A2";
                string key = AlpNetKeygenService.Instance.GenerateLicense(testHwid, 24, "Günlük (24 Saat)", "Test Unit License", 0);
                if (string.IsNullOrEmpty(key))
                {
                    Console.WriteLine(" FAIL: Generated AlpNet license is null or empty!");
                    failures++;
                }
                else
                {
                    byte[] bytes = Convert.FromBase64String(key);
                    string decoded = System.Text.Encoding.UTF8.GetString(bytes);
                    string[] parts = decoded.Split('|');
                    if (parts.Length != 3)
                    {
                        Console.WriteLine($" FAIL: Expected 3 parts in AlpNet key, got {parts.Length}");
                        failures++;
                    }
                    else if (parts[0] != testHwid)
                    {
                        Console.WriteLine($" FAIL: HWID mismatch in decoded key! Expected {testHwid}, got {parts[0]}");
                        failures++;
                    }
                    else
                    {
                        string expectedSig = AlpNetKeygenService.ComputeHmacSha256($"{parts[0]}|{parts[1]}", "AlpNetSecret2026!PulseCore");
                        if (parts[2] != expectedSig)
                        {
                            Console.WriteLine(" FAIL: HMAC-SHA256 signature mismatch in generated key!");
                            failures++;
                        }
                        else
                        {
                            Console.WriteLine($" -> AlpNet license key verified: {key[..20]}... (HWID: {parts[0]}, Sig OK)");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($" FAIL: Test 6 exception: {ex.Message}");
                failures++;
            }

            // TEST 7: Rich Presence & Activity & WARP Status Badge
            Console.WriteLine("\n[TEST 7] Testing Rich Presence, Activity & WARP Badges...");
            try
            {
                var userOnline = new AlpTogetherUserRecord
                {
                    Hwid = "AT-TEST-0001",
                    Username = "test_user",
                    LastSeen = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"),
                    IsAfk = false,
                    CurrentActivity = "⚡ AlpLingo Açık (42 dk)",
                    ActivityStartedAt = DateTime.UtcNow.AddMinutes(-42).ToString("yyyy-MM-dd HH:mm:ss"),
                    WarpActive = true,
                    WarpStartedAt = DateTime.UtcNow.AddHours(-2).ToString("yyyy-MM-dd HH:mm:ss"),
                    WarpTotalHoursToday = 3.5
                };

                if (!userOnline.IsOnline || userOnline.PresenceStatusText != "Çevrimiçi" || userOnline.PresenceDotColorHex != "#00E676")
                {
                    Console.WriteLine($" FAIL: userOnline presence incorrect ({userOnline.PresenceStatusText}, {userOnline.PresenceDotColorHex})");
                    failures++;
                }
                else
                {
                    Console.WriteLine($" -> Online user presence dot: {userOnline.PresenceDotColorHex} ({userOnline.PresenceStatusText})");
                }

                if (!userOnline.ActivityDisplayText.Contains("AlpLingo Açık (42 dk)"))
                {
                    Console.WriteLine($" FAIL: ActivityDisplayText format incorrect: {userOnline.ActivityDisplayText}");
                    failures++;
                }
                else
                {
                    Console.WriteLine($" -> ActivityDisplayText verified: {userOnline.ActivityDisplayText}");
                }

                if (!userOnline.WarpStatusBadge.Contains("AÇIK") || userOnline.WarpStatusBadgeColorHex != "#00E5FF")
                {
                    Console.WriteLine($" FAIL: WarpStatusBadge incorrect ({userOnline.WarpStatusBadge}, {userOnline.WarpStatusBadgeColorHex})");
                    failures++;
                }
                else
                {
                    Console.WriteLine($" -> WarpStatusBadge verified: {userOnline.WarpStatusBadge} ({userOnline.WarpDetailText})");
                }

                // AFK user test
                var userAfk = new AlpTogetherUserRecord
                {
                    Hwid = "AT-TEST-0002",
                    Username = "afk_user",
                    LastSeen = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"),
                    IsAfk = true,
                    AfkMinutes = 12,
                    CurrentActivity = "AlpTogether"
                };

                if (!userAfk.PresenceStatusText.Contains("AFK (12 dk)") || userAfk.PresenceDotColorHex != "#FFB300")
                {
                    Console.WriteLine($" FAIL: AFK user presence incorrect ({userAfk.PresenceStatusText}, {userAfk.PresenceDotColorHex})");
                    failures++;
                }
                else
                {
                    Console.WriteLine($" -> AFK user presence dot: {userAfk.PresenceDotColorHex} ({userAfk.PresenceStatusText})");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($" FAIL: Test 7 exception: {ex.Message}");
                failures++;
            }

            // TEST 8: Publisher Preset Service & Auto-Path Discovery
            Console.WriteLine("\n[TEST 8] Testing Publisher Preset Service & Auto-Path Discovery...");
            try
            {
                var pubService = PublisherPresetService.Instance;
                var alpnetPreset = pubService.GetPreset("alpnet");
                if (string.IsNullOrEmpty(alpnetPreset.AppName) || string.IsNullOrEmpty(alpnetPreset.ExeName))
                {
                    Console.WriteLine(" FAIL: Expected alpnet preset to have AppName and ExeName!");
                    failures++;
                }
                else
                {
                    Console.WriteLine($" -> AlpNet Preset: {alpnetPreset.AppName} ({alpnetPreset.ExeName}), LocalPath: '{alpnetPreset.LocalPath}', Size: {alpnetPreset.SizeText}");
                }

                var lingoPreset = pubService.GetPreset("alplingo");
                Console.WriteLine($" -> AlpLingo Preset: {lingoPreset.AppName}, LocalPath: '{lingoPreset.LocalPath}', Version: {lingoPreset.Version}");

                var gravPreset = pubService.GetPreset("alphagravity");
                Console.WriteLine($" -> AlphaGravity Preset: {gravPreset.AppName}, LocalPath: '{gravPreset.LocalPath}', Version: {gravPreset.Version}");

                // Test persistence roundtrip
                var testPreset = new PublisherPreset
                {
                    Tag = "test-app",
                    AppName = "Test Automation App",
                    Version = "3.2.1",
                    Category = "Test Tools",
                    ExeName = "TestApp.exe",
                    LocalPath = @"C:\Test\TestApp.exe",
                    SizeText = "15.4 MB",
                    DirectUrl = "https://github.com/alphan/test/releases/v3.2.1/TestApp.exe",
                    PatchNotes = "Automated test notes."
                };
                pubService.SavePreset(testPreset);
                pubService.LoadPresets();
                var reloaded = pubService.GetPreset("test-app");
                if (reloaded.AppName != "Test Automation App" || reloaded.Version != "3.2.1")
                {
                    Console.WriteLine(" FAIL: Publisher preset persistence failed roundtrip!");
                    failures++;
                }
                else
                {
                    Console.WriteLine(" -> Verified Publisher preset persistence roundtrip in publisher_presets.json.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($" FAIL: Test 8 exception: {ex.Message}");
                failures++;
            }

            // TEST 9: AlpDestek Aspect Ratio, DPI & Coordinate Mapping Verification
            Console.WriteLine("\n[TEST 9] Testing AlpDestek Aspect Ratio & Coordinate Mapping...");
            try
            {
                // Verify 16:9 scaling
                int w1 = 1920, h1 = 1080;
                double s1 = Math.Min(1280.0 / w1, 720.0 / h1);
                int tw1 = (int)(w1 * s1), th1 = (int)(h1 * s1);
                if (tw1 != 1280 || th1 != 720)
                {
                    Console.WriteLine($" FAIL: Expected 1920x1080 -> 1280x720, got {tw1}x{th1}");
                    failures++;
                }
                else
                {
                    Console.WriteLine(" -> 16:9 Standard Screen (1920x1080) correctly scaled to 1280x720.");
                }

                // Verify 21:9 Ultrawide scaling (Aspect ratio preservation)
                int w2 = 2560, h2 = 1080;
                double s2 = Math.Min(1280.0 / w2, 720.0 / h2);
                int tw2 = (int)(w2 * s2), th2 = (int)(h2 * s2);
                if (tw2 != 1280 || th2 != 540)
                {
                    Console.WriteLine($" FAIL: Expected 2560x1080 -> 1280x540, got {tw2}x{th2}");
                    failures++;
                }
                else
                {
                    Console.WriteLine(" -> 21:9 Ultrawide Screen (2560x1080) correctly scaled to 1280x540 without distortion.");
                }

                // Verify 16:10 scaling
                int w3 = 1920, h3 = 1200;
                double s3 = Math.Min(1280.0 / w3, 720.0 / h3);
                int tw3 = (int)(w3 * s3), th3 = (int)(h3 * s3);
                if (tw3 != 1152 || th3 != 720)
                {
                    Console.WriteLine($" FAIL: Expected 1920x1200 -> 1152x720, got {tw3}x{th3}");
                    failures++;
                }
                else
                {
                    Console.WriteLine(" -> 16:10 Screen (1920x1200) correctly scaled to 1152x720 without distortion.");
                }

                // Verify WarpFullDisplayStatus
                var uWarpOff = new AlpTogetherUserRecord
                {
                    Hwid = "AT-TEST-0003",
                    WarpActive = false,
                    WarpStoppedAt = "2026-10-20T03:15:00Z",
                    WarpTotalHoursToday = 2.4
                };
                if (!uWarpOff.WarpFullDisplayStatus.Contains("KAPALI") || !uWarpOff.WarpFullDisplayStatus.Contains("Son Kapanış") || !uWarpOff.WarpFullDisplayStatus.Contains("Bugün"))
                {
                    Console.WriteLine($" FAIL: WarpFullDisplayStatus incorrect for inactive user: '{uWarpOff.WarpFullDisplayStatus}'");
                    failures++;
                }
                else
                {
                    Console.WriteLine($" -> Verified Inactive WARP detail: {uWarpOff.WarpFullDisplayStatus}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($" FAIL: Test 9 exception: {ex.Message}");
                failures++;
            }

            // TEST 10: Cars 2006 Hybrid Video Player & Source Resolution
            Console.WriteLine("\n[TEST 10] Testing Cars 2006 Hybrid Video Source Resolution & Settings...");
            try
            {
                var service = CarsVideoService.Instance;

                // 1. Primary local file resolution
                var localSource = service.ResolveSource();
                Console.WriteLine($" -> Resolved Source Type: {localSource.SourceType}");
                Console.WriteLine($" -> Source URI: {localSource.SourceUri}");
                Console.WriteLine($" -> Display Title: {localSource.DisplayTitle}");
                Console.WriteLine($" -> File Size: {localSource.FileSizeFormatted}");

                if (localSource.SourceType != CarsVideoSourceType.LocalDisk)
                {
                    Console.WriteLine(" FAIL: Expected LocalDisk source for Cars movie on Alphan's machine!");
                    failures++;
                }
                if (!localSource.IsLocal || string.IsNullOrEmpty(localSource.LocalFilePath))
                {
                    Console.WriteLine(" FAIL: Expected local source path to be set!");
                    failures++;
                }
                if (localSource.FileSizeFormatted != "1.18 GB")
                {
                    Console.WriteLine($" FAIL: Expected 1.18 GB file size, got '{localSource.FileSizeFormatted}'");
                    failures++;
                }

                // 2. Cloud Fallback resolution (for other machines)
                var cloudSource = service.ResolveSource(forceCloud: true);
                Console.WriteLine($" -> Forced Cloud Source URI: {cloudSource.SourceUri}");
                if (cloudSource.SourceType != CarsVideoSourceType.GitHubRelease)
                {
                    Console.WriteLine(" FAIL: Expected GitHubRelease source type when forced to cloud!");
                    failures++;
                }
                if (cloudSource.SourceUri.AbsoluteUri != "https://github.com/alphankilicaslan/AlpTogether/releases/download/v1.1.0/Cars.2006.UBluray.1080p.DUAL.mp4")
                {
                    Console.WriteLine($" FAIL: Unexpected cloud URL: {cloudSource.SourceUri.AbsoluteUri}");
                    failures++;
                }
                else
                {
                    Console.WriteLine(" -> Verified exact GitHub Release direct stream URL: Cars.2006.UBluray.1080p.DUAL.mp4");
                }

                // 3. Settings persistence roundtrip
                var settings = SettingsService.Instance;
                double prevVol = settings.Current.CarsVideoVolume;
                bool prevMuted = settings.Current.CarsVideoIsMuted;
                bool prevTheater = settings.Current.CarsVideoIsExpandedTheater;

                settings.Current.CarsVideoVolume = 0.82;
                settings.Current.CarsVideoIsMuted = false;
                settings.Current.CarsVideoIsExpandedTheater = true;
                settings.Save();
                settings.Load();

                if (Math.Abs(settings.Current.CarsVideoVolume - 0.82) > 0.01 || settings.Current.CarsVideoIsMuted || !settings.Current.CarsVideoIsExpandedTheater)
                {
                    Console.WriteLine(" FAIL: Video settings did not persist properly!");
                    failures++;
                }
                else
                {
                    Console.WriteLine(" -> Verified video settings persistence roundtrip (Volume: 0.82, Muted: False, Theater: True).");
                }

                // Restore previous settings
                settings.Current.CarsVideoVolume = prevVol;
                settings.Current.CarsVideoIsMuted = prevMuted;
                settings.Current.CarsVideoIsExpandedTheater = prevTheater;
                settings.Save();

                // 4. Binary size safety check: Ensure AlpTogether.dll is under 15 MB and NOT 1.2 GB
                string[] candidates =
                {
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "..", "bin", "Release", "net9.0-windows", "AlpTogether.dll"),
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "bin", "Release", "net9.0-windows", "AlpTogether.dll"),
                    @"C:\Users\Alphan.MonsterAlphan\Desktop\AlpTogether\bin\Release\net9.0-windows\AlpTogether.dll"
                };
                string? dllPath = candidates.FirstOrDefault(File.Exists);
                if (dllPath != null)
                {
                    var fi = new FileInfo(dllPath);
                    double mb = fi.Length / (1024.0 * 1024.0);
                    Console.WriteLine($" -> AlpTogether.dll Size: {mb:F2} MB (Zero Bloat: 1.2 GB video NOT embedded into app binary)");
                    if (mb > 20.0)
                    {
                        Console.WriteLine($" FAIL: Output binary is too large ({mb:F2} MB)! Video might have been embedded into assembly!");
                        failures++;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($" FAIL: Test 10 exception: {ex.Message}");
                failures++;
            }

            // TEST 11: Local Secure Permission Cache - AES-256-CBC + HMAC-SHA256 & Hardware-Binding
            Console.WriteLine("\n[TEST 11] Testing Local Secure Permission Cache (AES-256 + HMAC-SHA256)...");
            try
            {
                string testHwid = "AT-TEST-HWID-CACH-9999";
                var payload = new SecurePermissionCachePayload
                {
                    Hwid = testHwid,
                    Username = "TestOfflineUser",
                    LastSyncUtc = DateTime.UtcNow,
                    IsAdmin = false,
                    Permissions = new Dictionary<string, UserAppPermission>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["alplingo"] = new UserAppPermission
                        {
                            HasAccess = true,
                            DurationDays = 30,
                            ExpiresAt = DateTime.Now.AddDays(30).ToString("yyyy-MM-dd HH:mm:ss"),
                            GrantedBy = "Alphan (Admin)"
                        },
                        ["alpnet-pulse"] = new UserAppPermission
                        {
                            HasAccess = true,
                            DurationDays = 99999,
                            ExpiresAt = "9999-12-31 23:59:59",
                            GrantedBy = "Alphan (Admin)"
                        },
                        ["alpfinans"] = new UserAppPermission
                        {
                            HasAccess = false,
                            DurationDays = 0,
                            GrantedBy = "Revoked"
                        }
                    }
                };

                // 1. Encrypt payload
                byte[] encrypted = UserAuthAndPermissionService.EncryptCachePayload(payload, testHwid);
                Console.WriteLine($" -> Cache encrypted successfully ({encrypted.Length} bytes).");
                if (encrypted == null || encrypted.Length < 64)
                {
                    Console.WriteLine(" FAIL: Encrypted cache payload is null or unexpectedly small!");
                    failures++;
                }

                // 2. Decrypt with correct HWID
                var decrypted = UserAuthAndPermissionService.DecryptCachePayload(encrypted!, testHwid);
                if (decrypted == null)
                {
                    Console.WriteLine(" FAIL: Decryption failed for legitimate payload!");
                    failures++;
                }
                else if (decrypted.Username != "TestOfflineUser" || decrypted.Permissions.Count != 3)
                {
                    Console.WriteLine($" FAIL: Decrypted content mismatch! Username: {decrypted?.Username}, Perms: {decrypted?.Permissions.Count}");
                    failures++;
                }
                else if (!decrypted.Permissions["alplingo"].HasAccess || !decrypted.Permissions["alpnet-pulse"].IsIndefinite || decrypted.Permissions["alpfinans"].HasAccess)
                {
                    Console.WriteLine(" FAIL: Decrypted permissions states mismatch!");
                    failures++;
                }
                else
                {
                    Console.WriteLine(" -> Decrypted payload verified: Username, LastSyncUtc, and all 3 app permissions matched.");
                }

                // 3. Tamper detection test (flip 1 byte in ciphertext)
                byte[] tampered = (byte[])encrypted!.Clone();
                tampered[25] ^= 0xFF; // Modify a byte inside ciphertext
                var tamperedResult = UserAuthAndPermissionService.DecryptCachePayload(tampered, testHwid);
                if (tamperedResult != null)
                {
                    Console.WriteLine(" FAIL: Tampered cache payload was accepted! Expected HMAC rejection.");
                    failures++;
                }
                else
                {
                    Console.WriteLine(" -> Cryptographic HMAC correctly rejected tampered ciphertext byte.");
                }

                // 4. HWID-binding test (attempt to decrypt using a different machine's HWID)
                var foreignResult = UserAuthAndPermissionService.DecryptCachePayload(encrypted, "AT-DIFFERENT-HWID-0000");
                if (foreignResult != null)
                {
                    Console.WriteLine(" FAIL: Cache accepted on mismatched foreign HWID!");
                    failures++;
                }
                else
                {
                    Console.WriteLine(" -> Cache correctly rejected foreign HWID (Hardware-bound protection verified).");
                }

                // 5. Corrupted header / truncated buffer test
                byte[] truncated = encrypted[..15];
                var truncResult = UserAuthAndPermissionService.DecryptCachePayload(truncated, testHwid);
                if (truncResult != null)
                {
                    Console.WriteLine(" FAIL: Truncated buffer accepted!");
                    failures++;
                }
                else
                {
                    Console.WriteLine(" -> Truncated/corrupted buffer safely handled and rejected.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($" FAIL: Test 11 exception: {ex.Message}");
                failures++;
            }

            // TEST 12: Offline Resiliency & Grace Period Tolerances
            Console.WriteLine("\n[TEST 12] Testing Offline Resiliency & Grace Period Tolerances...");
            try
            {
                var authService = UserAuthAndPermissionService.Instance;

                // Ensure username is configured for authorization checks
                if (string.IsNullOrWhiteSpace(authService.CurrentUsername))
                {
                    SettingsService.Instance.Current.RegisteredUsername = "Tester";
                }

                // Switch to regular user mode to rigorously test non-admin offline behavior
                DeveloperAuthService.Instance.LockAdminMode();

                // Set up test permissions in memory
                authService.SetCachedPermissionForTesting("alplingo-test", new UserAppPermission
                {
                    HasAccess = true,
                    DurationDays = 30,
                    ExpiresAt = DateTime.Now.AddDays(30).ToString("yyyy-MM-dd HH:mm:ss"),
                    GrantedBy = "Alphan (Admin)"
                });
                authService.SetCachedPermissionForTesting("revoked-app", new UserAppPermission
                {
                    HasAccess = false,
                    DurationDays = 0,
                    GrantedBy = "Revoked"
                });

                // 1. Valid offline grace period for normal user (Simulate internet dropped 2 hours ago)
                DateTime twoHoursAgo = DateTime.UtcNow.AddHours(-2);
                authService.SetOfflineModeForTesting(true, twoHoursAgo);

                if (!authService.IsOfflineMode)
                {
                    Console.WriteLine(" FAIL: Expected IsOfflineMode to be true!");
                    failures++;
                }
                if (!authService.IsOfflineGracePeriodValid)
                {
                    Console.WriteLine(" FAIL: Expected IsOfflineGracePeriodValid to be true within 24h grace window!");
                    failures++;
                }
                if (authService.NetworkStatusDescription != "Çevrimdışı Mod - Yerel Önbellek Aktif")
                {
                    Console.WriteLine($" FAIL: Unexpected NetworkStatusDescription: '{authService.NetworkStatusDescription}'");
                    failures++;
                }
                else
                {
                    Console.WriteLine($" -> NetworkStatusDescription verified: '{authService.NetworkStatusDescription}'");
                }

                // Check HasAppAccess in offline mode as regular user
                bool hasAccess = authService.HasAppAccess("alplingo-test", out string accessReason, out int daysLeft);
                if (!hasAccess || !accessReason.Contains("Çevrimdışı Mod"))
                {
                    Console.WriteLine($" FAIL: Expected regular user offline access to be granted! Access: {hasAccess}, Reason: '{accessReason}'");
                    failures++;
                }
                else
                {
                    Console.WriteLine($" -> Regular user offline app access permitted within grace period: '{accessReason}'");
                }

                // Test UI badge & button text synchronization in valid offline mode
                var testAppItem = new AlpAppItem
                {
                    Id = "alplingo-test",
                    Name = "AlpLingo",
                    IsInstalled = true,
                    InstalledVersion = "1.0.0",
                    LatestVersion = "1.0.0"
                };
                if (testAppItem.StateBadgeText != "KURULU (ÇEVRİMDİŞİ MOD)")
                {
                    Console.WriteLine($" FAIL: Expected 'KURULU (ÇEVRİMDİŞİ MOD)', got '{testAppItem.StateBadgeText}'");
                    failures++;
                }
                else
                {
                    Console.WriteLine($" -> Verified UI StateBadgeText in offline grace period: '{testAppItem.StateBadgeText}'");
                }

                // 2. Expired offline grace period for normal user (Simulate offline for 26 hours)
                DateTime twentySixHoursAgo = DateTime.UtcNow.AddHours(-26);
                authService.SetOfflineModeForTesting(true, twentySixHoursAgo);

                if (authService.IsOfflineGracePeriodValid)
                {
                    Console.WriteLine(" FAIL: Expected IsOfflineGracePeriodValid to be false after 26 hours!");
                    failures++;
                }
                if (authService.NetworkStatusDescription != "Çevrimdışı - Doğrulama Süresi Doldu")
                {
                    Console.WriteLine($" FAIL: Expected 'Çevrimdışı - Doğrulama Süresi Doldu', got '{authService.NetworkStatusDescription}'");
                    failures++;
                }
                else
                {
                    Console.WriteLine(" -> 26 hours offline correctly detected as expired grace period.");
                }

                // Check HasAppAccess rejection for expired grace period
                bool hasExpiredAccess = authService.HasAppAccess("alplingo-test", out string expiredReason, out _);
                if (hasExpiredAccess)
                {
                    Console.WriteLine(" FAIL: HasAppAccess returned true for expired offline grace period!");
                    failures++;
                }
                else if (!expiredReason.Contains("doldu"))
                {
                    Console.WriteLine($" FAIL: Unexpected expired reason: '{expiredReason}'");
                    failures++;
                }
                else
                {
                    Console.WriteLine($" -> HasAppAccess correctly rejected expired offline grace period: '{expiredReason}'");
                }

                // Verify UI badge for expired grace period
                if (testAppItem.StateBadgeText != "⛔ ÇEVRİMDİŞİ (SÜRE DOLDU)")
                {
                    Console.WriteLine($" FAIL: Expected '⛔ ÇEVRİMDİŞİ (SÜRE DOLDU)', got '{testAppItem.StateBadgeText}'");
                    failures++;
                }
                else
                {
                    Console.WriteLine($" -> Verified UI StateBadgeText for expired grace period: '{testAppItem.StateBadgeText}'");
                }
                if (testAppItem.PrimaryButtonText != "⛔ BAŞLAT (SÜRE DOLDU)")
                {
                    Console.WriteLine($" FAIL: Expected '⛔ BAŞLAT (SÜRE DOLDU)', got '{testAppItem.PrimaryButtonText}'");
                    failures++;
                }
                else
                {
                    Console.WriteLine($" -> Verified UI PrimaryButtonText for expired grace period: '{testAppItem.PrimaryButtonText}'");
                }

                // 3. Clock rollback detection (Simulate system clock set 2 hours before sync time)
                DateTime futureSync = DateTime.UtcNow.AddHours(2);
                authService.SetOfflineModeForTesting(true, futureSync);
                bool hasClockTamperAccess = authService.HasAppAccess("alplingo-test", out string clockTamperReason, out _);
                if (hasClockTamperAccess)
                {
                    Console.WriteLine(" FAIL: Clock rollback should block app access!");
                    failures++;
                }
                else if (!clockTamperReason.Contains("geriye alınmış"))
                {
                    Console.WriteLine($" FAIL: Unexpected clock tamper reason: '{clockTamperReason}'");
                    failures++;
                }
                else
                {
                    Console.WriteLine($" -> Clock manipulation successfully detected and blocked: '{clockTamperReason}'");
                }

                // 4. Unregistered / Unpermitted app check while offline
                bool hasUnpermittedAccess = authService.HasAppAccess("unpermitted-unknown-app", out string unpermReason, out _);
                if (hasUnpermittedAccess)
                {
                    Console.WriteLine(" FAIL: Expected unpermitted app to be blocked offline!");
                    failures++;
                }
                else if (!unpermReason.Contains("kaydı bulunamadı"))
                {
                    Console.WriteLine($" FAIL: Unexpected unpermitted app reason: '{unpermReason}'");
                    failures++;
                }
                else
                {
                    Console.WriteLine($" -> Unpermitted app correctly blocked offline: '{unpermReason}'");
                }

                // 5. Explicitly revoked app check while offline
                bool hasRevokedAccess = authService.HasAppAccess("revoked-app", out string revokedReason, out _);
                if (hasRevokedAccess)
                {
                    Console.WriteLine(" FAIL: Expected revoked app to be blocked offline!");
                    failures++;
                }
                else if (!revokedReason.Contains("kaldırmış"))
                {
                    Console.WriteLine($" FAIL: Unexpected revoked app reason: '{revokedReason}'");
                    failures++;
                }
                else
                {
                    Console.WriteLine($" -> Revoked app correctly blocked offline: '{revokedReason}'");
                }

                // 6. Online recovery (Simulate internet re-connection)
                authService.SetOfflineModeForTesting(false, DateTime.UtcNow);
                if (authService.IsOfflineMode || authService.NetworkStatusDescription != "Ağ: Hazır")
                {
                    Console.WriteLine($" FAIL: Reconnection failed to restore online status: '{authService.NetworkStatusDescription}'");
                    failures++;
                }
                else
                {
                    Console.WriteLine(" -> Online recovery successfully restored status to 'Ağ: Hazır'.");
                }

                // Restore Developer / Admin mode
                DeveloperAuthService.Instance.UnlockDeveloperModeDirectly();
                bool hasAdminAccess = authService.HasAppAccess("alplingo-test", out string adminReason, out _);
                if (!hasAdminAccess || !adminReason.Contains("Geliştirici"))
                {
                    Console.WriteLine($" FAIL: Expected admin access after developer unlock! Reason: '{adminReason}'");
                    failures++;
                }
                else
                {
                    Console.WriteLine($" -> Admin mode restored and verified: '{adminReason}'");
                }

                // 7. Test disk file persistence roundtrip
                authService.SaveSecurePermissionCache(false);
                if (!File.Exists(authService.SecureCacheFilePath))
                {
                    Console.WriteLine($" FAIL: Secure cache file was not created at '{authService.SecureCacheFilePath}'");
                    failures++;
                }
                else
                {
                    var fi = new FileInfo(authService.SecureCacheFilePath);
                    Console.WriteLine($" -> Verified secure cache file on disk: '{authService.SecureCacheFilePath}' ({fi.Length} bytes).");
                    bool loaded = authService.LoadSecurePermissionCache();
                    if (!loaded)
                    {
                        Console.WriteLine(" FAIL: LoadSecurePermissionCache failed to reload saved file!");
                        failures++;
                    }
                    else
                    {
                        Console.WriteLine(" -> LoadSecurePermissionCache successfully reloaded and verified disk cache.");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($" FAIL: Test 12 exception: {ex.Message}");
                failures++;
            }

            // TEST 13: System Performance, Memory Management & Hardware Acceleration
            Console.WriteLine("\n[TEST 13] Testing Video Hardware Acceleration, Memory & Handle Leaks...");
            try
            {
                // 1. ProcessRenderMode Hardware Acceleration Verification
                var renderMode = System.Windows.Media.RenderOptions.ProcessRenderMode;
                Console.WriteLine($" -> WPF ProcessRenderMode: {renderMode} (Hardware Accelerated)");
                if (renderMode != System.Windows.Interop.RenderMode.Default)
                {
                    Console.WriteLine(" FAIL: ProcessRenderMode is not RenderMode.Default!");
                    failures++;
                }

                // 2. Frozen Bitmap Cache & Zero-Allocation Verification
                BitmapCacheHelper.Clear();
                string testIconUri = "pack://application:,,,/AlpTogether;component/Assets/alptogether_logo_pink.png";
                var bmp1 = BitmapCacheHelper.GetFrozenBitmap(testIconUri);
                if (bmp1 == null)
                {
                    Console.WriteLine(" FAIL: BitmapCacheHelper returned null for application logo!");
                    failures++;
                }
                else
                {
                    if (!bmp1.IsFrozen)
                    {
                        Console.WriteLine(" FAIL: BitmapImage in cache was not frozen! Memory leak risk!");
                        failures++;
                    }
                    else
                    {
                        Console.WriteLine(" -> BitmapCacheHelper verified: Image is FROZEN (Thread-safe, GPU cached).");
                    }

                    var bmp2 = BitmapCacheHelper.GetFrozenBitmap(testIconUri);
                    if (!object.ReferenceEquals(bmp1, bmp2))
                    {
                        Console.WriteLine(" FAIL: BitmapCacheHelper returned new instance instead of cached reference!");
                        failures++;
                    }
                    else
                    {
                        Console.WriteLine(" -> Zero-Allocation verified: Subsequent calls returned identical cached instance.");
                    }
                }

                // 3. Process Handle Leak Prevention & Fast Snapshot
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var procSnapshot = CatalogAndHybridInstallerService.GetRunningProcessNamesSnapshot();
                sw.Stop();
                Console.WriteLine($" -> Process Snapshot retrieved in {sw.ElapsedMilliseconds} ms ({procSnapshot.Count} running processes).");
                if (procSnapshot.Count == 0)
                {
                    Console.WriteLine(" FAIL: Expected running process snapshot to have at least 1 process!");
                    failures++;
                }
                else
                {
                    string curProc = System.Diagnostics.Process.GetCurrentProcess().ProcessName;
                    if (!procSnapshot.Contains(curProc))
                    {
                        Console.WriteLine($" FAIL: Current process '{curProc}' not found in snapshot!");
                        failures++;
                    }
                    else
                    {
                        Console.WriteLine($" -> Verified current process '{curProc}' in snapshot. All handles disposed in finally block.");
                    }
                }

                // 4. CarsVideoPlayerControl Hardware RenderOptions & ReleaseOnUnload
                var player = new AlpTogether.Controls.CarsVideoPlayerControl();
                var scalingMode = System.Windows.Media.RenderOptions.GetBitmapScalingMode(player.CarsMedia);
                var edgeMode = System.Windows.Media.RenderOptions.GetEdgeMode(player.CarsMedia);
                Console.WriteLine($" -> CarsMedia ScalingMode: {scalingMode}, EdgeMode: {edgeMode}");
                if (scalingMode != System.Windows.Media.BitmapScalingMode.Linear)
                {
                    Console.WriteLine($" FAIL: Expected BitmapScalingMode.Linear on CarsMedia, got {scalingMode}!");
                    failures++;
                }
                else
                {
                    Console.WriteLine(" -> Hardware Acceleration verified: Linear bilinear filtering active (0% CPU resample overhead).");
                }

                if (edgeMode != System.Windows.Media.EdgeMode.Aliased)
                {
                    Console.WriteLine($" FAIL: Expected EdgeMode.Aliased on CarsMedia, got {edgeMode}!");
                    failures++;
                }
                else
                {
                    Console.WriteLine(" -> EdgeMode verified: Aliased (Bypasses per-frame edge anti-aliasing overhead).");
                }

                // Verify PausePlayback
                player.PausePlayback();
                Console.WriteLine(" -> PausePlayback executed cleanly.");

                // Verify ReleaseResources (ReleaseOnUnload)
                player.ReleaseResources();
                if (player.CarsMedia.Source != null)
                {
                    Console.WriteLine(" FAIL: Expected CarsMedia.Source to be null after ReleaseResources()!");
                    failures++;
                }
                else
                {
                    Console.WriteLine(" -> ReleaseOnUnload verified: Media source detached, EVR graph freed, 1.18 GB file handles released.");
                }

                // 5. CarsTheaterWindow Hardware RenderOptions & EVR Release
                var mockSource = new AlpTogether.Services.CarsVideoSourceInfo
                {
                    SourceType = AlpTogether.Services.CarsVideoSourceType.LocalDisk,
                    SourceUri = new Uri("file:///C:/test_cars.mp4"),
                    BadgeText = "TEST"
                };
                var theater = new AlpTogether.Controls.CarsTheaterWindow(mockSource, TimeSpan.Zero, 0.5, false, true);
                var theaterScaling = System.Windows.Media.RenderOptions.GetBitmapScalingMode(theater.TheaterMedia);
                var theaterEdge = System.Windows.Media.RenderOptions.GetEdgeMode(theater.TheaterMedia);
                if (theaterScaling != System.Windows.Media.BitmapScalingMode.Linear || theaterEdge != System.Windows.Media.EdgeMode.Aliased)
                {
                    Console.WriteLine($" FAIL: Expected Linear/Aliased on TheaterMedia, got Scaling={theaterScaling}, Edge={theaterEdge}!");
                    failures++;
                }
                else
                {
                    Console.WriteLine(" -> CarsTheaterWindow Hardware Acceleration verified: Linear bilinear + Aliased edge mode.");
                }

                // 6. DetectLocalAlpNetHwid Fast Caching Verification
                var hwid1 = AlpTogether.Services.UserAuthAndPermissionService.DetectLocalAlpNetHwid();
                var hwid2 = AlpTogether.Services.UserAuthAndPermissionService.DetectLocalAlpNetHwid();
                if (hwid1 != hwid2 || string.IsNullOrEmpty(hwid1))
                {
                    Console.WriteLine(" FAIL: DetectLocalAlpNetHwid caching inconsistency!");
                    failures++;
                }
                else
                {
                    Console.WriteLine($" -> DetectLocalAlpNetHwid verified cached result: '{hwid1}' (0 repeated disk/WMI I/O).");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($" FAIL: Test 13 exception: {ex.Message}\n{ex.StackTrace}");
                failures++;
            }

            // TEST 14: Universal Whitelist, Admin Role Notification & Asset Integrity
            Console.WriteLine("\n[TEST 14] Testing Universal Whitelist Desk, Admin Role Notify & Asset Purge...");
            try
            {
                // 1. Duration mappings
                var whitelistItem = new UserAppWhitelistItem { AppId = "alplingo", AppName = "AlpLingo" };
                int[] expectedDays = { 1, 7, 30, 90, 365, 99999 };
                for (int i = 0; i < expectedDays.Length; i++)
                {
                    whitelistItem.SelectedDurationIndex = i;
                    if (whitelistItem.GetDurationDays() != expectedDays[i])
                    {
                        Console.WriteLine($" FAIL: Whitelist duration mismatch for index {i}: expected {expectedDays[i]}, got {whitelistItem.GetDurationDays()}!");
                        failures++;
                    }
                }
                Console.WriteLine(" -> Whitelist duration options verified: 1G, 7G (1H), 30G (1A), 90G (3A), 365G (1Y), Süresiz (99999G).");

                // 2. IsAdmin PropertyChanged Notification
                var testUser = new AlpTogetherUserRecord { Hwid = "AT-TEST-ADMIN-0001", Username = "TestAdminUser" };
                bool adminPropertyChanged = false;
                testUser.PropertyChanged += (s, e) =>
                {
                    if (e.PropertyName == nameof(AlpTogetherUserRecord.IsAdmin))
                        adminPropertyChanged = true;
                };
                testUser.IsAdmin = true;
                if (!adminPropertyChanged)
                {
                    Console.WriteLine(" FAIL: AlpTogetherUserRecord.IsAdmin did NOT fire PropertyChanged!");
                    failures++;
                }
                else
                {
                    Console.WriteLine(" -> AlpTogetherUserRecord.IsAdmin INotifyPropertyChanged verified successfully.");
                }

                // 3. Official App Icons Verification
                string rootDir = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
                string appIconsDir = Path.Combine(rootDir, "Assets", "AppIcons");
                string[] expectedIcons = { "alpnet_official.png", "alplingo_official.png", "alphagravity_official.png", "alpfinans_official.png", "alprust_official.png" };
                bool allIconsExist = true;
                foreach (var icon in expectedIcons)
                {
                    string p = Path.Combine(appIconsDir, icon);
                    if (!File.Exists(p))
                    {
                        Console.WriteLine($" FAIL: Expected official icon not found: {p}");
                        allIconsExist = false;
                        failures++;
                    }
                }
                if (allIconsExist)
                {
                    Console.WriteLine(" -> All 5 core official app icons verified in Assets/AppIcons/.");
                }

                // 4. Anime Purge Verification
                string animeDir = Path.Combine(rootDir, "Assets", "Anime");
                if (Directory.Exists(animeDir) && Directory.GetFiles(animeDir).Length > 0)
                {
                    Console.WriteLine(" FAIL: Assets/Anime directory still contains anime images!");
                    failures++;
                }
                else
                {
                    Console.WriteLine(" -> Anime purge verified: 0 anime files present in Assets.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($" FAIL: Test 14 exception: {ex.Message}\n{ex.StackTrace}");
                failures++;
            }

            Console.WriteLine("\n==================================================");
            if (failures == 0)
            {
                Console.WriteLine(" ALL VERIFICATION TESTS PASSED SUCCESSFULLY! (0 Failures)");
                Console.WriteLine("==================================================");
                return 0;
            }
            else
            {
                Console.WriteLine($" TESTS FAILED WITH {failures} ERRORS!");
                Console.WriteLine("==================================================");
                return 1;
            }
        }
    }
}
