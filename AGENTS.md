# AGENTS.md

## Project Overview

**Edzio** — cross-platform P2P file sharing for non-technical users. Files transfer directly between devices via WebRTC; no cloud storage, no accounts. A lightweight signaling server handles peer discovery only — file data never touches it.

**Stack:** .NET 8 (Core library), .NET 10 MAUI (Windows desktop), ASP.NET Core 10 + SignalR (signaling server). Solution file: `Edzio.slnx`.

## Project Skills

| Skill | When to use |
| ----- | ----------- |
| **developing-edzio** | Adding features, modifying transfer logic, working on the desktop app or signaling server. Covers project structure, key types, conventions, build commands, and log-based debugging. |
| **testing-edzio** | Running tests, adding test coverage, debugging failures. Covers xUnit patterns, available fakes/stubs, and test file conventions. |
| **publishing-edzio** | Publishing or distributing the desktop app or signaling server. Covers publish commands, output locations, and runtime requirements. |
| **debugging-edzio** | Investigating any bug, hang, crash, or unexpected behavior. Creates and maintains a `docs/debug/<slug>/PROGRESS.md` so investigations survive session boundaries and already-verified theories are never re-examined. |

## Tech Stack

| Component | Technology |
| --------- | ---------- |
| Core library | .NET 8 class library (`net8.0`) |
| Signaling server | ASP.NET Core 10, SignalR hub (`net10.0`) |
| Desktop app | .NET MAUI 10 Windows (`net10.0-windows10.0.19041.0`) |
| P2P transport | WebRTC via SIPSorcery 6.2.3 |
| Local discovery | mDNS via Makaretu.Dns.Multicast 0.27.0 |
| Signaling client | Microsoft.AspNetCore.SignalR.Client 8.0 |
| Persistence | EF Core 8.0 + SQLite |
| Unit tests | xUnit 2.9, NSubstitute 5, FluentAssertions 6 |

## Project Structure

```
Edzio/
├── src/
│   ├── Edzio.Core/                  # Shared .NET 8 library — no UI dependencies
│   │   ├── Models/                  # TransferManifest, FileEntry, ChunkInfo (records)
│   │   ├── Transfer/                # ChunkEngine, TransferSession, ITransferChannel,
│   │   │                            # TransferMessageType, TransferProgress, TransferException
│   │   ├── Persistence/             # TransferDbContext, TransferRepository, EF entities
│   │   ├── Signaling/               # ISignalingClient, SignalingClient, SignalingMessages
│   │   ├── WebRtc/                  # WebRtcChannel (ITransferChannel impl), WebRtcRole
│   │   └── Discovery/               # ILocalDiscovery, MdnsDiscovery, LocalPeer
│   ├── Edzio.SignalingServer/       # ASP.NET Core minimal API + SignalR
│   │   ├── Hubs/SignalingHub.cs     # SignalR hub: RegisterReceiver, JoinAsSender, relay methods
│   │   └── Services/PairingCodeService.cs  # Thread-safe 6-char code generation
│   └── Edzio.Desktop/              # .NET MAUI Windows app
│       ├── Pages/                   # HomePage, SendPage, ReceivePage, SettingsPage
│       ├── ViewModels/              # MVVM — BaseViewModel, Home/Send/Receive/SettingsViewModel
│       ├── Services/                # SignalingHealthMonitor (polls /health every 30s)
│       └── Converters/              # InverseBoolConverter
└── tests/
    ├── Edzio.Core.Tests/            # 35 unit tests + 1 skipped integration test
    └── Edzio.SignalingServer.Tests/ # 6 unit tests
```

## Development Commands

```powershell
# Build everything
dotnet build Edzio.slnx

# Build just the desktop app (Windows)
dotnet build src/Edzio.Desktop/Edzio.Desktop.csproj -f net10.0-windows10.0.19041.0

# Run all tests
dotnet test Edzio.slnx

# Run only Core tests
dotnet test tests/Edzio.Core.Tests/Edzio.Core.Tests.csproj

# Run signaling server locally (listens on http://localhost:5000)
dotnet run --project src/Edzio.SignalingServer

# Publish single-file Windows exe (framework-dependent — requires .NET 10 on target)
dotnet publish src/Edzio.Desktop/Edzio.Desktop.csproj `
  -f net10.0-windows10.0.19041.0 -r win-x64 -c Release `
  /p:PublishSingleFile=true --self-contained false
```

