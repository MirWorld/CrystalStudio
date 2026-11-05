using System.IO;

namespace CrystalStudio;

public sealed record ProvinceRuin(int X, int Y, IReadOnlyList<(int X, int Y)> Approach,
    IReadOnlyList<TreePlacement> Objects);

public sealed class ProvinceRuinResources
{
    private IReadOnlyList<(string Role, BrushTree Tree)>? pieces;

    internal IReadOnlyList<(string Role, BrushTree Tree)> Load(AssetStore? assets, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (pieces != null) return pieces;
        var result = new List<(string, BrushTree)>();
        foreach (var (role, ids) in new (string, int[])[] {
            ("core", [6074, 6076, 6425, 6426]), ("wall", [3772, 3774, 3856, 4217, 4246]),
            ("statue", [154, 155, 156, 157]), ("debris", [1731, 1732, 6402, 6403, 6404]) })
        foreach (int id in ids)
        {
            token.ThrowIfCancellationRequested();
            string name = $"mir/mir_{id:00000}";
            if (ProvinceSceneryPalette.GroundSetId(name) != null) continue;
            try
            {
                string path = DataPaths.ResolveEditor("Objects", "mir", $"mir_{id:00000}.X");
                var tree = BrushTree.FromCells(name, path, CellCodec.LoadStamp(path), assets);
                // Some catalogue objects decode successfully but contain only
                // transparent cards. They cannot stand in for a visible ruin.
                bool visible = assets == null;
                if (assets != null)
                foreach (var front in tree.Cells.SelectMany(c => c.Cell.ForegroundImages).Distinct())
                {
                    var sprite = assets.Libraries[front.Library].Decode((front.Image & 0x7fff) - 1)
                        ?? throw new InvalidDataException("Missing ruin sprite.");
                    byte[] pixels = new byte[sprite.Width * sprite.Height * 4];
                    sprite.Bitmap.CopyPixels(pixels, sprite.Width * 4, 0);
                    for (int i = 3; i < pixels.Length; i += 4) if (pixels[i] != 0) { visible = true; break; }
                }
                if (visible) result.Add((role, tree));
            }
            catch (IOException) { }
        }
        if (!result.Any(p => p.Item1 == "core") || !result.Any(p => p.Item1 == "wall"))
            throw new InvalidDataException("Forest ruin artwork is unavailable in the selected libraries.");
        return pieces = result;
    }
}

internal static class ProvinceRuins
{
    private const int Radius = 25;

