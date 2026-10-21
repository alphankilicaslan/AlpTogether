using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using AlpTogether.Services;

namespace AlpTogether
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Donanım Hızlandırma & EVR / Direct3D video işleme optimizasyonu (GPU Hardware Decode)
            System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.Default;

            // Global Unhandled Exception Hooks -> Anlık Hata Telemetrisi
            DispatcherUnhandledException += App_DispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;

            AppLoggerService.Instance.Info(
                "SİSTEM",
                $"AlpTogether Client v{AlpTogetherUpdateService.CurrentVersion} başlatıldı (Cihaz: {SettingsService.Instance.DeviceId}).",
                "AlpTogether",
                "Hibrit Kurulum Motoru & Anlık Hata Telemetrisi Aktif");
        }

        private async void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            e.Handled = true;
            try
            {
                await TelemetryAndHelpService.Instance.ReportErrorImmediatelyAsync(
                    "AlpTogether",
                    "AlpTogether Client",
                    AlpTogetherUpdateService.CurrentVersion,
                    "DispatcherUnhandledException",
                    e.Exception.Message,
                    e.Exception.StackTrace ?? "");
            }
            catch { }
        }

        private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            try
            {
                if (e.ExceptionObject is Exception ex)
                {
                    _ = TelemetryAndHelpService.Instance.ReportErrorImmediatelyAsync(
                        "AlpTogether",
                        "AlpTogether Client",
                        AlpTogetherUpdateService.CurrentVersion,
                        "AppDomainUnhandledException",
                        ex.Message,
                        ex.StackTrace ?? "");
                }
            }
            catch { }
        }

        private void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            e.SetObserved();
            try
            {
                _ = TelemetryAndHelpService.Instance.ReportErrorImmediatelyAsync(
                    "AlpTogether",
                    "AlpTogether Client",
                    AlpTogetherUpdateService.CurrentVersion,
                    "UnobservedTaskException",
                    e.Exception.Message,
                    e.Exception.StackTrace ?? "");
            }
            catch { }
        }
    }
}
