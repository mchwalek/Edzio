# Instant LAN Send (pairdrop-style nearby devices) — Design

## Problem

Nearby-device discovery is broken: it inconsistently shows devices, and when it
shows anything it includes unrelated mDNS services (the current PC, a set-top
box, printers, etc.) because `MdnsDiscovery` queries **all** mDNS services
(`QueryAllServices`) instead of filtering to Edzio's own service type. There is
also no peer-removal signal, so stale peers never disappear.

Separately, tapping a nearby peer today does nothing — `SendViewModel`'s
`LocalPeerIp/Port/Name` query properties are no-ops. The desired end state
(per https://pairdrop.net/) is: see nearby Edzio devices reliably, tap one, the
receiver gets an Accept/Decline prompt naming the sender, and on accept the
transfer runs automatically over the LAN — no pairing code, no signaling
server involved for same-LAN transfers.

## Scope

**In scope:** fixing mDNS discovery correctness (filtering, self-exclusion,
liveness/removal); an always-on background LAN listener that advertises via
mDNS and accepts instant-send connections app-wide; a minimal offer/accept
control handshake; wiring "tap a nearby peer" to a real send; an
Accept/Decline prompt on the receiver.

**Out of scope:** changing the existing signaling-server + pairing-code flow
for cross-network transfers (unchanged); concurrent multi-transfer queueing
(explicitly deferred, see below); any changes to `TransferSession` wire
format after the handshake (Chunk/Done/ManifestChunk/etc. stay as-is).

## Architecture

The transfer engine (`TransferSession.SendAsync`/`ReceiveAsync`) already
operates over any `ITransferChannel`, and `LanDirect`/`LanDirectListener`
(`src/Edzio.Core/Lan/LanDirect.cs`) already produce an authenticated,
TLS-secured `TcpTransferChannel` given a `LanEndpointAdvertisement` (address
list, port, one-time token, cert SHA-256 fingerprint). Today that
advertisement is relayed through the signaling server's ICE-candidate channel
as an optimization within an already-established session.

This design repurposes the *same* `LanDirectListener` as an always-on,
app-wide service whose advertisement is published via mDNS instead of
signaling, so a transfer can bootstrap over LAN alone. A short control
handshake (offer → accept/decline) is added on top of the resulting channel,
before handing it to the unchanged `TransferSession`.

```
[Sender taps peer]
  -> resolve live LocalPeer (IP/port/cert) from ILocalDiscovery by instance-id
  -> LanDirect.TryConnectAsync(advertisement)         (existing, TLS + token auth)
  -> send Offer {senderName, files[]}                  (new control message)
  -> await Accept/Decline                              (new control message)
  -> [Accept] TransferSession.SendAsync(...)            (existing, unchanged)

[Receiver, always running]
  InstantReceiveService (singleton, started at app launch)
    -> LanDirectListener.Start()                        (existing)
    -> feeds real port + cert fingerprint + instance-id to MdnsDiscovery.StartAsync
    -> loop: AcceptAsync -> read Offer -> raise IncomingOffer event
         -> UI shows Accept/Decline dialog naming sender + file summary
         -> [Accept] send Accept response -> TransferSession.ReceiveAsync(...)
         -> [Decline] send Decline response -> close
```

## Components

### 1. `MdnsDiscovery` fix (`src/Edzio.Core/Discovery/MdnsDiscovery.cs`)

- Filter `OnServiceInstanceDiscovered`/resolution to instances whose service
  type is `_edzio._tcp` only. This alone removes unrelated devices (set-top
  box, printers, Chromecast, etc.).
- Resolve SRV (port), TXT (`displayName`, `certSha256`, `instanceId`,
  `version`), and A/AAAA records scoped to that specific service instance —
  not a blind scrape of all `Answers`/`AdditionalRecords`.
- Advertise a random `instanceId` (GUID) in our own TXT record; skip any
  discovered instance whose `instanceId` matches ours. This replaces the
  fragile machine-name self-check.
- Handle `ServiceInstanceShutdown` and add a periodic liveness sweep
  (peer expires if not re-seen within a timeout) so stale peers are dropped.
- `ILocalDiscovery` gains a way to signal removal (either a new
  `PeerLeft`/`PeersChanged`-with-current-snapshot approach — implementation
  detail for the plan, but the contract must let the UI drop stale peers, not
  only add them).
- `LocalPeer` gains `CertSha256Hex` and `InstanceId` fields (`DisplayName`,
  `IpAddress`, `Port` already exist).
- `MdnsDiscovery.StartAsync` no longer self-generates the advertised port
  (previously a fixed default of 7777); it advertises whatever port + cert
  fingerprint + instance-id `InstantReceiveService` supplies (see below), so
  discovery reflects the real listener identity.

### 2. `InstantReceiveService` (new, Core, singleton)

Owns the always-on LAN identity:

- On start: `LanDirectListener.Start()` (existing type, unchanged), then
  calls `MdnsDiscovery.StartAsync` with that listener's real port + cert
  fingerprint + a generated instance-id, so discovery advertises the correct
  values. One class owns the LAN identity; `MdnsDiscovery` remains purely the
  mDNS transport for it (per approved wiring: "service owns listener, feeds
  MdnsDiscovery").
- Runs an accept loop: `LanDirectListener.AcceptAsync()` → read the Offer
  control message → raise an `IncomingOffer` event (sender name + file
  list) for the UI → wait for the app's accept/decline response → send the
  Accept/Decline control message back → on accept, run
  `TransferSession.ReceiveAsync` into `_settings.DownloadLocation`; on
  decline, close.
- Started once at app launch (`MauiProgram`/App startup), not tied to any
  page's lifecycle — matches the "always-on" decision. `HomeViewModel` no
  longer starts/stops discovery on `OnAppearing`/`OnDisappearing`.
- Concurrency: one inbound transfer at a time to start.
  `// ponytail: serialize incoming transfers; add a queue if concurrent sends matter.`
  A second offer arriving mid-transfer is declined automatically.

### 3. Control handshake (new wire messages)

Two new `TransferMessageType` byte tags (next free values after existing
0x01–0x09), sent over the `TcpTransferChannel` before the existing protocol
begins:

- **Offer** (sender → receiver): UTF-8 JSON `{ senderName, files: [{name,
  size}] }`. Enough for the Accept/Decline dialog; validated (size caps,
  well-formed JSON) before being surfaced to the UI, since this is
  unauthenticated-content-wise (though the connection itself is already
  TLS+token authenticated by `LanDirect`) trust-boundary input.
- **Response** (receiver → sender): Accept or Decline. On Decline, both
  sides close the channel cleanly. On Accept, both sides proceed directly
  into the existing `TransferSession.SendAsync`/`ReceiveAsync` — the wire
  protocol after this point is entirely unchanged (ManifestChunk 0x06 →
  Resume 0x07 → Chunk 0x03 → Done 0x04, etc.).

### 4. Sender flow (`SendViewModel`)

- `SendToLocalPeerCommand` (`HomeViewModel`) navigates passing only the
  peer's `instanceId` (not the full IP/port/cert) — per approved decision,
  to avoid stale/bloated navigation state.
- `SendViewModel`'s `LocalPeerId` query property (replacing the current
  no-op `LocalPeerIp/Port/Name` properties) resolves the live `LocalPeer`
  from the shared `ILocalDiscovery` peer list by instance-id.
- Send flow: build manifest from selected files → construct
  `LanEndpointAdvertisement` from the resolved peer →
  `LanDirect.TryConnectAsync` → send Offer → await Accept/Decline → on
  accept, `TransferSession.SendAsync`. No `ISignalingClient`, no pairing
  code, no `TransferChannelNegotiator` involved in this path.
- Connect failure, decline, or handshake timeout surface a clear error to
  the sender and close cleanly — not simplified away.

### 5. Receiver UI

- `InstantReceiveService.IncomingOffer` is wired at the app/shell level (not
  a specific page) so the Accept/Decline dialog appears regardless of the
  current page — matches "always-on" behavior.
- Dialog names the sender and summarizes the files (count/names or a short
  summary) before the user accepts or declines.
- On accept, the existing receive progress UI reports transfer progress as
  it does today.

### 6. DI / lifecycle (`MauiProgram.cs`)

- `InstantReceiveService` registered as a singleton, started at app launch.
- `ILocalDiscovery` (`MdnsDiscovery`) stays a singleton but is now driven by
  `InstantReceiveService` (real port/cert) instead of self-advertising a
  fixed port.
- `// ponytail: start listener at app launch; no explicit teardown beyond
  app exit.`

## Error Handling

- Trust boundary at the Offer message: validate JSON shape and cap file-list
  size before it reaches UI code or triggers any disk activity.
- `LanDirect`'s existing TLS + one-time-token authentication continues to
  gate *who* can open a channel; the new Accept/Decline gate controls *what*
  gets written to disk. Both layers are kept — accept/decline is not a
  substitute for transport auth.
- Connection failures, timeouts, and declines all produce a clear
  sender-side error and a clean channel close on both ends; no silent
  failures.

## Testing

- **Discovery filtering regression test:** feed `MdnsDiscovery` (or its
  underlying `Makaretu.Dns` event handlers) a mix of instances — a
  non-Edzio service (e.g., set-top box), our own instance-id, and a genuine
  peer — and assert only the genuine peer surfaces in `DiscoveredPeers`.
- **Removal/liveness test:** simulate a shutdown or expiry and assert the
  peer is dropped. `FakeLocalDiscovery`
  (`tests/Edzio.Core.Tests/Discovery/FakeLocalDiscovery.cs`) already exposes
  `SimulateDiscovery`/`SimulateRemoval` hooks for consumers of
  `ILocalDiscovery`; the real `MdnsDiscovery` needs equivalent coverage for
  its own filtering/removal logic.
- **Control handshake test:** loopback test using a real
  `LanDirectListener` + `LanDirect.TryConnectAsync`, asserting
  offer → accept → `TransferSession` runs to completion, and
  offer → decline → clean close with no data written.
- Existing `TransferSession`, `LanDirect`, and signaling-based transfer
  tests remain valid and unchanged — the post-accept protocol is untouched.

## Open Items For The Implementation Plan

None outstanding — all decisions above were confirmed with the user during
brainstorming (scope, consent model, always-on listener, mDNS TXT for
advertisement, service-owns-listener wiring, instance-id lookup for the
sender). The plan should decide concrete implementation details (exact
`ILocalDiscovery` removal-signal shape, exact JSON schema for Offer/Response,
exact `TransferMessageType` byte values) as part of task breakdown.
