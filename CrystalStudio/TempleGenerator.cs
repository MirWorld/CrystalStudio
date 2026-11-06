using System.IO;

namespace CrystalStudio;

public sealed record TempleDefinition(string Name, string WallId, string FloorId, int FloorOffset = 4, int Margin = 32,
    int? FloorFrontOffset = null, int FloorRepairPadding = 0)
{
    public static IReadOnlyList<TempleDefinition> All { get; } =
    [
        new("Wooma Temple", "wooma-temple", "0:0:2000"),
        new("Zuma Temple", "zuma-temple-stone", "0:0:2400"),
        new("Stone Temple", "stone-temple", "0:0:2500"),
        new("Bug Cave", "bug-cave-dungeon", "0:0:2450"),
        new("Insect Cave", "bug-cave", "0:0:2150"),
        new("Mines", "mines", "0:0:2150"),
        new("Red Moon Valley", "black-dragon-dungeon", "0:0:2950", FloorOffset: 0, FloorFrontOffset: -2),
        new("Prajna Temple", "prajna-temple-masonry", "0:0:3100", FloorOffset: 0, FloorFrontOffset: -2),
        new("Oma Cave", "oma-cave", "0:0:1900", FloorOffset: 2),
        new("Prajna Stone Cave", "prajna-stone-cave", "0:0:3050", Margin: 48, FloorFrontOffset: -4, FloorRepairPadding: 2),
        new("Fox Cave", "fox-cave", "0:0:2950", FloorOffset: 0, FloorFrontOffset: -2, FloorRepairPadding: 2)
    ];
    public override string ToString() => Name;
}

public enum DungeonType { Temple, Cave }

public sealed record TempleOptions(TempleDefinition Temple, int Seed = 1, int Rooms = 12, int RoomSize = 5,
    int CorridorWidth = 2, DungeonType Type = DungeonType.Temple, int DoorCount = 0, DungeonTheme Theme = DungeonTheme.Classic);

public sealed record TemplePlan(MapDocument Source, long SourceRevision, MapDocument Generated,
    TempleOptions Options, int RoomCount, IReadOnlyList<(int X, int Y)> RoomCentres)
{
    public IReadOnlyList<PlacedStamp> Decorations { get; init; } = [];
    public IReadOnlyList<GeneratedEntrance> Entrances { get; init; } = [];
    public IReadOnlyList<GeneratedSideRoom> SideRooms { get; init; } = [];
}

public static class TempleGenerator
{
    private const int Blocked = 0x60000000;
    private static readonly (int U, int V)[] Directions = [(1, 0), (0, 1), (-1, 0), (0, -1)];

