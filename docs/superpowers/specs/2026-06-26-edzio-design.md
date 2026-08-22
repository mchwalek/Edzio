# Edzio — Design Specification
*Date: 2026-06-26*

## Overview

Edzio is a cross-platform peer-to-peer file sharing application that allows users to share arbitrarily large files and folders directly between devices, without accounts, without knowing each other's IP addresses, and without any central server handling file data. It supports resumable transfers (BitTorrent-style chunking) and targets desktop, mobile, and web platforms using .NET.

Primary use case: a non-technical user (e.g., a grandparent) sends large files like vacation photos to family members on different devices and potentially different networks.

---

## Goals

- Direct P2P transfers — file data never passes through a central server
- Resumable transfers — interrupted transfers can be continued from the last completed chunk
- Folder sharing — transfer entire folder trees without zipping
- No accounts — zero sign-up required
- No IP knowledge — users pair via short human-readable codes or local auto-discovery
- Cross-platform — Windows desktop (Phase 1), then Android, web, macOS, iOS
- Non-technical UX — as easy as possible; modeled after PairDrop's simplicity

---

## Non-Goals

- Encrypted-at-rest storage (files are transferred, not stored)
- Group sharing to more than 2 peers in a single session (future)
- Chat / messaging features
- File versioning

---

## Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│                     Signaling Server                            │
│           (ASP.NET Core + SignalR, hosted by owner)             │
│  - Generates pairing codes                                      │
│  - Matches peers by code                                        │
│  - Relays WebRTC ICE candidates                                 │
│  - Never sees file data                                         │
└────────────────────────┬────────────────────────────────────────┘
                         │ SignalR WebSocket (tiny JSON messages only)
           ┌─────────────┴──────────────┐
           │                            │
    ┌──────▼──────┐              ┌──────▼──────┐
    │  Peer A     │              │  Peer B     │
    │ (Sender)    │◄────────────►│ (Receiver)  │
    │             │  WebRTC P2P  │             │
    └─────────────┘  Data Channel└─────────────┘
