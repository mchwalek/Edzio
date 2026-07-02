using Edzio.Core.Transfer;
using FluentAssertions;
using Xunit;

namespace Edzio.Core.Tests.Transfer;

public class TransferManifestBuilderTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    public TransferManifestBuilderTests() => Directory.CreateDirectory(_tempDir);
    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    [Fact]
    public async Task BuildAsync_SingleFile_ProducesManifestWithOneEntry()
    {
        var file = Path.Combine(_tempDir, "a.txt");
        await File.WriteAllTextAsync(file, "hello");

        var manifest = await TransferManifestBuilder.BuildAsync("ses1", new[] { file });

        manifest.SessionId.Should().Be("ses1");
        manifest.Files.Should().HaveCount(1);
        manifest.Files[0].RelativePath.Should().Be("a.txt");
        manifest.TotalBytes.Should().Be(5);
    }

    [Fact]
    public async Task BuildAsync_Directory_IncludesAllFilesWithRelativePaths()
    {
        var sub = Path.Combine(_tempDir, "sub");
        Directory.CreateDirectory(sub);
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "root.txt"), "r");
        await File.WriteAllTextAsync(Path.Combine(sub, "child.txt"), "c");

        var manifest = await TransferManifestBuilder.BuildAsync("ses2", new[] { _tempDir });

        manifest.Files.Should().HaveCount(2);
        manifest.Files.Select(f => f.RelativePath).Should().Contain(p => p.Contains("root.txt"));
        manifest.Files.Select(f => f.RelativePath).Should().Contain(p => p.Contains("child.txt"));
        manifest.Files.All(f => !f.RelativePath.Contains("\\")).Should().BeTrue();
    }
}
