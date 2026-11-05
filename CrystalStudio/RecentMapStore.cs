using System.IO;
using System.Text.Json;

namespace CrystalStudio;

public sealed class RecentMapStore
{
    public const int Limit = 20;
    public string FilePath { get; }
    public IReadOnlyList<string> Maps => maps;
    private List<string> maps = [];

    public RecentMapStore(string? filePath = null) => FilePath = filePath ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CrystalStudio", "recent-maps.json");

    public void Load()
    {
        if (!File.Exists(FilePath)) return;
        var saved = StudioJson.Deserialize<List<string?>>(File.ReadAllText(FilePath)) ?? [];
        var clean = new List<string>();
        foreach (var entry in saved)
        {
            if (string.IsNullOrWhiteSpace(entry) || !Path.IsPathFullyQualified(entry) || !Path.GetExtension(entry).Equals(".map", StringComparison.OrdinalIgnoreCase)) continue;
            string path;
            try { path = Path.GetFullPath(entry); } catch (ArgumentException) { continue; }
            if (!clean.Contains(path, StringComparer.OrdinalIgnoreCase)) clean.Add(path);
        }
        maps = clean.Take(Limit).ToList();
    }

    public void Remember(string path)
    {
        path = Path.GetFullPath(path);
        if (!Path.GetExtension(path).Equals(".map", StringComparison.OrdinalIgnoreCase)) return;
        maps.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        maps.Insert(0, path);
        if (maps.Count > Limit) maps.RemoveRange(Limit, maps.Count - Limit);
        AtomicFile.WriteJson(FilePath, maps);
    }
}
