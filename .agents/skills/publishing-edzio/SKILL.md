---
name: publishing-edzio
description: Use when publishing or distributing the Edzio desktop app. Covers the publish command, output location, runtime requirements, and per-platform notes.
metadata:
  internal: true
---

# Publishing Edzio

## Pre-publish checklist

- [ ] `dotnet build Edzio.slnx` — 0 errors
- [ ] `dotnet test Edzio.slnx` — all tests pass (2 skipped WebRTC integration tests are expected)
- [ ] Changes committed to git

## Windows Desktop (Phase 1)

Single-file exe, framework-dependent (requires .NET 10 runtime on the target machine):

```powershell
dotnet publish src/Edzio.Desktop/Edzio.Desktop.csproj `
  -f net10.0-windows10.0.19041.0 `
  -r win-x64 `
  -c Release `
  /p:PublishSingleFile=true `
  --self-contained false
```

**Output:** `src/Edzio.Desktop/bin/Release/net10.0-windows10.0.19041.0/win-x64/publish/`

| File | Keep? | Notes |
| ---- | ----- | ----- |
| `Edzio.Desktop.exe` | ✅ | The app (~290 MB, bundles all managed assemblies + WinUI runtime) |
| `Edzio.Desktop.pdb` | Optional | Debug symbols — include for crash reports, exclude for clean distribution |
| `Edzio.Core.pdb` | Optional | Core library symbols |

**Runtime requirement:** The target machine must have [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) installed. If you want a self-contained exe (no runtime needed), add `--self-contained true` — the output will be ~2× larger.

## Signaling Server

The signaling server is a standard ASP.NET Core app. Publish with:

```powershell
dotnet publish src/Edzio.SignalingServer/Edzio.SignalingServer.csproj `
  -c Release -r linux-x64 --self-contained false
```

Deploy to fly.io, Azure App Service, or any host with .NET 10. After deploying:
1. Update `SettingsViewModel.DefaultSignalingUrl` with the live URL
2. Rebuild and republish the desktop app

The server exposes:
- `GET /health` → `"ok"` (polled every 30s by the desktop app's status indicator)
- `WS /signaling` → SignalR hub (listens on all interfaces: `0.0.0.0:5000`)

## Future platforms

| Platform | Phase | Publish command (placeholder) |
| -------- | ----- | ----------------------------- |
| Android | 2 | `dotnet publish ... -f net10.0-android` |
| macOS | 3 | `dotnet publish ... -f net10.0-maccatalyst` |
| iOS | 3 | `dotnet publish ... -f net10.0-ios` |
| Web (Blazor WASM) | 3 | `dotnet publish ... -f net10.0` |

## Log file location (post-publish)

After the app runs, logs are written to:
```
%LOCALAPPDATA%\Edzio\logs\edzio-YYYY-MM-DD.log
```

Share this file when debugging connection issues — it contains the full WebRTC/ICE trace.
