using System.IO;

namespace CrystalStudio;

internal sealed record MapResourceCheck(string Label, bool Required, bool Available, string? Detail = null);

internal sealed record MapResourceReport(IReadOnlyList<MapResourceCheck> Checks)
{
    public bool CanGenerate => Checks.All(check => !check.Required || check.Available);
    public IReadOnlyList<string> Errors => Checks.Where(check => check.Required && !check.Available).Select(Describe).ToArray();
    public IReadOnlyList<string> Warnings => Checks.Where(check => !check.Required && !check.Available).Select(Describe).ToArray();
    private static string Describe(MapResourceCheck check) => LocalText.Format(check.Required
        ? "Required artwork missing: {0}. {1}" : "Optional artwork unavailable; omitted: {0}. {1}", LocalText.T(check.Label), check.Detail ?? "").Trim();
    public string Summary => string.Join("\n", new[] { LocalText.T(CanGenerate ? "Required artwork ready" : "Cannot generate: restore required artwork") }
        .Concat(Errors).Concat(Warnings));
    public void EnsureReady()
    {
        if (!CanGenerate) throw GenerationDiagnostics.Reject(GenerationFailureKind.MissingAssets, "Artwork preflight", string.Join("\n", Errors));
    }
}

internal sealed class MapResourceCache(TerrainCatalogue terrain, AssetStore assets)
{
    private readonly Dictionary<string, bool> ground = [];
    private readonly Dictionary<(string, bool), bool> walls = [];
    private readonly Dictionary<(string, bool), bool> entrances = [];
    public bool Ground(string id)
    {
        if (!ground.TryGetValue(id, out bool available)) ground[id] = available = MapResourcePreflight.Available(() =>
            terrain.Sets.FirstOrDefault(set => set.Id == id) is { } set && terrain.MatchesAssets(set, assets));
        return available;
    }
    public string GroundDetail(string id, bool? available = null)
    {
        var set = terrain.Sets.FirstOrDefault(candidate => candidate.Id == id);
        if (set == null) return LocalText.Format("Catalogue entry not found: {0}", id);
        string source = assets.Libraries.TryGetValue(set.Library, out var library)
            ? library.Path + ((available ?? Ground(id)) ? "" : "\n" + LocalText.T("Loaded artwork does not match the catalogue (empty, unreadable or not 96 × 64)."))
            : LocalText.Format("Library {0} is not loaded.", set.Library);
        return $"{set.Name} ({id})\n{source}";
    }
    public void Clear() { ground.Clear(); walls.Clear(); entrances.Clear(); }
    public bool Wall(WallFamily? family, bool interior)
    {
        if (family == null) return false;
        var key = (family.Id, interior);
        if (!walls.TryGetValue(key, out bool available)) walls[key] = available = MapResourcePreflight.Available(() =>
        {
            var required = family.WithMode(interior); required.RequestedDoorCount = 0;
            return required.Availability(assets) == null;
        });
        return available;
    }
    public bool Entrances(WallFamily? family, bool interior)
    {
        if (family == null) return false;
        var key = (family.Id, interior);
        if (!entrances.TryGetValue(key, out bool available)) entrances[key] = available = MapResourcePreflight.Available(() =>
        {
            if (interior && family.Id == "stone-temple")
                return new[] { 377, 381 }.SelectMany(number => CellCodec.LoadStamp(DataPaths.ResolveEditor("Objects", "mir", $"mir_{number:00000}.X")))
                    .SelectMany(cell => cell.Cell.ForegroundImages).All(front => assets.Libraries.TryGetValue(front.Library, out var library)
                        && front.Image > 0 && front.Image <= library.Count && library.Decode(front.Image - 1) != null);
            var entranceFamily = family.WithMode(interior); entranceFamily.RequestedDoorCount = 1;
            return family.SupportsDoors && entranceFamily.Availability(assets) == null;
        });
        return available;
    }
    public string WallDetail(WallFamily? family, bool interior)
    {
        if (family == null) return LocalText.T("Required wall catalogue entry is missing.");
        var selected = family.WithMode(interior);
        var used = (selected.DrawInterior ? selected.InnerVariants : selected.Variants).SelectMany(variant => variant.Parts)
            .SelectMany(part => new[] { part.PieceId }.Concat(part.Choices?.Select(choice => choice.PieceId) ?? [])).ToHashSet();
        var libraries = selected.Pieces.Where(piece => used.Contains(piece.Id)).SelectMany(piece => piece.Cells)
            .SelectMany(cell => cell.Cell.ForegroundImages).Select(front => front.Library).Distinct().Order();
        return LocalText.T(family.Name) + "\n" + string.Join("\n", libraries.Select(id => assets.Libraries.TryGetValue(id, out var library)
            ? library.Path : LocalText.Format("Library {0} is not loaded.", id)));
    }
}

