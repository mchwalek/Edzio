# Connection Status UX — Design Spec

**Date:** 2026-08-29
**Component:** Edzio.Desktop (.NET MAUI 10 Windows) + Edzio.Core signaling
**Status:** Approved for implementation

## Problem

The app-wide title bar shows a colored status dot (green/red/gray) with the text
"Relay: Online/Offline/Checking…". Users dislike it: it is always present, it uses
jargon ("Relay"), and a persistent always-green indicator trains users to ignore it
(Von Restorff / Aesthetic-Usability). The dot also reflects a `/health` HTTP poll that
is **decoupled** from the actual SignalR connection used for transfers, so it can be
green while a real transfer attempt fails.

## Goals

1. Remove the always-on dot. When everything is healthy, show **nothing**.
2. Surface connection trouble only when it matters, with plain, non-technical language
   and a clear recovery path (manual retry + automatic retry).
3. Make the app usable before the signaling connection is established. Only the parts
   that need signaling wait; same-network (LAN/mDNS) transfers are never blocked.
4. Make the status reflect the **real** SignalR connection, not a separate health poll.

## UX Decisions (locked)

### Global status bar
A thin bar rendered app-wide (directly under the title area), visible only in non-healthy
states:

| State | Bar | Color | Content |
| ----- | --- | ----- | ------- |
| Connected / healthy | hidden | — | nothing shown |
| First launch, fast connect | hidden | — | no transient "connecting" bar |
| Reconnecting (mid-session drop, auto-retry in flight) | shown | amber `#8a5a00` | animated spinner + "Trying to reconnect…" |
| Failed (initial connect failed OR reconnect gave up) | shown | red `#C42B1C` | "⚠  Can't connect right now. Transfers to distant devices are paused. Retrying…" + **Try again** button |

Rules:
- One consistent rule for initial-connect failure and mid-session drop (same bar).
- Auto-retry runs on a timer while failed; the **Try again** button forces an immediate retry.
- Wording avoids "server", "relay", "internet". "Distant devices" = peers not on the local network.
- Keep the ⚠ symbol. Button label is **"Try again"** (not "Retry").

### Send / Receive local waiting states
The app is usable before signaling connects; only the signaling-dependent part waits,
shown **inline, local to the flow** (not the global bar):

- **Receive:** while connecting, show placeholder code `– – – – – –` + inline blue spinner
  "Getting you a code…". When connected, show the real 6-char code.
- **Send:** while connecting, the **Send button is disabled** (user can still pick files and
  type the code) with inline spinner "Connecting… you can pick files meanwhile". Button arms
  when connected.
- On genuine failure (not merely slow), the global red bar appears at the top of that page;
  the flow explains the code appears once reconnected.
- Same-network (LAN/mDNS) transfers are never blocked by signaling state.

## Architecture

### 1. Core: expose connection state (Approach A)

Add a connection-state signal to `ISignalingClient` so the desktop can react to the real
SignalR lifecycle instead of an HTTP poll.

```csharp
public enum SignalingConnectionState { Disconnected, Connecting, Connected, Reconnecting }
```

Add to `ISignalingClient`:
```csharp
SignalingConnectionState ConnectionState { get; }
event EventHandler<SignalingConnectionState> ConnectionStateChanged;
Task WaitForConnectedAsync(CancellationToken ct = default);
```

Map in `SignalingClient` from the existing hooks (already wired for logging):
- `ConnectAsync` start → `Connecting`; after `StartAsync` succeeds → `Connected`; on throw → `Disconnected`.
- `_connection.Reconnecting` → `Reconnecting`.
- `_connection.Reconnected` → `Connected`.
- `_connection.Closed` → `Disconnected`.

`WaitForConnectedAsync` returns immediately if already `Connected`, otherwise completes when
the state next becomes `Connected` (or throws on cancellation). Implemented with a
`TaskCompletionSource` reset on each state change — no polling.

### 2. Desktop: `SignalingConnectionManager` (new singleton)

Replaces `SignalingHealthMonitor` and the `/health` poll entirely.