## Code Conventions

- Nullable reference types enabled everywhere (`<Nullable>enable</Nullable>`)
- Implicit usings enabled (`<ImplicitUsings>enable</ImplicitUsings>`)
- All public types and members have XML doc comments
- File paths in manifests always use forward slashes regardless of OS
- Desktop pages are created from DI (never `new Page()`) — see AppShell.xaml.cs
- ViewModels inherit `BaseViewModel : INotifyPropertyChanged` with `SetProperty<T>` helper
- `TransferMessageType` byte prefix on every ITransferChannel message (0x01–0x05)

## Key Interfaces

```csharp
// The P2P transport abstraction — implemented by WebRtcChannel
interface ITransferChannel : IAsyncDisposable {
    Task SendAsync(byte[] data, CancellationToken ct = default);
    Task<byte[]> ReceiveAsync(CancellationToken ct = default);
    Task WaitForOpenAsync(CancellationToken ct = default);
}

// Signaling (cloud or local) — implemented by SignalingClient
interface ISignalingClient : IAsyncDisposable {
    Task ConnectAsync(string serverUrl, CancellationToken ct = default);
    Task<string> RegisterAsReceiverAsync(CancellationToken ct = default);
    Task<bool> JoinAsSenderAsync(string code, CancellationToken ct = default);
    Task SendOfferAsync(string sdp, CancellationToken ct = default);
    Task SendAnswerAsync(string sdp, CancellationToken ct = default);
    Task SendIceCandidateAsync(string candidateJson, CancellationToken ct = default);
    event EventHandler<string> OfferReceived;
    event EventHandler<string> AnswerReceived;
    event EventHandler<string> IceCandidateReceived;
    event EventHandler PeerJoined;
    event EventHandler PeerDisconnected;
}

// Local network discovery — implemented by MdnsDiscovery
interface ILocalDiscovery : IAsyncDisposable {
    Task StartAsync(CancellationToken ct = default);
    Task StopAsync();
    IReadOnlyList<LocalPeer> DiscoveredPeers { get; }
    event EventHandler<IReadOnlyList<LocalPeer>> PeersChanged;
}
```

## Transfer Protocol (wire format)

Every `ITransferChannel` message starts with a 1-byte type tag:

| Byte | Type | Payload |
| ---- | ---- | ------- |
| `0x01` | Manifest | UTF-8 JSON of `TransferManifest` |
| `0x02` | Resume | UTF-8 JSON array of `{fileIndex, chunkIndex}` already received |
| `0x03` | Chunk | 4-byte LE fileIndex + 4-byte LE chunkIndex + raw chunk bytes |
| `0x04` | Done | empty |
| `0x05` | Error | UTF-8 error message |

## Signaling Server Endpoints

- `GET /health` → `"ok"` (used by the desktop app's status indicator)
- `WS /signaling` → SignalR hub

Hub methods (client → server): `RegisterReceiver()→string`, `JoinAsSender(code)→bool`, `SendOffer(sdp)`, `SendAnswer(sdp)`, `SendIceCandidate(json)`

Client callbacks (server → client): `OfferReceived(sdp)`, `AnswerReceived(sdp)`, `IceCandidateReceived(json)`, `PeerJoined()`, `PeerDisconnected()`

## Pre-Commit Checklist

Before committing, verify:

1. `dotnet build Edzio.slnx` — 0 errors
2. `dotnet test Edzio.slnx` — all tests pass (2 skipped WebRTC integration tests are expected)

**Do NOT commit:**
- `progress.md`
- `docs/superpowers/`
- `docs/debug/` (debug investigation logs — gitignored, local only)
- Build output (`bin/`, `obj/`)
