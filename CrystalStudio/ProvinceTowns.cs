using System.IO;

namespace CrystalStudio;

public sealed record ProvinceTown(int X, int Y, TownPlan Plan, IReadOnlyList<(int X, int Y)> Approach);

// The dialog serializes generation. Verify a family's sprites once per dialog,
// rather than decoding and hashing the same buildings on every seed change.
public sealed class ProvinceTownResources
{
    private TownCatalogue? catalogue;
    private TownWallResources? walls;
    private readonly Dictionary<string, TownFamily> prepared = [];
    private readonly Dictionary<(int Library, int Image), Sprite> sprites = [];
    internal TownWallResources? Walls(AssetStore? assets, CancellationToken token) =>
        assets == null ? null : walls ??= TownWallResources.Load(assets, sprites, token);
    internal TownFamily Family(ProvinceStyle style, AssetStore? assets, CancellationToken token)
    {
        string id = style.Arid ? "mongchon" : "bichon";
        catalogue ??= TownCatalogue.Load();
        var family = catalogue.Families.Single(f => f.Id == id);
        if (assets == null) return family;
        if (!prepared.TryGetValue(id, out var result))
            prepared[id] = result = TownCatalogue.Prepare(family, assets, sprites, out _, token);
        return result;
    }
}

internal sealed record ProvinceSettlements(List<ProvinceTown> Towns, bool[] Protected);