Responsibilities:
- On app launch, call `ISignalingClient.ConnectAsync(url)` eagerly (connect on startup).
- Expose the current UI-facing state (`Connecting` / `Connected` / `Reconnecting` / `Failed`)
  and a `StateChanged` event for the status bar to bind/subscribe to.
- On connect failure, auto-retry on a timer (interval `N` seconds — default **10s**).
- Expose `RetryNow()` for the **Try again** button (cancels the wait and retries immediately).
- Expose `WaitForConnectedAsync()` (delegates to the client) for Send/Receive to await.
- Re-connect when the signaling URL changes (moves the URL-change reactivity here from
  `SignalingHealthMonitor`).

Registered singleton in `MauiProgram.cs`, started once on app launch (from
`AppShell` code-behind, mirroring where the monitor is started today).

### 3. Desktop: status bar UI

- Remove the dot + label from `AppShell.xaml` `Shell.TitleView`.
- Add a global bar under the title area, bound to the manager's state. It is collapsed
  (`IsVisible=false`) when Connected. A ViewModel (e.g. `ConnectionStatusViewModel` using
  `BaseViewModel`) exposes: `IsBarVisible`, `IsFailed`, `IsReconnecting`, `Message`,
  `BarColor`, and a `TryAgainCommand`. This replaces today's imperative
  `UpdateStatusUi` code-behind with a proper binding.
- **Placement note:** `Shell.TitleView` is a single-row title area. The bar must sit
  *below* the title, spanning all routes. Implementation resolves this by hosting the bar
  in a shared layout wrapper (verified during implementation — either a `Shell`-level
  template or a shared content container). This is a mechanical layout detail, not a design
  decision; the required behavior is "app-wide bar directly under the title, hidden when
  healthy".

### 4. Send / Receive wiring

- **Receive** (`ReceiveViewModel.StartAsync`): replace the direct `ConnectAsync` +
  status-refresh with `await manager.WaitForConnectedAsync(ct)` before
  `RegisterAsReceiverAsync`. Show inline placeholder/spinner while waiting.
- **Send** (`SendViewModel`): allow file pick + code entry immediately; keep Send disabled
  and show inline spinner until `WaitForConnectedAsync` completes, then arm the button.
- Both reuse the shared singleton `ISignalingClient` connection opened at startup.

### 5. Color tokens

Add semantic status tokens to `Resources/Styles/Colors.xaml` (none exist today):
- `StatusError` = `#C42B1C` (red bar)
- `StatusWarning` = `#8a5a00` (amber bar)
- No "healthy"/neutral token — healthy shows nothing, so no resting bar color is needed.

Inline spinner text in Send/Receive uses the existing blue (`Primary` / `#0078D4`).

## Removed / replaced

- **Deleted:** `SignalingHealthMonitor` service, its `ServerStatus` enum, and its DI
  registration. The `/health` endpoint stays on the signaling server (unrelated to the app UI).
- **Deleted:** the status dot/label markup in `AppShell.xaml` and `UpdateStatusUi` +
  color constants in `AppShell.xaml.cs`.

## Error handling

- Connect/reconnect failures never throw into the UI thread; they transition state and the
  bar reflects it.
- Auto-retry is bounded only by the timer (keeps retrying while failed); the user can force
  an immediate attempt via **Try again**.
- Cancellation (app shutdown, URL change) cancels the wait/retry loop cleanly.

## Testing

- **Core:** unit-test `SignalingClient` state transitions by driving the mapped hooks
  (`Connecting` on start, `Connected` after start, `Reconnecting`/`Connected`/`Disconnected`
  from the connection callbacks) and `WaitForConnectedAsync` (already-connected returns
  immediately; pending completes on next Connected; cancellation throws). Use the existing
  fake/stub signaling patterns in `Edzio.Core.Tests`.
- **Desktop manager:** test auto-retry fires on failure, `RetryNow()` forces an immediate
  attempt, and URL change triggers reconnect. Inject a fake `ISignalingClient` and a
  controllable time source for the retry timer.
- No UI-automation tests required; the ViewModel state mapping is the unit under test.

## Non-goals

- No change to the transfer protocol, WebRTC/LAN channels, or mDNS discovery.
- No change to the `/health` endpoint on the server.
- No visual redesign beyond the status bar and the two inline Send/Receive waiting states.
