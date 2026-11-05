using System.IO;

namespace CrystalStudio;

public enum TownDecorations { None, Sparse, Moderate, Detailed }
// Retain retired IDs so old settings cannot silently select a different surface.
public enum TownPathStyle { Native, StoneTiles, DarkTiles, Mud, StonyDirt, CrackedDirt, Sand }
public sealed record TownGroundChoice(TownPathStyle Style, string Name, TerrainSet Base, TerrainSet Path)
{
    public override string ToString() => Name;
}

public sealed record TownOptions(int Size = 96, int Seed = 1, int Spacing = 3, int Density = 65, bool Ground = true, bool Greenery = true,
    TownLayout Layout = TownLayout.Automatic, TownPathStyle Paths = TownPathStyle.Native, TownDecorations Decorations = TownDecorations.Sparse, int WallChance = 40);
public sealed record TownBuilding(TownPiece Piece, int X, int Y, TownBounds Lot, int AccessX, int AccessY);
public sealed record TownPlan(TownFamily Family, TownOptions Options, List<StampCell> Cells, List<TownBuilding> Buildings, HashSet<(int X, int Y)> Roads)
{
    public IReadOnlyList<TownWallRun> Walls { get; init; } = [];
    public string LayoutName { get; init; } = "Planned town";
    public string GroundName { get; init; } = "Existing ground";
    public string Name => $"Town Stamp · {Family.Name} · {Options.Size}×{Options.Size} · Seed {Options.Seed}";
    public int Offset => -(Options.Size / 2 & ~1);
}

