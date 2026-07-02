using Microsoft.EntityFrameworkCore;

namespace Edzio.Core.Persistence;

public class TransferRepository
{
    private readonly TransferDbContext _db;

    public TransferRepository(TransferDbContext db)
    {
        _db = db;
    }

    public async Task SaveSessionAsync(string sessionId, string peerName, TransferDirection direction,
        string manifestJson, TransferStatus status)
    {
        var existing = await _db.Sessions.FindAsync(sessionId);
        if (existing != null)
        {
            existing.PeerName = peerName;
            existing.Direction = direction;
            existing.ManifestJson = manifestJson;
            existing.Status = status;
        }
        else
        {
            _db.Sessions.Add(new TransferSessionEntity
            {
                SessionId = sessionId,
                PeerName = peerName,
                Direction = direction,
                ManifestJson = manifestJson,
                Status = status,
                CreatedAt = DateTimeOffset.UtcNow
            });
        }
        await _db.SaveChangesAsync();
    }

    public async Task<TransferSessionEntity?> GetSessionAsync(string sessionId)
    {
        return await _db.Sessions.FindAsync(sessionId);
    }

    public async Task MarkChunkReceivedAsync(string sessionId, int fileIndex, int chunkIndex)
    {
        var exists = await _db.ReceivedChunks
            .AnyAsync(c => c.SessionId == sessionId && c.FileIndex == fileIndex && c.ChunkIndex == chunkIndex);
        if (!exists)
        {
            _db.ReceivedChunks.Add(new ReceivedChunkEntity
            {
                SessionId = sessionId,
                FileIndex = fileIndex,
                ChunkIndex = chunkIndex
            });
            await _db.SaveChangesAsync();
        }
    }

    public async Task<IReadOnlyList<(int FileIndex, int ChunkIndex)>> GetReceivedChunksAsync(string sessionId)
    {
        return await _db.ReceivedChunks
            .Where(c => c.SessionId == sessionId)
            .Select(c => new { c.FileIndex, c.ChunkIndex })
            .ToListAsync()
            .ContinueWith(t => (IReadOnlyList<(int, int)>)t.Result
                .Select(c => (c.FileIndex, c.ChunkIndex))
                .ToList());
    }

    public async Task DeleteExpiredSessionsAsync(TimeSpan maxAge)
    {
        var cutoff = DateTimeOffset.UtcNow - maxAge;
        // Load all and filter client-side: DateTimeOffset comparison is not translatable by EF SQLite provider
        var all = await _db.Sessions.ToListAsync();
        var expired = all.Where(s => s.CreatedAt < cutoff).ToList();
        if (expired.Count > 0)
        {
            _db.Sessions.RemoveRange(expired);
            await _db.SaveChangesAsync();
        }
    }

    public async Task UpdateStatusAsync(string sessionId, TransferStatus status)
    {
        var entity = await _db.Sessions.FindAsync(sessionId);
        if (entity != null)
        {
            entity.Status = status;
            await _db.SaveChangesAsync();
        }
    }
}
