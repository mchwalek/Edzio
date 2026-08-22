# WAN Transfer Throughput — Design

**Date:** 2026-08-02
**Status:** Approved, ready for implementation planning
**Scope:** Improve WebRTC transfer throughput between peers on different networks.
**Out of scope:** LAN-direct transport (already shipped in `49255af`), NAT traversal / TURN, the intermittent LAN-direct fallback tracked as Phase B in `docs/debug/slow-webrtc-transfer-throughput/PROGRESS.md`.

## Problem

Peers on different networks connect successfully but transfer at roughly 1–2 MB/s
against a 54 Mbps (6.75 MB/s) uplink — 20–30% of the available link. The LAN-direct
TCP path added in `49255af` does not apply, so these transfers fall through to
`WebRtcChannel` and SIPSorcery's SCTP implementation.

## Root cause

`docs/debug/slow-webrtc-transfer-throughput/PROGRESS.md` currently attributes the
ceiling to `MAX_BURST (4) × MTU (1300) / RTT` ≈ 5200 bytes per RTT. Reading the
actual v8.0.23 source
(`https://raw.githubusercontent.com/sipsorcery-org/sipsorcery/v8.0.23/src/net/SCTP/SctpDataSender.cs`)
shows the binding constraint is the congestion window, not the burst size:

```csharp
// ctor — RFC 4960 7.2.1. With MTU 1300 this evaluates to 4380 bytes.
_congestionWindow = Math.Min(4 * _defaultMTU, Math.Max(2 * _defaultMTU, CONGESTION_WINDOW_FACTOR));

// DoSend — new data is only sent while cwnd exceeds bytes in flight
if (chunksSent < burstSize && _sendQueue.Count > 0 && _congestionWindow > outstandingBytes) { ... }

// CalculateCongestionWindow — slow start only grows cwnd while cwnd is below bytes in flight
if (_congestionWindow < _outstandingBytes) return _congestionWindow + Math.Min(lastAckedSize, _defaultMTU);
else return _congestionWindow;
```

The send condition and the growth condition are near-mutually-exclusive. The sender
stops adding data once `outstanding >= cwnd`, and growth requires `cwnd < outstanding`.
The window therefore only creeps upward on the small overshoot a burst leaves behind
(the inner send loop does not re-check cwnd per packet), and in practice stays close to
its 4380-byte initial value for the life of the association.

`4380 / RTT` matches the measured data better than `5200 / RTT`:

| Path | RTT | Predicted | Observed |
| --- | --- | --- | --- |
| Loopback | ~0.2 ms | ~21 MB/s | 23.9 MB/s |
| Wi-Fi LAN | ~5 ms | 0.87 MB/s | 0.86 MB/s |
| WAN | ~3 ms | ~1.4 MB/s | 1–2 MB/s |

This also explains why the existing `_burstPeriodMilliseconds` 50 → 1 ms reflection
hack (`WebRtcChannel.cs:328`) only lifted WAN to 1–2 MB/s: it removed the *sleep* but
not the *window*.

### Rejected: forcing the congestion window by reflection

`_congestionWindow` is `internal` and the existing reflection walk already reaches
`SctpDataSender`, so it is writable. It is still not a safe lever. On any loss both the
fast-recovery and retransmit paths set `_congestionWindow = _defaultMTU` (1300 bytes)
with no working growth path back up. On a lossy WAN link that converts a slow transfer
into a stalled one. Correcting this from outside means reimplementing RFC 4960
congestion control through reflection against a moving upstream target.

### Rejected: raising the SCTP MTU

Initial cwnd scales with MTU, but anything meaningfully above ~1400 bytes causes IP
fragmentation of the DTLS/UDP datagrams on real internet paths, which is worse on
exactly the lossy links this is meant to help. No usable headroom.

### Rejected: libdatachannel via P/Invoke

The upstream-quality fix, but a multi-week native subproject: 5+ RIDs, an iOS static
xcframework, per-platform CI. Android and iOS clients are on the roadmap, which makes a
native dependency substantially more expensive than it looks today. Revisit only if the
managed approach below fails to deliver.

## Phase C — Measure

No behavior change. Nothing ships to users. The purpose is to confirm the congestion
window model on a real cross-network path before building anything, and to capture an
N=1 baseline for Phase A to be measured against.

**Implementation.** Extend the existing reflection walk in
`WebRtcChannel.TryReduceSctpBurstPeriod` (`WebRtcChannel.cs:328`) into a **read-only**
`SctpDiagnostics` that samples the live `SctpDataSender` on a ~500 ms timer while a
transfer is in progress and writes one line per sample to the existing log sink
(`%LOCALAPPDATA%\Edzio\logs\edzio-YYYY-MM-DD.log`).

