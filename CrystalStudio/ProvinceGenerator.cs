using System.IO;

namespace CrystalStudio;

public sealed record ProvinceStyle(string Name, string WallId, string Region, string BaseId)
{
    public string PatchId { get; init; } = "0:0:950";
    public double PatchCoverage { get; init; } = .55;
    public double TreeDensity { get; init; } = 1;
    public double BushDensity { get; init; } = 1;
    public double RockDensity { get; init; } = 1;
    public double GroveScale { get; init; } = 1;
    public bool Arid { get; init; }
    public static IReadOnlyList<ProvinceStyle> All { get; } =
    [
        new("Bichon woodland", "bichon-province", "BichonProvince", "0:0:0"),
        new("Mongchon desert", "mongchon-province", "MongchonProvince", "0:0:2300")
            { PatchId = "0:0:2350", PatchCoverage = .18, TreeDensity = .18, BushDensity = .3, RockDensity = .65, GroveScale = .55, Arid = true },
        new("Canyon badlands", "canyon", "SerpentValley", "0:0:0")
            { PatchId = "0:0:50", PatchCoverage = .55, TreeDensity = .08, BushDensity = .2, RockDensity = 2.4, GroveScale = .4, Arid = true }
    ];
    public override string ToString() => Name;
}

public enum ProvinceScene { Wilderness, VillageCountryside, ForestRuins, MiningQuarry }
public sealed record ProvinceOptions(int Seed = 1, ProvinceStyle? Style = null, int ForestChance = 85,
    int CliffChance = 70, int BeachChance = 35, int RiverChance = 55, int Vegetation = 55, int TownCount = 0, int DoorCount = 0,
    ProvinceScene Scene = ProvinceScene.Wilderness, int RuinCount = 2, int MineCount = 2)
{
    public int AlgorithmVersion { get; init; } = GenerationAlgorithms.Legacy;
    internal MapDesignSettings? Design { get; init; }
}
public sealed record ProvinceScenery(BrushTree Tree, SpecialBrushKind Kind, IReadOnlyList<string> Regions)
{
    public string? CatalogueId { get; init; }
}
public sealed record ProvincePlan(MapDocument Source, long SourceRevision, MapDocument Generated,
    ProvinceOptions Options, ProvinceStyle Style, IReadOnlyList<string> Features, IReadOnlyList<string> Notes)
{
    public IReadOnlyList<ProvinceMine> Mines { get; init; } = [];
    public IReadOnlyList<ProvinceRuin> Ruins { get; init; } = [];
    public IReadOnlyList<ProvinceTown> Towns { get; init; } = [];
    public IReadOnlyList<GeneratedEntrance> Entrances { get; init; } = [];
}

public static class ProvinceGenerator
{
    private const int Blocked = 0x60000000;

    public static IReadOnlyList<ProvinceScenery> LoadScenery(AssetStore assets, CancellationToken cancellation = default)
    {
        var index = StampCategories.Read(StampCategories.FindIndex() ?? throw new FileNotFoundException("The outdoor object catalogue is unavailable."));
        return LoadScenery(index, assets, cancellation);
    }

