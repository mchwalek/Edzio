# Transfer UI Improvements — Design

Date: 2026-07-16
Status: Approved (Approach A for helper placement)

## Problem

Four independent UI gaps in the Windows desktop app (`Edzio.Desktop`):

1. No transfer speed or ETA shown during send/receive.
2. Sender's pairing-code `Entry` doesn't uppercase as the user types (only at submit time).
3. No drag-and-drop support for adding files to send.
4. The Receive page shows the same "Receiving… NN%" text three times (redundant label) — see reported screenshot.

## Goals

- Show live transfer speed and remaining time on **both** Send and Receive pages, as separate labels (speed / remaining / transferred-so-far).
- Uppercase the sender's code `Entry` live, per keystroke.
- Support drag-and-drop of files onto the Send page (Windows only; adds to the existing selection, files only — no folders).
- Remove the duplicate label on the Receive page.

## Non-goals

- No changes to `Edzio.Core`'s wire protocol, `TransferProgress` record shape, or `TransferSession`.
- No folder drag-and-drop.
- No mobile/web drag-and-drop (not feasible; Desktop-only feature).
- No new Desktop test project — Desktop has none today and adding one is out of scope for this change. New pure-logic helpers go in `Edzio.Core` specifically so they get real unit test coverage; ViewModel/XAML wiring changes are verified via build + manual smoke test.

## Architecture

Two new pure-logic types live in `Edzio.Core/Transfer/` (Approach A — testable, reusable by future platforms):

- **`ByteFormatter`** — static class. `Format(long bytes)` → human string ("4.2 MB"). `FormatRate(double bytesPerSecond)` → "2.4 MB/s". `FormatDuration(double seconds)` → "12s" or "1:32".
- **`TransferRateTracker`** — small stateful class. `Sample(long bytesSoFar, long totalBytes, DateTimeOffset now)` records a data point and returns a `TransferRateSnapshot` (`BytesPerSecond`, `EtaSeconds?`). Uses an exponential moving average (alpha ≈ 0.3) seeded by the first two samples, so it settles quickly without being jittery on the ~256 KB chunk cadence. `EtaSeconds` is `null` until at least one non-zero-duration sample exists (UI shows "calculating…" during that gap). Takes `DateTimeOffset` as a parameter (not `DateTimeOffset.Now` internally) so it's testable without wall-clock sleeps.

Both are plain C#, no MAUI dependency, live in Core, and get xUnit tests in `Edzio.Core.Tests/Transfer/`.

### Desktop wiring

`SendViewModel` and `ReceiveViewModel` each own one `TransferRateTracker` instance (reset per transfer) and three new bound string properties:

- `SpeedText` (e.g. `"2.4 MB/s"`, or `"—"` before the first sample)
- `RemainingText` (e.g. `"12s remaining"`, or `"calculating…"`)
- `TransferredText` (e.g. `"4.2 MB / 38 MB"`)

These are updated inside the existing `Progress<TransferProgress>` callback, alongside the existing `ProgressValue`/`StatusMessage` updates — no new progress-reporting plumbing needed.

`ReceivePage.xaml` / `SendPage.xaml` add three labels under the progress bar bound to these properties, visible only while `ShowProgress` is true.

### Pairing code live uppercase

`SendViewModel.PairingCode` setter uppercases the incoming value (`value.ToUpperInvariant()`) before storing/raising `PropertyChanged`. Two-way binding means the `Entry` reflects the uppercased text immediately as the user types. The existing `.ToUpperInvariant()` call at send time (`SendViewModel.cs:84`) is left in place as a harmless no-op safety net.

### Drag-and-drop (Send page, Windows)

- `SendViewModel` gains a public method `AddPaths(IEnumerable<string> paths)` that adds new, non-duplicate paths to `SelectedPaths` and calls `((Command)SendCommand).ChangeCanExecute()`. `PickFilesAsync` is refactored to call this same method instead of duplicating the add-logic, so there's one code path for "files got added to the selection."
- `SendPage.xaml` wraps its root `VerticalStackLayout` content in a container (the existing layout, `AllowDrop="True"` via a `DropGestureRecognizer` added to `GestureRecognizers`) so the **whole page** is a drop target, per the approved design.
- `SendPage.xaml.cs` adds a `Drop` handler. On Windows, MAUI surfaces the native drag event via `DropEventArgs.PlatformArgs.DragEventArgs` (WinUI `DragEventArgs`). The handler:
  1. Bails out if `PlatformArgs` is null (non-Windows / no native args) — this makes the feature a safe no-op elsewhere.
  2. Checks `DataView.Contains(StandardDataFormats.StorageItems)`.
  3. Awaits `DataView.GetStorageItemsAsync()`, filters to `StorageFile` (ignores folders — files only, per design), maps to `.Path`.
  4. Calls `_vm.AddPaths(paths)`.
