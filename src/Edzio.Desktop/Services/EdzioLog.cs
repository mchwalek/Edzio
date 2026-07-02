using Microsoft.Extensions.Logging;

namespace Edzio.Desktop.Services;

/// <summary>
/// Simple static logger that writes to a daily rolling file under
/// %LOCALAPPDATA%\Edzio\logs\ and to the debug output simultaneously.
/// File path is printed on startup so you know where to look.
/// </summary>
public static class EdzioLog
{
    public static readonly string LogPath = Path.Combine(
        AppPaths.LogsDirectory, $"edzio-{DateTime.Now:yyyy-MM-dd}.log");

    private static readonly object _lock = new();

    static EdzioLog()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            var header = $"\n{'='.ToString().PadRight(80, '=')}\nEdzio started at {DateTime.Now:yyyy-MM-dd HH:mm:ss}\nLog file: {LogPath}\n{'='.ToString().PadRight(80, '=')}\n";
            File.AppendAllText(LogPath, header);
        }
        catch { /* never crash due to logging setup */ }
    }

    public static void Info(string component, string message)  => Write("INF", component, message);
    public static void Warn(string component, string message)  => Write("WRN", component, message);
    public static void Error(string component, string message, Exception? ex = null)
        => Write("ERR", component, ex is null ? message : $"{message} — {ex.GetType().Name}: {ex.Message}");

    private static void Write(string level, string component, string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} [{level}] [{component}] {message}";
        lock (_lock)
        {
            try { File.AppendAllText(LogPath, line + "\n"); }
            catch { }
        }
        System.Diagnostics.Debug.WriteLine(line);
    }
}

/// <summary>
/// ILoggerProvider that writes to the Edzio log file.
/// Register this so SIPSorcery's internal logging flows to the same file.
/// </summary>
public sealed class EdzioLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new EdzioFileLogger(categoryName);
    public void Dispose() { }
}

internal sealed class EdzioFileLogger : ILogger
{
    private readonly string _category;

    // Shorten the category for readability: keep only the last two segments
    private readonly string _label;

    public EdzioFileLogger(string category)
    {
        _category = category;
        var parts = category.Split('.');
        _label = parts.Length > 2 ? string.Join(".", parts[^2..]) : category;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    // Log DEBUG+ for our own code, INFO+ for SIPSorcery (very verbose at debug level)
    public bool IsEnabled(LogLevel level)
        => _category.StartsWith("Edzio") ? level >= LogLevel.Debug : level >= LogLevel.Information;

    public void Log<TState>(LogLevel level, EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(level)) return;
        var lvl = level switch
        {
            LogLevel.Trace       => "TRC",
            LogLevel.Debug       => "DBG",
            LogLevel.Information => "INF",
            LogLevel.Warning     => "WRN",
            LogLevel.Error       => "ERR",
            LogLevel.Critical    => "CRT",
            _                    => "???"
        };
        var msg = formatter(state, exception);
        var line = $"{DateTime.Now:HH:mm:ss.fff} [{lvl}] [{_label}] {msg}";
        if (exception is not null) line += $"\n  {exception.GetType().Name}: {exception.Message}";
        lock (EdzioLog.LogPath)
        {
            try { File.AppendAllText(EdzioLog.LogPath, line + "\n"); }
            catch { }
        }
        System.Diagnostics.Debug.WriteLine(line);
    }
}
