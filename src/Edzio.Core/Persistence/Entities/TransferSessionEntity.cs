namespace Edzio.Core.Persistence;

public enum TransferDirection { Send, Receive }
public enum TransferStatus { InProgress, Completed, Failed }

public class TransferSessionEntity
{
    public string SessionId { get; set; } = "";
    public string PeerName { get; set; } = "";
    public TransferDirection Direction { get; set; }
    public string ManifestJson { get; set; } = "";
    public TransferStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public ICollection<ReceivedChunkEntity> ReceivedChunks { get; set; } = new List<ReceivedChunkEntity>();
}
