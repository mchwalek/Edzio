using System.Reflection;

namespace Edzio.Desktop.Services;

/// <summary>
/// Exposes the build timestamp embedded by the csproj's AssemblyMetadata item,
/// so the UI and logs can always show exactly which build is running.
/// </summary>
public static class BuildInfo
{
    /// <summary>Build timestamp, e.g. "2026-07-18 14:03 UTC", or "unknown" for unstamped builds.</summary>
    public static string BuildTimestamp { get; } =
        typeof(BuildInfo).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "BuildTimestampUtc")?.Value ?? "unknown";

    /// <summary>UI-ready label text, e.g. "Build: 2026-07-18 14:03 UTC".</summary>
    public static string BuildDisplayText { get; } = $"Build: {BuildTimestamp}";
}
