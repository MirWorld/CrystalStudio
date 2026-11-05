using System.IO;
using System.Text.Json;

namespace CrystalStudio;

public sealed class TerrainCatalogue
{
    public int SchemaVersion { get; set; }
    public string ReviewBasis { get; set; } = "";
    public List<TerrainSet> Sets { get; set; } = [];
    public List<TerrainBlend> Blends { get; set; } = [];
    public List<TerrainRoute> Routes { get; set; } = [];
    public List<TerrainRejectedBlend> RejectedBlends { get; set; } = [];
    public TerrainWaterCatalogue? Water { get; private set; }
    public static TerrainCatalogue Load(string? path = null)
    {
        var result = StudioJson.Deserialize<TerrainCatalogue>(File.ReadAllText(path ?? DataPaths.ResolveEditor("catalogue", "terrain.json")), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidDataException("Empty terrain catalogue.");
        if (result.SchemaVersion != 1 || result.Sets.Count == 0 || result.Sets.Select(s => s.Id).Distinct().Count() != result.Sets.Count) throw new InvalidDataException("Invalid terrain catalogue version or IDs.");
        foreach (var s in result.Sets)
        {
            if (s.Layer != 0 || s.Library < 0 || string.IsNullOrWhiteSpace(s.Name) || !s.Shapes.ContainsKey(15) || s.Shapes.Any(p => p.Key is < 1 or > 15 or 6 or 9 || p.Value.Count == 0 || p.Value.Any(v => v.Index < 0 || v.Weight <= 0 || v.PixelSha256.Length != 64))) throw new InvalidDataException($"Invalid terrain set: {s.Id}");
        }
        foreach (var b in result.Blends)
            if (b.Status != "reviewed" || b.BaseId == b.OverlayId || !result.Sets.Any(s => s.Id == b.BaseId && s.Base) || !result.Sets.Any(s => s.Id == b.OverlayId && s.Shapes.Count == 13) || b.Count < 8 || b.Directions.Distinct().Count() != 4 || b.Maps.Count == 0 || !b.BackgroundCheck.Passed || b.BackgroundCheck.Method != "outside-band-colour-v1" || !double.IsFinite(b.BackgroundCheck.MeanDistance) || !double.IsFinite(b.BackgroundCheck.MaxDistance) || b.BackgroundCheck.MeanDistance > 8 || b.BackgroundCheck.MaxDistance > 14) throw new InvalidDataException("Invalid terrain blend or baked-background evidence.");
        foreach (var rejected in result.RejectedBlends)
        {
            string baseId=$"0:0:{rejected.Base}",overlayId=$"0:0:{rejected.Overlay}";
            if(string.IsNullOrWhiteSpace(rejected.Reason)||!result.Sets.Any(s=>s.Id==baseId)||!result.Sets.Any(s=>s.Id==overlayId)
                ||result.Blends.Any(b=>b.BaseId==baseId&&b.OverlayId==overlayId))
                throw new InvalidDataException("Explicitly rejected terrain blends must remain excluded from direct generation.");
        }
        foreach (var route in result.Routes)
        {
            if (route.ViaIds.Count != 1 || string.IsNullOrWhiteSpace(route.Reason)) throw new InvalidDataException("Invalid terrain transition route.");
            string[] steps = [route.BaseId, .. route.ViaIds, route.OverlayId];
            if (steps.Distinct().Count() != steps.Length || steps.Any(id => !result.Sets.Any(s => s.Id == id)) || steps.Zip(steps.Skip(1)).Any(pair => !result.Blends.Any(b => b.BaseId == pair.First && b.OverlayId == pair.Second))) throw new InvalidDataException("Every route step needs its own reviewed direct blend.");
        }
        string waterPath = Path.Combine(Path.GetDirectoryName(path ?? DataPaths.ResolveEditor("catalogue", "terrain.json"))!, "water.json");
        if (File.Exists(waterPath))
        {
            result.Water = TerrainWaterCatalogue.Load(waterPath);
            if (!result.Sets.Any(s => s.Id == result.Water.LandId && s.Base) || !result.Sets.Any(s => s.Id == result.Water.WaterId && s.Material == "Water")) throw new InvalidDataException("Water catalogue surfaces are missing.");
        }
        return result;
    }
    public bool CanBlend(TerrainSet basis, TerrainSet overlay) => Blends.Any(b => b.BaseId == basis.Id && b.OverlayId == overlay.Id && b.Status == "reviewed");
    public IReadOnlyList<TerrainSet>? GenerationLayers(TerrainSet basis, TerrainSet overlay)
    {
        if (CanBlend(basis, overlay)) return [overlay];
        var route = Routes.FirstOrDefault(r => r.BaseId == basis.Id && r.OverlayId == overlay.Id);
        return route == null ? null : route.ViaIds.Select(id => Sets.Single(s => s.Id == id)).Append(overlay).ToList();
    }
    public bool CanGenerate(TerrainSet basis, TerrainSet overlay) => GenerationLayers(basis, overlay) != null;
    public IReadOnlyList<TerrainSet> Overlays(TerrainSet basis) => Sets.Where(s => CanGenerate(basis, s)).OrderBy(s => s.Name).ToList();
    // Verify only the catalogue sprites, so an unrelated asset change does not
    // invalidate reviewed terrain. Numeric library IDs alone are not identity.
    public bool MatchesAssets(TerrainSet set, AssetStore assets)
    {
        if (!assets.Libraries.TryGetValue(set.Library, out var library)) return false;
        foreach (var v in set.Shapes.Values.SelectMany(v => v).DistinctBy(v => v.Index))
        {
            var sprite = library.Decode(v.Index);
            if (sprite == null || sprite.Width != 96 || sprite.Height != 64) return false;
        }
        return true;
    }
}
public sealed class TerrainSet
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Biome { get; set; } = "";
    public string Material { get; set; } = "";
    public int Library { get; set; }
    public int Layer { get; set; }
    public int Start { get; set; }
    public bool Base { get; set; }
    public Dictionary<int, List<TerrainVariant>> Shapes { get; set; } = [];
    public List<TerrainUsage> Usage { get; set; } = [];
    public long NativeCount { get; set; }
    public override string ToString() => Name;
}
public sealed class TerrainVariant { public int Index { get; set; } public long Weight { get; set; } public string PixelSha256 { get; set; } = ""; }
public sealed class TerrainUsage { public string FileName { get; set; } = ""; public string Title { get; set; } = ""; public long Count { get; set; } }
public sealed class TerrainBlend
{
    public string BaseId { get; set; } = ""; public string OverlayId { get; set; } = ""; public string Status { get; set; } = "";
    public long Count { get; set; } public List<string> Directions { get; set; } = []; public List<string> Maps { get; set; } = [];
    public TerrainBackgroundCheck BackgroundCheck { get; set; } = new();
}
public sealed class TerrainBackgroundCheck
{
    public string Method { get; set; } = ""; public double MeanDistance { get; set; } public double MaxDistance { get; set; } public bool Passed { get; set; }
}
public sealed class TerrainRoute
{
    public string BaseId { get; set; } = ""; public string OverlayId { get; set; } = ""; public List<string> ViaIds { get; set; } = []; public string Reason { get; set; } = "";
}

public sealed class TerrainRejectedBlend
{
    public int Base { get; set; }
    public int Overlay { get; set; }
    public string Reason { get; set; } = "";
}
