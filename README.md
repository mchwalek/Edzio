# Edzio

> **Early development.** Edzio is a young, actively-developed project and not yet production-ready. APIs, protocols, and behavior may change without notice, and you should expect bugs. Only the Windows desktop app and signaling server exist today; see [Current status](#current-status) below.

P2P file sharing for non-technical users. Send arbitrarily large files and folders directly between devices — no accounts, no cloud storage, no need to know anyone's IP address.

Inspired by [PairDrop](https://pairdrop.net/), with added support for resumable transfers and native desktop and mobile apps.

---

## How it works

Two devices pair using a short 6-character code (e.g. `MAPLE7`). One person taps **Receive**, shares the code with the other, who enters it on the **Send** screen. A direct WebRTC connection is established between the two devices; the signaling server matches them and then steps aside — file data travels peer-to-peer and never passes through any server.

On the same local network, devices appear automatically without any code entry (like AirDrop).

Transfers are chunked and resumable. If a connection drops mid-transfer, reconnecting and entering a new code resumes from the last completed chunk.

---

## Features

- Direct P2P transfer — file data never touches a server
- Resumable transfers — interrupted sends continue from where they left off
- Folder sharing — send entire directory trees without zipping
- No accounts — zero sign-up, zero configuration for end users
- Local network auto-discovery via mDNS
- Internet transfers via short pairing codes
- Optional TURN relay for difficult NAT environments (user-configured)

---

## Current status

**Early development (Phase 1 in progress):** Windows desktop app + signaling server exist and can transfer files end-to-end, but the project is young, largely untested outside development, and still has rough edges (see recent fixes in `decisions.md`/commit history for examples of bugs found and fixed during early dogfooding). Not recommended for production or mission-critical use yet.

| Platform | Status |
|----------|--------|
| Windows desktop | Early / in development |
| Android | Planned (Phase 2) |
| Web (Blazor) | Planned (Phase 3) |
| macOS | Planned (Phase 3) |
| iOS | Planned (Phase 3) |

---

## Project structure

```
Edzio/
├── src/
│   ├── Edzio.Core/              # Shared .NET 8 library
│   │   ├── Models/              # TransferManifest, FileEntry, ChunkInfo
│   │   ├── Transfer/            # ChunkEngine, TransferSession, ITransferChannel
│   │   ├── Persistence/         # SQLite session/chunk tracking (EF Core)
│   │   ├── Signaling/           # ISignalingClient, SignalingClient (SignalR)
│   │   ├── WebRtc/              # WebRtcChannel (SIPSorcery)
│   │   └── Discovery/           # MdnsDiscovery (Makaretu.Dns)
│   ├── Edzio.SignalingServer/   # ASP.NET Core + SignalR pairing hub
│   └── Edzio.Desktop/          # .NET MAUI Windows app
└── tests/
    ├── Edzio.Core.Tests/        # 35 unit tests
    └── Edzio.SignalingServer.Tests/ # 6 unit tests
```

---

## Architecture

```
┌──────────────────────────────────────────────────────┐
│               Signaling Server                       │
│        (ASP.NET Core + SignalR)                      │
│  Generates pairing codes, matches peers,             │
│  relays WebRTC ICE candidates.                       │
│  File data never passes through here.                │
└─────────────────────┬────────────────────────────────┘
                      │ WebSocket (tiny JSON messages only)
          ┌───────────┴───────────┐
          │                       │
   ┌──────▼──────┐         ┌──────▼──────┐
   │  Peer A     │         │  Peer B     │
   │  (Sender)   │◄───────►│  (Receiver) │
   │             │ WebRTC  │             │
   └─────────────┘  P2P   └─────────────┘
```

**Transfer protocol:** Files are split into 256 KB chunks, each with a SHA-256 hash. The sender streams chunks; the receiver verifies each one and records progress to SQLite. On reconnect, the receiver reports which chunks it already has and the sender skips them.

**NAT traversal:** STUN by default (Google's public servers). Users can configure their own TURN relay in Settings for networks where direct P2P fails.

---

## Building

Prerequisites: .NET 10 SDK, MAUI workload (`dotnet workload install maui-windows`).

```powershell
# Run all tests
dotnet test Edzio.slnx

# Build Windows desktop app
dotnet build src/Edzio.Desktop/Edzio.Desktop.csproj -f net10.0-windows10.0.19041.0

# Publish single-file exe (requires .NET 10 runtime on target machine)
dotnet publish src/Edzio.Desktop/Edzio.Desktop.csproj `
  -f net10.0-windows10.0.19041.0 -r win-x64 -c Release `
  /p:PublishSingleFile=true --self-contained false

# Run signaling server locally
dotnet run --project src/Edzio.SignalingServer
```

---

## Signaling server deployment

The signaling server runs on Azure Container Apps (free tier, scale-to-zero, single instance — see `docs/superpowers/specs/2026-07-02-signaling-server-azure-deployment-design.md` for the full design). Deployment is automated via GitHub Actions.

**One-time setup (already done for the primary deployment, needed only if redeploying from scratch):**

1. Provision infrastructure: `az deployment sub create --location westeurope --template-file infra/main.bicep --parameters infra/main.parameters.json`
2. Run `infra/setup-oidc.ps1` and add the three printed values as GitHub repository **Variables** (Settings → Secrets and variables → Actions → Variables): `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`.
3. Push to `main` (or run the workflow manually via **Actions → Deploy Signaling Server → Run workflow**). The first successful run publishes `ghcr.io/mchwalek/edzio-signaling` — go to the package's settings on GitHub and change its visibility to **Public** so Azure Container Apps can pull it without credentials (only needed once, the first time the package is created).

**Ongoing deployment:** any push to `main` touching `src/Edzio.SignalingServer/**` automatically builds, pushes to ghcr.io, and updates the live Container App — no manual steps.

The server exposes:
- `GET /health` → `"ok"` (used by the desktop app's status indicator)
- `WS /signaling` → SignalR hub for pairing and ICE relay

After deploying, update the default URL in `SettingsViewModel.DefaultSignalingUrl`:
```csharp
public const string DefaultSignalingUrl = "https://edzio-signaling.kindmeadow-5769cf71.westeurope.azurecontainerapps.io";
```

---

## Key design decisions

| Decision | Choice | Reason |
|----------|--------|--------|
| P2P transport | WebRTC (SIPSorcery on native) | Single protocol for all clients; built-in NAT traversal and encryption |
| NAT traversal | STUN only by default | File data must not pass through any third-party server |
| Local discovery | mDNS (`_edzio._tcp`) | Zero-config, works like AirDrop on the same network |
| Internet pairing | 6-char codes, 10-min expiry | Easy to communicate verbally; short window limits brute-force |
| Code alphabet | `ABCDEFGHJKMNPQRSTUVWXYZ23456789` | Excludes visually ambiguous characters (0, 1, I, O, L) |
| Chunk size | 256 KB | Balance of overhead vs. memory; negotiated in manifest so it can change |
| Chunk integrity | SHA-256 per chunk | Detects corruption; corrupt chunks are re-requested |
| Persistence | SQLite via EF Core | Embedded, zero-config, tracks resume state across restarts |
| Signaling server | User-hosted, user-overridable | Non-technical users get zero-config; privacy-conscious users can self-host |
| Accounts | None | Zero friction; security comes from short-lived codes and DTLS encryption |
| Desktop/mobile UI | .NET MAUI | Single codebase for Windows, macOS, Android, iOS |
| Web UI | Blazor WASM (Phase 3) | Consistent .NET stack; shares Edzio.Core |

See `decisions.md` for the full rationale behind each decision.
