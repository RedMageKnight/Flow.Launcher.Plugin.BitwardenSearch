using System;
using System.IO;

namespace Flow.Launcher.Plugin.BitwardenSearch
{
    public static class Logger
    {
        private static string LogFilePath { get; set; } = string.Empty;
        private static BitwardenFlowSettings? _settings;

        public static void Initialize(string pluginDirectory, BitwardenFlowSettings settings)
        {
            var logDirectory = Path.Combine(pluginDirectory, "Logs");
            Directory.CreateDirectory(logDirectory);
            LogFilePath = Path.Combine(logDirectory, $"BitwardenFlow_{DateTime.Now:yyyyMMdd}.log");
            _settings = settings;
        }

        public static void Log(string message, LogLevel level = LogLevel.Info)
        {
            if (string.IsNullOrEmpty(LogFilePath))
            {
                Console.WriteLine($"Logger not initialized. Message: {message}");
                return;
            }

            if (!ShouldLog(level)) return;

            try
            {
                File.AppendAllText(LogFilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] - {message}{Environment.NewLine}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to write to log file: {ex.Message}");
            }
        }

        public static void LogError(string message, Exception ex)
        {
            if (!ShouldLog(LogLevel.Error)) return;

            Log($"ERROR - {message}", LogLevel.Error);
            Log($"Exception: {ex.GetType().Name}", LogLevel.Error);
            Log($"Message: {ex.Message}", LogLevel.Error);
            #if DEBUG
            Log($"StackTrace: {ex.StackTrace}", LogLevel.Error);
            #endif
        }

        private static bool ShouldLog(LogLevel level)
        {
            if (_settings == null) return true; // Log everything if settings are not initialized

            return level switch
            {
                LogLevel.Debug => _settings.LogDebug,
                LogLevel.Info => _settings.LogInfo,
                LogLevel.Warning => _settings.LogWarning,
                LogLevel.Error => _settings.LogError,
                _ => false,
            };
        }
    }

    public enum LogLevel
    {
        Debug,
        Info,
        Warning,
        Error
    }
}