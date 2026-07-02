---
name: developing-edzio
description: Use when adding features, modifying transfer logic, working on the desktop app or signaling server, or navigating the Edzio codebase for the first time. Covers project structure, key types, DI patterns, and build commands.
metadata:
  internal: true
---

# Developing Edzio

## Architecture in One Paragraph

Edzio has three parts: **Edzio.Core** (shared .NET 8 library — no UI, no platform code), **Edzio.SignalingServer** (ASP.NET Core + SignalR, matches peers via 6-char codes, never sees file data), and **Edzio.Desktop** (MAUI Windows app). The transfer goes: mDNS or pairing code → WebRTC data channel → chunked manifest → SHA-256-verified chunks → resume on reconnect. All transfer logic lives in Core so future platforms (Android, Web) reuse it unchanged.

## Key Types

| Type | Location | Purpose |
| ---- | -------- | ------- |
| `ITransferChannel` | `Core/Transfer/` | Abstraction over P2P transport. Only implementation: `WebRtcChannel`. |
| `TransferSession` | `Core/Transfer/` | Static class. `SendAsync` + `ReceiveAsync` implement the full wire protocol. |
| `ChunkEngine` | `Core/Transfer/` | Splits files into 256 KB chunks, computes SHA-256, writes/assembles on receive. |
| `TransferManifestBuilder` | `Core/Transfer/` | Builds `TransferManifest` from a list of file/folder paths. |
| `TransferRepository` | `Core/Persistence/` | SQLite via EF Core. Tracks session state and which chunks have been received. |
| `ISignalingClient` | `Core/Signaling/` | WebRTC handshake via SignalR. Real impl: `SignalingClient`. Test double: `FakeSignalingClient`. |
| `ILocalDiscovery` | `Core/Discovery/` | mDNS via Makaretu.Dns. Real impl: `MdnsDiscovery`. Test double: `FakeLocalDiscovery`. |
| `SignalingHub` | `SignalingServer/Hubs/` | SignalR hub. Manages pairing codes and ICE relay. Stateless (no DB). |
| `PairingCodeService` | `SignalingServer/Services/` | Thread-safe 6-char code generation + matching. Codes expire after 10 minutes. |
| `SignalingHealthMonitor` | `Desktop/Services/` | Polls `/health` every 30s. Drives the status dot in the nav bar. |

## Wire Protocol

Every `ITransferChannel` message starts with a 1-byte type prefix:

| Byte | Name | Payload |
| ---- | ---- | ------- |
| `0x01` | Manifest | UTF-8 JSON of `TransferManifest` |
| `0x02` | Resume | UTF-8 JSON array of `{fileIndex,chunkIndex}` |
| `0x03` | Chunk | `[4-byte LE fileIndex][4-byte LE chunkIndex][data]` |
| `0x04` | Done | empty |
| `0x05` | Error | UTF-8 message |

## Adding a Feature to Core

1. Write the test first (`tests/Edzio.Core.Tests/`)
2. Add production code under `src/Edzio.Core/`
3. Run `dotnet test tests/Edzio.Core.Tests/Edzio.Core.Tests.csproj`
4. If you add a new interface, add a fake/stub in the test project (mirrors the `FakeSignalingClient` / `FakeLocalDiscovery` pattern)

## Adding a Page to Desktop

MAUI pages **must** be resolved from DI — never `new Page()`. The `AppShell.xaml.cs` pattern:

```csharp
// AppShell constructor
HomeContent.Content = services.GetRequiredService<Pages.HomePage>();
// Navigation targets use Routing.RegisterRoute
Routing.RegisterRoute("send", typeof(Pages.SendPage));
```

Steps:
1. Create `Pages/MyPage.xaml` + `.xaml.cs` (constructor takes a ViewModel)
2. Create `ViewModels/MyViewModel.cs` (inherits `BaseViewModel`)
3. Register both in `MauiProgram.cs` — `AddTransient<MyViewModel>()`, `AddTransient<MyPage>()`
4. Register route in `AppShell.xaml.cs` — `Routing.RegisterRoute("mypage", typeof(Pages.MyPage))`
5. Navigate with `await Shell.Current.GoToAsync("mypage")`

## MAUI DI Gotcha

`ContentTemplate="{DataTemplate pages:Foo}"` in XAML bypasses DI and calls the parameterless constructor. If your page requires injected services this will throw `MissingMethodException` at runtime with no compile error. Always set `ShellContent.Content` from DI in code-behind instead.

## Signaling Server Changes

The server is stateless — all session state lives in `PairingCodeService` (in-memory, singleton). If you add a new hub method:
- Client → server: add method to `SignalingHub`
- Server → client: use `Clients.Client(id).SendAsync("EventName", payload)` and add a corresponding `.On<T>` handler in `SignalingClient`
- Add constants to `SignalingMessages.cs` to avoid magic strings

## Pairing Code Alphabet

`ABCDEFGHJKMNPQRSTUVWXYZ23456789` — excludes 0, 1, I, O, L to avoid visual ambiguity. Codes are 6 characters, single-use, expire after 10 minutes. Generation uses `RandomNumberGenerator.GetInt32`.

## Build Commands

```powershell
dotnet build Edzio.slnx                        # build everything
dotnet build src/Edzio.Desktop/... -f net10.0-windows10.0.19041.0  # desktop only
dotnet run --project src/Edzio.SignalingServer  # run server locally
dotnet publish src/Edzio.Desktop/... -f net10.0-windows10.0.19041.0 -r win-x64 -c Release /p:PublishSingleFile=true --self-contained false
```

## Debugging

For any bug, hang, crash, or unexpected behavior use the **`debugging-edzio`** skill. It covers log file locations, the WebRTC/ICE diagnostic checklist, and the persistent `docs/debug/<slug>/PROGRESS.md` workflow that lets investigations survive session boundaries.

## Code Conventions

- Nullable reference types enabled everywhere
- XML doc comments on all public members
- File paths in manifests/SQLite always use forward slashes (`/`), never backslashes
- `TransferMessageType` enum values are the canonical byte prefixes — use them, never raw literals
- ViewModels: use `SetProperty<T>(ref field, value)` for property change notification
