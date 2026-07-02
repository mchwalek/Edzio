using Microsoft.EntityFrameworkCore;

namespace Edzio.Core.Persistence;

public class TransferDbContext : DbContext
{
    public TransferDbContext(DbContextOptions<TransferDbContext> options) : base(options) { }

    public DbSet<TransferSessionEntity> Sessions { get; set; } = null!;
    public DbSet<ReceivedChunkEntity> ReceivedChunks { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.Entity<TransferSessionEntity>().HasKey(e => e.SessionId);

        mb.Entity<ReceivedChunkEntity>().HasKey(e => e.Id);
        mb.Entity<ReceivedChunkEntity>()
            .HasIndex(e => new { e.SessionId, e.FileIndex, e.ChunkIndex }).IsUnique();

        mb.Entity<TransferSessionEntity>()
            .HasMany(s => s.ReceivedChunks)
            .WithOne()
            .HasForeignKey(c => c.SessionId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
