using Edzio.Core.WebRtc;
using FluentAssertions;
using Xunit;

namespace Edzio.Core.Tests.WebRtc;

/// <summary>
/// Deterministic, real-time-independent tests for
/// <see cref="MultiWebRtcChannel.WaitForPumpsOrStallAsync"/> — the bounded-wait
/// mechanism used by <c>DisposeAsync</c> to avoid hanging forever on a lane whose
/// SCTP send buffer never drains (stalled, not failed). Mirrors
/// <c>WebRtcChannel.WaitForDrainOrStallAsync</c> and its tests in
/// <see cref="WebRtcChannelFlushLogicTests"/>: a fake <c>delay</c> function lets
/// these run instantly instead of waiting out a real 15-second timeout.
/// </summary>
public class MultiWebRtcChannelDisposeLogicTests
{
    /// <summary>
    /// Regression test: a pump loop that never completes (the stalled-lane case
    /// I2 describes — stuck inside <c>WaitForSendBufferSpaceAsync</c> with no
    /// SCTP-level failure to observe) must not hang <c>DisposeAsync</c> forever;
    /// once the stall timeout elapses, the wait must give up and report "not
    /// completed in time" so the caller can cancel and unblock it.
    /// </summary>
    [Fact]
    public async Task WaitForPumpsOrStallAsync_ReturnsFalse_WhenPumpsNeverComplete()
    {
        var neverCompletes = new TaskCompletionSource();

        var result = await MultiWebRtcChannel.WaitForPumpsOrStallAsync(
            neverCompletes.Task,
            TimeSpan.FromSeconds(15),
            delay: _ => Task.CompletedTask); // simulates the stall timeout elapsing instantly

        result.Should().BeFalse("the pumps never completed, so this must be treated as a stall");
    }

    /// <summary>
    /// The normal (non-stalled) case: pumps that finish on their own must be
    /// reported as completed without waiting for the stall timeout at all — this
    /// is what preserves in-flight message delivery guarantees during a clean
    /// shutdown.
    /// </summary>
    [Fact]
    public async Task WaitForPumpsOrStallAsync_ReturnsTrue_WhenPumpsCompleteBeforeTimeout()
    {
        // An infinite delay proves the pumps branch won on its own — if the
        // implementation waited on the timeout instead, this test would hang.
        var result = await MultiWebRtcChannel.WaitForPumpsOrStallAsync(
            Task.CompletedTask,
            TimeSpan.FromSeconds(15),
            delay: _ => new TaskCompletionSource().Task);

        result.Should().BeTrue();
    }
}
