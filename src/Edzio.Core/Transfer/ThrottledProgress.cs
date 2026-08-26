namespace Edzio.Core.Transfer;

/// <summary>
/// Wraps an <see cref="IProgress{T}"/> so that reports are forwarded at most
/// once per <paramref name="minInterval"/>, smoothing high-frequency progress
/// callbacks (e.g. one per transfer chunk) into a UI-friendly refresh rate.
/// The final report — determined by <paramref name="isFinal"/> — is always
/// forwarded even if it arrives before the interval elapses, so completion
/// is never dropped.
/// </summary>
public class ThrottledProgress<T> : IProgress<T>
{
    private readonly IProgress<T> _inner;
    private readonly TimeSpan _minInterval;
    private readonly Func<T, bool> _isFinal;
    private readonly Func<DateTimeOffset> _now;
    private DateTimeOffset? _lastReportTime;

    /// <param name="inner">The progress sink to forward throttled reports to.</param>
    /// <param name="minInterval">Minimum time between forwarded reports.</param>
    /// <param name="isFinal">Predicate identifying a report that must always be forwarded (e.g. 100% complete).</param>
    /// <param name="now">Current time source, injected for testability.</param>
    public ThrottledProgress(IProgress<T> inner, TimeSpan minInterval, Func<T, bool> isFinal, Func<DateTimeOffset>? now = null)
    {
        _inner = inner;
        _minInterval = minInterval;
        _isFinal = isFinal;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public void Report(T value)
    {
        var now = _now();
        if (_isFinal(value) || _lastReportTime is null || now - _lastReportTime.Value >= _minInterval)
        {
            _lastReportTime = now;
            _inner.Report(value);
        }
    }
}
