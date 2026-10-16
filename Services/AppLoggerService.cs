using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using AlpTogether.Models;

namespace AlpTogether.Services
{
    public class AppLoggerService
    {
        public static AppLoggerService Instance { get; } = new();

        public ObservableCollection<LogEntryItem> Entries { get; } = new();
        private readonly string _logDir;
        private readonly string _logFilePath;
        private readonly object _fileLock = new();

        public event Action<LogEntryItem>? OnNewLog;

        private AppLoggerService()
        {
            _logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AlpTogether", "logs");
            try
            {
                if (!Directory.Exists(_logDir))
                    Directory.CreateDirectory(_logDir);
            }
            catch { }

            _logFilePath = Path.Combine(_logDir, $"alptogether_{DateTime.Now:yyyyMMdd}.log");
        }

        public void Info(string category, string message, string appName = "AlpTogether", string details = "")
            => Write(category, "INFO", message, appName, details);

        public void Success(string category, string message, string appName = "AlpTogether", string details = "")
            => Write(category, "SUCCESS", message, appName, details);

        public void Warn(string category, string message, string appName = "AlpTogether", string details = "")
            => Write(category, "WARN", message, appName, details);

        public void Error(string category, string message, string appName = "AlpTogether", string details = "")
            => Write(category, "ERROR", message, appName, details);

        public void Write(string category, string level, string message, string appName = "AlpTogether", string details = "")
        {
            var entry = new LogEntryItem
            {
                TimeText = DateTime.Now.ToString("HH:mm:ss"),
                Category = category.ToUpperInvariant(),
                Level = level.ToUpperInvariant(),
                AppName = appName,
                Message = message,
                Details = details
            };

            try
            {
                lock (_fileLock)
                {
                    string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{entry.Level}] [{entry.Category}] [{entry.AppName}] {entry.Message}" +
                                  (string.IsNullOrWhiteSpace(entry.Details) ? "" : $" | Detay: {entry.Details}") + Environment.NewLine;
                    File.AppendAllText(_logFilePath, line, Encoding.UTF8);
                }
            }
            catch { }

            if (Application.Current?.Dispatcher != null)
            {
                Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    Entries.Insert(0, entry);
                    while (Entries.Count > 350)
                        Entries.RemoveAt(Entries.Count - 1);
                    OnNewLog?.Invoke(entry);
                }));
            }
        }

        public string GetRecentLogsText(int maxEntries = 40)
        {
            try
            {
                var snapshot = Entries.Take(maxEntries).Reverse().ToList();
                var sb = new StringBuilder();
                foreach (var item in snapshot)
                {
                    sb.AppendLine($"[{item.TimeText}] [{item.Level}] [{item.Category}] ({item.AppName}): {item.Message}");
                    if (!string.IsNullOrWhiteSpace(item.Details))
                        sb.AppendLine($"   -> {item.Details}");
                }
                return sb.ToString();
            }
            catch
            {
                return string.Empty;
            }
        }

        public void ClearUiLogs()
        {
            Application.Current?.Dispatcher?.Invoke(() => Entries.Clear());
        }

        public string GetLogDirectory() => _logDir;
    }
}
