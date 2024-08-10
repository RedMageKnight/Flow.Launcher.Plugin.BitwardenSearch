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
            Console.WriteLine($"Logger not initialized. Message: {SanitizeMessage(message)}");
            return;
        }

        if (!ShouldLog(level)) return;

        try
        {
            File.AppendAllText(LogFilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] - {SanitizeMessage(message)}{Environment.NewLine}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to write to log file: {ex.Message}");
        }
    }

    public static void LogError(string message, Exception ex)
    {
        if (!ShouldLog(LogLevel.Error)) return;

        Log($"ERROR - {SanitizeMessage(message)}", LogLevel.Error);
        Log($"Exception: {ex.GetType().Name}", LogLevel.Error);
        Log($"Message: {SanitizeMessage(ex.Message)}", LogLevel.Error);
        #if DEBUG
        Log($"StackTrace: {SanitizeMessage(ex.StackTrace)}", LogLevel.Error);
        #endif
    }

    public static string SanitizeQuery(string query)
    {
        if (string.IsNullOrEmpty(query))
        {
            return "[EMPTY QUERY]";
        }

        string[] parts = query.Split(new[] { ' ' }, 2);
        if (parts.Length > 1 && parts[0].Equals("/unlock", StringComparison.OrdinalIgnoreCase))
        {
            return $"{parts[0]} [REDACTED]";
        }

        return query;
    }

    private static string SanitizeMessage(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return "[EMPTY MESSAGE]";
        }
        // List of sensitive keywords to look for
        var sensitiveKeywords = new[] { "password", "secret", "key", "token", "credential" };

        // Check if the message contains any sensitive keywords
        foreach (var keyword in sensitiveKeywords)
        {
            if (message.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                // If a sensitive keyword is found, replace the entire message
                return "[REDACTED SENSITIVE INFORMATION]";
            }
        }

        return message;
    }

        public static void Trace(string message)
        {
            Log(message, LogLevel.Trace);
        }

        private static bool ShouldLog(LogLevel level)
        {
            if (_settings == null) return true; // Log everything if settings are not initialized

            return level switch
            {
                LogLevel.Trace => _settings.LogTrace,
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
        Trace,
        Debug,
        Info,
        Warning,
        Error
    }
}