    public static TemplePlan Plan(MapDocument source, WallFamily walls, TerrainSet floor, TempleOptions options,
        CancellationToken cancellation = default, AssetStore? assets = null, DungeonThemeResources? themes = null)
    {
        cancellation.ThrowIfCancellationRequested();
        if (walls.Id != options.Temple.WallId || floor.Id != options.Temple.FloorId || !walls.DrawInterior)
            throw new ArgumentException("The selected dungeon style needs its matching interior wall brush and floor tiles.");
        if (options.Rooms is < 1 or > 30 || options.RoomSize is not (0 or 3 or 4 or 5) || options.CorridorWidth is < 1 or > 2 ||
            options.DoorCount is < 0 or > WallEntrances.MaximumCount || !Enum.IsDefined(options.Type) || !Enum.IsDefined(options.Theme) || options.Theme != DungeonTheme.Classic && options.RoomSize == 0)
            throw new ArgumentOutOfRangeException(nameof(options));
        if (source.Width < 64 || source.Height < 64 || (long)source.Width * source.Height > MapDimensions.MaximumGeneratedCells)
            throw new ArgumentException("Dungeon generation needs at least 64×64 cells and supports up to 1,000,000 cells.");
        if (options.Theme != DungeonTheme.Classic && (options.Temple != DungeonThemes.Definition(options.Theme) || options.Type != DungeonThemes.Type(options.Theme)))
            throw new ArgumentException("The selected theme requires its matching layout and native artwork.");
        long revision = source.PickupRevision;
        walls=walls.WithMode(true);
        walls.RequestedDoorCount=options.DoorCount;
        walls.DoorCountPerMap=true;
        if (options.DoorCount>0 && assets!=null && walls.Availability(assets) is { } entranceError) throw new InvalidDataException(entranceError);
        var random = new Random(options.Seed);
        // Chambers lie on the wall brush's native diagonal lattice. A spare
        // section separates neighbouring rooms so corners never touch alone.
        // Keep every boundary run long enough for the family's complete
        // native shoulders. These caves use four small wall pieces per unit.
        int unit = walls.BoundarySpans.Where(s => s.Interior).Select(s => s.MinimumPieces).DefaultIfEmpty(1).Max();
        bool corridorsOnly = options.RoomSize == 0;
        int roomSize = corridorsOnly ? 5 : options.RoomSize;
        int maxRoom = unit == 1 ? roomSize : roomSize - 2;
        int minRoom = corridorsOnly ? options.CorridorWidth : unit == 1 ? 3 : 1;
        int corridorWidth = unit == 1 ? options.CorridorWidth : Math.Min(options.CorridorWidth, maxRoom);
        int gap = unit == 1 ? 2 : 1;
        int side = (Math.Min(source.Width, source.Height) - options.Temple.Margin) / (2 * walls.Step);
        int minimum = Math.Max(64, minRoom * unit * 2 * walls.Step + options.Temple.Margin);
        if (Math.Min(source.Width, source.Height) < minimum)
            throw new ArgumentException($"{options.Temple.Name} needs a map at least {minimum}×{minimum} cells for its native walls.");
        maxRoom = Math.Min(maxRoom, side / unit);
        corridorWidth = Math.Min(corridorWidth, maxRoom);
        minRoom = Math.Max(minRoom, corridorWidth);
        int capacity = Math.Clamp((side / unit + gap) / (minRoom + gap), 1, 8);
        int slots = Math.Min(capacity, (int)Math.Ceiling(Math.Sqrt(options.Rooms)));
        // Room size is a maximum. Use smaller chambers when that lets the
        // requested network fit instead of turning large-wall temples into
        // one oversized room on an otherwise usable map.
        if (options.Theme == DungeonTheme.Classic)
            maxRoom = Math.Min(maxRoom, (side / unit - (slots - 1) * gap) / slots);
        int pitch = maxRoom + gap;
        int count = options.Type == DungeonType.Cave ? options.Rooms : Math.Min(options.Rooms, slots * slots);
        HashSet<(int U, int V)> nodes;
        (int U, int V)[] roomNodes;
        if (options.Theme != DungeonTheme.Classic)
        {
            (nodes, roomNodes) = DungeonThemeLayout.Create(options.Theme, side / unit, Math.Max(1, maxRoom),
                options.Rooms, corridorWidth, random, cancellation);
            count = roomNodes.Length;
        }
        else if (options.Type == DungeonType.Cave)
        {
            // Winding tunnels can reach every edge of their source grid. Leave
            // room for their width before fitting, rather than carving a full
            // grid and widening it beyond the native wall margins afterwards.
            int caveExtent = corridorsOnly ? Math.Max(1, (Math.Min(source.Width, source.Height) - options.Temple.Margin -
                (2 * corridorWidth + 1) * unit * walls.Step) / (2 * unit * walls.Step) + 1) : side / unit;
            (nodes, roomNodes) = CaveLayout.Create(caveExtent, maxRoom, count, corridorWidth, corridorsOnly, random, cancellation);
        }
        else
        {
            var chosen = new List<(int U, int V)> { (random.Next(slots), random.Next(slots)) };
            var occupied = chosen.ToHashSet();
            var edges = new List<((int U, int V) A, (int U, int V) B)>();
            while (chosen.Count < count)
            {
                cancellation.ThrowIfCancellationRequested();
                var frontier = chosen.SelectMany(p => Directions.Select(d => (A: p, B: (U: p.U + d.U, V: p.V + d.V))))
                    .Where(e => e.B.U >= 0 && e.B.V >= 0 && e.B.U < slots && e.B.V < slots && !occupied.Contains(e.B)).ToArray();
                var edge = frontier[random.Next(frontier.Length)];
                edges.Add(edge); chosen.Add(edge.B); occupied.Add(edge.B);
            }
            // A few additional links produce loops as well as branching passages.
            foreach (var a in chosen)
            foreach (var d in Directions.Take(2))
            {
                var b = (U: a.U + d.U, V: a.V + d.V);
                if (occupied.Contains(b) && !edges.Any(e => e.A == a && e.B == b || e.A == b && e.B == a) && random.NextDouble() < .25)
                    edges.Add((a, b));
            }
            nodes = new HashSet<(int U, int V)>();
            foreach (var room in chosen)
            {
                if (corridorsOnly) { nodes.Add((room.U * pitch, room.V * pitch)); continue; }
                int size = random.Next(Math.Max(minRoom, corridorWidth), maxRoom + 1), start = -(size / 2);
                for (int u = start; u < start + size; u++)
                for (int v = start; v < start + size; v++) nodes.Add((room.U * pitch + u, room.V * pitch + v));
            }
            foreach (var (a, b) in edges)
            {
                for (int u = Math.Min(a.U, b.U) * pitch; u <= Math.Max(a.U, b.U) * pitch; u++)
                for (int v = Math.Min(a.V, b.V) * pitch; v <= Math.Max(a.V, b.V) * pitch; v++)
                for (int w = 0; w < (corridorsOnly ? 1 : corridorWidth); w++)
                    nodes.Add(a.U == b.U ? (u + w, v) : (u, v + w));
            }
            roomNodes = chosen.Select(p => (U: p.U * pitch, V: p.V * pitch)).ToArray();
            // Even one junction produces a passage, rather than an empty plan or room.
            if (corridorsOnly && count == 1)
                for (int u = 1; u <= 2; u++) nodes.Add((roomNodes[0].U + u, roomNodes[0].V));
        }
        if (options.Type == DungeonType.Cave) MergeVertexContacts(nodes);
        (nodes, roomNodes) = FitMap(nodes, roomNodes, source, walls, unit, options.Temple.Margin, cancellation,
            corridorsOnly ? corridorWidth : 0);
        if (options.Theme == DungeonTheme.Classic && options.Type == DungeonType.Cave)
            CaveLayout.AddInteriorWalls(nodes, roomNodes, corridorWidth, random, cancellation);
        if (unit > 1)
            nodes = nodes.SelectMany(p => Enumerable.Range(0, unit).SelectMany(u =>
                Enumerable.Range(0, unit).Select(v => (U: p.U * unit + u, V: p.V * unit + v)))).ToHashSet();
        var raw = nodes.Select(n => walls.GridPoint(n.U, n.V)).ToArray();
        var offset = walls.Snap((source.Width - raw.Min(p => p.X) - raw.Max(p => p.X)) / 2,
            (source.Height - raw.Min(p => p.Y) - raw.Max(p => p.Y)) / 2);
        var anchors = raw.Select(p => (X: p.X + offset.X, Y: p.Y + offset.Y)).OrderBy(p => p.Y).ThenBy(p => p.X).ToArray();
        var ground = new HashSet<(int X, int Y)>();
        var floorRegion = new HashSet<(int X, int Y)>();
        var open = new HashSet<(int X, int Y)>();
        int radius = walls.VertexNeighbours.Max(d => Math.Max(Math.Abs(d.X), Math.Abs(d.Y))) / 2;
        foreach (var p in anchors)
        for (int y = -radius; y <= radius; y++)
        for (int x = -radius; x <= radius; x++)
        {
            if (!walls.ContainsOffset(x, y, boundary: true)) continue;
            open.Add((p.X + x, p.Y + y));
        }
        var generated = new MapDocument(source.Width, source.Height);
        IReadOnlyList<GeneratedSideRoom> sideRooms = [];
        if (walls.Id == "zuma-temple-stone")
        {
            // Select the native side rooms before painting ground: their floor
            // and thresholds extend beyond the corridor's wall-brush lattice.
            var outline = WallBrush.PlanStroke(generated, walls, anchors, variationSeed: options.Seed, clipToMap: false, specialWalls: true);
            if (!outline.Allowed) throw new InvalidDataException(outline.Error);
            sideRooms = outline.SideRooms;
            open.UnionWith(ZumaTempleSideRooms.OpenCells(walls, sideRooms));
        }
        // Ground sprites cover 2×2 cells from their top-left anchor. Sample
        // all four corners using this family's floor offsets. Some families
        // have thin foreground ridges and deep rear walls; cover both feet
        // rather than translating the entire floor away from either edge.
        int floorOffset = options.Temple.FloorOffset;
        int frontOffset = options.Temple.FloorFrontOffset ?? floorOffset;
        for (int y = 0; y < source.Height; y += 2)
        {
            cancellation.ThrowIfCancellationRequested();
            for (int x = 0; x < source.Width; x += 2)
            for (int offsetY = Math.Min(floorOffset, frontOffset); offsetY <= Math.Max(floorOffset, frontOffset); offsetY += 2)
            {
                bool a = open.Contains((x, y + offsetY)), b = open.Contains((x + 2, y + offsetY));
                bool c = open.Contains((x, y + offsetY + 2)), d = open.Contains((x + 2, y + offsetY + 2));
                // Artwork can complete a tile intersecting the intended floor,
                // but cannot turn an enclosed outside region into another room.
                if (a || b || c || d) floorRegion.Add((x, y));
                if (a && b && c && d) ground.Add((x, y));
            }
        }
        foreach (var room in sideRooms)
        for (int y = (room.Y - 10) & ~1; y <= room.Y + 10; y += 2)
        for (int x = (room.X - 10) & ~1; x <= room.X + 10; x += 2)
        {
            // Native rooms carry their own wall height/footprint, independent
            // of the corridor's floor offset. Cover their enclosed floor too.
            var corners = new[] { (x, y), (x + 2, y), (x, y + 2), (x + 2, y + 2) };
            if (corners.Any(p => Math.Abs(p.Item1 - room.X - .5) + Math.Abs(p.Item2 - room.Y - 1) <= 8.5)) floorRegion.Add((x, y));
            if (corners.All(p => Math.Abs(p.Item1 - room.X - .5) + Math.Abs(p.Item2 - room.Y - 1) <= 8.5)) ground.Add((x, y));
        }
        TerrainSet? exterior = null;
        if (options.Theme != DungeonTheme.Classic)
        {
            var catalogue = TerrainCatalogue.Load(format: assets?.Format ?? AssetSourceFormat.Default);
            exterior = catalogue.Sets.Single(s => s.Id == DungeonThemes.Exterior(options.Theme));
            if (assets != null && (!catalogue.MatchesAssets(exterior, assets) || !catalogue.MatchesAssets(floor, assets)))
                throw new InvalidDataException("The selected libraries do not contain the matching themed ground artwork.");
        }
        var variants = floor.Shapes[15];
        long totalWeight = variants.Sum(v => v.Weight);
        for (int y = 0; y < source.Height; y++)
        {
            cancellation.ThrowIfCancellationRequested();
            for (int x = 0; x < source.Width; x++)
            {
                var cell = new Cell { BackImage = open.Contains((x, y)) ? 0 : Blocked };
                if (ground.Contains((x, y)))
                {
                    long roll = random.NextInt64(totalWeight);
                    var variant = variants[^1];
                    foreach (var candidate in variants) { roll -= candidate.Weight; if (roll < 0) { variant = candidate; break; } }
                    cell.BackLibrary = checked((short)floor.Library); cell.BackImage |= variant.Index + 1;
                }
                else if (exterior != null && x % 2 == 0 && y % 2 == 0 && !floorRegion.Contains((x, y)))
                {
                    var background = exterior.Shapes[15];
                    // Stable per-cell texture: exterior rendering cannot consume
                    // the room-floor random sequence or alter walkable geometry.
                    int index = (int)(unchecked((uint)(x * 73856093 ^ y * 19349663 ^ options.Seed)) % (uint)background.Count);
                    cell.BackLibrary = (short)exterior.Library; cell.BackImage |= background[index].Index + 1;
                }
                generated.Set(x, y, cell);
            }
        }
        cancellation.ThrowIfCancellationRequested();
        var wallPlan = WallBrush.PlanStroke(generated, walls, anchors, variationSeed: options.Seed, clipToMap: false, specialWalls: true);
        if (!wallPlan.Allowed) throw new InvalidDataException(wallPlan.Error);
        if (assets != null)
        {
            // Some native corner shoulders enclose floor one tile beyond the
            // lattice footprint. Permit repairs there without extending the
            // painted floor into transparent exterior or solid wall islands.
            int padding = options.Temple.FloorRepairPadding;
            if (padding > 0)
                floorRegion = floorRegion.SelectMany(p => Enumerable.Range(-padding / 2, padding + 1).SelectMany(y =>
                    Enumerable.Range(-padding / 2, padding + 1).Select(x => (X: p.X + x * 2, Y: p.Y + y * 2))))
                    .Where(p => generated.Contains(p.X, p.Y)).ToHashSet();
            // Native masks have different feet and corner silhouettes. Complete
            // enclosed floor gaps against the actual composed artwork, then build
            // ownership against the finished floor so subsequent wall edits undo cleanly.
            var missing = TempleFloorCoverage.FindMissing(generated, wallPlan.Cells, floorRegion, assets, cancellation);
            foreach (var p in missing.OrderBy(p => p.Y).ThenBy(p => p.X))
            {
                var cell = generated.Get(p.X, p.Y).Copy();
                long roll = random.NextInt64(totalWeight);
                var variant = variants[^1];
                foreach (var candidate in variants) { roll -= candidate.Weight; if (roll < 0) { variant = candidate; break; } }
                cell.BackLibrary = checked((short)floor.Library);
                cell.BackImage = (cell.BackImage & Blocked) | (variant.Index + 1);
                generated.Set(p.X, p.Y, cell);
            }
            if (missing.Count > 0)
                wallPlan = WallBrush.PlanStroke(generated, walls, anchors, variationSeed: options.Seed, clipToMap: false, specialWalls: true);
            if (!wallPlan.Allowed) throw new InvalidDataException(wallPlan.Error);
        }
        if (exterior != null)
        {
            for (int y = 0; y < source.Height; y += 2)
            {
                cancellation.ThrowIfCancellationRequested();
                for (int x = 0; x < source.Width; x += 2)
                {
                    var cell = generated.Get(x, y);
                    if ((cell.BackImage & 0x1fffffff) != 0 || (cell.BackImage & Blocked) == 0) continue;
                    var copy = cell.Copy(); copy.BackLibrary = (short)exterior.Library;
                    copy.BackImage |= exterior.Shapes[15][0].Index + 1;
                    generated.Set(x, y, copy);
                }
            }
            // Ownership is composed against the finished background.
            wallPlan = WallBrush.PlanStroke(generated, walls, anchors, variationSeed: options.Seed, clipToMap: false, specialWalls: true);
            if (!wallPlan.Allowed) throw new InvalidDataException(wallPlan.Error);
        }
        // Do not accept the wall brush's edge clipping for generated temples.
        if (anchors.Any(p => p.X < radius || p.Y < radius || p.X + radius >= source.Width || p.Y + radius >= source.Height))
            throw new InvalidDataException("This layout does not fit the map. Reduce room size or use a larger map.");
        foreach (var (p, cell) in wallPlan.Cells) generated.Set(p.X, p.Y, cell);
        generated.RestoreStamps(wallPlan.Stamps);
        cancellation.ThrowIfCancellationRequested();
        var centres = roomNodes.Select(p => walls.GridPoint(p.U * unit + unit / 2, p.V * unit + unit / 2))
            .Select(p => (X: p.X + offset.X, Y: p.Y + offset.Y)).Concat(sideRooms.Select(r => (r.X, r.Y + 1))).ToArray();
        var entrances=walls.Id=="stone-temple"
            ? GeneratorEntrances.AddStoneStairs(generated,options.DoorCount,options.Seed,assets,cancellation,centres)
            : GeneratorEntrances.Locations(wallPlan);
        IReadOnlyList<PlacedStamp> decorations = [];
        if (options.Theme != DungeonTheme.Classic)
            (centres, decorations) = DungeonThemeDetails.Place(generated, centres, options.Theme, options.Seed, assets, themes ?? new(), cancellation);
        cancellation.ThrowIfCancellationRequested();
        if (source.PickupRevision != revision) throw new InvalidOperationException("The map changed while generating the dungeon preview.");
        return new(source, revision, generated, options, (corridorsOnly && options.Theme == DungeonTheme.Classic ? 0 : count) + sideRooms.Count, centres)
            { Entrances=entrances, SideRooms=sideRooms, Decorations=decorations };
    }

