using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace CrystalStudio;

public sealed record TreeListEntry(string Id, string Name, string Type, List<StampCell> Cells);
public sealed record TreeListDocument(int Version, string Name, List<TreeListEntry> Trees);

// Share layouts and library/image references, so custom trees do not depend on local .X file paths.
public static class TreeBrushList
{
    private const int MaxTrees = 1000, MaxCells = 250_000, MaxBytes = 32 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };

    public static TreeListEntry Entry(string name, string type, IEnumerable<StampCell> cells)
    {
        var tree = BrushTree.FromCells(name, "", cells);
        string id = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { Name = name, Cells = tree.Cells })));
        return new(id, name, type, tree.Cells.Select(c => c with { Cell = c.Cell.Copy() }).ToList());
    }

    internal static TreeListDocument Validate(TreeListDocument? document, string kind = "tree")
    {
        if (document == null || document.Version != 1 || string.IsNullOrWhiteSpace(document.Name) || document.Name.Length > 256 ||
            document.Trees == null || document.Trees.Count > MaxTrees)
            throw new InvalidDataException($"Unsupported or invalid {kind} list.");
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<TreeListEntry>();
        int count = 0;
        foreach (var entry in document.Trees)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.Id) || entry.Id.Length > 128 || !entry.Id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') ||
                !ids.Add(entry.Id) || string.IsNullOrWhiteSpace(entry.Name) || entry.Name.Length > 256 ||
                string.IsNullOrWhiteSpace(entry.Type) || entry.Type.Length > 128 || entry.Cells == null || entry.Cells.Count > 10_000)
                throw new InvalidDataException($"Invalid or duplicate {kind} list entry.");
            count += entry.Cells.Count;
            if (count > MaxCells) throw new InvalidDataException($"{kind} list contains too many cells.");
            var tree = BrushTree.FromCells(entry.Name, "", entry.Cells);
            entries.Add(entry with { Cells = tree.Cells.Select(c => c with { Cell = c.Cell.Copy() }).ToList() });
        }
        return document with { Trees = entries };
    }

    public static TreeListDocument Read(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length > MaxBytes) throw new InvalidDataException("Tree list exceeds the 32 MiB limit.");
        try { return Validate(JsonSerializer.Deserialize<TreeListDocument>(stream, JsonOptions)); }
        catch (JsonException error) { throw new InvalidDataException("Invalid tree list cell data.", error); }
    }

    public static void Save(string path, string name, IEnumerable<TreeListEntry> trees)
    {
        var document = Validate(new(1, name, trees.ToList()));
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
        if (json.Length > MaxBytes) throw new InvalidDataException("Tree list exceeds the 32 MiB limit.");
        AtomicFile.Write(path, stream => stream.Write(json));
    }
}