    internal static IReadOnlyList<ProvinceRuin> Place(MapDocument map, TerrainCatalogue terrain, ProvinceStyle style,
        ProvinceOptions options, bool[] reserved, IEnumerable<(int X, int Y)> roads,
        ProvinceRuinResources resources, AssetStore? assets, List<string> notes, CancellationToken token)
    {
        var pieces = resources.Load(assets, token);
        if (pieces.Count < 18) notes.Add("Some optional forest ruin objects are unavailable; only complete visible objects were used.");
        int width = map.Width, height = map.Height, columns = (width + 1) / 2, rows = (height + 1) / 2;
        var known = terrain.Sets.Where(s => s.Id == style.BaseId || s.Id == style.PatchId)
            .SelectMany(s => s.Shapes.Values.SelectMany(v => v).Select(v => (s.Library, v.Index + 1))).ToHashSet();
        var hard = new bool[width * height];
        for (int y = 0; y < height; y++)
        {
            token.ThrowIfCancellationRequested();
            for (int x = 0; x < width; x++)
            {
                var c = map.Get(x, y); var ground = map.Get(x & ~1, y & ~1);
                hard[y * width + x] = x < 22 || y < 22 || x >= width - 22 || y >= height - 22 ||
                    (c.BackImage & 0x60000000) != 0 || c.ForegroundImages.Count > 0 || c.MiddleImage != 0 ||
                    !known.Contains(((int)ground.BackLibrary, ground.BackImage & 0x1fffffff));
            }
        }
        var navigation = new bool[columns * rows];
        for (int y = 2; y < rows - 2; y++) for (int x = 2; x < columns - 2; x++)
        {
            bool clear = true;
            for (int dy = -2; clear && dy <= 2; dy++) for (int dx = -2; dx <= 2; dx++)
                if (hard[(y * 2 + dy) * width + x * 2 + dx]) { clear = false; break; }
            navigation[y * columns + x] = clear;
        }
        var network = roads.Select(p => p.Y / 2 * columns + p.X / 2).Distinct()
            .Where(p => p >= 0 && p < navigation.Length && navigation[p]).ToHashSet();
        if (network.Count == 0) { notes.Add("No accessible trail was available for forest ruins."); return []; }
        // Use one land component: two ruins must not silently join unrelated
        // trails on opposite banks of an unbridged river.
        int anchor = network.OrderBy(p => Math.Abs(p % columns - columns / 2) + Math.Abs(p / columns - rows / 2)).ThenBy(p => p).First();
        var connected = new bool[navigation.Length]; var queue = new Queue<int>();
        connected[anchor] = true; queue.Enqueue(anchor);
        while (queue.TryDequeue(out int p))
        {
            token.ThrowIfCancellationRequested();
            foreach (int n in Neighbours(p)) if (navigation[n] && !connected[n]) { connected[n] = true; queue.Enqueue(n); }
        }
        network.RemoveWhere(p => !connected[p]);
        var random = new Random(options.Seed ^ 0x4A173);
        var candidates = new List<(int X, int Y, int Rank)>();
        for (int y = 50; y < height - 50; y += 8) for (int x = 50; x < width - 50; x += 8)
            if (connected[(y + Radius + 1) / 2 * columns + x / 2]) candidates.Add((x, y, random.Next()));
        var result = new List<ProvinceRuin>(); var trails = new HashSet<(int X, int Y)>();
        var history = new EditHistory();
        foreach (var site in candidates.OrderBy(c => c.Rank))
        {
            token.ThrowIfCancellationRequested();
            if (result.Count == options.RuinCount) break;
            bool clear = true;
            for (int y = site.Y - Radius; clear && y <= site.Y + Radius; y++)
            for (int x = site.X - Radius; x <= site.X + Radius; x++)
                if (hard[y * width + x] || reserved[y * width + x]) { clear = false; break; }
            if (!clear) continue;
            var objects = new List<TreePlacement>();
            foreach (var (role, dx, dy) in new[] { ("core", 0, -10), ("wall", -17, -1), ("wall", 17, -1),
                ("statue", -6, 9), ("statue", 6, 9), ("debris", -14, 16), ("debris", 14, 16) })
            {
                var pool = pieces.Where(p => p.Role == role).ToArray();
                if (pool.Length == 0) continue;
                var tree = pool[random.Next(pool.Length)].Tree;
                objects.Add(new(tree, site.X + dx, site.Y + dy, tree.Cells));
            }
            // Complete object bounding boxes leave a central, south-facing
            // entrance. Never crop, mirror or overlap native sprite cards.
            var occupied = new HashSet<(int, int)>();
            foreach (var obj in objects)
            for (int y = obj.Y + obj.Cells.Min(c => c.Y); y <= obj.Y + obj.Cells.Max(c => c.Y); y++)
            for (int x = obj.X + obj.Cells.Min(c => c.X); x <= obj.X + obj.Cells.Max(c => c.X); x++)
                if (Math.Abs(x - site.X) > Radius || Math.Abs(y - site.Y) > Radius ||
                    !occupied.Add((x, y)) || Math.Abs(x - site.X) <= 2 && y >= site.Y + 2) clear = false;
            if (!clear) continue;
            int start = (site.Y + Radius + 1) / 2 * columns + site.X / 2;
            var parent = new int[navigation.Length]; Array.Fill(parent, -1);
            queue.Clear(); queue.Enqueue(start); parent[start] = start; int end = -1;
            while (queue.TryDequeue(out int p))
            {
                token.ThrowIfCancellationRequested();
                if (network.Contains(p)) { end = p; break; }
                foreach (int n in Neighbours(p))
                {
                    int x = n % columns * 2, y = n / columns * 2;
                    if (!navigation[n] || parent[n] != -1 ||
                        Math.Abs(x - site.X) <= Radius + 2 && Math.Abs(y - site.Y) <= Radius) continue;
                    parent[n] = p; queue.Enqueue(n);
                }
            }
            if (end < 0) continue;
            var approach = new HashSet<(int X, int Y)>();
            void Lane(int x, int y)
            {
                for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++) approach.Add((x + dx, y + dy));
            }
            for (int p = end;; p = parent[p]) { Lane(p % columns * 2, p / columns * 2); if (p == start) break; }
            for (int y = site.Y + 3; y <= site.Y + Radius + 1; y++) Lane(site.X, y);
            foreach (var obj in objects)
                if (map.PlaceStamp(obj.Cells, obj.X, obj.Y, "Forest ruin · " + obj.Tree.Name, history, (_, _) => { }) == null)
                    throw new InvalidDataException("A complete forest ruin object could not be placed.");
            // An irregular clearing keeps the playable centre open while trees
            // can meet the walls at its edges, rather than leaving a bare square.
            for (int y = site.Y - Radius; y <= site.Y + Radius; y++)
            for (int x = site.X - Radius; x <= site.X + Radius; x++)
            {
                int key = y * width + x;
                if (Math.Pow((x - site.X) / 25.0, 2) + Math.Pow((y - site.Y) / 24.0, 2) <= 1.05 || occupied.Contains((x, y))) reserved[key] = true;
                navigation[y / 2 * columns + x / 2] = false;
                hard[key] = true;
            }
            foreach (var p in approach)
            {
                trails.Add(p); network.Add(p.Y / 2 * columns + p.X / 2);
                for (int dy = -2; dy <= 2; dy++) for (int dx = -2; dx <= 2; dx++) reserved[(p.Y + dy) * width + p.X + dx] = true;
            }
            result.Add(new(site.X, site.Y, approach.OrderBy(p => p.Y).ThenBy(p => p.X).ToArray(), objects));
        }
        ProvinceTowns.BlendPaths(map, terrain, style, trails, options.Seed, token);
        if (result.Count < options.RuinCount) notes.Add($"Placed {result.Count} of {options.RuinCount} forest ruins where complete scenes and accessible trails fit safely.");
        return result;

