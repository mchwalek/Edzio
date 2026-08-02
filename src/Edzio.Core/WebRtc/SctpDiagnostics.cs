using System.Collections;
using System.Reflection;
using SIPSorcery.Net;

namespace Edzio.Core.WebRtc;

/// <summary>
/// One read-only sample of SIPSorcery's SCTP sender state.
/// </summary>
/// <param name="CongestionWindow">Bytes the sender is allowed to have in flight.</param>
/// <param name="OutstandingBytes">Bytes actually in flight and unacknowledged.</param>
/// <param name="ReceiverWindow">The peer's advertised receive window.</param>
/// <param name="RetransmissionTimeout">Current RTO in seconds, the sender's RTT estimate.</param>
/// <param name="MissingChunks">Chunks reported missing by SACKs — non-zero means loss.</param>
internal readonly record struct SctpSample(
    uint CongestionWindow,
    uint OutstandingBytes,
    uint ReceiverWindow,
    double RetransmissionTimeout,
    int MissingChunks);

/// <summary>
/// Read-only diagnostic access to SIPSorcery's internal <c>SctpDataSender</c>.
/// </summary>
/// <remarks>
/// Diagnostics only — this type never mutates SCTP state and never throws into the
/// transfer path. It exists to test the hypothesis that WAN throughput is bounded by a
/// congestion window pinned near its 4380-byte RFC 4960 initial value rather than by
/// the link. See docs/superpowers/specs/2026-08-02-wan-transfer-throughput-design.md.
///
/// Every failure mode returns null. Members are located by declared type name where
/// possible, matching <see cref="WebRtcChannel.FindMemberValueByTypeName"/>; the leaf
/// fields have no distinguishing type and must be named, which is the fragile part and
/// is why SctpDiagnosticsTests exists.
/// </remarks>
internal static class SctpDiagnostics
{
    private const BindingFlags MemberFlags =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    /// <summary>
    /// Walks the peer connection down to the live SCTP data sender.
    /// Returns null if the association is not established or the internals moved.
    /// </summary>
    internal static object? TryResolveDataSender(RTCPeerConnection pc)
    {
        try
        {
            object? transport = pc.sctp;
            if (transport is null) return null;

            var association = WebRtcChannel.FindMemberValueByTypeName(transport, "SctpAssociation");
            if (association is null) return null;

            return WebRtcChannel.FindMemberValueByTypeName(association, "SctpDataSender");
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Reads one snapshot of the sender's congestion state.
    /// Returns null if any field is missing, which means SIPSorcery's internals changed.
    /// </summary>
    internal static SctpSample? TrySample(object dataSender)
    {
        try
        {
            var type = dataSender.GetType();

            if (TryReadUInt(type, dataSender, "_congestionWindow") is not { } cwnd) return null;
            if (TryReadUInt(type, dataSender, "_outstandingBytes") is not { } outstanding) return null;
            if (TryReadUInt(type, dataSender, "_receiverWindow") is not { } rwnd) return null;
            if (Read(type, dataSender, "_rto") is not double rto) return null;
            if (Read(type, dataSender, "_missingChunks") is not ICollection missing) return null;

            return new SctpSample(cwnd, outstanding, rwnd, rto, missing.Count);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Samples the sender every <paramref name="interval"/> and writes one line per
    /// sample to <paramref name="log"/>. Dispose the result to stop sampling.
    /// Returns a no-op disposable if the sender cannot be resolved.
    /// </summary>
    internal static IDisposable Start(
        RTCPeerConnection pc,
        string label,
        Action<string> log,
        TimeSpan interval)
    {
        var sender = TryResolveDataSender(pc);
        if (sender is null)
        {
            SafeLog(log, $"[SctpDiag {label}] unavailable — SIPSorcery internals changed?");
            return new Sampler(null);
        }

        var cts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            // Both failure modes below are reported exactly once. A silent sampler is
            // indistinguishable from a healthy quiet link, but a per-iteration complaint
            // would bury the samples this type exists to produce.
            var sampleFailureLogged = false;

            try
            {
                while (!cts.IsCancellationRequested)
                {
                    if (TrySample(sender) is { } s)
                    {
                        SafeLog(log, $"[SctpDiag {label}] cwnd={s.CongestionWindow} " +
                            $"outstanding={s.OutstandingBytes} rwnd={s.ReceiverWindow} " +
                            $"rto={s.RetransmissionTimeout:F3}s missing={s.MissingChunks}");
                    }
                    else if (!sampleFailureLogged)
                    {
                        sampleFailureLogged = true;
                        SafeLog(log, $"[SctpDiag {label}] sampling failed — SIPSorcery " +
                            "internals changed? (reported once; sampling continues)");
                    }

                    await Task.Delay(interval, cts.Token);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected on dispose.
            }
            catch (Exception ex)
            {
                // Diagnostics must never fault the transfer, but they must not vanish
                // without a trace either — this is the last line this sampler will write.
                SafeLog(log, $"[SctpDiag {label}] sampling stopped: " +
                    $"{ex.GetType().Name}: {ex.Message}");
            }
        }, cts.Token);

        return new Sampler(cts);
    }

    /// <summary>
    /// Writes one line to <paramref name="log"/>, absorbing any fault from the
    /// caller-supplied delegate. Diagnostics must never throw into the transfer path.
    /// </summary>
    private static void SafeLog(Action<string> log, string message)
    {
        try
        {
            log(message);
        }
        catch
        {
            // A broken log sink is not worth failing a transfer over.
        }
    }

    private static object? Read(Type type, object instance, string fieldName)
    {
        for (var t = type; t is not null; t = t.BaseType)
        {
            var field = t.GetField(fieldName, MemberFlags);
            if (field is not null) return field.GetValue(instance);

            var property = t.GetProperty(fieldName, MemberFlags);
            if (property is not null) return property.GetValue(instance);
        }

        return null;
    }

    // _outstandingBytes is a computed property in some versions and a field in others;
    // both are uint, but read defensively in case the width changes.
    private static uint? TryReadUInt(Type type, object instance, string fieldName) =>
        Read(type, instance, fieldName) switch
        {
            uint u => u,
            int i and >= 0 => (uint)i,
            long l and >= 0 => (uint)l,
            _ => null,
        };

    private sealed class Sampler(CancellationTokenSource? cts) : IDisposable
    {
        public void Dispose()
        {
            cts?.Cancel();
            cts?.Dispose();
        }
    }
}