public static class TownGenerator
{
    public static TownPlan Generate(TownFamily family, TownOptions options, TerrainCatalogue? terrain = null, CancellationToken cancellationToken = default, TownWallResources? wallResources = null)
    {
        if (options.Size is < 64 or > 192 || options.Size % 2 != 0 || options.Spacing is < 2 or > 8 || options.Density is < 25 or > 90 || !Enum.IsDefined(options.Layout) || !Enum.IsDefined(options.Paths) || !Enum.IsDefined(options.Decorations) || options.WallChance is < 0 or > 100)
            throw new ArgumentException("Town size must be even and between 64 and 192 cells; spacing 2–8 and density 25–90%.");
        if (options.Ground)
        {
            if (terrain == null) throw new ArgumentException("Town ground needs the reviewed terrain catalogue.");
            _ = GroundChoice(family, terrain, options.Paths);
        }
        int size = options.Size;
        var random = new Random(options.Seed);
        var layout = TownStreetLayout.Create(size, random, options.Layout);
        var roads = new HashSet<(int X, int Y)>(layout.Roads);
        var occupied = new HashSet<(int X, int Y)>();
        var reserved = new HashSet<(int X, int Y)>();
        var buildings = new List<TownBuilding>();
        var enclosure = TownWalls.Plan(wallResources, options, cancellationToken);
        var wallBlocks = new HashSet<(int X, int Y)>();
        if (enclosure != null)
        {
            if (!TownWalls.Streets(enclosure, roads, size)) { enclosure = null; roads = new(layout.Roads); }
            else
            {
                wallBlocks = TownWalls.RoadBlocks(enclosure);
                occupied.UnionWith(wallBlocks);
                foreach (var p in enclosure.Footprint)
                    for (int dy = -options.Spacing; dy <= options.Spacing; dy++) for (int dx = -options.Spacing; dx <= options.Spacing; dx++) reserved.Add((p.X + dx, p.Y + dy));
                layout.Roads.Clear(); layout.Roads.UnionWith(roads);
                layout.Frontages.RemoveAll(f => !roads.Contains((f.X, f.Y)));
                foreach (var p in roads.Where(p => p.X % 2 == 0 && p.Y % 2 == 0))
                    layout.Frontages.Add(new(p.X, p.Y, roads.Contains((p.X + 1, p.Y))));
            }
        }
        void Disc(int x, int y, int radius)
        {
            for (int dy = -radius; dy <= radius; dy++) for (int dx = -radius; dx <= radius; dx++)
                if (dx * dx + dy * dy <= radius * radius && x + dx >= 4 && y + dy >= 4 && x + dx < size - 4 && y + dy < size - 4 && !occupied.Contains((x + dx, y + dy)))
                    roads.Add((x + dx, y + dy));
        }
        bool InBounds(TownBounds b) => b.X >= 4 && b.Y >= 4 && b.X + b.Width < size - 4 && b.Y + b.Height < size - 4;
        List<(int X, int Y)>? Connect(int sx, int sy, HashSet<(int X, int Y)> obstacles)
        {
            var queue = new Queue<(int X, int Y)>();
            var previous = new Dictionary<(int X, int Y), (int X, int Y)>();
            var first = (X: sx, Y: sy); queue.Enqueue(first); previous[first] = first;
            // Stable shuffled neighbour priority makes shortest alleys vary by seed.
            var neighbours = new[] { (1, 0), (-1, 0), (0, 1), (0, -1) }.OrderBy(_ => random.Next()).ToArray();
            while (queue.TryDequeue(out var point))
            {
                if (layout.Roads.Contains(point))
                {
                    var result = new List<(int X, int Y)> { point };
                    while (point != first) { point = previous[point]; result.Add(point); }
                    return result;
                }
                foreach (var delta in neighbours)
                {
                    var next = (X: point.X + delta.Item1, Y: point.Y + delta.Item2);
                    if (Math.Abs(next.X - sx) + Math.Abs(next.Y - sy) > 8 || next.X < 4 || next.Y < 4 || next.X >= size - 4 || next.Y >= size - 4 || obstacles.Contains(next) || previous.ContainsKey(next)) continue;
                    previous[next] = point; queue.Enqueue(next);
                }
            }
            return null;
        }
        bool Place(TownPiece piece, int x, int y, bool connect)
        {
            var bounds = piece.Bounds with { X = piece.Bounds.X + x, Y = piece.Bounds.Y + y };
            var footprint = piece.Footprint.Select(p => (X: p.X + x, Y: p.Y + y)).ToHashSet();
            if (!InBounds(bounds) || footprint.Any(p => roads.Contains(p) || reserved.Contains(p) || enclosure?.Purpose == "Town perimeter" && !enclosure.Interior.Contains(p))) return false;
            if (enclosure?.Purpose == "Garden" && piece.Role is "house" or "market" or "landmark" && footprint.Any(enclosure.Interior.Contains) &&
                buildings.Any(b => b.Piece.Role is "house" or "market" or "landmark" && enclosure.Interior.Contains((b.X, b.Y)))) return false;
            int ax = -1, ay = -1;
            List<(int X, int Y)>? approach = null;
            List<(int X, int Y)>? route = null;
            if (connect)
            {
                int dx = piece.Direction == "SW" ? -1 : 1;
                var front = piece.Footprint.OrderByDescending(p => p.X * dx + p.Y).ThenByDescending(p => p.Y).First();
                ax = x + front.X + dx; ay = y + front.Y + 1;
                approach = Enumerable.Range(1, 4).Select(i => (X: x + front.X + dx * i, Y: y + front.Y + i)).ToList();
                if (approach.Any(p => wallBlocks.Contains(p) || p.X < 4 || p.Y < 4 || p.X >= size - 4 || p.Y >= size - 4 || occupied.Contains(p))) return false;
                var obstacles = new HashSet<(int X, int Y)>(occupied); obstacles.UnionWith(footprint);
                route = Connect(approach[^1].X, approach[^1].Y, obstacles);
                if (route == null) return false;
            }
            buildings.Add(new(piece, x, y, bounds, ax, ay));
            foreach (var point in footprint)
            {
                occupied.Add(point);
                for (int oy = -options.Spacing; oy <= options.Spacing; oy++) for (int ox = -options.Spacing; ox <= options.Spacing; ox++) reserved.Add((point.X + ox, point.Y + oy));
            }
            if (route != null && approach != null)
            {
                // Supercover the diagonal approach so four-way walking remains connected.
                for (int i = 0; i < approach.Count - 1; i++) Disc(approach[i].X, approach[i].Y, 1);
                foreach (var point in route) Disc(point.X, point.Y, 1);
            }
            return true;
        }
        // Align silhouette edges to each street's setback. Native facings are
        // retained; only the short entrance approach bends towards the road.
        (int X, int Y) FacingSite(TownPiece piece, TownFrontage street)
        {
            int dx = piece.Direction == "SW" ? -1 : 1;
            var front = piece.Footprint.OrderByDescending(p => p.X * dx + p.Y).ThenByDescending(p => p.Y).First();
            int setback = street.Setback;
            if (street.Horizontal)
                return (street.X - front.X - dx * setback, street.Y - piece.Footprint.Max(p => p.Y) - setback);
            int edge = dx == 1 ? piece.Footprint.Max(p => p.X) : piece.Footprint.Min(p => p.X);
            return (street.X - edge - dx * setback, street.Y - front.Y - setback);
        }
        // A stable sweep fills neighbours along each street instead of choosing
        // unrelated positions. Seeded artwork ordering gives each town variety.
        var sites = layout.Frontages.OrderBy(f => enclosure != null && enclosure.Interior.Contains((f.X, f.Y)) ? 0 : 1).ThenBy(f => f.Y).ThenBy(f => f.X).ToArray();
        bool Fit(TownPiece piece, bool central = false)
        {
            var candidates = central ? sites.OrderBy(f => layout.Centres.Min(c => Math.Abs(f.X - c.X) + Math.Abs(f.Y - c.Y))) : sites.AsEnumerable();
            foreach (var street in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (central && layout.Centres.All(c => Math.Abs(street.X - c.X) + Math.Abs(street.Y - c.Y) > size / 3)) continue;
                var site = FacingSite(piece, street);
                if (Place(piece, site.X, site.Y, true)) return true;
            }
            return false;
        }
        foreach (var piece in family.Pieces.Where(p => p.Role == "landmark" && size >= 96).OrderBy(_ => random.Next()).Take(1)) Fit(piece, true);
        void FitMarkets()
        {
            foreach (var piece in family.Pieces.Where(p => p.Role == "market").OrderBy(_ => random.Next()).Take(size < 90 ? 1 : 2)) Fit(piece, true);
        }
        // On a small footprint, reserve homes before optional market stalls
        // consume the few usable lots beside a bend or junction.
        if (size >= 90) FitMarkets();
        var houses = family.Pieces.Where(p => p.Role == "house").ToArray();
        int target = Math.Max(4, (int)(size * size / 650.0 * options.Density / 65.0));
        for (int count = 0; count < target; count++)
        {
            bool fitted = false;
            foreach (var piece in houses.OrderBy(p => size < 90 && options.Spacing >= 6 ? p.Bounds.Width * p.Bounds.Height : 0).ThenBy(_ => random.Next()))
                if (Fit(piece)) { fitted = true; break; }
            if (!fitted) break;
        }
        if (enclosure != null && (buildings.Count(p => p.Piece.Role == "house") < 3 ||
            !buildings.Any(b => b.Piece.Role == "house" && b.Piece.Footprint.All(p => enclosure.Interior.Contains((b.X + p.X, b.Y + p.Y))))))
            return Generate(family, options, terrain, cancellationToken, null);
        if (buildings.Count(p => p.Piece.Role == "house") < 3) throw new InvalidDataException($"Too few houses fit {layout.Name} ({buildings.Count} buildings). Reduce spacing or increase town size.");
        if (size < 90) FitMarkets();
        var walls = enclosure == null ? new List<TownWallRun>() : [enclosure];
        if (options.Decorations == TownDecorations.Detailed)
            TownSceneDetails.Place(family, buildings, roads, layout.Centres, options, Place, cancellationToken);
        else if (options.Decorations != TownDecorations.None)
        {
            var homes = buildings.Where(b => b.Piece.Role is "house" or "market" or "landmark").ToArray();
            var decorRandom = GroundRandom(unchecked(options.Seed + 137));
            var decorations = family.Pieces.Where(p => p.Role == "prop" || options.Greenery && p.Role == "greenery").ToArray();
            int houseCount = homes.Count(b => b.Piece.Role == "house");
            int propTarget = Math.Max(1, houseCount / (options.Decorations == TownDecorations.Sparse ? 4 : 2));
            int gardenTarget = options.Greenery ? Math.Max(1, houseCount / (options.Decorations == TownDecorations.Sparse ? 3 : 2)) : 0;
            int props = 0, gardens = 0;
            for (int attempt = 0; decorations.Length > 0 && attempt < homes.Length * 160 && (props < propTarget || gardens < gardenTarget); attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var piece = decorations[decorRandom.Next(decorations.Length)];
                if (piece.Role == "prop" ? props >= propTarget : gardens >= gardenTarget) continue;
                string use = DecorationUse(piece);
                // A single notice board or well serves the settlement. Benches
                // belong to gathering places; storage and work props to a yard.
                int limit = use is "notice" or "well" ? 1 : use == "bench" ? 2 : int.MaxValue;
                if (buildings.Count(b => DecorationUse(b.Piece) == use && b.Piece.Role == piece.Role) >= limit) continue;
                bool publicUse = use is "notice" or "well" or "bench";
                var hosts = publicUse ? homes.Where(b => b.Piece.Role is "market" or "landmark").ToArray() : homes;
                if (hosts.Length == 0) hosts = homes;
                var host = hosts[decorRandom.Next(hosts.Length)];
                int gap = options.Spacing + 1;
                int x = host.Lot.X + decorRandom.Next(-gap - piece.Bounds.Width, host.Lot.Width + gap + 1) - piece.Bounds.X;
                int y = host.Lot.Y + decorRandom.Next(-gap - piece.Bounds.Height, host.Lot.Height + gap + 1) - piece.Bounds.Y;
                var footprint = piece.Footprint.Select(p => (X: p.X + x, Y: p.Y + y)).ToArray();
                var hostFootprint = host.Piece.Footprint.Select(p => (X: p.X + host.X, Y: p.Y + host.Y)).ToArray();
                if (!footprint.Any(p => hostFootprint.Any(h => Math.Max(Math.Abs(p.X - h.X), Math.Abs(p.Y - h.Y)) <= gap + 2))) continue;
                if (publicUse)
                {
                    bool nearEntrance = footprint.Any(p => Math.Abs(p.X - host.AccessX) + Math.Abs(p.Y - host.AccessY) <= 10);
                    bool nearSquare = footprint.Any(p => layout.Centres.Any(c => Math.Abs(p.X - c.X) + Math.Abs(p.Y - c.Y) <= 10));
                    bool besideStreet = footprint.Any(p => roads.Any(r => Math.Abs(p.X - r.X) + Math.Abs(p.Y - r.Y) <= 3));
                    if ((!nearEntrance && !nearSquare) || !besideStreet) continue;
                }
                if (Place(piece, x, y, false)) { if (piece.Role == "prop") props++; else gardens++; }
            }
        }
        if (enclosure != null)
        {
            var enclosedHomes = buildings.Where(b => b.Piece.Role is "house" or "market" or "landmark" &&
                b.Piece.Footprint.All(p => enclosure.Interior.Contains((b.X + p.X, b.Y + p.Y)))).ToArray();
            if (enclosure.Purpose == "Garden" || enclosure.Purpose == "Neighbourhood" && enclosedHomes.Length == 1)
            {
                enclosure = enclosure with { Purpose = "Garden" }; walls[0] = enclosure;
                if (options.Greenery && options.Decorations != TownDecorations.None)
                {
                    int planted = 0;
                    foreach (var point in enclosure.Interior.OrderBy(p => p.Y).ThenBy(p => p.X))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (planted >= 2) break;
                        foreach (var plant in family.Pieces.Where(p => p.Role == "greenery").OrderByDescending(p => Math.Min(80, p.Bounds.Width * p.Bounds.Height)))
                        {
                            if (!plant.Footprint.All(p => enclosure.Interior.Contains((point.X + p.X, point.Y + p.Y)))) continue;
                            if (!enclosedHomes.Any(h => h.Piece.Footprint.Any(p => plant.Footprint.Any(q =>
                                Math.Max(Math.Abs(h.X + p.X - point.X - q.X), Math.Abs(h.Y + p.Y - point.Y - q.Y)) <= options.Spacing + 3)))) continue;
                            if (Place(plant, point.X, point.Y, false)) { planted++; break; }
                        }
                    }
                }
            }
        }
        var cells = new Dictionary<(int, int), Cell>();
        // Guard the whole area, including empty gardens and streets. Placement
        // is atomic and cannot silently clip a town at the map edge.
        for (int y = 0; y < size; y++) for (int x = 0; x < size; x++) cells[(x, y)] = new();
        string groundName = "Existing ground";
        if (options.Ground)
        {
            if (terrain == null) throw new ArgumentException("Town ground needs the reviewed terrain catalogue.");
            // Surface choices and vegetation never reroll the street plan.
            groundName = AddGround(cells, roads, size, family, terrain, options.Paths, GroundRandom(options.Seed));
        }
        foreach (var b in buildings) foreach (var source in b.Piece.Cells)
        {
            var c = cells[(b.X + source.X, b.Y + source.Y)];
            c.BackImage |= source.Cell.BackImage; if (source.Cell.Foreground is { } front) c.Foreground = front;
            if (source.Cell.Door != 0) { c.Door = source.Cell.Door; c.DoorOffset = source.Cell.DoorOffset; }
            if (source.Cell.Light != 0) c.Light = source.Cell.Light;
        }
        foreach (var wall in walls) foreach (var source in wall.Cells)
        {
            var cell = cells[(source.X, source.Y)];
            cell.BackImage |= source.Cell.BackImage & 0x60000000;
            if (source.Cell.Foreground is { } front) cell.Foreground = front;
        }
        int offset = -(size / 2 & ~1);
        return new(family, options, cells.Select(p => new StampCell(p.Key.Item1 + offset, p.Key.Item2 + offset, p.Value)).ToList(), buildings, roads) { LayoutName = layout.Name, GroundName = groundName, Walls = walls };
    }

    private static Random GroundRandom(int seed)
    {
        // Keep surface variants and decoration choices independent of the
        // number of placement attempts used to fit the buildings.
        uint value = unchecked((uint)seed + 0x9e3779b9U);
        value = unchecked((value ^ (value >> 16)) * 0x85ebca6bU);
        value = unchecked((value ^ (value >> 13)) * 0xc2b2ae35U);
        return new Random(unchecked((int)(value ^ (value >> 16))));
    }

    internal static string DecorationUse(TownPiece piece) => piece.Name.Split(' ')[0].ToLowerInvariant() switch
    {
        "notice" => "notice", "bench" => "bench", "well" => "well", _ => "yard"
    };

    public static IReadOnlyList<TownGroundChoice> GroundChoices(TownFamily family, TerrainCatalogue catalogue)
    {
        (TownPathStyle Style, string Name, string Base, string Path)[] recipes =
        [
            (TownPathStyle.Native, family.Id == "mongchon" ? "Sand on coarse sand (native)" : "Dirt on rough grass (native)", family.BaseTerrainId, family.PathTerrainId),
            (TownPathStyle.Mud, "Mud on fine grass", "0:0:1450", "0:0:1000"),
            (TownPathStyle.StonyDirt, "Stony dirt on dirt", "0:0:0", "0:0:2050"),
            (TownPathStyle.CrackedDirt, "Cracked dirt on dirt", "0:0:0", "0:0:50"),
            (TownPathStyle.Sand, "Sand on coarse sand", "0:0:2350", "0:0:2300")
        ];
        var result = new List<TownGroundChoice>();
        foreach (var recipe in recipes.DistinctBy(r => (r.Base, r.Path)))
        {
            var basis = catalogue.Sets.SingleOrDefault(s => s.Id == recipe.Base);
            var path = catalogue.Sets.SingleOrDefault(s => s.Id == recipe.Path);
            if (basis is { Base: true } && path != null && catalogue.CanBlend(basis, path) &&
                Enumerable.Range(1, 15).Where(mask => mask is not (6 or 9)).All(path.Shapes.ContainsKey))
                result.Add(new(recipe.Style, recipe.Name, basis, path));
        }
        return result;
    }

    public static TownGroundChoice GroundChoice(TownFamily family, TerrainCatalogue catalogue, TownPathStyle style) =>
        GroundChoices(family, catalogue).FirstOrDefault(c => c.Style == style)
        ?? throw new ArgumentException("Choose a compatible ground and path pair. This surface has no reviewed transition tiles for town ground.");

    private static string AddGround(Dictionary<(int, int), Cell> cells, HashSet<(int X, int Y)> roads, int size,
        TownFamily family, TerrainCatalogue catalogue, TownPathStyle style, Random random)
    {
        var choice = GroundChoice(family, catalogue, style);
        var basis = choice.Base;
        var path = choice.Path;
        int n = size / 2;
        var vertices = new bool[n + 1, n + 1];
        // Keep ground tied to the planned streets and squares. Large random
        // clearings would erase the street pattern underneath the buildings.
        for (int y = 2; y < n - 1; y++) for (int x = 2; x < n - 1; x++)
            vertices[x, y] = roads.Contains((x * 2, y * 2));
        int Mask(int x, int y) => (vertices[x, y] ? 1 : 0) | (vertices[x + 1, y] ? 2 : 0) | (vertices[x, y + 1] ? 4 : 0) | (vertices[x + 1, y + 1] ? 8 : 0);
        // Resolve diagonal islands monotonically while retaining shared vertices.
        bool changed;
        do
        {
            changed = false;
            for (int y = 0; y < n; y++) for (int x = 0; x < n; x++)
                if (Mask(x, y) is 6 or 9) { vertices[x, y] = vertices[x + 1, y] = vertices[x, y + 1] = vertices[x + 1, y + 1] = true; changed = true; }
        } while (changed);
        for (int y = 0; y < n; y++) for (int x = 0; x < n; x++)
        {
            int mask = Mask(x, y);
            var set = mask == 0 ? basis : path;
            var pool = set.Shapes[mask == 0 ? 15 : mask];
            long roll = random.NextInt64(pool.Sum(p => p.Weight));
            var variant = pool[^1]; foreach (var candidate in pool) { roll -= candidate.Weight; if (roll < 0) { variant = candidate; break; } }
            var c = cells[(x * 2, y * 2)]; c.BackLibrary = (short)set.Library; c.BackImage = variant.Index + 1;
        }
        return choice.Name;
    }
}
