---
name: testing-edzio
description: Use when running tests, adding test coverage, debugging test failures, or understanding the test structure for Edzio. Covers xUnit patterns, available fakes and stubs, in-memory SQLite setup, and how to test Core vs SignalingServer.
metadata:
  internal: true
---

# Testing Edzio

## Test Suite Overview

| Suite | Command | Files | What it tests |
| ----- | ------- | ----- | ------------- |
| Core unit tests | `dotnet test tests/Edzio.Core.Tests/` | `tests/Edzio.Core.Tests/**/*.cs` | Transfer engine, chunking, persistence, signaling client, discovery |
| Server unit tests | `dotnet test tests/Edzio.SignalingServer.Tests/` | `tests/Edzio.SignalingServer.Tests/**/*.cs` | PairingCodeService code generation and matching |
| Full suite | `dotnet test Edzio.slnx` | both | All 42 tests (36 Core + 6 Server, 1 skipped) |

The single skipped test is `WebRtcChannelLoopbackTest.TwoChannels_ExchangeData_Bidirectionally` — requires real network interfaces and is marked `[Fact(Skip = "Integration - requires loopback ICE negotiation; run manually")]`. The other two tests in that class run normally. This 1 skip is expected.

## Running Tests

```powershell
# All tests
dotnet test Edzio.slnx

# Core only, with output
dotnet test tests/Edzio.Core.Tests/Edzio.Core.Tests.csproj --logger "console;verbosity=normal"

# Filter by name
dotnet test Edzio.slnx --filter "FullyQualifiedName~ChunkEngine"

# Single test class
dotnet test Edzio.slnx --filter "FullyQualifiedName~TransferSessionSendTests"
```

## Test Doubles Available

These live in the test project and can be reused in new tests:

| Type | Location | Use for |
| ---- | -------- | ------- |
| `FakeSignalingClient` | `Core.Tests/Signaling/FakeSignalingClient.cs` | Testing anything that depends on `ISignalingClient`. Has `SimulateOfferReceived(sdp)`, `SimulatePeerJoined()`, etc. Tracks `SentOffers`, `SentAnswers`, `SentIceCandidates`. |
| `FakeLocalDiscovery` | `Core.Tests/Discovery/FakeLocalDiscovery.cs` | Testing anything that depends on `ILocalDiscovery`. Has `SimulateDiscovery(peer)` and `SimulateRemoval(peer)`. |
| `StubChannel` (inline) | Defined locally in each test file | Testing `TransferSession` send/receive. No shared reusable class exists — define it inline in the test file. See `TransferSessionReceiveTests.cs` for the pattern (uses `EnqueueInbound(byte[])` + `SentMessages`). |

## In-Memory SQLite Pattern

`TransferRepository` requires `TransferDbContext`. Use an open `SqliteConnection` to keep the in-memory database alive across multiple DbContext instances:

```csharp
private SqliteConnection CreateDb(out TransferRepository repo)
{
    var conn = new SqliteConnection("Data Source=:memory:");
    conn.Open();
    var opts = new DbContextOptionsBuilder<TransferDbContext>()
        .UseSqlite(conn).Options;
    var db = new TransferDbContext(opts);
    db.Database.EnsureCreated();
    repo = new TransferRepository(db);
    return conn; // dispose in test cleanup
}
```

Do NOT use `"DataSource=:memory:"` with a fresh DbContext per call — each call gets a separate empty database.

## Progress<T> Gotcha

`new Progress<T>(callback)` dispatches callbacks asynchronously on the thread pool. This makes timing assertions flaky. Use a synchronous wrapper instead:

```csharp
private class SyncProgress<T> : IProgress<T>
{
    private readonly Action<T> _handler;
    public SyncProgress(Action<T> handler) => _handler = handler;
    public void Report(T value) => _handler(value);
}
```

## Unit Test Patterns

### xUnit + NSubstitute + FluentAssertions

```csharp
using FluentAssertions;
using NSubstitute;
using Xunit;

public class MyTests : IDisposable
{
    private readonly ISignalingClient _signaling = Substitute.For<ISignalingClient>();

    [Fact]
    public async Task DoSomething_WhenCondition_ProducesResult()
    {
        _signaling.RegisterAsReceiverAsync().Returns("ABC123");

        var result = await _signaling.RegisterAsReceiverAsync();

        result.Should().Be("ABC123");
    }

    public void Dispose() { /* cleanup */ }
}
```

### Temp directory for file tests

```csharp
public class MyFileTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public MyFileTests() => Directory.CreateDirectory(_tempDir);
    public void Dispose() => Directory.Delete(_tempDir, recursive: true);
}
```

### Testing TransferSession

Define a `StubChannel` inline in your test file (see `TransferSessionReceiveTests.cs` for the full implementation). The receive side uses `EnqueueInbound(byte[])` to pre-queue messages; the send side stores outbound messages in a `SentMessages` list. Build framed messages manually:

```csharp
// 0x01 Manifest message
var json = JsonSerializer.Serialize(manifest);
var manifestMsg = new byte[] { 0x01 }.Concat(Encoding.UTF8.GetBytes(json)).ToArray();
stub.EnqueueInbound(manifestMsg);

// 0x02 Resume message (empty — no chunks received yet)
var resumeMsg = new byte[] { 0x02 }.Concat(Encoding.UTF8.GetBytes("[]")).ToArray();
stub.EnqueueInbound(resumeMsg);

// 0x03 Chunk message
var chunkMsg = new byte[1 + 4 + 4 + data.Length];
chunkMsg[0] = 0x03;
BinaryPrimitives.WriteInt32LittleEndian(chunkMsg.AsSpan(1), fileIndex);
BinaryPrimitives.WriteInt32LittleEndian(chunkMsg.AsSpan(5), chunkIndex);
data.CopyTo(chunkMsg, 9);
stub.EnqueueInbound(chunkMsg);

// 0x04 Done
stub.EnqueueInbound(new byte[] { 0x04 });
```

## Adding New Tests

### New Core unit test

1. Create file in the matching subfolder under `tests/Edzio.Core.Tests/`
2. Use `FakeSignalingClient`, `FakeLocalDiscovery`, or `StubTransferChannel` as needed
3. For file-touching tests: use a temp directory pattern
4. For persistence tests: use in-memory SQLite pattern
5. Run: `dotnet test tests/Edzio.Core.Tests/Edzio.Core.Tests.csproj`

### New SignalingServer test

1. Create file under `tests/Edzio.SignalingServer.Tests/`
2. `PairingCodeService` is a pure class — test it directly with `new PairingCodeService()`
3. Run: `dotnet test tests/Edzio.SignalingServer.Tests/`

## Test File Naming Conventions

| What you're testing | File name pattern |
| ------------------- | ----------------- |
| A class or static class | `{ClassName}Tests.cs` |
| A fake/stub for use by others | `Fake{InterfaceName}.cs` |
| A stub channel | `StubTransferChannel.cs` |

Test files mirror the `src/` structure under `tests/Edzio.Core.Tests/`.