internal static class MapResourcePreflight
{
    public static MapResourceReport ProvinceGround(ProvinceStyle style, MapResourceCache cache) => new([
        new("Base ground", true, cache.Ground(style.BaseId), cache.GroundDetail(style.BaseId)),
        new("Patch ground", true, cache.Ground(style.PatchId), cache.GroundDetail(style.PatchId))
    ]);

    internal static bool Available(Func<bool> check)
    {
        try { return check(); }
        catch (IOException) { return false; }
        catch (InvalidDataException) { return false; }
        catch (System.Text.Json.JsonException) { return false; }
    }

    public static (ProvinceOptions Options, MapResourceReport Report) Province(TerrainCatalogue terrain, WallCatalogue walls,
        AssetStore assets, IReadOnlyList<ProvinceScenery> scenery, TerrainWaterResources? water, ProvinceOptions requested,
        ProvinceTownResources towns, MapResourceCache? cache = null, CancellationToken cancellation = default)
    {
        cache ??= new(terrain, assets);
        var style = requested.Style ?? ProvinceStyle.All[0];
        var checks = ProvinceGround(style, cache).Checks.ToList();
        var options = requested;
        if (requested.CliffChance > 0)
        {
            bool available = cache.Wall(walls.Families.FirstOrDefault(family => family.Id == style.WallId), false) && cache.Ground(ProvinceCliffTerrain.PlateauId(style.WallId));
            checks.Add(new("Cliffs and plateau", false, available, cache.WallDetail(walls.Families.FirstOrDefault(family => family.Id == style.WallId), false)));
            if (!available) options = options with { CliffChance = 0, DoorCount = 0 };
        }
        if (requested.DoorCount > 0)
        {
            bool available = options.CliffChance > 0 && cache.Entrances(walls.Families.FirstOrDefault(family => family.Id == style.WallId), false);
            checks.Add(new("Entrances", false, available));
            if (!available) options = options with { DoorCount = 0 };
        }
        bool shoreRoute = terrain.Sets.FirstOrDefault(set => set.Id == style.BaseId) is { } basis && terrain.Water?.LandRoute(terrain, basis) is { } route && route.All(set => cache.Ground(set.Id))
            && cache.Ground(terrain.Water.WaterId);
        if (requested.BeachChance > 0)
        {
            bool available = water?.Shore != null && shoreRoute;
            checks.Add(new("Beach and shoreline", false, available));
            if (!available) options = options with { BeachChance = 0 };
        }
        if (requested.RiverChance > 0)
        {
            bool available = water?.Banks != null && shoreRoute;
            checks.Add(new("River banks", false, available));
            if (!available) options = options with { RiverChance = 0 };
        }
        if (requested.ForestChance > 0 || requested.Vegetation > 0)
        {
            bool available = scenery.Count > 0;
            checks.Add(new("Outdoor scenery", false, available));
            if (!available) options = options with { ForestChance = 0, Vegetation = 0 };
        }
        if (requested.TownCount > 0)
        {
            bool available = Available(() =>
            {
                cancellation.ThrowIfCancellationRequested();
                var family = towns.Family(style, assets, cancellation);
                towns.Walls(assets, cancellation);
                return family.Pieces.Count > 0;
            });
            checks.Add(new("Town buildings", false, available));
            if (!available) options = options with { TownCount = 0 };
        }
        return (options, new(checks));
    }