    private static (HashSet<(int U, int V)> Nodes, (int U, int V)[] Centres) FitMap(
        HashSet<(int U, int V)> nodes, (int U, int V)[] centres, MapDocument map, WallFamily walls,
        int unit, int margin, CancellationToken cancellation, int corridorWidth = 0)
    {
        // Resize occupied lattice areas, never artwork. Independent X/Y factors
        // use the long dimension of rectangular maps as well as square maps.
        int step = walls.Step * unit;
        var points = nodes.Select(p => (X: p.U + p.V, Y: p.U - p.V)).ToArray();
        int minX = points.Min(p => p.X), maxX = points.Max(p => p.X);
        int minY = points.Min(p => p.Y), maxY = points.Max(p => p.Y);
        int areaPadding = corridorWidth == 0 ? 2 : 0;
        // Reserve space for route rounding, width applied below, and end caps.
        double inset = (corridorWidth == 0 ? 2 : 2 * corridorWidth + 1) * step;
        double sx = Math.Max(1, (map.Width - margin - inset) / (step * Math.Max(1, maxX - minX + areaPadding)));
        double sy = Math.Max(1, (map.Height - margin - inset) / (step * Math.Max(1, maxY - minY + areaPadding)));
        if (sx == 1 && sy == 1 && corridorWidth == 0) return (nodes, centres);
        double cx = (minX + maxX) / 2.0, cy = (minY + maxY) / 2.0;
        (int U, int V) Transform((int U, int V) p)
        {
            double x = (p.U + p.V - cx) * sx, y = (p.U - p.V - cy) * sy;
            return ((int)Math.Round((x + y) / 2), (int)Math.Round((x - y) / 2));
        }
        var result = new HashSet<(int U, int V)>();
        if (corridorWidth > 0) result.UnionWith(nodes.Select(Transform));
        int extent = (int)Math.Ceiling(((maxX - minX) * sx + (maxY - minY) * sy) / 4) + 2;
        for (int u = -extent; corridorWidth == 0 && u <= extent; u++)
        {
            cancellation.ThrowIfCancellationRequested();
            for (int v = -extent; v <= extent; v++)
            {
                double x = (u + v) / sx + cx, y = (u - v) / sy + cy;
                if (nodes.Contains(((int)Math.Round((x + y) / 2), (int)Math.Round((x - y) / 2)))) result.Add((u, v));
            }
        }
        // Sampling a stretched narrow passage can leave diagonal-only contacts.
        // Join transformed neighbours with a four-connected supercover path.
        foreach (var p in nodes)
        foreach (var d in Directions.Take(2))
        {
            var neighbour = (p.U + d.U, p.V + d.V);
            if (!nodes.Contains(neighbour)) continue;
            var a = Transform(p); var b = Transform(neighbour);
            int du = Math.Abs(b.U - a.U), dv = Math.Abs(b.V - a.V), iu = 0, iv = 0;
            result.Add(a);
            while (iu < du || iv < dv)
            {
                if (iv == dv || iu < du && (iu + .5) / du < (iv + .5) / dv) { a.U += Math.Sign(b.U - a.U); iu++; }
                else { a.V += Math.Sign(b.V - a.V); iv++; }
                result.Add(a);
            }
        }
        // Discard isolated rounding slivers; every room is on the connected backbone.
        var connected = new HashSet<(int U, int V)>();
        var queue = new Queue<(int U, int V)>(); queue.Enqueue(Transform(centres[0]));
        while (queue.TryDequeue(out var p))
        {
            if (!result.Contains(p) || !connected.Add(p)) continue;
            foreach (var d in Directions) queue.Enqueue((p.U + d.U, p.V + d.V));
        }
        // Corridors Only stretches the route, then applies its native width.
        // Scaling the occupied area would turn a single passage into a chamber.
        if (corridorWidth > 1)
            connected = connected.SelectMany(p => Enumerable.Range(0, corridorWidth).SelectMany(u =>
                Enumerable.Range(0, corridorWidth).Select(v => (U: p.U + u, V: p.V + v)))).ToHashSet();
        MergeVertexContacts(connected);
        return (connected, centres.Select(Transform).ToArray());
    }

    private static void MergeVertexContacts(HashSet<(int U, int V)> connected)
    {
        // Both the floor and the empty space must meet along edges. A diagonal
        // contact would ask two native corner sprites to occupy the same vertex.
        var vertices = new Queue<(int U, int V)>(connected.SelectMany(p => new[]
            { p, (p.U - 1, p.V), (p.U, p.V - 1), (p.U - 1, p.V - 1) }).Distinct());
        while (vertices.TryDequeue(out var vertex))
        {
            var (u, v) = vertex;
            int mask = (connected.Contains((u, v)) ? 1 : 0) | (connected.Contains((u + 1, v)) ? 2 : 0) |
                (connected.Contains((u, v + 1)) ? 4 : 0) | (connected.Contains((u + 1, v + 1)) ? 8 : 0);
            if (mask is not (6 or 9)) continue;
            foreach (var p in new[] { (U: u, V: v), (U: u + 1, V: v), (U: u, V: v + 1), (U: u + 1, V: v + 1) })
                if (connected.Add(p))
                    foreach (var next in new[] { p, (p.U - 1, p.V), (p.U, p.V - 1), (p.U - 1, p.V - 1) }) vertices.Enqueue(next);
        }
    }
}
