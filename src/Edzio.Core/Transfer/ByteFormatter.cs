using System.Globalization;

namespace Edzio.Core.Transfer;

/// <summary>
/// Formats byte counts, transfer rates, and durations into short,
/// human-readable strings for progress UIs.
/// </summary>
public static class ByteFormatter
{
    private static readonly string[] Units = { "B", "KB", "MB", "GB", "TB" };

    /// <summary>Formats a byte count as e.g. "512 B", "1.5 KB", "4.2 MB". Negative values clamp to zero.</summary>
    public static string Format(long bytes)
    {
        if (bytes < 0) bytes = 0;

        double value = bytes;
        int unitIndex = 0;
        while (value >= 1024 && unitIndex < Units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return unitIndex == 0
            ? $"{(long)value} {Units[unitIndex]}"
            : $"{value.ToString("0.#", CultureInfo.InvariantCulture)} {Units[unitIndex]}";
    }

    /// <summary>Formats a rate in bytes/second as e.g. "2.4 MB/s". Non-positive rates format as "0 B/s".</summary>
    public static string FormatRate(double bytesPerSecond)
    {
        if (bytesPerSecond <= 0) return "0 B/s";
        return $"{Format((long)Math.Round(bytesPerSecond))}/s";
    }

    /// <summary>
    /// Formats a duration in seconds as e.g. "12s" (under a minute) or "1:32"
    /// (minutes:seconds). Does not include a trailing word like "remaining" —
    /// callers compose that themselves.
    /// </summary>
    public static string FormatDuration(double seconds)
    {
        int total = (int)Math.Round(Math.Max(0, seconds));
        if (total < 60) return $"{total}s";

        int minutes = total / 60;
        int secs = total % 60;
        return $"{minutes}:{secs:D2}";
    }
}
