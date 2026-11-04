using System.IO;
using System.Text.Json;

namespace CrystalStudio;

public sealed record SpecialBrushListDocument(int Version, SpecialBrushKind Kind, string Name, List<TreeListEntry> Items);

public static class SpecialBrushList
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true, WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter<SpecialBrushKind>() }
    };

    public static TreeListDocument Read(string path, SpecialBrushKind kind)
    {
        if (kind == SpecialBrushKind.Tree) return TreeBrushList.Read(path);
        using var stream = File.OpenRead(path);
        if (stream.Length > 32 * 1024 * 1024) throw new InvalidDataException("Brush list exceeds the 32 MiB limit.");
        SpecialBrushListDocument? document;
        try { document = JsonSerializer.Deserialize<SpecialBrushListDocument>(stream, JsonOptions); }
        catch (JsonException error) { throw new InvalidDataException("Invalid brush list cell data.", error); }
        if (document == null || document.Kind != kind)
            throw new InvalidDataException($"Choose a {kind.Singular()} list file.");
        return TreeBrushList.Validate(new(document.Version, document.Name, document.Items), kind.Singular());
    }

    public static void Save(string path, SpecialBrushKind kind, string name, IEnumerable<TreeListEntry> items)
    {
        if (kind == SpecialBrushKind.Tree) { TreeBrushList.Save(path, name, items); return; }
        var document = TreeBrushList.Validate(new(1, name, items.ToList()), kind.Singular());
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(new SpecialBrushListDocument(1, kind, document.Name, document.Trees), JsonOptions);
        if (json.Length > 32 * 1024 * 1024) throw new InvalidDataException("Brush list exceeds the 32 MiB limit.");
        AtomicFile.Write(path, stream => stream.Write(json));
    }
}