        IEnumerable<int> Neighbours(int p)
        {
            int x = p % columns, y = p / columns;
            if (x > 0) yield return p - 1; if (x + 1 < columns) yield return p + 1;
            if (y > 0) yield return p - columns; if (y + 1 < rows) yield return p + columns;
        }
    }

    internal static int PlantOvergrowth(MapDocument map, IReadOnlyList<ProvinceRuin> ruins,
        IReadOnlyList<ProvinceScenery> scenery, TerrainCatalogue terrain, ProvinceStyle style,
        bool[] reserved, ProvinceOptions options, CancellationToken token)
    {
        if (options.Vegetation == 0 || ruins.Count == 0) return 0;
        var shrubs = ProvinceSceneryPalette.Select(scenery, style, SpecialBrushKind.Bush)
            .Where(t => t.Cells.Max(c => c.X) - t.Cells.Min(c => c.X) <= 5 &&
                t.Cells.Max(c => c.Y) - t.Cells.Min(c => c.Y) <= 6).ToArray();
        if (shrubs.Length == 0) return 0;
        var ground = new ProvinceSceneryGround(terrain); var random = new Random(options.Seed ^ 0x0A34);
        var history = new EditHistory(); int count = 0;
        foreach (var ruin in ruins)
        {
            var forbidden = new HashSet<(int X, int Y)>();
            foreach (var p in ruin.Approach.Concat(ruin.Objects.SelectMany(o => o.Cells.Select(c => (X: o.X + c.X, Y: o.Y + c.Y)))))
                for (int dy = -2; dy <= 2; dy++) for (int dx = -2; dx <= 2; dx++) forbidden.Add((p.X + dx, p.Y + dy));
            // Small colonies around the remaining structure and rubble leave
            // the central entrance and every complete native object untouched.
            foreach (var obj in ruin.Objects)
            for (int attempt = 0; attempt < 8; attempt++)
            {
                token.ThrowIfCancellationRequested();
                if (random.Next(100) >= options.Vegetation) continue;
                int x = obj.X + random.Next(-8, 9), y = obj.Y + random.Next(-7, 8);
                var tree = shrubs[random.Next(shrubs.Length)];
                if (tree.Cells.Any(c => !map.Contains(x + c.X, y + c.Y) || Math.Abs(x + c.X - ruin.X) >= Radius ||
                    Math.Abs(y + c.Y - ruin.Y) >= Radius || forbidden.Contains((x + c.X, y + c.Y)) ||
                    (map.Get(x + c.X, y + c.Y).BackImage & 0x60000000) != 0 || map.Get(x + c.X, y + c.Y).MiddleImage != 0) ||
                    !ground.CanPlace(map, tree, x, y) || !map.CanPlaceStamp(tree.Cells, x, y)) continue;
                if (map.PlaceStamp(tree.Cells, x, y, tree.Name, history, (_, _) => { }) == null) continue;
                count++;
                foreach (var c in tree.Cells)
                {
                    reserved[(y + c.Y) * map.Width + x + c.X] = true;
                    for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++) forbidden.Add((x + c.X + dx, y + c.Y + dy));
                }
            }
        }
        return count;
    }
}
