using System.IO;
namespace CrystalStudio;

public sealed record TownWallRun(string FamilyId, string Name, IReadOnlyList<StampCell> Cells, IReadOnlyList<TownPoint> Footprint)
{
    public string Purpose { get; init; } = "Neighbourhood";
    public IReadOnlySet<(int X, int Y)> Interior { get; init; } = new HashSet<(int, int)>();
    public IReadOnlyList<(int X, int Y)> Gateway { get; init; } = [];
    public string GateId { get; init; } = "";
}

public sealed class TownWallResources
{
    internal List<WallFamily> Families { get; } = [];
    internal Dictionary<string, List<StampCell>> Gates { get; } = [];
    internal Dictionary<(int, int, byte), IReadOnlyList<TownPoint>> Footprints { get; } = [];
    internal Dictionary<(string, int, int), TownWallRun?> Templates { get; } = [];
    public IReadOnlyList<string> AvailableFamilies => Families.Select(f => f.Id).ToArray();
    public static bool IsTownWall(WallFamily family) => !family.HasFacingModes && !family.PaintsAreas &&
        family.Id is "bichon-wall" or "small-fence" or "large-fence" or "brick-wall";

    public static TownWallResources Load(AssetStore assets, Dictionary<(int Library, int Image), Sprite> sprites, CancellationToken cancellation = default)
    {
        var families = WallCatalogue.DefinitionPaths(WallCatalogue.ResolvePath())
            .Where(path => Path.GetFileName(Path.GetDirectoryName(path)) is "bichon-wall" or "small-fence" or "large-fence" or "brick-wall")
            .SelectMany(path => WallCatalogue.Read(path, assets.Format).Families);
        return Prepare(families, assets, sprites, cancellation);
    }

    public static TownWallResources Prepare(IEnumerable<WallFamily> families, AssetStore assets,
        Dictionary<(int Library, int Image), Sprite> sprites, CancellationToken cancellation = default)
    {
        var result = new TownWallResources();
        var footprints = result.Footprints;
        Sprite? ReadSprite(Front front)
        {
            cancellation.ThrowIfCancellationRequested();
            var key = ((int)front.Library, front.Image & 0x7fff);
            if (sprites.TryGetValue(key, out var cached)) return cached;
            if (!assets.Libraries.TryGetValue(front.Library, out var library)) return null;
            var sprite = library.Decode(key.Item2 - 1);
            if (sprite != null) sprites[key] = sprite;
            return sprite;
        }
        foreach (var source in families.Where(IsTownWall))
        {
            cancellation.ThrowIfCancellationRequested();
            var family = source.WithMode(false);
            if (family.Availability(assets) != null) continue;
            void PrepareFront(Front front)
            {
                cancellation.ThrowIfCancellationRequested();
                int index = front.Image & 0x7fff;
                var key = ((int)front.Library, index, front.Frames);
                if (footprints.ContainsKey(key)) return;
                var sprite = ReadSprite(front) ?? throw new InvalidDataException("Missing town wall artwork.");
                var bounds = MapSpriteDrawing.Bounds(sprite, 0, 0, front.Library, index - 1, true, true, MapSpriteDrawing.FrontBlend(front.Frames));
                var bitmap = sprite.Bitmap.Format == RasterFormats.Bgra32 || sprite.Bitmap.Format == RasterFormats.Pbgra32
                    ? sprite.Bitmap : new ConvertedRasterImage(sprite.Bitmap, RasterFormats.Bgra32, null, 0);
                byte[] pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
                bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
                var occupied = new HashSet<TownPoint>();
                for (int y = 0; y < bitmap.PixelHeight; y++) for (int x = 0; x < bitmap.PixelWidth; x++)
                    if (pixels[(y * bitmap.PixelWidth + x) * 4 + 3] != 0)
                        occupied.Add(new((int)Math.Floor((bounds.X + x) / 48), (int)Math.Floor((bounds.Y + y) / 32)));
                footprints[key] = occupied.ToArray();
            }
            var fronts = family.Pieces.SelectMany(p => p.Cells).SelectMany(c => c.Cell.ForegroundImages).Distinct().ToArray();
            if (fronts.Any(f => ReadSprite(f) == null)) continue;
            foreach (var front in fronts) PrepareFront(front);

            int gate = family.Id == "bichon-wall" ? 7362 : family.Id == "brick-wall" ? 52 : 0;
            if (gate != 0)
            {
                var cells = CellCodec.LoadStamp(DataPaths.ResolveEditor("Objects", "mir", $"mir_{gate:00000}.X"), assets.Format);
                if (cells.SelectMany(c => c.Cell.ForegroundImages).Any(f => ReadSprite(f) == null)) continue;
                result.Gates[family.Id] = cells;
                foreach (var front in cells.SelectMany(c => c.Cell.ForegroundImages).Distinct())
                    PrepareFront(front);
            }
            result.Families.Add(family);
        }
        return result;
    }
}


