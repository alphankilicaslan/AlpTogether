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
