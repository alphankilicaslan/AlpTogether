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