internal static class TownWalls
{
    private static readonly (int X, int Y)[] Steps = [(1, 0), (0, 1), (-1, 0), (0, -1)];

    internal static TownWallRun? Plan(TownWallResources? resources, TownOptions options, CancellationToken cancellation)
    {
        var random = new Random(unchecked(options.Seed ^ 0x5a712e39));
        if (resources == null || random.Next(100) >= options.WallChance) return null;
        // A complete boundary is chosen before lots are filled. Fortifications
        // belong to large settlements; gardens and small villages use low walls.
        int purpose = random.Next(100);
        bool perimeter = purpose < 45;
        bool garden = purpose >= 75 && options.Greenery && options.Decorations != TownDecorations.None;
        var families = resources.Families.Where(f => (f.Id != "bichon-wall" || perimeter && options.Size >= 144) && (!garden || f.Kind == "fence"))
            .OrderBy(f => perimeter && options.Size >= 144 && f.Id == "bichon-wall" ? 0 : 1).ThenBy(_ => random.Next()).ToArray();
        foreach (var family in families)
        {
            cancellation.ThrowIfCancellationRequested();
            int n, m;
            if (family.Id == "bichon-wall") { n = 3; m = options.Size >= 192 ? 3 : 2; }
            else
            {
                int span = perimeter ? options.Size - 14 : Math.Min(options.Size - 24, garden ? 44 : Math.Max(36, options.Size / 2));
                n = Math.Max(3, span / (2 * family.Step)); m = n;
                if (family.Id == "brick-wall") n = Math.Max(3, n);
            }
            var template = Template(resources, family, n, m);
            if (template == null) continue;
            int minX = template.Footprint.Min(p => p.X), maxX = template.Footprint.Max(p => p.X);
            int minY = template.Footprint.Min(p => p.Y), maxY = template.Footprint.Max(p => p.Y);
            int margin = family.Id == "bichon-wall" && options.Size == 144 ? 0 : 5;
            if (maxX - minX >= options.Size - 2 * margin || maxY - minY >= options.Size - 2 * margin) continue;
            int x = (options.Size - maxX - minX) / 2;
            int y = (options.Size - maxY - minY) / 2;
            if (!perimeter)
            {
                x = Math.Clamp(x + (random.Next(2) == 0 ? -1 : 1) * options.Size / 8, 5 - minX, options.Size - 6 - maxX);
                y = Math.Clamp(y + random.Next(-options.Size / 10, options.Size / 10 + 1), 5 - minY, options.Size - 6 - maxY);
            }
            return template with {
                Purpose = perimeter ? "Town perimeter" : garden ? "Garden" : "Neighbourhood",
                Cells = template.Cells.Select(c => c with { X = c.X + x, Y = c.Y + y }).ToArray(),
                Footprint = template.Footprint.Select(p => new TownPoint(p.X + x, p.Y + y)).ToArray(),
                Interior = template.Interior.Select(p => (p.X + x, p.Y + y)).ToHashSet(),
                Gateway = template.Gateway.Select(p => (X: p.X + x, Y: p.Y + y)).Where(p => p.X >= 4 && p.Y >= 4 && p.X < options.Size - 4 && p.Y < options.Size - 4).ToArray()
            };
        }
        return null;
    }

