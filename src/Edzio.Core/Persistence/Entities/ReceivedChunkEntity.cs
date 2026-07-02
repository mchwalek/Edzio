namespace Edzio.Core.Persistence;

public class ReceivedChunkEntity
{
    public int Id { get; set; }
    public string SessionId { get; set; } = "";
    public int FileIndex { get; set; }
    public int ChunkIndex { get; set; }
}