    internal static IReadOnlyList<ProvinceScenery> LoadScenery(StampCategoryIndex index, AssetStore assets, CancellationToken cancellation = default)
    {
        var result = new List<ProvinceScenery>();
        foreach (var stamp in index.Objects.OrderBy(s => s.Id))
        {
            cancellation.ThrowIfCancellationRequested();
            var kinds = Enum.GetValues<SpecialBrushKind>().Where(k => k.Matches(stamp)).ToArray();
            if (kinds.Length == 0 || !ProvinceSceneryPalette.IsDesertObject(stamp.Id) && !stamp.RegionGroups.Any(r => ProvinceStyle.All.Any(s => s.Region == r) || r is "WoomyonWoods" or "SerpentValley")) continue;
            // A forest canopy needs living trees; trunks, roots and dead trees
            // are useful brush objects but cannot form a thick woodland border.
            bool bareTree = kinds[0] == SpecialBrushKind.Tree && stamp.Subtype is { } subtype &&
                new[] { "dead", "stump", "trunk", "root" }.Any(s => subtype.Contains(s, StringComparison.OrdinalIgnoreCase));
            if (bareTree && !ProvinceSceneryPalette.IsDesertObject(stamp.Id)) continue;
            try
            {
                var tree = BrushTree.Load(stamp.Name, StampCategories.Resolve(index.ObjectRoot, stamp.File), assets)
                    with { ProvinceGroundSetId = ProvinceSceneryPalette.GroundSetId(stamp.Id) };
                if (tree.Cells.Max(c => c.X) - tree.Cells.Min(c => c.X) > 30 || tree.Cells.Max(c => c.Y) - tree.Cells.Min(c => c.Y) > 40) continue;
                result.Add(new(tree, kinds[0], bareTree ? ["MongchonProvince"] : stamp.RegionGroups) { CatalogueId = stamp.Id });
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                StudioLog.Warning("Province.Scenery", $"Skipping unavailable object; Id={stamp.Id}; File={stamp.File}", ex);
            }
        }
        return result;
    }