    private static TownWallRun? Template(TownWallResources resources, WallFamily family, int n, int m)
    {
        var key = (family.Id, n, m);
        if (resources.Templates.TryGetValue(key, out var cached)) return cached;
        var origin = family.Snap(384, 384);
        var boundary = new List<(int U, int V)>();
        for (int u = 0; u <= n; u++) { boundary.Add((u, 0)); boundary.Add((u, m)); }
        for (int v = 1; v < m; v++) { boundary.Add((0, v)); boundary.Add((n, v)); }
        var nodes = boundary.Select(p => family.GridPoint(p.U, p.V)).Select(p => (p.X + origin.X, p.Y + origin.Y));
        var brush = WallBrush.PlanStroke(new MapDocument(1024, 1024), family, nodes, variationSeed: 1, clipToMap: false);
        if (!brush.Allowed) return resources.Templates[key] = null;
        var cells = brush.Cells.Where(c => c.Value.ForegroundImages.Count > 0 || (c.Value.BackImage & 0x60000000) != 0)
            .ToDictionary(c => (X: c.Key.X - origin.X, Y: c.Key.Y - origin.Y), c => c.Value);
        // The gate replaces a complete native run on the SW side (v=0).
        // Bichon gate 7362 joins wall 7364 at (-19,-22) and (18,15)
        // in 0.map: its straight-wall phase is exactly three cells above its anchor.
        int gx = n * family.Step / 2;
        int straight = family.Id switch { "bichon-wall" => 7364, "brick-wall" => 27, "large-fence" => 1246, _ => 1239 };
        var source = family.Pieces.Single(p => p.Id == $"mir/mir_{straight:00000}");
        var phases = from c in cells where Math.Abs(c.Key.X - gx) <= family.Step && Math.Abs(c.Key.Y - c.Key.X) < 12
                     from f in c.Value.ForegroundImages from s in source.Cells where s.Cell.ForegroundImages.Contains(f)
                     select c.Key.Y - c.Key.X - (s.Y - s.X);
        if (!phases.Any()) return resources.Templates[key] = null;
        int phase = phases.GroupBy(p => p).OrderByDescending(g => g.Count()).First().Key;
        var gate = new List<StampCell>();
        int left, right;
        string gateId;
        if (resources.Gates.TryGetValue(family.Id, out var native))
        {
            int gy = gx + phase + (family.Id == "bichon-wall" ? 3 : 1);
            gate.AddRange(native.Select(c => c with { X = c.X + gx, Y = c.Y + gy }));
            left = gate.Min(c => c.X); right = gate.Max(c => c.X);
            gateId = family.Id == "bichon-wall" ? "mir/mir_07362" : "mir/mir_00052";
        }
        else
        {
            left = gx - 3; right = gx + 3;
            int end = family.Id == "small-fence" ? 1234 : 1247;
            int start = family.Id == "small-fence" ? 1233 : 1248;
            foreach (var (id, x, y) in new[] {
                (end, left - 1, left - 1 + phase - (family.Id == "large-fence" ? 2 : 0)),
                (start, right, right + phase + (family.Id == "large-fence" ? 1 : 0)) })
                gate.AddRange(family.Pieces.Single(p => p.Id == $"mir/mir_{id:00000}").Cells.Select(c => c with { X = c.X + x, Y = c.Y + y }));
            gateId = $"mir/mir_{end:00000}+mir/mir_{start:00000}";
        }
        // Keep the opposite side intact when its X coordinates overlap the gate.
        foreach (var p in cells.Keys.Where(p => p.X >= left && p.X <= right && Math.Abs(p.Y - p.X - phase) < family.Step).ToArray()) cells.Remove(p);
        foreach (var c in gate) cells[(c.X, c.Y)] = c.Cell;
        var footprint = new HashSet<(int X, int Y)>();
        var gateFootprint = new HashSet<(int X, int Y)>();
        void Cover(StampCell cell, HashSet<(int X, int Y)> coverage)
        {
            coverage.Add((cell.X, cell.Y));
            foreach (var f in cell.Cell.ForegroundImages)
                if (resources.Footprints.TryGetValue((f.Library, f.Image & 0x7fff, f.Frames), out var points))
                    foreach (var p in points) coverage.Add((cell.X + p.X, cell.Y + p.Y));
        }
        foreach (var c in cells) Cover(new(c.Key.X, c.Key.Y, c.Value), footprint);
        foreach (var c in gate) Cover(c, gateFootprint);
        var interior = new HashSet<(int X, int Y)>();
        for (int y = -m * family.Step; y <= n * family.Step; y++)
        for (int x = 0; x <= (n + m) * family.Step; x++)
        {
            int u = x + y, v = x - y;
            if (u > 0 && u < 2 * n * family.Step && v > 0 && v < 2 * m * family.Step) interior.Add((x, y));
        }
        var blocked = footprint.Except(gateFootprint).ToHashSet();
        blocked.UnionWith(cells.Where(c => (c.Value.BackImage & 0x60000000) != 0).Select(c => c.Key));
        int mid = (left + right) / 2;
        var startPoint = (X: mid + 12, Y: mid + phase - 12);
        var endPoint = (X: mid - 12, Y: mid + phase + 12);
        var route = Route(startPoint, p => p == endPoint, p => !blocked.Contains(p) &&
            p.X >= Math.Min(left - 6, endPoint.X - 1) && p.X <= Math.Max(right + 12, startPoint.X + 1) && p.Y >= mid + phase - 18 && p.Y <= mid + phase + 18);
        if (route == null) return resources.Templates[key] = null;
        return resources.Templates[key] = new(family.Id, family.Name, cells.Select(c => new StampCell(c.Key.X, c.Key.Y, c.Value)).ToArray(),
            footprint.Select(p => new TownPoint(p.X, p.Y)).ToArray()) { Interior = interior, Gateway = route, GateId = gateId };
    }

