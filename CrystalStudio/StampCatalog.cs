using System.IO;
namespace CrystalStudio;

public sealed record StampEntry(string FilePath, string Name, string RelativePath);
public static class StampCatalog
{
    public static string? FindFolder()
    {
        string path = DataPaths.ResolveEditor("Objects");
        return Directory.Exists(path) ? path : null;
    }
    public static List<StampEntry> Read(string? folder)
    {
        if (folder == null || !Directory.Exists(folder)) return [];
        return Directory.EnumerateFiles(folder, "*.X", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MatchCasing = MatchCasing.CaseInsensitive })
            .Select(path => new StampEntry(path, Path.GetFileNameWithoutExtension(path), Path.GetRelativePath(folder, path)))
            .OrderBy(entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