- No visual "drop zone" styling is required (whole-page target, per design decision) but a lightweight visual affordance (e.g., a border highlight on `DragOver`) is a nice-to-have, not required for this iteration — omitted to keep scope tight.

### Receive page redundant label fix

Root cause: `ReceivePage.xaml`'s "initial status" label (bound to `StatusMessage`, visible whenever `ShowCode` is false) stays visible during progress and completion too, duplicating the progress block's own `StatusMessage` label. Fix: give `ReceiveViewModel` a computed/derived visibility, or simplest — add a dedicated bool `ShowInitialStatus` property (true only when not showing code, not showing progress, and not complete) and bind the initial-status label to that instead of the `InverseBool` of `ShowCode`. This removes the third duplicate line without touching the two legitimate ones (status text + percentage).

## Data flow

No change to the wire protocol or `TransferSession`. Data flow addition is purely local to the Desktop ViewModels:

```
TransferSession.SendAsync/ReceiveAsync
  → Progress<TransferProgress>.Report(p)
    → ViewModel callback:
        - existing: ProgressValue, StatusMessage
        - new: tracker.Sample(p.BytesSent, p.TotalBytes, DateTimeOffset.UtcNow)
               → SpeedText, RemainingText, TransferredText (via ByteFormatter)
```

## Error handling

- `TransferRateTracker` never throws; if `totalBytes <= 0` or elapsed time is `0`, it returns a snapshot with `EtaSeconds = null` and `BytesPerSecond = 0`, and the UI shows placeholder text ("calculating…", "—").
- Drag-drop handler swallows/no-ops on any platform where `PlatformArgs` isn't the expected WinUI type — it must never throw or crash the page.
- `AddPaths` de-duplicates using ordinal path comparison; adding zero new paths is a no-op (no exception).

## Testing

- `Edzio.Core.Tests/Transfer/ByteFormatterTests.cs` — unit tests for byte/rate/duration formatting at boundary values (0, <1KB, exact MB, >1GB, sub-second/multi-minute durations).
- `Edzio.Core.Tests/Transfer/TransferRateTrackerTests.cs` — unit tests for: first sample (no rate yet), rate after two samples with known elapsed time, ETA calculation, zero-total-bytes edge case.
- Desktop (ViewModel/XAML) changes: no automated test project exists for Desktop; verified via `dotnet build src/Edzio.Desktop/... -f net10.0-windows10.0.19041.0` (0 errors) plus a manual smoke-test pass (send+receive a real file, confirm labels, live-uppercase, drag-drop, no duplicate text) — recorded in `progress.md`, not automated.
- Full solution regression: `dotnet test Edzio.slnx` must stay green (35 Core tests + new tests, 6 SignalingServer tests, 2 skipped WebRTC integration tests expected).

## File-level plan (for implementation)

| File | Change |
|---|---|
| `src/Edzio.Core/Transfer/ByteFormatter.cs` | New |
| `src/Edzio.Core/Transfer/TransferRateTracker.cs` | New |
| `tests/Edzio.Core.Tests/Transfer/ByteFormatterTests.cs` | New |
| `tests/Edzio.Core.Tests/Transfer/TransferRateTrackerTests.cs` | New |
| `src/Edzio.Desktop/ViewModels/ReceiveViewModel.cs` | Modify — add SpeedText/RemainingText/TransferredText/ShowInitialStatus |
| `src/Edzio.Desktop/Pages/ReceivePage.xaml` | Modify — new labels, fix redundant label binding |
| `src/Edzio.Desktop/ViewModels/SendViewModel.cs` | Modify — live-uppercase PairingCode, AddPaths, SpeedText/RemainingText/TransferredText |
| `src/Edzio.Desktop/Pages/SendPage.xaml` | Modify — new labels, DropGestureRecognizer |
| `src/Edzio.Desktop/Pages/SendPage.xaml.cs` | Modify — Drop event handler |

## Open questions resolved during brainstorming

- Speed/ETA layout: separate labels (not one combined line).
- Uppercase timing: live, per keystroke.
- Drag-drop scope: whole page is drop target, adds to (doesn't replace) selection, files only (no folders).
- Speed/ETA shown on both Send and Receive.
- Helper placement: `Edzio.Core` (Approach A), for testability.
