namespace Edzio.Core.Transfer;

public record TransferProgress(
    long BytesSent,
    long TotalBytes,
    int ChunksComplete,
    int ChunksTotal)
{
    public double Percentage => TotalBytes == 0 ? 0 : (double)BytesSent / TotalBytes * 100;
}
