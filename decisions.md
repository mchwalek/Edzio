# Edzio — Architecture Decisions

This file documents key architectural decisions and their rationale.
Created: 2026-06-26

---

## ADR-001: WebRTC for all clients (Approach A)
**Decision:** Use WebRTC data channels as the sole P2P transport for all clients (web and native).
**Rationale:** Ensures full interoperability between web, desktop, and mobile clients. A grandma on the web app can send to a grandchild on the desktop app. SIPSorcery provides a mature pure-C# WebRTC implementation for native clients.
**Alternatives rejected:**
- Custom QUIC+STUN (native only — breaks web↔native interop)
- Hybrid WebRTC+QUIC (over-engineered for Phase 1; revisit if throughput is a proven bottleneck)

---

## ADR-002: STUN-only by default, user-configured optional TURN
**Decision:** Ship with public STUN servers (Google's). No default TURN server. Users can configure their own TURN credentials in Settings.
**Rationale:** File data must not pass through any third-party server by default (user requirement). Paid TURN services relay file data, which creates legal and privacy concerns. ~85-90% of transfers will succeed without TURN.
**Failure path:** Graceful error message with suggestion to use same WiFi or configure TURN.

---

## ADR-003: Short code pairing (6-char) for internet, mDNS for local
**Decision:** Internet pairing via 6-char human-readable codes (e.g., "MAPLE7"). Local pairing via mDNS auto-discovery showing device names.
**Rationale:** Short codes are easy for non-technical users to communicate verbally or via text message. mDNS requires no user action on local networks (AirDrop-style).

---

## ADR-004: Signaling server is owner-hosted with user override
**Decision:** A default signaling server URL is baked into clients. Advanced users can override it in Settings.
**Rationale:** Non-technical users get zero-config experience. Organizations or privacy-conscious users can self-host the signaling server.

---

## ADR-005: .NET MAUI for desktop and mobile
**Decision:** Use .NET MAUI for Windows/macOS desktop and Android/iOS mobile.
**Rationale:** Microsoft's official cross-platform .NET UI framework. Best tooling, strongest Windows support, single codebase for desktop+mobile. Linux desktop dropped as not a priority.

---

## ADR-006: Blazor WASM for web
**Decision:** Use Blazor WebAssembly for the web client.
**Rationale:** Consistent with .NET stack. Allows sharing of Edzio.Core library (where feasible). WebRTC requires JS interop in Blazor — acceptable given web is an acknowledged limited platform.

---

## ADR-007: 256 KB chunk size
**Decision:** Files are split into 256 KB chunks for transfer.
**Rationale:** Balance between overhead (too many small chunks) and memory pressure (too-large chunks). Can be tuned without protocol changes since chunk size is negotiated in the manifest.

---

## ADR-008: SQLite for transfer state persistence (native)
**Decision:** Use SQLite (via Microsoft.Data.Sqlite or EF Core) to persist transfer session state and chunk progress on native apps.
**Rationale:** Lightweight, embedded, zero-config database. Perfect for tracking which chunks have been received. Available on all MAUI target platforms.

---

## ADR-009: SHA-256 per chunk for integrity
**Decision:** Each chunk carries a SHA-256 hash in the transfer manifest. Receiver verifies each chunk.
**Rationale:** Detects corruption during transfer without relying solely on transport integrity. Corrupt chunks are re-requested. SHA-256 is fast enough on modern hardware.

---

## ADR-010: Phase 1 = Signaling Server + Windows Desktop only
**Decision:** Phase 1 scopes to the signaling server (deployable) and the Windows desktop MAUI app.
**Rationale:** Allows end-to-end testing of the complete protocol (pairing, P2P connection, chunked transfer, resume) on platforms the developer can test. Android, web, macOS, iOS follow in later phases.

---

## ADR-011: mDNS library — Makaretu.Dns
**Decision:** Use `Makaretu.Dns` NuGet package for mDNS advertisement and browsing.
**Rationale:** Pure .NET implementation, no native bindings, cross-platform, well-maintained.

---

## ADR-012: SignalR for signaling server WebSocket communication
**Decision:** Use ASP.NET Core SignalR for the signaling server hub.
**Rationale:** Built-in .NET, handles WebSocket + fallback, scales to Azure SignalR Service easily if needed. Native clients use the SignalR .NET client library.

---

## ADR-013: SIPSorcery for WebRTC on native clients
**Decision:** Use `SIPSorcery` NuGet package for WebRTC implementation on MAUI.
**Rationale:** Only mature, pure-C# WebRTC library available for .NET. Supports ICE, DTLS, SCTP data channels. Used in production VoIP/video products.

---

## ADR-014: Code alphabet excludes ambiguous characters
**Decision:** Pairing code alphabet: A-H, J-N, P-Z (no I, O), digits 2-9 (no 0, 1).
**Rationale:** Eliminates common visual ambiguities (0 vs O, 1 vs I vs L) when codes are communicated verbally or on paper.

---

## ADR-015: No user accounts
**Decision:** Zero authentication, no user registration.
**Rationale:** Core product requirement. Reduces friction to zero for non-technical users. Security is provided by short-lived pairing codes and DTLS encryption.

---

## ADR-016: Desktop targets .NET 10 MAUI (not .NET 8)
**Decision:** Edzio.Desktop uses net10.0-windows10.0.19041.0 instead of net8.0-windows.
**Rationale:** The installed MAUI workload (dotnet workload install maui-windows) installs .NET 10 MAUI. A separate net8 MAUI workload would be required for net8.0-windows. Using .NET 10 for Desktop is forward-compatible and the SDK on this machine is 10.0.203.
**Impact:** Desktop project references .NET 8 Core library (net8.0) — this is fine since .NET 10 can consume net8.0 targets.

## ADR-017: Solution file is .slnx format
**Decision:** The solution file is Edzio.slnx (new XML format) not Edzio.sln.
**Rationale:** .NET 10 SDK creates .slnx by default. Functionally equivalent. All dotnet CLI commands work with .slnx.

## ADR-018: SIPSorcery 6.2.3 (not 6.2.2)
**Decision:** SIPSorcery resolves to 6.2.3 (6.2.2 not available on NuGet).
**Rationale:** 6.2.3 is the closest available version. Minor patch version difference, no API changes expected.