    internal static HashSet<(int X, int Y)> RoadBlocks(TownWallRun wall)
    {
        var result = wall.Footprint.Select(p => (p.X, p.Y)).ToHashSet();
        result.ExceptWith(wall.Gateway);
        return result;
    }

    internal static bool Streets(TownWallRun wall, HashSet<(int X, int Y)> roads, int size)
    {
        var blocks = RoadBlocks(wall);
        bool Allowed((int X, int Y) p) => p.X >= 4 && p.Y >= 4 && p.X < size - 4 && p.Y < size - 4 && !blocks.Contains(p);
        roads.RemoveWhere(p => blocks.Contains(p) || wall.Purpose == "Town perimeter" && !wall.Interior.Contains(p) ||
            wall.Purpose == "Garden" && wall.Interior.Contains(p));
        roads.UnionWith(wall.Gateway.Where(Allowed));
        // Connect every retained lane through the matching gateway, including a
        // road from a whole-town gate to the edge of the stamp.
        if (wall.Purpose == "Town perimeter")
        {
            var exit = Route(wall.Gateway[^1], p => p.X == 4 || p.Y == size - 5, Allowed);
            if (exit == null) return false;
            roads.UnionWith(exit);
        }
        if (roads.Count == 0) return false;
        var connected = Flood(roads.First(), roads.Contains);
        while (connected.Count < roads.Count)
        {
            var next = roads.Where(p => !connected.Contains(p)).OrderBy(p => p.Y).ThenBy(p => p.X).First();
            var route = Route(next, connected.Contains, Allowed);
            if (route == null) return false;
            roads.UnionWith(route); connected = Flood(roads.First(), roads.Contains);
        }
        return true;
    }

    internal static HashSet<(int X, int Y)> Flood((int X, int Y) start, Func<(int X, int Y), bool> allowed)
    {
        var visited = new HashSet<(int X, int Y)> { start }; var queue = new Queue<(int X, int Y)>(); queue.Enqueue(start);
        while (queue.TryDequeue(out var p)) foreach (var d in Steps)
        {
            var next = (p.X + d.X, p.Y + d.Y);
            if (allowed(next) && visited.Add(next)) queue.Enqueue(next);
        }
        return visited;
    }

    private static List<(int X, int Y)>? Route((int X, int Y) start, Func<(int X, int Y), bool> target, Func<(int X, int Y), bool> allowed)
    {
        var previous = new Dictionary<(int X, int Y), (int X, int Y)> { [start] = start };
        var queue = new Queue<(int X, int Y)>(); queue.Enqueue(start);
        while (queue.TryDequeue(out var p))
        {
            if (target(p))
            {
                var path = new List<(int X, int Y)> { p };
                while (p != start) { p = previous[p]; path.Add(p); }
                path.Reverse(); return path;
            }
            foreach (var d in Steps)
            {
                var next = (p.X + d.X, p.Y + d.Y);
                if (allowed(next) && previous.TryAdd(next, p)) queue.Enqueue(next);
            }
        }
        return null;
    }
}
