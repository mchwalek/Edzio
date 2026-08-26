using Edzio.Core.Transfer;
using FluentAssertions;
using Xunit;

namespace Edzio.Core.Tests.Transfer;

/// <summary>
/// A synchronous <see cref="IProgress{T}"/> test double. The built-in
/// <see cref="Progress{T}"/> posts its callback through the captured
/// <see cref="System.Threading.SynchronizationContext"/> (or the thread pool)
/// rather than invoking it inline, which makes it unsuitable for
/// synchronous assertions immediately after <c>Report</c>.
/// </summary>
file sealed class SynchronousProgress<T>(Action<T> onReport) : IProgress<T>
{
    public void Report(T value) => onReport(value);
}

public class ThrottledProgressTests
{
    [Fact]
    public void Report_FirstCall_IsForwarded()
    {
        var received = new List<int>();
        var inner = new SynchronousProgress<int>(received.Add);
        var now = DateTimeOffset.UtcNow;
        var throttled = new ThrottledProgress<int>(inner, TimeSpan.FromMilliseconds(500), _ => false, () => now);

        throttled.Report(1);

        received.Should().Equal(1);
    }

    [Fact]
    public void Report_WithinInterval_IsSuppressed()
    {
        var received = new List<int>();
        var inner = new SynchronousProgress<int>(received.Add);
        var now = DateTimeOffset.UtcNow;
        var throttled = new ThrottledProgress<int>(inner, TimeSpan.FromMilliseconds(500), _ => false, () => now);

        throttled.Report(1);
        now = now.AddMilliseconds(200);
        throttled.Report(2);

        received.Should().Equal(1);
    }

    [Fact]
    public void Report_AfterIntervalElapses_IsForwarded()
    {
        var received = new List<int>();
        var inner = new SynchronousProgress<int>(received.Add);
        var now = DateTimeOffset.UtcNow;
        var throttled = new ThrottledProgress<int>(inner, TimeSpan.FromMilliseconds(500), _ => false, () => now);

        throttled.Report(1);
        now = now.AddMilliseconds(500);
        throttled.Report(2);

        received.Should().Equal(1, 2);
    }

    [Fact]
    public void Report_FinalValue_IsAlwaysForwardedEvenWithinInterval()
    {
        var received = new List<int>();
        var inner = new SynchronousProgress<int>(received.Add);
        var now = DateTimeOffset.UtcNow;
        var throttled = new ThrottledProgress<int>(inner, TimeSpan.FromMilliseconds(500), v => v == 100, () => now);

        throttled.Report(1);
        now = now.AddMilliseconds(50);
        throttled.Report(100);

        received.Should().Equal(1, 100);
    }
}
