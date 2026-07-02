namespace Edzio.Desktop.Services;

/// <summary>
/// Single source of truth for every path Edzio Desktop writes to on disk.
/// All app storage lives under a single, predictable root
/// (%LOCALAPPDATA%\Edzio on Windows) instead of the inconsistent,
/// app-identity-derived <c>FileSystem.AppDataDirectory</c>.
/// </summary>
public static class AppPaths
{
    /// <summary>Root folder for all Edzio application data.</summary>
    public static string RootDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Edzio");

    /// <summary>Folder where rolling log files are written.</summary>
    public static string LogsDirectory => Path.Combine(RootDirectory, "logs");

    /// <summary>Full path of the SQLite transfer-history database.</summary>
    public static string DatabasePath => Path.Combine(RootDirectory, "transfers.db");

    /// <summary>
    /// Default download destination shown in Settings until the user picks
    /// a different folder. Uses the user's home directory + "Downloads" —
    /// no platform-specific API, works the same way .NET resolves the user
    /// profile on Windows, macOS, and Linux, and matches every mainstream
    /// OS's own convention for where downloads land (least astonishment).
    /// </summary>
    public static string DefaultDownloadDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    static AppPaths()
    {
        Directory.CreateDirectory(RootDirectory);
    }
}
