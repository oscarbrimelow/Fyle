using System;
using System.IO;

namespace Fyle.Services
{
    public static class Logger
    {
        private static readonly string LogPath;
        private static readonly object _lock = new object();

        static Logger()
        {
            var logDir = GetDefaultLogDirectory();
            try
            {
                Directory.CreateDirectory(logDir);
                LogPath = Path.Combine(logDir, "fyle_log.txt");
            }
            catch
            {
                LogPath = Path.Combine(AppContext.BaseDirectory, "fyle_log.txt");
            }
            
            // Clear old log on startup
            try
            {
                File.WriteAllText(LogPath, $"=== Fyle Log Started {DateTime.Now} ===\n\n");
            }
            catch { }
        }

        public static void Log(string message)
        {
            try
            {
                lock (_lock)
                {
                    File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss.fff}] {message}\n");
                }
            }
            catch { }
        }

        public static void LogError(string context, Exception ex)
        {
            try
            {
                lock (_lock)
                {
                    File.AppendAllText(LogPath, 
                        $"\n[{DateTime.Now:HH:mm:ss.fff}] ERROR in {context}:\n" +
                        $"  Message: {ex.Message}\n" +
                        $"  Type: {ex.GetType().Name}\n" +
                        $"  Stack: {ex.StackTrace}\n" +
                        $"  Inner: {ex.InnerException?.Message ?? "None"}\n\n");
                }
            }
            catch { }
        }

        public static string GetLogPath() => LogPath;

        private static string GetDefaultLogDirectory()
        {
            var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(baseDir))
            {
                baseDir = AppContext.BaseDirectory;
            }
            return Path.Combine(baseDir, "Fyle", "Logs");
        }
    }
}

