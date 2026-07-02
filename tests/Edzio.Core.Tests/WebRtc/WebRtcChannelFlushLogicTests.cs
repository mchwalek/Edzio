using Edzio.Core.WebRtc;
using FluentAssertions;
using Xunit;

namespace Edzio.Core.Tests.WebRtc;

/// <summary>
/// Deterministic, real-time-independent tests for
/// <see cref="WebRtcChannel.WaitForDrainOrStallAsync"/> — the stall-detection
/// algorithm used by <c>DisposeAsync</c> to decide when to give up waiting for
/// the outbound SCTP send buffer to drain.
///
/// These use fake <c>utcNow</c>/<c>delay</c> functions (no real
/// <c>Task.Delay</c> waits) so the tests run instantly and can simulate
/// arbitrarily long elapsed time — including durations that would have
/// exceeded the original, buggy fixed 10-second timeout — without actually
/// waiting for it.
/// </summary>
public class WebRtcChannelFlushLogicTests
{
    /// <summary>
    /// Regression test: a large transfer whose send buffer keeps shrinking
    /// (i.e. genuine progress is being made) must eventually report "drained",
    /// even if the total simulated elapsed time is far longer than the
    /// original fixed 10-second timeout. This is the exact bug reported in
    /// production for an ~84 MB file: the old fixed-timeout implementation
    /// would give up and close the connection long before a large file's send
    /// queue could realistically drain, regardless of whether transmission was
    /// still actively progressing.
    /// </summary>
    [Fact]
    public async Task WaitForDrainOrStallAsync_KeepsWaitingWhileBufferShrinks_EvenPastOldFixedTimeout()
    {
        // Simulates a large file's send queue draining slowly: 1000 polls,
        // each representing 1 second of simulated time (1000s total — far
        // longer than the old fixed 10s timeout), decreasing by 1 unit each
        // poll until it reaches zero.
        const int totalPolls = 1000;
        var bufferedAmount = (ulong)totalPolls;
        var simulatedNow = DateTimeOffset.UtcNow;

        var result = await WebRtcChannel.WaitForDrainOrStallAsync(
            getBufferedAmount: () => bufferedAmount,
            pollInterval: TimeSpan.FromMilliseconds(20), // irrelevant with a fake delay
            stallTimeout: TimeSpan.FromSeconds(15),
            delay: _ =>
            {
                // Each "poll" advances simulated time by 1 second (progress
                // keeps resetting the stall clock) and drains one more unit.
                simulatedNow = simulatedNow.AddSeconds(1);
                if (bufferedAmount > 0) bufferedAmount--;
                return Task.CompletedTask;
            },
            utcNow: () => simulatedNow);

        result.Should().BeTrue("the buffer fully drained, even though total elapsed time (1000s) " +
                                "far exceeded the old fixed 10s timeout");
        bufferedAmount.Should().Be(0);
    }

    /// <summary>
    /// A connection that stops making progress (buffered amount stays constant)
    /// must be detected as stalled and given up on after <c>stallTimeout</c> —
    /// this is what actually indicates a dead/unresponsive peer, as opposed to
    /// merely "a lot of time has passed."
    /// </summary>
    [Fact]
    public async Task WaitForDrainOrStallAsync_GivesUp_WhenBufferStopsShrinking()
    {
        const ulong stalledAt = 42;
        var simulatedNow = DateTimeOffset.UtcNow;

        var result = await WebRtcChannel.WaitForDrainOrStallAsync(
            getBufferedAmount: () => stalledAt, // never decreases
            pollInterval: TimeSpan.FromMilliseconds(20),
            stallTimeout: TimeSpan.FromSeconds(15),
            delay: _ =>
            {
                simulatedNow = simulatedNow.AddSeconds(1);
                return Task.CompletedTask;
            },
            utcNow: () => simulatedNow);

        result.Should().BeFalse("the buffer never decreased, so this should be treated as a stall " +
                                 "once stallTimeout has elapsed with no progress");
    }

    /// <summary>
    /// If the buffer is already at zero, no waiting/delay should occur at all.
    /// </summary>
    [Fact]
    public async Task WaitForDrainOrStallAsync_AlreadyZero_ReturnsImmediatelyWithoutDelay()
    {
        var delayCalls = 0;

        var result = await WebRtcChannel.WaitForDrainOrStallAsync(
            getBufferedAmount: () => 0,
            pollInterval: TimeSpan.FromMilliseconds(20),
            stallTimeout: TimeSpan.FromSeconds(15),
            delay: _ => { delayCalls++; return Task.CompletedTask; },
            utcNow: () => DateTimeOffset.UtcNow);

        result.Should().BeTrue();
        delayCalls.Should().Be(0);
    }
}
