using Edzio.Core.Models;

namespace Edzio.Core.Transfer;

public static class TransferManifestBuilder
{
    public static async Task<TransferManifest> BuildAsync(string sessionId, IEnumerable<string> paths)
    {
        var fileEntries = new List<FileEntry>();

        foreach (var path in paths)
        {
            if (File.Exists(path))
            {
                var relativePath = Path.GetFileName(path);
                var entry = await ChunkEngine.BuildFileEntryAsync(path, relativePath);
                fileEntries.Add(entry);
            }
            else if (Directory.Exists(path))
            {
                var basePath = Path.GetDirectoryName(path)!;
                var files = Directory.GetFiles(path, "*", SearchOption.AllDirectories);
                Array.Sort(files);

                foreach (var file in files)
                {
                    var relativePath = Path.GetRelativePath(basePath, file).Replace('\\', '/');
                    var entry = await ChunkEngine.BuildFileEntryAsync(file, relativePath);
                    fileEntries.Add(entry);
                }
            }
        }

        var totalBytes = fileEntries.Sum(e => e.SizeBytes);
        return new TransferManifest(sessionId, totalBytes, fileEntries);
    }
}
