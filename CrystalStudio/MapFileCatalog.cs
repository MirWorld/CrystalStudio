using System.IO;

namespace CrystalStudio;

public sealed record MapFileEntry(string FilePath, string RelativePath);

public static class MapFileCatalog
{
    public static bool IsMapFile(string path) => Path.GetExtension(path).Equals(".map", StringComparison.OrdinalIgnoreCase);

    // 只建立文件索引，点击后才解析地图；跳过目录链接，避免重复或循环扫描。
    public static IReadOnlyList<MapFileEntry> Read(string folder, CancellationToken cancellation = default)
    {
        string root = Path.GetFullPath(folder);
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            MatchCasing = MatchCasing.CaseInsensitive
        };
        var files = new List<MapFileEntry>();
        foreach (string file in Directory.EnumerateFiles(root, "*", options))
        {
            cancellation.ThrowIfCancellationRequested();
            if (IsMapFile(file)) files.Add(new(Path.GetFullPath(file), Path.GetRelativePath(root, file)));
        }
        return files.OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
