using Edzio.Core.Models;
using FluentAssertions;
using Xunit;

namespace Edzio.Core.Tests.Transfer;

public class TransferManifestTests
{
    [Fact]
    public void TransferManifest_RoundTripsJson()
    {
        var manifest = new TransferManifest(
            SessionId: "abc-123",
            TotalBytes: 1024,
            Files: new[]
            {
                new FileEntry(
                    RelativePath: "folder/file.txt",
                    SizeBytes: 1024,
                    Chunks: new[] { new ChunkInfo(0, 1024, "deadbeef") })
            });

        var json = System.Text.Json.JsonSerializer.Serialize(manifest);
        var restored = System.Text.Json.JsonSerializer.Deserialize<TransferManifest>(json);

        restored.Should().BeEquivalentTo(manifest);
    }

    [Fact]
    public void FileEntry_RelativePath_UsesForwardSlashes()
    {
        var entry = new FileEntry("a/b/c.txt", 100, Array.Empty<ChunkInfo>());
        entry.RelativePath.Should().NotContain("\\");
    }
}