```

**Local network:** Peers discover each other via mDNS. No signaling server needed for same-LAN transfers.

**Internet:** Peers use a short pairing code via the signaling server to establish a WebRTC connection through STUN-assisted NAT traversal.

---

## Components

### 1. Edzio.SignalingServer
- **Technology:** ASP.NET Core minimal API + SignalR hub
- **Responsibilities:**
  - Generate short pairing codes (6 uppercase chars, no ambiguous chars: 0/O/1/I/L)
  - Match two peers who present the same code within 10 minutes
  - Relay WebRTC SDP offers/answers and ICE candidates
  - Expire sessions after connection is established or code expires
- **Stores:** Nothing persistently — all state is in-memory (or Redis for scale)
- **Hosts:** Owner-hosted (fly.io or Azure App Service). Default URL baked into clients. Configurable by user.

### 2. Edzio.Core (shared .NET library)
- **Technology:** .NET 8 class library
- **Responsibilities:**
  - `TransferSession` — manages the lifecycle of a file transfer
  - `TransferManifest` — describes files/folders (paths, sizes, chunk counts, SHA-256 hashes per chunk)
  - `ChunkEngine` — splits files into 256 KB chunks for sending; reassembles on receive
  - `ResumeState` — persists transfer progress to SQLite (chunk index → received bool)
  - `ITransferChannel` — abstraction over the transport (WebRTC data channel implementation)
  - `WebRtcChannel` — `ITransferChannel` implemented via SIPSorcery
  - `LocalDiscovery` — mDNS advertisement and browsing (via `Makaretu.Dns`)
  - `SignalingClient` — SignalR client for pairing code flow and ICE relay

### 3. Edzio.Desktop (Phase 1)
- **Technology:** .NET MAUI (Windows first, macOS later)
- **Pages:**
  - Home — shows "Send" / "Receive" and auto-discovered local peers
  - Send flow — select files/folders → choose peer (local) or enter code → progress
  - Receive flow — generate code → wait → accept transfer → progress
  - History — completed and incomplete transfers with resume option
  - Settings — signaling server URL, optional TURN credentials

### 4. Edzio.Mobile (Phase 2)
- **Technology:** .NET MAUI (Android first, iOS later)
- Same core feature set as desktop with a mobile-optimized layout

### 5. Edzio.Web (Phase 3)
- **Technology:** Blazor WebAssembly
- **Limitations vs. native:**
  - WebRTC access requires JavaScript interop
  - Transfer cannot resume across page refreshes (session-bound only)
  - No access to filesystem beyond browser file picker / download
- **Capabilities:** Full send/receive of files (not folders), within-session resumption

---

## Transfer Protocol

### Pairing Flow (Internet)
1. Receiver opens Edzio → taps "Receive" → server assigns code `MAPLE7` → displayed prominently
2. Sender opens Edzio → enters `MAPLE7`
3. Server joins both to SignalR group `MAPLE7`
4. Sender sends WebRTC SDP offer → server relays to Receiver
5. Receiver sends SDP answer → server relays to Sender
6. Both exchange ICE candidates via server
7. P2P data channel opens; server is no longer involved

### Pairing Flow (Local Network)
1. Both devices are on same LAN; Edzio advertises via mDNS (`_edzio._tcp`)
2. UI shows discovered peers by display name
3. Sender taps peer → sends transfer invitation over mDNS-resolved local IP
4. Receiver accepts → WebRTC-style handshake directly over local network (no signaling server)

### Transfer Manifest Format
```json
{
  "sessionId": "uuid",
  "totalBytes": 2147483648,
  "files": [
    {
      "relativePath": "Vacation/beach.jpg",
      "sizeBytes": 4194304,
      "chunks": [
        { "index": 0, "sizeBytes": 262144, "sha256": "abc123..." },
        { "index": 1, "sizeBytes": 262144, "sha256": "def456..." }
      ]
    }
  ]
}
```

### Chunked Transfer
1. Sender sends manifest JSON over data channel
2. Receiver replies with `RESUME` message listing any already-received chunk indices (empty on fresh start)
3. Sender streams chunks in order, skipping acknowledged ones
4. Each chunk message: `[4-byte file index][4-byte chunk index][N bytes data]`
5. Receiver writes each chunk to a temp file, marks it in SQLite
6. On completion of all chunks for a file, SHA-256 of assembled file is verified
7. Sender sends `DONE` message; both sides close the session

### Resume Protocol
- Native app persists SQLite DB at `%AppData%\Edzio\transfers.db`
- `TransferSession` table: sessionId (UUID), peerName, direction, manifest JSON, status
- `ReceivedChunk` table: sessionId, fileIndex, chunkIndex
- **Session UUID is the resume key.** The sender generates a UUID when creating the transfer and includes it in the manifest. Both sides store it.
- On reconnect (new pairing code, new WebRTC session): the sender sends the manifest with the same UUID as before. The receiver looks up that UUID in SQLite and responds with `RESUME {sessionId} [array of (fileIndex, chunkIndex) already received]`. The sender skips all acknowledged chunks.
- If the receiver has no record of the session UUID, transfer starts from the beginning (fresh start).
- Sessions expire after 7 days of inactivity (SQLite rows deleted).

---

## Local Discovery (mDNS)

- Each Edzio instance advertises: `_edzio._tcp.local` service record
- Service TXT record includes: display name (device name), Edzio version, session availability
- Library: `Makaretu.Dns` (pure .NET, no native deps)
- UI refreshes peer list every 5 seconds

---

## NAT Traversal

- **STUN:** Public STUN servers used for ICE candidate gathering (e.g., `stun.l.google.com:19302`)
- **TURN:** Optional, user-configures TURN server URL + credentials in Settings; never shipped with a default TURN server
- **Failure handling:** If ICE fails and no TURN configured, show friendly error: "Could not establish direct connection. Try connecting to the same Wi-Fi, or configure a relay server in Settings."

---

## Security

- **Transport encryption:** WebRTC DTLS-SRTP is mandatory; all data channel traffic is encrypted end-to-end between peers
- **Chunk integrity:** SHA-256 hash per chunk; corrupt chunks are re-requested
- **No server-side auth:** Signaling server is unauthenticated by design (codes are short-lived and single-use)
- **Code brute-force:** Codes expire after 10 minutes; server rate-limits guessing attempts (max 10 wrong attempts per IP per minute)

---

## Chunk and Performance Parameters

| Parameter | Value | Rationale |
|-----------|-------|-----------|
| Chunk size | 256 KB | Balance of overhead vs memory; adjustable later |
| Checksum algorithm | SHA-256 | Standard, fast enough, collision-resistant |
| Pairing code length | 6 chars | ~26^6 ≈ 300M combinations; 10-minute window limits brute-force |
| Code alphabet | A-Z except I, O; 2-9 (no 0, 1) | Avoids visual ambiguity |
| Code lifetime | 10 minutes | Long enough for user to type, short enough to limit guessing |
| Session expiry | 7 days | Allows reasonable resume window |
| Max simultaneous transfers | 1 per session | Keeps UI simple for v1 |

---

## Phased Delivery

### Phase 1 — Core (this spec)
- Edzio.SignalingServer deployed and reachable
- Edzio.Core library with transfer engine, chunking, resume, WebRTC channel
- Edzio.Desktop for Windows: Send/Receive UI, local discovery, internet pairing, resumable transfers, folder support

### Phase 2
- Edzio.Mobile (Android)
- Push notification for incoming transfer request (optional)

### Phase 3
- Edzio.Web (Blazor WASM)
- macOS desktop support
- iOS mobile support

---

## Key Decisions (Documented in decisions.md)

See `decisions.md` for the rationale behind each major decision made during design.