internal static class ProvinceTowns
{
    internal static ProvinceSettlements Place(MapDocument map, TerrainPlan ground, TerrainCatalogue terrain, ProvinceStyle style,
        ProvinceOptions options, bool[] coast, bool[] reserved, ProvinceTownResources resources, AssetStore? assets,
        List<string> notes, CancellationToken token)
    {
        int width = map.Width, height = map.Height, columns = (width + 1) / 2, rows = (height + 1) / 2;
        var random = new Random(options.Seed ^ 0x70A45);
        var family = resources.Family(style, assets, token);
        if (options.Scene == ProvinceScene.VillageCountryside)
            family = family with { Pieces = family.Pieces.Where(p => p.Role is not ("prop" or "greenery") ||
                ProvinceSceneryPalette.GroundSetId(p.Id) != "unverified").ToList() };
        var wallResources = options.TownCount > 0 ? resources.Walls(assets, token) : null;
        var basis = terrain.Sets.Single(s => s.Id == style.BaseId);
        var patch = terrain.Sets.Single(s => s.Id == style.PatchId);
        var masks = new Dictionary<(int, int), int>();
        foreach (var shape in basis.Shapes) foreach (var tile in shape.Value) masks[(basis.Library, tile.Index + 1)] = 0;
        foreach (var shape in patch.Shapes) foreach (var tile in shape.Value) masks[(patch.Library, tile.Index + 1)] = shape.Key;
        // Ground does not change until BlendStreets. Resolve each ground tile
        // once for site scoring and road searches, rather than per map cell.
        var groundMasks = new int[columns * rows];
        for (int y = 0; y < rows; y++)
        {
            token.ThrowIfCancellationRequested();
            for (int x = 0; x < columns; x++)
            {
                var tile = map.Get(x * 2, y * 2);
                groundMasks[y * columns + x] = masks.GetValueOrDefault((tile.BackLibrary, tile.BackImage & 0x1fffffff), -1);
            }
        }
        var hard = (bool[])coast.Clone();
        foreach (var stamp in map.PlacedStamps.Where(s => s.Wall != null))
            foreach (var c in stamp.Cells) Mark(hard, stamp.X + c.X, stamp.Y + c.Y, 4);
        // A site must contain ordinary province land throughout, not a plateau,
        // a beach's dry-looking native cards, or an isolated patch inside water.
        for (int y = 0; y < height; y++)
        {
            token.ThrowIfCancellationRequested();
            for (int x = 0; x < width; x++)
            {
                var c = map.Get(x, y);
                if (x < 22 || y < 22 || x >= width - 22 || y >= height - 22 ||
                    (c.BackImage & 0x60000000) != 0 || c.ForegroundImages.Count > 0 || c.MiddleImage != 0 ||
                    groundMasks[y / 2 * columns + x / 2] < 0) hard[y * width + x] = true;
            }
        }
        var blocked = Prefix((x, y) => hard[y * width + x]);
        var open = Prefix((x, y) => groundMasks[y / 2 * columns + x / 2] == 0);
        var protectedCells = new bool[width * height];
        var result = new List<ProvinceTown>();
        var streets = new HashSet<(int X, int Y)>();
        var network = new bool[columns * rows];
        var paths = ground.PathCells.Where(p => !hard[p.Y * width + p.X]).ToArray();
        var landmarks = paths.Where((_, i) => i % 12 == 0).ToArray();
        // Navigation uses ground tiles with enough clearance for a walkable
        // three-cell lane. This also stops diagonal routes cutting bank corners.
        var navigable = new bool[columns * rows];
        for (int y = 2; y < rows - 2; y++) for (int x = 2; x < columns - 2; x++)
            navigable[y * columns + x] = Count(blocked, x * 2 - 3, y * 2 - 3, 7) == 0;
        foreach (var p in paths) if (navigable[p.Y / 2 * columns + p.X / 2]) network[p.Y / 2 * columns + p.X / 2] = true;

        // Measure connected usable land, not the sea or inaccessible plateaus.
        // Labels also prevent repeated road searches to unreachable faraway sites.
        var regions = new int[navigable.Length];
        var regionAreas = new List<int> { 0 };
        var frontier = new Queue<int>();
        for (int start = 0; start < regions.Length; start++)
        {
            if (!navigable[start] || regions[start] != 0) continue;
            int region = regionAreas.Count, area = 0;
            regions[start] = region; frontier.Enqueue(start);
            while (frontier.TryDequeue(out int key))
            {
                if ((area & 4095) == 0) token.ThrowIfCancellationRequested();
                area += 4;
                int x = key % columns, y = key / columns;
                Visit(x - 1, y); Visit(x + 1, y); Visit(x, y - 1); Visit(x, y + 1);
                void Visit(int nx, int ny)
                {
                    if (nx < 0 || ny < 0 || nx >= columns || ny >= rows) return;
                    int next = ny * columns + nx;
                    if (!navigable[next] || regions[next] != 0) return;
                    regions[next] = region; frontier.Enqueue(next);
                }
            }
            regionAreas.Add(area);
        }
        var trailRegions = Enumerable.Range(0, network.Length).Where(i => network[i]).Select(i => regions[i]).ToHashSet();
        int settledRegion = 0;
        bool reducedSpacing = false;
        // Failed candidate sites share the same search workspace. Retain the
        // large buffers for this generation only, instead of allocating per try.
        float[]? routeCosts = null;
        int[]? routePrevious = null;
        var routeQueue = new PriorityQueue<int, float>();

        for (int number = 0; number < options.TownCount; number++)
        {
            token.ThrowIfCancellationRequested();
            bool placed = false;
            int preferred = Math.Min(112, Math.Min(width, height) / 3) & ~1;
            var sizes = new[] { Math.Max(64, preferred - random.Next(3) * 8), 80, 64 }.Distinct().ToArray();
            // Try smaller settlements at the full spacing before compromising
            // separation. Restricted maps can still fit their requested count.
            foreach (double spacingScale in result.Count == 0 ? new[] { 1.0 } : [1.0, .85, .7, 0])
            {
                foreach (int size in sizes)
                {
                    var candidates = new List<(int X, int Y, double Score, double Distance, int Region)>();
                    for (int y = 28; y + size + 6 < height - 22; y += 8)
                    {
                        token.ThrowIfCancellationRequested();
                        for (int x = 28; x + size + 6 < width - 22; x += 8)
                        {
                            if (Count(blocked, x - 6, y - 6, size + 12) != 0) continue;
                            if (result.Any(t => x < t.X + t.Plan.Options.Size + 28 && x + size + 28 > t.X &&
                                y < t.Y + t.Plan.Options.Size + 28 && y + size + 28 > t.Y)) continue;
                            int cx = x + size / 2, cy = y + size / 2;
                            int region = regions[cy / 2 * columns + cx / 2];
                            if (region == 0 || settledRegion != 0 && region != settledRegion ||
                                settledRegion == 0 && paths.Length > 0 && !trailRegions.Contains(region)) continue;
                            double nearest = result.Count == 0 ? double.PositiveInfinity : result.Min(t => Math.Sqrt(
                                Math.Pow(cx - t.X - t.Plan.Options.Size / 2.0, 2) + Math.Pow(cy - t.Y - t.Plan.Options.Size / 2.0, 2)));
                            double spacing = .85 * Math.Sqrt(regionAreas[region] / (double)options.TownCount);
                            if (nearest < spacing * spacingScale) continue;
                            double distance = landmarks.Length == 0 ? 0 : landmarks.Min(p => Math.Abs(p.X - cx) + Math.Abs(p.Y - cy));
                            // Spread across the province before choosing between nearby
                            // clearings. Local terrain quality must not cluster towns.
                            double score = Count(open, x, y, size) / (double)(size * size) * 50 - distance * .18 + random.NextDouble() * 12;
                            if (result.Count > 0) score = Math.Clamp(score, -30, 60) + 200 * Math.Min(nearest / spacing, 2);
                            candidates.Add((x, y, score, nearest, region));
                        }
                    }
                    if (candidates.Count == 0) continue;
                    if (settledRegion == 0)
                    {
                        // Start on the landmass offering the most actual town sites,
                        // rather than a small island with a particularly good trail.
                        int region = candidates.GroupBy(c => c.Region).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).First().Key;
                        candidates.RemoveAll(c => c.Region != region);
                    }
                    TownPlan? town = null;
                    for (int attempt = 0; attempt < 4 && town == null; attempt++)
                    {
                        TownLayout[] layouts = [TownLayout.WindingStreet, TownLayout.ForkedLanes, TownLayout.LinkedHamlets, TownLayout.HighStreet];
                        try
                        {
                            town = TownGenerator.Generate(family, new(size, random.Next(), 3, random.Next(48, 71),
                                Ground: false, Greenery: !style.Arid, Layout: layouts[random.Next(layouts.Length)],
                                Decorations: options.Scene == ProvinceScene.VillageCountryside ? TownDecorations.Detailed : TownDecorations.Sparse),
                                cancellationToken: token, wallResources: wallResources);
                        }
                        catch (InvalidDataException) { }
                    }
                    if (town == null) continue;
                    // Native samples can carry the runtime door-open bit. Crystal
                    // reloads the seven-bit door ID, so canonicalize it in preview too.
                    town = town with { Cells = town.Cells.Select(c => c.Cell.Door < 128 ? c :
                        c with { Cell = c.Cell with { Door = (byte)(c.Cell.Door & 0x7f) } }).ToList() };
                    foreach (var site in candidates.OrderByDescending(c => c.Score).Take(48))
                    {
                        token.ThrowIfCancellationRequested();
                        var localRoads = town.Roads.Select(p => (X: p.X + site.X, Y: p.Y + site.Y)).ToArray();
                        var buildingBlocks = town.Buildings.SelectMany(b => b.Piece.Footprint.Select(p =>
                            ((site.Y + b.Y + p.Y) / 2) * columns + (site.X + b.X + p.X) / 2)).ToHashSet();
                        // Route on the same two-cell grid as province roads. Keep
                        // gateways open, but never connect through an enclosure.
                        foreach (var wall in town.Walls)
                            foreach (var p in TownWalls.RoadBlocks(wall))
                                buildingBlocks.Add((site.Y + p.Y) / 2 * columns + (site.X + p.X) / 2);
                        var route = Connect(localRoads, buildingBlocks);
                        // Towns after the first must join the same accessible road
                        // network; don't silently strand settlements across a river.
                        if (route == null && (result.Count > 0 || paths.Length > 0)) continue;
                        int ax = site.X - town.Offset, ay = site.Y - town.Offset;
                        // Keep towns as plans until streets have their final ground.
                        // Otherwise a valid prop can be left on incompatible soil
                        // when the coarse-sand or grass road blend repaints its yard.
                        if (!map.CanPlaceStamp(town.Cells, ax, ay)) continue;
                        route ??= [];
                        settledRegion = site.Region;
                        reducedSpacing |= site.Distance < .85 * Math.Sqrt(regionAreas[site.Region] / (double)options.TownCount);
                        result.Add(new(site.X, site.Y, town, route));
                        // Reserve building-shaped yards and lanes rather than a
                        // square stamp boundary. Trees can feather into unused lots.
                        foreach (var b in town.Buildings) foreach (var p in b.Piece.Footprint)
                            Mark(protectedCells, site.X + b.X + p.X, site.Y + b.Y + p.Y, 4);
                        foreach (var wall in town.Walls) foreach (var p in wall.Footprint)
                            Mark(protectedCells, site.X + p.X, site.Y + p.Y, 4);
                        foreach (var c in town.Cells.Where(c => !c.Cell.Empty))
                            Mark(protectedCells, ax + c.X, ay + c.Y, 2);
                        foreach (var p in localRoads.Concat(route))
                        {
                            streets.Add(p); Mark(protectedCells, p.X, p.Y, 4);
                        }
                        // Only connected settlement roads become the target for
                        // subsequent towns (unrelated terrain trails aren't enough).
                        if (result.Count == 1) Array.Clear(network);
                        foreach (var p in streets) network[p.Y / 2 * columns + p.X / 2] = true;
                        foreach (int key in buildingBlocks) navigable[key] = false;
                        placed = true; break;
                    }
                    if (placed) break;
                }
                if (placed) break;
            }
            if (!placed) break;
        }
        if (result.Count < options.TownCount)
            notes.Add($"Placed {result.Count} of {options.TownCount} requested towns; the remaining sites were too small or cut off by water/cliffs.");
        if (reducedSpacing) notes.Add("Water, cliffs or limited space required closer town spacing.");
        BlendStreets(map, basis, patch, masks, hard, streets, options.Seed, token,
            options.Scene == ProvinceScene.VillageCountryside ? 0 : 1);
        var decorationGround = new ProvinceSceneryGround(terrain);
        for (int i = 0; i < result.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var town = result[i];
            var compatible = MatchDecorations(map, town.Plan, town.X, town.Y, decorationGround);
            if (map.PlaceStamp(compatible.Cells, town.X - compatible.Offset, town.Y - compatible.Offset,
                "Province " + compatible.Name, new(), (_, _) => { }) == null)
                throw new InvalidDataException("The planned province town no longer fits.");
            result[i] = town with { Plan = compatible };
        }
        for (int i = 0; i < reserved.Length; i++) reserved[i] |= protectedCells[i];
        return new(result, protectedCells);