    public static (TempleOptions Options, MapResourceReport Report) Dungeon(TempleOptions requested, WallCatalogue walls, AssetStore assets, MapResourceCache cache)
    {
        var temple = requested.Temple;
        var family = walls.Families.FirstOrDefault(family => family.Id == temple.WallId);
        var checks = new List<MapResourceCheck>
        {
            new("Dungeon floor", true, cache.Ground(temple.FloorId), cache.GroundDetail(temple.FloorId)),
            new("Dungeon walls", true, cache.Wall(family, true), cache.WallDetail(family, true))
        };
        var options = requested;
        if (requested.DoorCount > 0)
        {
            bool available = cache.Entrances(family, true);
            checks.Add(new("Entrances", false, available));
            if (!available) options = options with { DoorCount = 0 };
        }
        return (options, new(checks));
    }
}

internal enum GeneratorPreviewPhase { Checking, Waiting, Generating, Ready, Failed, Cancelling, Cancelled }

internal sealed class GeneratorPreviewState
{
    public MapResourceReport? Resources { get; private set; }
    public GeneratorPreviewPhase Phase { get; private set; } = GeneratorPreviewPhase.Checking;
    public string SourceDescription { get; private set; } = "";
    public string? Failure { get; private set; }
    internal GenerationFailure? FailureDetails { get; private set; }
    public string Stage { get; private set; } = "Checking required and optional artwork…";
    public GenerationImpact? Impact { get; private set; }
    public event EventHandler? Changed;
    public event EventHandler? Completed;
    public void SetSource(AssetStore assets)
    {
        SourceDescription = LocalText.Format("Source format: {0} (.lib)\nLoaded folder: {1}", assets.Format == AssetSourceFormat.Sanxiu ? "Sanxiu" : "Crystal",
            assets.LoadedFolder ?? LocalText.T("No folder metadata; see loaded library paths below."))
            + "\n" + LocalText.Format("Loaded library paths (first {0} of {1}):", Math.Min(12, assets.Libraries.Count), assets.Libraries.Count)
            + "\n" + string.Join("\n", assets.Libraries.OrderBy(pair => pair.Key).Take(12).Select(pair => $"{pair.Key}: {pair.Value.Path}"));
    }
    public void SetPhase(GeneratorPreviewPhase phase, string? failure = null, GenerationFailure? diagnostic = null)
    {
        Phase = phase; Failure = failure; FailureDetails = diagnostic;
        Stage = phase switch
        {
            GeneratorPreviewPhase.Checking => "Checking required and optional artwork…",
            GeneratorPreviewPhase.Generating => "Planning layout and filling artwork…",
            GeneratorPreviewPhase.Waiting => "Parameters changed. Waiting to rebuild; the old preview cannot be applied.",
            GeneratorPreviewPhase.Cancelling => "Stopping… Please wait for cancellation to finish.",
            GeneratorPreviewPhase.Cancelled => "Generation cancelled. The map was not changed.",
            GeneratorPreviewPhase.Ready => "Preview ready",
            _ => "Preview failed. Review artwork details or change the style."
        };
        if (phase == GeneratorPreviewPhase.Waiting) Resources = null;
        Notify();
    }
    internal void SetFailure(Exception exception, string stage)
    {
        var diagnostic = GenerationDiagnostics.Describe(exception, stage);
        SetPhase(GeneratorPreviewPhase.Failed, diagnostic.ToString(), diagnostic);
    }
    public void SetStage(string stage) { Stage = stage; Notify(); }
    public void SetImpact(GenerationImpact impact) { Impact = impact; Notify(); }
    public void Report(MapResourceReport report) { Resources = report; Notify(); }
    public void Notify() => Changed?.Invoke(this, EventArgs.Empty);
    public void Complete() { SetPhase(GeneratorPreviewPhase.Ready); Completed?.Invoke(this, EventArgs.Empty); }
}