    public static ProvincePlan Plan(MapDocument source, TerrainCatalogue terrain, WallCatalogue walls,
        IReadOnlyList<ProvinceScenery> scenery, ProvinceOptions options, CancellationToken cancellation = default,
        RiverCoastPieces? banks = null, RiverShoreComponents? shore = null, AssetStore? assets = null,
        Action<string, TimeSpan>? timing = null, ProvinceTownResources? towns = null, ProvinceRuinResources? ruins = null, ProvinceMineResources? mines = null)
    {
        GenerationAlgorithms.Validate(options.AlgorithmVersion);
        var watch = timing == null ? null : System.Diagnostics.Stopwatch.StartNew();
        void Mark(string stage) { if (watch != null) { timing!(stage, watch.Elapsed); watch.Restart(); } }
        cancellation.ThrowIfCancellationRequested();
        if (source.Width < 128 || source.Height < 128 || (long)source.Width * source.Height > MapDimensions.MaximumGeneratedCells)
            throw new ArgumentException("Province generation needs at least 128×128 cells and supports up to 1,000,000 cells. Larger maps give rivers and borders more room.");
        if (new[] { options.ForestChance, options.CliffChance, options.BeachChance, options.RiverChance, options.Vegetation }.Any(v => v is < 0 or > 100) ||
            options.MineCount is < 1 or > 4 || options.RuinCount is < 1 or > 4 || options.TownCount is < 0 or > 8 || options.DoorCount is < 0 or > WallEntrances.MaximumCount ||
            !Enum.IsDefined(options.Scene) || options.Style != null && !ProvinceStyle.All.Contains(options.Style)) throw new ArgumentException("Invalid province settings.");
        long revision = source.PickupRevision;
        bool refined = GenerationAlgorithms.UsesConstraints(options.AlgorithmVersion);
        if (refined) scenery = scenery.OrderBy(item => item.CatalogueId, StringComparer.Ordinal).ThenBy(item => item.Tree.Name, StringComparer.Ordinal).ThenBy(item => item.Tree.Path, StringComparer.Ordinal).ToArray();
        var constraints = refined ? GenerationConstraints.Compile(source, options.Design, cancellation) : null;
        var boundary = ProvinceBoundary.CreateMask(source.Width, source.Height, options.Seed);
        constraints?.CheckForbidden(boundary, cancellation, (column, row) => column < ProvinceBoundary.MinimumDepth || row < ProvinceBoundary.MinimumDepth ||
            source.Width - 1 - column < ProvinceBoundary.MinimumDepth || source.Height - 1 - row < ProvinceBoundary.MinimumDepth);
        var random = refined ? GenerationRandom.Stream(options.Seed, 0x1001) : new Random(options.Seed);
        var style = options.Style ?? ProvinceStyle.All[options.Scene is ProvinceScene.ForestRuins or ProvinceScene.MiningQuarry ? 0 : random.Next(ProvinceStyle.All.Count)];
        if (options.Scene is ProvinceScene.ForestRuins or ProvinceScene.MiningQuarry && style != ProvinceStyle.All[0])
            throw new ArgumentException("Forest ruins and mining yards require the Bichon woodland landscape.");
        bool forest = random.Next(100) < options.ForestChance, cliffs = random.Next(100) < options.CliffChance;
        bool beach = random.Next(100) < options.BeachChance, river = random.Next(100) < options.RiverChance;
        var notes = new List<string>(); var features = new List<string>();
        var basis = terrain.Sets.Single(s => s.Id == style.BaseId);
        var patch = terrain.Sets.Single(s => s.Id == style.PatchId);
        if (assets != null && new[] { basis, patch }.Any(s => !terrain.MatchesAssets(s, assets)))
            throw GenerationDiagnostics.Reject(GenerationFailureKind.MissingAssets, "Artwork preflight", "The current libraries do not contain the matching province terrain artwork.");
        if (river && banks == null) { river = false; notes.Add("River artwork is unavailable; the river was omitted."); }
        if (beach && shore == null) throw GenerationDiagnostics.Reject(GenerationFailureKind.MissingAssets, "Artwork preflight", "Beach artwork is unavailable. Load the matching shoreline libraries to generate this province.");
        if ((river || beach) && terrain.Water?.LandRoute(terrain, basis) == null)
        {
            if (beach) throw new InvalidDataException("This ground has no reviewed beach transition.");
            river = false; notes.Add("This ground has no reviewed shore transition; water was omitted.");
        }
        var waterMode = river ? beach ? TerrainWaterMode.RiverToSea : TerrainWaterMode.River : beach ? TerrainWaterMode.Beach : TerrainWaterMode.None;
        var generated = new MapDocument(source.Width, source.Height);
        var groundRandom = refined ? GenerationRandom.Stream(options.Seed, 0x1002) : random;
        double coverage = options.Scene == ProvinceScene.ForestRuins ? .8 + groundRandom.NextDouble() * .12 : style.PatchCoverage + groundRandom.NextDouble() * .2;
        var groundOptions = new TerrainOptions(basis, patch, null, options.Seed, coverage,
            Math.Clamp(Math.Min(source.Width, source.Height) / 10.0, 12, 65) + groundRandom.Next(12), 2 + groundRandom.Next(3), 1 + groundRandom.Next(2), waterMode)
            { KeepPathsOnBase = true, CompactBeach = beach && new Random(options.Seed ^ 0xBEAC4).Next(3) != 0, AlgorithmVersion = options.AlgorithmVersion };
        var ground = TerrainGenerator.Plan(generated, new(0, 0, source.Width, source.Height), terrain, groundOptions,
            cancellation, coastPieces: banks, shoreComponents: shore);
        if (beach && !ground.CoastObjects.Any(c => c.Layout.WaterMode is TerrainWaterMode.Beach or TerrainWaterMode.RiverToSea))
            throw new InvalidOperationException("A complete native beach could not fit this map. " + string.Join(" ", ground.Notes));
        notes.AddRange(ground.Notes);
        Mark("Terrain and water planning");
        foreach (var tile in ground.Tiles)
            generated.Set(tile.X, tile.Y, new Cell { BackLibrary = (short)tile.Library, BackImage = tile.Index + 1 });
        var history = new EditHistory();
        history.Begin();
        foreach (var coast in ground.CoastObjects)
        {
            if (generated.PlaceStamp(coast.Layout.Cells, coast.X, coast.Y, TerrainCoastObjects.StampPrefix + coast.Layout.Name, history, (_, _) => { }) == null)
                throw new InvalidDataException("The complete native shoreline did not fit.");
            features.Add(coast.Layout.Name);
        }
        history = new EditHistory();
        Mark("Ground and coast placement");
        constraints?.ReserveRoutes(generated, cancellation, boundary);

        int width = generated.Width, height = generated.Height;
        var grassSets = terrain.Sets.Where(s => s.Material == "Grass").Select(s => s.Id).ToHashSet();
        var grass = ground.Tiles.Where(t => grassSets.Contains(t.SetId))
            .Select(t => (t.X, t.Y)).ToHashSet();
        var reserved = new bool[width * height];
        void Reserve(int x, int y, int radius)
        {
            for (int yy = Math.Max(0, y - radius); yy <= Math.Min(height - 1, y + radius); yy++)
            for (int xx = Math.Max(0, x - radius); xx <= Math.Min(width - 1, x + radius); xx++) reserved[yy * width + xx] = true;
        }
        // Keep every native bank/sea cell and a buffer around it free of scenery.
        foreach (var coast in ground.CoastObjects)
            foreach (var cell in coast.Layout.Cells) Reserve(coast.X + cell.X, coast.Y + cell.Y, 3);
        var coastReserved = (bool[])reserved.Clone();
        constraints?.MergeInto(coastReserved, cancellation);
        constraints?.MergeInto(reserved, cancellation);
        foreach (var tile in ground.PathCells)
            if (Math.Min(Math.Min(tile.X, width - 1 - tile.X), Math.Min(tile.Y, height - 1 - tile.Y)) >= ProvinceBoundary.MaximumDepth + 3)
                Reserve(tile.X, tile.Y, 3);
        // Large clearings and their surroundings remain usable even when a
        // seed chooses a dense forest. The terrain paths join these open areas.
        double Field(double x, double y, double scale, int salt) => Noise(x / scale, y / scale, options.Seed ^ salt);
        var borderRandom = refined ? GenerationRandom.Stream(options.Seed, 0x1003) : random;
        bool[] forestSides = Enumerable.Range(0, 4).Select(_ => forest && borderRandom.NextDouble() < .7).ToArray();
        if (forest && !forestSides.Any(b => b)) forestSides[borderRandom.Next(4)] = true;
        Mark("Reservations");
        IReadOnlyList<GeneratedEntrance> entrances=[];

        if (cliffs)
        {
            var family = walls.Families.Single(f => f.Id == style.WallId).WithMode(false);
            family.RequestedDoorCount=0;
            if (assets != null && family.Availability(assets) is { } error) throw new InvalidDataException(error);
            int sides = ProvinceCliffBorders.Place(generated, family, coastReserved, options.Seed, cancellation);
            Mark("Border cliffs");
            if (sides > 0) features.Add(style.Name.Split(' ')[0] + $" cliff borders ({sides}-side layout)");
            else notes.Add("Cliff borders could not fit around the water for this seed.");
            foreach (var stamp in generated.PlacedStamps.Where(s => s.Wall != null))
                foreach (var cell in stamp.Cells) Reserve(stamp.X + cell.X, stamp.Y + cell.Y, 3);
            int inland = ProvinceLandmarks.PlaceCliffs(generated, family, reserved, options.Seed, cancellation);
            if (inland > 0) features.Add($"{inland} inland cliff formations");
            Mark("Inland cliffs");
            entrances=GeneratorEntrances.AddToCliffs(generated,family,options.DoorCount,options.Seed,assets);
        }
        if (options.DoorCount>0)
        {
            features.Add($"{entrances.Count} entrances");
            if (entrances.Count<options.DoorCount) notes.Add($"Placed {entrances.Count} of {options.DoorCount} entrances where matching cliff openings fit safely.");
        }
        // Protect the complete cliff footprint, including tall foreground cards.
        foreach (var stamp in generated.PlacedStamps.Where(s => s.Wall != null))
            foreach (var cell in stamp.Cells) Reserve(stamp.X + cell.X, stamp.Y + cell.Y, 2);

        if (generated.PlacedStamps.Any(s => s.Wall != null))
        {
            var rock = terrain.Sets.Single(s => s.Id == ProvinceCliffTerrain.PlateauId(style.WallId));
            if (assets != null && !terrain.MatchesAssets(rock, assets)) throw new InvalidDataException("The cliff plateau terrain artwork is unavailable.");
            ProvinceCliffTerrain.Apply(generated, ground, terrain, walls.Families.Single(f => f.Id == style.WallId).WithMode(false), options.Seed, cancellation, style);
        }
        Mark("Cliff terrain");
        var settlements = options.TownCount == 0 ? null : ProvinceTowns.Place(generated, ground, terrain, style,
            options, coastReserved, reserved, towns ?? new(), assets, notes, cancellation);
        if (settlements != null)
        {
            features.Add($"{settlements.Towns.Count} towns · {settlements.Towns.Sum(t => t.Plan.Buildings.Count(b => b.Piece.Role == "house"))} houses");
            Mark("Towns and connecting roads");
        }
        var roads = ground.PathCells.Concat(settlements?.Towns.SelectMany(t =>
            t.Plan.Roads.Select(p => (X: t.X + p.X, Y: t.Y + p.Y)).Concat(t.Approach)) ?? []).ToArray();
        var ruinScenes = options.Scene == ProvinceScene.ForestRuins
            ? ProvinceRuins.Place(generated, terrain, style, options, reserved, roads, ruins ?? new(), assets, notes, cancellation) : [];
        if (ruinScenes.Count > 0) features.Add(LocalText.Format("{0} forest ruin clearings", ruinScenes.Count));
        Mark("Forest ruins and connecting trails");
        var miningSites = options.Scene == ProvinceScene.MiningQuarry
            ? ProvinceMines.Place(generated, terrain, style, options, reserved, roads, mines ?? new(), assets, notes, cancellation) : [];
        if (miningSites.Count > 0) features.Add(LocalText.Format("{0} mining and quarry yards", miningSites.Count));
        Mark("Mining yards and haul roads");
        var sceneryGround = new ProvinceSceneryGround(terrain);
        var boundaryClearings = options.Scene is ProvinceScene.ForestRuins or ProvinceScene.MiningQuarry ? reserved : settlements?.Protected;
        if (constraints != null)
        {
            boundaryClearings = boundaryClearings == null ? new bool[width * height] : (bool[])boundaryClearings.Clone();
            constraints.MergeInto(boundaryClearings, cancellation);
        }
        int perimeterObjects = ProvinceBoundary.Plant(generated, boundary, scenery, style, options.Seed, cancellation, forestSides, boundaryClearings, sceneryGround);
        Mark("Boundary scenery");
        var groves = ProvinceLandmarks.PlantGroves(generated, scenery, style, reserved, options.Seed, options.Vegetation, cancellation, sceneryGround);
        if (groves.Groves > 0) features.Add($"{groves.Groves} dense inland groves");
        int edgePlants = ProvinceEcology.PlantEdges(generated, terrain, scenery, style, reserved, coastReserved,
            roads.Concat(ruinScenes.SelectMany(r => r.Approach)).Concat(miningSites.SelectMany(m => m.Approach)), options.Seed, options.Vegetation, cancellation, options.AlgorithmVersion);
        edgePlants += ProvinceRuins.PlantOvergrowth(generated, ruinScenes, scenery, terrain, style, reserved, options, cancellation);
        if (edgePlants > 0) features.Add(LocalText.Format("{0} woodland, trail and shore edge plants", edgePlants));
        int trees = (style.Arid ? 0 : perimeterObjects) + groves.Trees + Scatter(SpecialBrushKind.Tree, 4);
        int bushes = edgePlants + Scatter(SpecialBrushKind.Bush, 3);
        int rocks = (style.Arid ? perimeterObjects : 0) + Scatter(SpecialBrushKind.Rock, 6);
        Mark("Interior scenery");
        if (perimeterObjects > 0) features.Add(style.Arid ? "Rocky desert borders" : "Thick woodland borders");
        else if (forest) notes.Add("The wider border could not fit around the water and cliffs for this seed.");
        features.Add($"{trees:N0} trees · {bushes:N0} bushes · {rocks:N0} rocks");
        ProvinceBoundary.Seal(generated, boundary, cancellation);
        constraints?.EnsureSatisfied(generated, cancellation);
        Mark("Seal");
        features.Add("Continuous 10–15 tile unwalkable perimeter");
        foreach (var kind in Enum.GetValues<SpecialBrushKind>())
            if (!scenery.Any(s => s.Kind == kind)) notes.Add($"No matching outdoor {kind.Plural()} are available.");
        cancellation.ThrowIfCancellationRequested();
        if (source.PickupRevision != revision) throw new InvalidOperationException("The map changed while generating the province preview.");
        return new(source, revision, generated, options, style, features, notes) { Towns = settlements?.Towns ?? [], Entrances=entrances, Ruins = ruinScenes, Mines = miningSites };

        int Scatter(SpecialBrushKind kind, int spacing)
        {
            var pool = ProvinceSceneryPalette.Select(scenery, style, kind);
            if (pool.Length == 0) return 0;
            double Density(int x, int y)
            {
                double grove = Math.Clamp((Field(x, y, 34, 331) - .44) * 3.5, 0, 1);
                double clearing = Math.Pow((x - width * .5) / (width * .18), 2) + Math.Pow((y - height * .5) / (height * .18), 2);
                double density = kind switch
                {
                    SpecialBrushKind.Tree => grove * (grass.Contains((x & ~1, y & ~1)) ? .6 : .1) * options.Vegetation / 100.0 * style.TreeDensity,
                    SpecialBrushKind.Bush => (.09 + grove * .12) * options.Vegetation / 100.0 * style.BushDensity,
                    _ => (.045 + .09 * Field(x, y, 23, 812)) * style.RockDensity
                };
                return density * (.1 + .9 * Math.Clamp((Math.Sqrt(clearing) - .25) / .75, 0, 1));
            }
            if (refined) return ProvinceScenerySampling.Place(generated, pool, sceneryGround, reserved, spacing, options.Seed, 0x4000 + (int)kind, Density, cancellation);
            int count = 0;
            // One jittered candidate per site avoids rows without making the
            // distribution depend on preview size or asynchronous rendering.
            for (int sy = 2; sy < height - 2; sy += spacing)
            {
                cancellation.ThrowIfCancellationRequested();
                for (int sx = 2; sx < width - 2; sx += spacing)
                {
                    int x = sx + random.Next(spacing), y = sy + random.Next(spacing);
                    if (!generated.Contains(x, y)) continue;
                    double density = Density(x, y);
                    if (random.NextDouble() >= density) continue;
                    var tree = pool[random.Next(pool.Length)];
                    if (!sceneryGround.CanPlace(generated, tree, x, y)) continue;
                    int left = x + tree.Cells.Min(c => c.X), right = x + tree.Cells.Max(c => c.X);
                    int top = y + tree.Cells.Min(c => c.Y), bottom = y + tree.Cells.Max(c => c.Y);
                    if (left < 1 || top < 1 || right >= width - 1 || bottom >= height - 1) continue;
                    bool blocked = false;
                    for (int yy = top; yy <= bottom && !blocked; yy++)
                    for (int xx = left; xx <= right; xx++)
                        if (reserved[yy * width + xx]) { blocked = true; break; }
                    // Allow crowns to overlap naturally, while keeping every
                    // authored sprite anchor and collision footprint distinct.
                    if (tree.Cells.Any(c => (generated.Get(x + c.X, y + c.Y).BackImage & Blocked) != 0 || generated.Get(x + c.X, y + c.Y).ForegroundImages.Count > 0)) blocked = true;
                    if (blocked) continue;
                    // Reuse validated special-brush stamps and their native collisions.
                    if (generated.PlaceStamp(tree.Cells, x, y, tree.Name, history, (_, _) => { }) != null)
                    {
                        count++;
                    }
                }
            }
            return count;
        }
    }

    private static double Noise(double x, double y, int seed)
    {
        int ix = (int)Math.Floor(x), iy = (int)Math.Floor(y);
        double fx = x - ix, fy = y - iy; fx = fx * fx * (3 - 2 * fx); fy = fy * fy * (3 - 2 * fy);
        double At(int a, int b)
        {
            uint h = unchecked((uint)a * 0x9e3779b9u ^ (uint)b * 0x85ebca6bu ^ (uint)seed);
            h ^= h >> 16; h *= 0x7feb352d; h ^= h >> 15; h *= 0x846ca68b; h ^= h >> 16;
            return h / (double)uint.MaxValue;
        }
        return (At(ix, iy) * (1 - fx) + At(ix + 1, iy) * fx) * (1 - fy) + (At(ix, iy + 1) * (1 - fx) + At(ix + 1, iy + 1) * fx) * fy;
    }
}