Fields sampled — all `internal` on `SctpDataSender` in v8.0.23:

| Field | Type | Why |
| --- | --- | --- |
| `_congestionWindow` | `uint` | The hypothesis under test |
| `_outstandingBytes` | `uint` (computed) | Confirms the sender is window-blocked, not queue-starved |
| `_receiverWindow` | `uint` | Rules out receiver-side flow control as the limiter |
| `_rto` | `double` | Gives the RTT estimate the model needs |
| `_missingChunks.Count` | `int` | Detects loss, which would explain a collapsed window |

Same defensive posture as the existing walk: members located by **type name** rather
than member name (`FindMemberValueByTypeName`, `WebRtcChannel.cs:354`), any failure
returns silently, and nothing throws into the transfer path. `_slowStartThreshold` is
`private` and is deliberately not sampled.

Also log the ICE-selected candidate pair type, to confirm the WAN path is going
host/srflx direct and is not silently relaying through TURN. Two lines, folded in here
because it is cheap and would otherwise be a confound.

**Success criterion (falsifiable, fixed in advance).** The congestion window stays
below ~10 KB for the whole transfer. If it climbs into the hundreds of KB, the model
above is wrong, Phase A is unjustified, and work stops for re-diagnosis rather than
proceeding.

**Deliverable.** One WAN run on a fixed test file with logs from both peers, appended
as Session 7 to `docs/debug/slow-webrtc-transfer-throughput/PROGRESS.md`, plus a
correction to that document's "Root Cause" section, which currently names `MAX_BURST`
alone.

## Phase A — Parallel SCTP associations

Conditional on Phase C confirming the model.

One association is pinned near 4380 bytes in flight. Run N of them. Aggregate
throughput ≈ `N × 4380 / RTT`.

The transfer protocol needs no wire-format change. Chunks carry `(fileIndex,
chunkIndex)`, are written at manifest-derived offsets
(`ChunkEngine.WriteChunkAsync:127`), and are SHA-256 verified individually on receipt.
**Chunk ordering is already irrelevant**, which is what makes striping across
independent associations safe.

### Composition

`MultiWebRtcChannel : ITransferChannel` owns N `WebRtcChannel` instances.
`TransferChannelNegotiator` constructs it in place of `WebRtcChannel` in the WebRTC
fallback branch — `ConnectAsSenderAsync` (`TransferChannelNegotiator.cs:88-98`) and
`ConnectAsReceiverAsync` (`TransferChannelNegotiator.cs:113`). The LAN-direct path is
untouched; `TcpTransferChannel` already runs at wire speed and gains nothing here.

### Signaling demultiplexing

N peer connections share one `ISignalingClient`, so offers, answers and ICE candidates
must be routed to the correct lane.

`IndexedSignalingClient : ISignalingClient` decorates the shared client: it tags
outbound payloads with a lane index and re-raises inbound events only when the index
matches. Each `WebRtcChannel` receives its own instance and requires **no
modification** — the demux lives entirely in the decorator.

This reuses the envelope trick already proven by `edzioLanEndpoint`: `SignalingHub`
relays these strings blindly (`SignalingHub.cs:56-62`), so the deployed Azure signaling
server needs no change or redeploy.

Constraint: `IndexedSignalingClient.DisposeAsync` must **not** dispose the shared inner
client.

### Send striping

`MultiWebRtcChannel.SendAsync` writes into a `Channel.CreateBounded<byte[]>(N * 2)`.
N pump tasks each drain that queue into their own lane.

Work-stealing falls out of the shared queue for free, so a temporarily slow lane cannot
head-of-line the others — which a round-robin assignment would, because
`WebRtcChannel.SendAsync` blocks in `WaitForSendBufferSpaceAsync`
(`WebRtcChannel.cs:400`).

The queue is bounded so backpressure still propagates to
`ChunkEngine.ReadChunksAsync`; an unbounded queue would buffer the entire file in
memory.

### Ordering and the flush barrier

The single ordering constraint that matters is that `Done` (`0x04`) must arrive after
every chunk. The other control messages are already self-protecting:

- The sender cannot send chunks until the Resume round-trip completes, so all
  `ManifestChunk` fragments have necessarily arrived by then.
- `TransferSession.ReceiveFragmentedAsync` (`TransferSession.cs:76`) is
  index-addressed, so fragments interleaved across lanes reassemble correctly.

To enforce the `Done` barrier, add to `ITransferChannel`:

