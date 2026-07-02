using Edzio.Core.Persistence;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Edzio.Core.Tests.Persistence;

public class TransferRepositoryTests : IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private readonly TransferDbContext _db;
    private readonly TransferRepository _repo;

    public TransferRepositoryTests()
    {
        // Keep connection open so the in-memory database persists for the test lifetime
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<TransferDbContext>()
            .UseSqlite(_connection)
            .Options;
        _db = new TransferDbContext(options);
        _db.Database.EnsureCreated();
        _repo = new TransferRepository(_db);
    }

    public async ValueTask DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task SaveAndGetSession_RoundTrips()
    {
        await _repo.SaveSessionAsync("ses1", "Alice", TransferDirection.Send, "{}", TransferStatus.InProgress);
        var loaded = await _repo.GetSessionAsync("ses1");

        loaded.Should().NotBeNull();
        loaded!.SessionId.Should().Be("ses1");
        loaded.PeerName.Should().Be("Alice");
        loaded.Direction.Should().Be(TransferDirection.Send);
    }

    [Fact]
    public async Task MarkChunkReceived_ThenGetReceivedChunks_ReturnsIt()
    {
        await _repo.SaveSessionAsync("ses2", "Bob", TransferDirection.Receive, "{}", TransferStatus.InProgress);
        await _repo.MarkChunkReceivedAsync("ses2", 0, 5);
        await _repo.MarkChunkReceivedAsync("ses2", 1, 0);

        var chunks = await _repo.GetReceivedChunksAsync("ses2");

        chunks.Should().HaveCount(2);
        chunks.Should().Contain((0, 5));
        chunks.Should().Contain((1, 0));
    }

    [Fact]
    public async Task MarkChunkReceived_Idempotent_DoesNotDuplicate()
    {
        await _repo.SaveSessionAsync("ses3", "Carol", TransferDirection.Receive, "{}", TransferStatus.InProgress);
        await _repo.MarkChunkReceivedAsync("ses3", 0, 0);
        await _repo.MarkChunkReceivedAsync("ses3", 0, 0); // duplicate

        var chunks = await _repo.GetReceivedChunksAsync("ses3");
        chunks.Should().HaveCount(1);
    }

    [Fact]
    public async Task DeleteExpiredSessions_RemovesOldOnes()
    {
        await _repo.SaveSessionAsync("old", "Dan", TransferDirection.Send, "{}", TransferStatus.InProgress);
        // Manually age it
        var entity = await _db.Sessions.FindAsync("old");
        entity!.CreatedAt = DateTimeOffset.UtcNow.AddDays(-8);
        await _db.SaveChangesAsync();

        await _repo.DeleteExpiredSessionsAsync(TimeSpan.FromDays(7));

        var loaded = await _repo.GetSessionAsync("old");
        loaded.Should().BeNull();
    }
}
