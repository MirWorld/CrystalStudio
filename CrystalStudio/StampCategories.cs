using System.IO;
using System.Text.Json;

namespace CrystalStudio;

// A compact projection of the catalogue, independent of its map occurrence data.
public sealed record StampCategoryIndex(int SchemaVersion, string ObjectRoot, string PreviewRoot, List<CategorizedStamp> Objects);
public sealed record CategorizedStamp(string Id, string File, string Name, string? Type, string? Subtype,
    List<string> Regions, List<string> RegionGroups, List<string> Environments, List<string> Tags,
    string? Direction, string DirectionStatus, string? Preview, bool PreviewComplete, string ReviewStatus)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public ModuleConnections? Connections { get; init; }
}

public static class StampCategories
{
    public static string? FindIndex()
    {
        string path = DataPaths.ResolveEditor("catalogue", "palette.json");
        return File.Exists(path) ? path : null;
    }

    public static StampCategoryIndex Read(string path)
    {
        using var stream = File.OpenRead(path);
        var index = StudioJson.Deserialize<StampCategoryIndex>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("Empty stamp category catalogue.");
        string dataRoot = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(path)))!;
        index = index with { ObjectRoot = Path.GetFullPath(index.ObjectRoot, dataRoot), PreviewRoot = Path.GetFullPath(index.PreviewRoot, dataRoot) };
        if (index.SchemaVersion != 1 || index.Objects == null || index.Objects.Any(s => s == null) ||
            index.Objects.Select(s => s.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != index.Objects.Count)
            throw new InvalidDataException("Unsupported or invalid stamp category catalogue.");
        foreach (var stamp in index.Objects)
        {
            Resolve(index.ObjectRoot, stamp.File);
            if (stamp.Preview != null) Resolve(index.PreviewRoot, stamp.Preview);
            if (stamp.Regions == null || stamp.RegionGroups == null || stamp.Environments == null || stamp.Tags == null || string.IsNullOrWhiteSpace(stamp.Name))
                throw new InvalidDataException("Invalid stamp category entry.");
        }
        return index;
    }

    public static string Resolve(string root, string relative)
    {
        string parent = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string path = Path.GetFullPath(Path.Combine(parent, relative));
        if (!path.StartsWith(parent, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Stamp reference escapes its catalogue root.");
        return path;
    }

    public static string CatalogueFile(string catalogueRoot, string stampPath, string? sourceRoot)
    {
        string Relative(string root) => Path.GetRelativePath(root, stampPath).Replace('\\', '/');
        string relative = Relative(catalogueRoot);
        try { Resolve(catalogueRoot, relative); return relative; }
        catch (InvalidDataException) when (sourceRoot != null)
        {
            relative = Relative(sourceRoot);
            Resolve(sourceRoot, relative);
            Resolve(catalogueRoot, relative);
            return relative;
        }
    }
    public static IEnumerable<string> Groups(CategorizedStamp stamp, string grouping) => grouping switch
    {
        "Region" => stamp.Regions.Concat(stamp.RegionGroups).Distinct().DefaultIfEmpty("Unassigned region"),
        "Environment" => stamp.Environments.DefaultIfEmpty("Unknown environment"),
        "Facing" => [stamp.DirectionStatus == "known" ? stamp.Direction ?? "Unknown facing" : stamp.DirectionStatus == "notApplicable" ? "Not applicable" : "Unknown facing"],
        _ => [stamp.Type ?? "Unknown type"]
    };

    public static bool Matches(CategorizedStamp stamp, string query) => string.IsNullOrWhiteSpace(query) ||
        string.Join(' ', new[] { stamp.Name, stamp.File, stamp.Type, stamp.Subtype, stamp.Direction }
            .Concat(stamp.Regions).Concat(stamp.RegionGroups).Concat(stamp.Environments).Concat(stamp.Tags))
            .Contains(query.Trim(), StringComparison.OrdinalIgnoreCase);
}