```csharp
Task FlushAsync(CancellationToken ct = default) => Task.CompletedTask;
```

A **default interface method**, so `WebRtcChannel` and `TcpTransferChannel` need no
edit. `TransferSession` awaits it once before sending `Done`.
`MultiWebRtcChannel.FlushAsync` completes when the stripe queue is empty and every
lane's `bufferedAmount` has reached zero.

This is preferred over having `MultiWebRtcChannel` sniff the `0x04` message type byte:
comparable line count, and it keeps protocol knowledge out of the transport layer.

The receive side is an unordered merge across the N inbound queues, which the above
makes safe.

### Lane count

Fixed at **N = 8**. The design-time estimate assumed 5 ms RTT; the Task 3 WAN run
(Session 7) measured a real single-lane run instead: 18.5 MB in 18.449 s ≈ 1.05 MB/s,
cwnd climbing from 4380 to a 43,324-byte plateau over ~13 s. Back-calculating RTT from
the plateau (43,324 bytes / 1.05 MB/s) gives ~41 ms, not 5 ms. Recomputed from the
measurement directly — N = target_uplink / measured_per_lane_throughput = 6.75 MB/s /
1.05 MB/s ≈ 6.4 — N = 8 still clears this with headroom. Kept as-is rather than lowered
to 7: the 13 s climb-then-4 s-plateau shape means one 18.5 MB sample may not have
reached true steady state, so the extra margin is deliberate. It lives as an
`internal const` on `MultiWebRtcChannel` so tests can parameterize it — **not** a
user-facing setting in `SettingsViewModel`. No adaptive ramping; that needs RTT
measurement and a control loop for a value the Phase C data already fixes.

On fairness: eight lanes each bounded near a single healthy flow's own congestion
window is not the antisocial parallel-download pattern it resembles.

### Failure handling

If a lane dies mid-transfer, **the whole transfer fails** — identical to today's
single-channel behavior, so no regression. Resume already covers the retry. Graceful
degradation to N-1 lanes is deliberately not designed; it would require the stripe queue
to recover the item in flight on the dead lane.

### Implementation risks to verify

- **Setup cost.** N × `RTCPeerConnection` means N × DTLS certificate generation, and
  that constructor already blocks for hundreds of milliseconds
  (documented at `WebRtcChannel.cs:86-171`). Check whether a shared certificate can be
  supplied via `RTCConfiguration.certificates`. If it can, most of the added setup cost
  disappears; if not, construct all N concurrently and measure the added handshake time
  before accepting the default of 8.
- **Reflection fragility.** Phase C's `SctpDiagnostics` inherits the same exposure to
  SIPSorcery internals as the existing burst-period hack. It must remain covered by a
  test that fails loudly if a package bump breaks the walk, matching the existing
  guard.

### Testing

| Test | Kind | Value |
| --- | --- | --- |
| `IndexedSignalingClient` tags outbound and filters inbound by index | Pure unit, no network | Highest value per line; the demux is where routing bugs will live |
| `FlushAsync` barrier — `Done` is written only after the stripe queue drains | Unit, fake channel | Protects the one real ordering constraint |
| Existing `WebRtcChannelLoopbackTest` harness parameterized over N | Integration, skip-by-default | Detects aggregate regressions |

**Success criterion (falsifiable, fixed in advance).** On the two-machine WAN rig,
throughput scales roughly linearly with N up to the uplink limit. If N = 8 does not
reach at least 4× the N = 1 baseline, the model is wrong and work stops rather than
tuning N upward.

## Sequencing

1. Phase C — instrument, run the WAN rig, record Session 7. **Gate:** cwnd stays below
   ~10 KB, otherwise stop.
2. Phase A — `IndexedSignalingClient`, `MultiWebRtcChannel`, `FlushAsync`, negotiator
   wiring. **Gate:** N = 8 reaches ≥ 4× the N = 1 WAN baseline.

## Documentation debt to clear alongside this work

`AGENTS.md` is stale on three points found during this design:

1. SIPSorcery is **8.0.23** (`src/Edzio.Core/Edzio.Core.csproj:9`), not 6.2.3.
   `Microsoft.Extensions.Logging.Abstractions` is 10.0.7, not 8.0.
2. The protocol table omits `0x06 ManifestChunk` and `0x07 ResumeChunk`, which are the
   actively used types. `0x01 Manifest` and `0x02 Resume` are legacy and no longer sent.
3. The project structure omits `src/Edzio.Core/Lan/` and
   `src/Edzio.Core/Transfer/TransferChannelNegotiator.cs`. WebRTC is now the *fallback*
   transport, not the primary one.
