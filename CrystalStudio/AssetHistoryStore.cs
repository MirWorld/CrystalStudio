using System.IO;
using System.Text.Json;

namespace CrystalStudio;

// Keep the library file identity as well as its numeric ID: two library folders
// can use the same IDs for entirely different sprites.
public sealed record AssetReference(string Source, int Library, int Index, MapLayer Layer,
    TilePatternLayout Pattern, string? StampPath = null)
{
    public static AssetReference Tile(string libraryPath, int library, int index, MapLayer layer, TilePatternLayout pattern)
    {
        if (pattern != TilePatternLayout.None && index >= 0)
        {
            int start = index / AutoTileBrush.BlockSize(pattern) * AutoTileBrush.BlockSize(pattern);
            index = start + (pattern == TilePatternLayout.Mir3 && index % 30 is >= 5 and <= 9 ? 5 : 0);
        }
        return new(Path.GetFullPath(libraryPath), library, index, layer, pattern);
    }
    public static AssetReference Stamp(string path) =>
        new(Path.GetFullPath(path), 0, 0, MapLayer.Objects, TilePatternLayout.None, Path.GetFullPath(path));
    public bool SameAsset(AssetReference other) =>
        string.Equals(Source, other.Source, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(StampPath, other.StampPath, StringComparison.OrdinalIgnoreCase) &&
        Library == other.Library && Index == other.Index && Layer == other.Layer && Pattern == other.Pattern;
    public bool Valid => !string.IsNullOrWhiteSpace(Source) && Path.IsPathFullyQualified(Source) &&
        Enum.IsDefined(Layer) && Layer is not (MapLayer.LowCollision or MapLayer.HighCollision) && Enum.IsDefined(Pattern) && Library >= 0 && Index >= 0 &&
        (StampPath == null || Path.IsPathFullyQualified(StampPath));
}

public sealed class AssetHistoryStore
{
    public sealed record AssetAlias(AssetReference Asset, string Name);
    public const int RecentLimit = 48;
    public string FilePath { get; }
    public List<AssetReference> Favourites { get; private set; } = [];
    public List<AssetReference> Recent { get; private set; } = [];
    public List<AssetAlias> Aliases { get; private set; } = [];
    public sealed record SavedAssets(List<AssetReference>? Favourites = null, List<AssetReference>? Recent = null, List<AssetAlias>? Aliases = null);

    public AssetHistoryStore(string? filePath = null) => FilePath = filePath ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CrystalStudio", "assets.json");

    public void Load()
    {
        if (!File.Exists(FilePath)) return;
        var saved = StudioJson.Deserialize<SavedAssets>(File.ReadAllText(FilePath)) ?? new();
        static List<AssetReference> Clean(List<AssetReference>? entries)
        {
            List<AssetReference> result = [];
            foreach (var entry in entries ?? [])
                if (entry is { Valid: true } && !result.Any(entry.SameAsset)) result.Add(entry);
            return result;
        }
        Favourites = Clean(saved.Favourites);
        Recent = Clean(saved.Recent).Take(RecentLimit).ToList();
        Aliases = [];
        foreach (var alias in saved.Aliases ?? [])
            if (alias is { Asset.Valid: true, Name.Length: > 0 and <= 80 } && !alias.Name.Any(char.IsControl) && !Aliases.Any(item => item.Asset.SameAsset(alias.Asset)))
                Aliases.Add(alias);
    }

    public bool IsFavourite(AssetReference asset) => Favourites.Any(asset.SameAsset);
    public bool ToggleFavourite(AssetReference asset)
    {
        if (!asset.Valid) return false;
        bool pinned = !IsFavourite(asset);
        if (pinned) Favourites.Add(asset); else Favourites.RemoveAll(asset.SameAsset);
        return pinned;
    }
    public bool Remember(AssetReference asset)
    {
        if (!asset.Valid || Recent.FirstOrDefault() is AssetReference first && asset.SameAsset(first)) return false;
        Recent.RemoveAll(asset.SameAsset);
        Recent.Insert(0, asset);
        if (Recent.Count > RecentLimit) Recent.RemoveRange(RecentLimit, Recent.Count - RecentLimit);
        return true;
    }
    public string Alias(AssetReference? asset) => asset == null ? "" : Aliases.FirstOrDefault(item => item.Asset.SameAsset(asset))?.Name ?? "";
    public void SetAlias(AssetReference asset, string name)
    {
        name = name.Trim();
        if (!asset.Valid || name.Length > 80 || name.Any(char.IsControl)) throw new ArgumentException(LocalText.T("Enter an alias of at most 80 characters."));
        Aliases.RemoveAll(item => item.Asset.SameAsset(asset));
        if (name.Length > 0) Aliases.Add(new(asset, name));
    }
    public void Save() => AtomicFile.WriteJson(FilePath, new SavedAssets(Favourites, Recent, Aliases));
}