        void Mark(bool[] cells, int cx, int cy, int radius)
        {
            for (int y = Math.Max(0, cy - radius); y <= Math.Min(height - 1, cy + radius); y++)
            for (int x = Math.Max(0, cx - radius); x <= Math.Min(width - 1, cx + radius); x++)
                if ((x - cx) * (x - cx) + (y - cy) * (y - cy) <= radius * radius) cells[y * width + x] = true;
        }
        int[] Prefix(Func<int, int, bool> test)
        {
            var sum = new int[(width + 1) * (height + 1)];
            for (int y = 0; y < height; y++)
            {
                token.ThrowIfCancellationRequested(); int line = 0;
                for (int x = 0; x < width; x++)
                {
                    if (test(x, y)) line++;
                    sum[(y + 1) * (width + 1) + x + 1] = sum[y * (width + 1) + x + 1] + line;
                }
            }
            return sum;
        }
        int Count(int[] sum, int x, int y, int size)
        {
            if (x < 0 || y < 0 || x + size > width || y + size > height) return int.MaxValue;
            int stride = width + 1;
            return sum[(y + size) * stride + x + size] - sum[y * stride + x + size] - sum[(y + size) * stride + x] + sum[y * stride + x];
        }
        List<(int X, int Y)>? Connect((int X, int Y)[] localRoads, HashSet<int> buildings)
        {
            if (!network.Any(v => v)) return null;
            var queue = routeQueue; queue.Clear();
            var costs = routeCosts ??= new float[network.Length]; Array.Fill(costs, float.PositiveInfinity);
            var previous = routePrevious ??= new int[network.Length]; Array.Fill(previous, -1);
            foreach (var p in localRoads)
            {
                int key = p.Y / 2 * columns + p.X / 2;
                if (!navigable[key] || buildings.Contains(key) || costs[key] == 0) continue;
                costs[key] = 0; queue.Enqueue(key, 0);
            }
            int visits = 0;
            while (queue.TryDequeue(out int key, out float cost))
            {
                if (cost != costs[key]) continue;
                if ((visits++ & 1023) == 0) token.ThrowIfCancellationRequested();
                if (network[key])
                {
                    var route = new List<(int, int)>();
                    while (key >= 0) { route.Add((key % columns * 2, key / columns * 2)); key = previous[key]; }
                    return route;
                }
                int x = key % columns, y = key / columns;
                for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0 || x + dx < 0 || y + dy < 0 || x + dx >= columns || y + dy >= rows) continue;
                    int next = key + dy * columns + dx;
                    if (!navigable[next] || buildings.Contains(next) || !navigable[key + dx] || !navigable[key + dy * columns] ||
                        buildings.Contains(key + dx) || buildings.Contains(key + dy * columns)) continue;
                    float step = groundMasks[next] == 0 ? 1 : 1.7f;
                    float nextCost = cost + step * (dx != 0 && dy != 0 ? 1.4142f : 1);
                    if (nextCost >= costs[next]) continue;
                    costs[next] = nextCost; previous[next] = key; queue.Enqueue(next, nextCost);
                }
            }
            return null;
        }
    }

    internal static TownPlan MatchDecorations(MapDocument map, TownPlan plan, int x, int y, ProvinceSceneryGround ground)
    {
        var buildings = plan.Buildings.Where(b => b.Piece.Role is not ("prop" or "greenery") ||
            ground.CanPlace(map, b.Piece.Cells, ProvinceSceneryPalette.GroundSetId(b.Piece.Id), x + b.X, y + b.Y)).ToList();
        if (buildings.Count == plan.Buildings.Count) return plan;
        if (plan.Options.Ground)
            throw new InvalidDataException("Province decoration filtering requires a town without authored ground.");

        // Recompose from retained pieces so omitted props leave neither sprites
        // nor collision marks behind. Preserve the complete town placement guard.
        var cells = plan.Cells.ToDictionary(c => (c.X, c.Y), c => new StampCell(c.X, c.Y, new Cell()));
        foreach (var building in buildings)
        foreach (var source in building.Piece.Cells)
        {
            var cell = cells[(building.X + source.X + plan.Offset, building.Y + source.Y + plan.Offset)].Cell;
            cell.BackImage |= source.Cell.BackImage;
            if (source.Cell.Foreground is { } front) cell.Foreground = front;
            if (source.Cell.Door != 0) { cell.Door = (byte)(source.Cell.Door & 0x7f); cell.DoorOffset = source.Cell.DoorOffset; }
            if (source.Cell.Light != 0) cell.Light = source.Cell.Light;
        }
        foreach (var wall in plan.Walls)
        foreach (var source in wall.Cells)
        {
            var cell = cells[(source.X + plan.Offset, source.Y + plan.Offset)].Cell;
            cell.BackImage |= source.Cell.BackImage & 0x60000000;
            if (source.Cell.Foreground is { } front) cell.Foreground = front;
        }
        return plan with { Buildings = buildings, Cells = cells.Values.ToList() };
    }

    private static void BlendStreets(MapDocument map, TerrainSet basis, TerrainSet patch,
        Dictionary<(int, int), int> masks, bool[] hard, HashSet<(int X, int Y)> streets, int seed, CancellationToken token, int shoulder)
    {
        int columns = (map.Width + 1) / 2, rows = (map.Height + 1) / 2, stride = columns + 1;
        var cover = new bool[stride * (rows + 1)];
        var locked = new bool[cover.Length];
        Span<int> corners = stackalloc int[4];
        for (int y = 0; y < rows; y++)
        {
            token.ThrowIfCancellationRequested();
            for (int x = 0; x < columns; x++)
            {
                var cell = map.Get(x * 2, y * 2);
                bool known = masks.TryGetValue((cell.BackLibrary, cell.BackImage & 0x1fffffff), out int mask);
                int first = y * stride + x;
                corners[0] = first; corners[1] = first + 1; corners[2] = first + stride; corners[3] = first + stride + 1;
                for (int k = 0; k < 4; k++)
                {
                    if (known && (mask & (1 << k)) != 0) cover[corners[k]] = true;
                    if (!known || hard[y * 2 * map.Width + x * 2]) locked[corners[k]] = true;
                }
            }
        }
        var changed = new HashSet<int>();
        var queue = new Queue<int>();
        // On pale desert sand use the reviewed coarse-sand overlay for worn
        // tracks. Woodland and badlands expose their dirt base through cover.
        bool roadCover = basis.Id == "0:0:2300";
        void Paint(int vx, int vy)
        {
            if (vx < 0 || vy < 0 || vx > columns || vy > rows) return;
            int key = vy * stride + vx;
            if (cover[key] == roadCover || locked[key]) return;
            cover[key] = roadCover;
            for (int y = Math.Max(0, vy - 1); y <= Math.Min(rows - 1, vy); y++)
            for (int x = Math.Max(0, vx - 1); x <= Math.Min(columns - 1, vx); x++)
            { int tile = y * columns + x; changed.Add(tile); queue.Enqueue(tile); }
        }
        // Village lanes already have a walkable width in the town plan. Avoid
        // widening every garden spur into a square that clears the whole yard.
        foreach (var p in streets)
            for (int dy = -shoulder; dy <= shoulder; dy++) for (int dx = -shoulder; dx <= shoulder; dx++) Paint(p.X / 2 + dx, p.Y / 2 + dy);
        int Mask(int x, int y)
        {
            int first = y * stride + x;
            return (cover[first] ? 1 : 0) | (cover[first + 1] ? 2 : 0) |
                (cover[first + stride] ? 4 : 0) | (cover[first + stride + 1] ? 8 : 0);
        }
        while (queue.TryDequeue(out int key))
        {
            token.ThrowIfCancellationRequested();
            int x = key % columns, y = key / columns;
            if (Mask(x, y) is not (6 or 9)) continue;
            Paint(x, y); Paint(x + 1, y); Paint(x, y + 1); Paint(x + 1, y + 1);
        }
        var random = new Random(seed ^ 0x57EE7);
        foreach (int key in changed.Order())
        {
            int x = key % columns, y = key / columns, mask = Mask(x, y);
            var set = mask == 0 ? basis : patch;
            if (!set.Shapes.TryGetValue(mask == 0 ? 15 : mask, out var variants)) continue;
            var cell = map.Get(x * 2, y * 2).Copy();
            cell.BackLibrary = (short)set.Library;
            cell.BackImage = (cell.BackImage & unchecked((int)0xE0000000)) | (variants[random.Next(variants.Count)].Index + 1);
            map.Set(x * 2, y * 2, cell);
        }
    }
}
