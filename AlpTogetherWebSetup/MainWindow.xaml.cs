using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace AlpTogetherWebSetup
{
    public partial class MainWindow : Window
    {
        private const string GitHubReleaseClientUrl = "https://github.com/alphankilicaslan/AlpTogether/releases/latest/download/AlpTogether.exe";

        public MainWindow()
        {
            InitializeComponent();
            Loaded += MainWindow_Loaded;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            var args = Environment.GetCommandLineArgs();
            if (args.Any(a => a.Equals("/auto", StringComparison.OrdinalIgnoreCase) ||
                              a.Equals("-auto", StringComparison.OrdinalIgnoreCase) ||
                              a.Equals("/silent", StringComparison.OrdinalIgnoreCase)))
            {
                BtnLaunch_Click(this, new RoutedEventArgs());
            }
        }

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
                DragMove();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Application.Current.Shutdown();

        private async void BtnLaunch_Click(object sender, RoutedEventArgs e)
        {
            BtnLaunch.Visibility = Visibility.Collapsed;
            PanelProgress.Visibility = Visibility.Visible;
            TxtStatus.Text = "Kurulum dizini hazırlanıyor...";
            PbDownload.Value = 15;

            try
            {
                string installDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "AlpTogether");

                if (!Directory.Exists(installDir))
                    Directory.CreateDirectory(installDir);

                string targetExe = Path.Combine(installDir, "AlpTogether.exe");
                string tempExe = Path.Combine(installDir, "AlpTogether.tmp");

                bool installed = false;

                // 1. Check if AlpTogether.exe is bundled alongside Setup
                string localBundled = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "AlpTogether.exe");
                if (File.Exists(localBundled))
                {
                    TxtStatus.Text = "AlpTogether paket dosyaları kopyalanıyor...";
                    PbDownload.Value = 65;
                    await Task.Delay(300);
                    File.Copy(localBundled, targetExe, true);
                    installed = true;
                }
                else
                {
                    // 2. Download from GitHub Releases
                    TxtStatus.Text = "GitHub üzerinden güncel AlpTogether indiriliyor...";
                    installed = await DownloadClientAsync(GitHubReleaseClientUrl, tempExe);
                    if (installed && File.Exists(tempExe))
                    {
                        if (File.Exists(targetExe))
                        {
                            try { File.Delete(targetExe); } catch { }
                        }
                        File.Move(tempExe, targetExe);
                    }
                }

                if (!installed || !File.Exists(targetExe))
                {
                    throw new Exception("AlpTogether.exe indirilemedi veya bulunamadı.");
                }

                if (ChkCreateDesktopShortcut.IsChecked == true)
                {
                    PbDownload.Value = 90;
                    TxtStatus.Text = "Masaüstü kısayolu oluşturuluyor...";
                    CreateDesktopShortcut(targetExe, installDir, "AlpTogether");
                }

                PbDownload.Value = 100;
                TxtStatus.Text = "AlpTogether başlatılıyor...";
                await Task.Delay(350);

                Process.Start(new ProcessStartInfo
                {
                    FileName = targetExe,
                    WorkingDirectory = installDir,
                    UseShellExecute = true
                });

                Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Kurulum sırasında hata oluştu:\n" + ex.Message, "AlpTogether Kurulum Hatası", MessageBoxButton.OK, MessageBoxImage.Error);
                BtnLaunch.Visibility = Visibility.Visible;
                PanelProgress.Visibility = Visibility.Collapsed;
            }
        }

        private async Task<bool> DownloadClientAsync(string url, string dest)
        {
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
                using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                if (!response.IsSuccessStatusCode) return false;

                long total = response.Content.Headers.ContentLength ?? -1L;
                using var stream = await response.Content.ReadAsStreamAsync();
                using var fs = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None);

                byte[] buffer = new byte[81920];
                long readTotal = 0;
                int read;
                while ((read = await stream.ReadAsync(buffer, 0, buffer.Length)) != 0)
                {
                    await fs.WriteAsync(buffer, 0, read);
                    readTotal += read;
                    if (total > 0)
                    {
                        double pct = Math.Min(95, (double)readTotal / total * 100.0);
                        PbDownload.Value = pct;
                        TxtStatus.Text = $"İndiriliyor... %{pct:0}";
                    }
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void CreateDesktopShortcut(string targetPath, string workingDir, string shortcutName)
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
                        st.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
                    }
                }
            }
            catch { }
        }
    }
}
