namespace Edzio.Core.Transfer;

/// <summary>
/// A snapshot of the current transfer rate and estimated time remaining,
/// produced by <see cref="TransferRateTracker.Sample"/>.
/// </summary>
/// <param name="BytesPerSecond">Smoothed transfer rate in bytes per second. Zero until a rate can be computed.</param>
/// <param name="EtaSeconds">Estimated seconds remaining, or null if it cannot yet be computed (no rate yet, or total bytes unknown).</param>
public record TransferRateSnapshot(double BytesPerSecond, double? EtaSeconds);

/// <summary>
/// Tracks bytes-transferred samples over time and computes a smoothed
/// transfer rate (exponential moving average) and an ETA. Instances are
/// not thread-safe and are meant to be used by a single transfer's progress
/// callback.
/// </summary>
public class TransferRateTracker
{
    // ponytail: lower weight than a naive 0.3 because callers now throttle
    // reports to ~2/sec (see ThrottledProgress); a heavier smooth keeps the
    // displayed rate stable rather than tracking every instant fluctuation.
    private const double SmoothingFactor = 0.15;

    private DateTimeOffset? _lastSampleTime;
    private long _lastBytes;
    private double? _smoothedRate;

    /// <summary>
    /// Records a new progress sample and returns the current rate/ETA snapshot.
    /// The first call establishes a baseline and always returns a zero rate
    /// with a null ETA.
    /// </summary>
    /// <param name="bytesSoFar">Total bytes transferred so far.</param>
    /// <param name="totalBytes">Total bytes expected for the whole transfer.</param>
    /// <param name="now">Current time, passed explicitly so this class is testable without wall-clock delays.</param>
    public TransferRateSnapshot Sample(long bytesSoFar, long totalBytes, DateTimeOffset now)
    {
        if (_lastSampleTime is null)
        {
            _lastSampleTime = now;
            _lastBytes = bytesSoFar;
            return new TransferRateSnapshot(0, null);
        }

        var elapsedSeconds = (now - _lastSampleTime.Value).TotalSeconds;
        if (elapsedSeconds > 0)
        {
            var instantRate = (bytesSoFar - _lastBytes) / elapsedSeconds;
            _smoothedRate = _smoothedRate is null
                ? instantRate
                : SmoothingFactor * instantRate + (1 - SmoothingFactor) * _smoothedRate.Value;

            _lastSampleTime = now;
            _lastBytes = bytesSoFar;
        }

        var rate = _smoothedRate ?? 0;
        return new TransferRateSnapshot(rate, ComputeEtaSeconds(rate, bytesSoFar, totalBytes));
    }

    private static double? ComputeEtaSeconds(double bytesPerSecond, long bytesSoFar, long totalBytes)
    {
        if (bytesPerSecond <= 0 || totalBytes <= 0) return null;

        var remainingBytes = totalBytes - bytesSoFar;
        if (remainingBytes <= 0) return 0;

        return remainingBytes / bytesPerSecond;
    }
}
