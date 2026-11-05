using System.IO;

namespace CrystalStudio;

internal sealed record ProvinceClearing(int X, int Y, IReadOnlyList<(int X, int Y)> Approach,
    IReadOnlyList<TreePlacement> Objects);
internal sealed record ProvinceClearingLayout(IReadOnlyList<TreePlacement> Objects, IReadOnlyList<(int X, int Y)> Paths);

// Shared dry-land siting and lane protection for forest ruins and mining yards.
internal static class ProvinceClearings
{
    private const int Radius = 25;

    internal static IReadOnlyList<ProvinceClearing> Place(MapDocument map, TerrainCatalogue terrain, ProvinceStyle style,
        int seed, int count, bool[] reserved, IEnumerable<(int X, int Y)> roads, string name,
        Func<Random, ProvinceClearingLayout> layout, List<string> notes, CancellationToken token,
        int laneRadius = 1, bool preferCliffs = false)
    {
        token.ThrowIfCancellationRequested();
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
            for (int dy = -laneRadius - 1; clear && dy <= laneRadius + 1; dy++) for (int dx = -laneRadius - 1; dx <= laneRadius + 1; dx++)
                if (hard[(y * 2 + dy) * width + x * 2 + dx]) { clear = false; break; }
            navigation[y * columns + x] = clear;
        }
        var network = roads.Select(p => p.Y / 2 * columns + p.X / 2).Distinct()
            .Where(p => p >= 0 && p < navigation.Length && navigation[p]).ToHashSet();
        if (network.Count == 0) { notes.Add($"No accessible trail was available for {name}."); return []; }
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
        var random = new Random(seed ^ 0x4A173);
        var candidates = new List<(int X, int Y, int Rank)>();
        for (int y = 50; y < height - 50; y += 8) for (int x = 50; x < width - 50; x += 8)
            if (connected[(y + Radius + 1) / 2 * columns + x / 2]) candidates.Add((x, y, random.Next()));
        var result = new List<ProvinceClearing>(); var trails = new HashSet<(int X, int Y)>();
        var history = new EditHistory();
        var cliffs = preferCliffs ? map.PlacedStamps.Where(s => s.Wall != null).Select(s => (s.X, s.Y)).ToArray() : [];
        foreach (var site in candidates.OrderBy(c => cliffs.Length == 0 ? 0 : cliffs.Min(p => Math.Abs(p.X - c.X) + Math.Abs(p.Y - c.Y)) / 24).ThenBy(c => c.Rank))
        {
            token.ThrowIfCancellationRequested();
            if (result.Count == count) break;
            bool clear = true;
            for (int y = site.Y - Radius; clear && y <= site.Y + Radius; y++)
            for (int x = site.X - Radius; x <= site.X + Radius; x++)
                if (hard[y * width + x] || reserved[y * width + x]) { clear = false; break; }
            if (!clear) continue;
            var planned = layout(random);
            var objects = planned.Objects.Select(o => o with { X = o.X + site.X, Y = o.Y + site.Y }).ToList();
            var lanes = new HashSet<(int X, int Y)>();
            foreach (var p in planned.Paths)
                for (int dy = -laneRadius - 1; dy <= laneRadius + 1; dy++)
                for (int dx = -laneRadius - 1; dx <= laneRadius + 1; dx++) lanes.Add((p.X + dx, p.Y + dy));
            // Keep complete native cards away from working lanes, even if
            // their collision marks occupy only part of the bounding box.
            var occupied = new HashSet<(int, int)>();
            foreach (var obj in objects)
            for (int y = obj.Y + obj.Cells.Min(c => c.Y); y <= obj.Y + obj.Cells.Max(c => c.Y); y++)
            for (int x = obj.X + obj.Cells.Min(c => c.X); x <= obj.X + obj.Cells.Max(c => c.X); x++)
                if (Math.Abs(x - site.X) > Radius || Math.Abs(y - site.Y) > Radius ||
                    !occupied.Add((x, y)) || lanes.Contains((x - site.X, y - site.Y))) clear = false;
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
                for (int dy = -laneRadius; dy <= laneRadius; dy++) for (int dx = -laneRadius; dx <= laneRadius; dx++) approach.Add((x + dx, y + dy));
            }
            for (int p = end;; p = parent[p]) { Lane(p % columns * 2, p / columns * 2); if (p == start) break; }
            foreach (var p in planned.Paths) Lane(site.X + p.X, site.Y + p.Y);
            foreach (var obj in objects)
                if (map.PlaceStamp(obj.Cells, obj.X, obj.Y, name + " · " + obj.Tree.Name, history, (_, _) => { }) == null)
                    throw new InvalidDataException($"A complete {name} object could not be placed.");
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
        ProvinceTowns.BlendPaths(map, terrain, style, trails, seed, token);
        if (result.Count < count) notes.Add($"Placed {result.Count} of {count} {name} sites where complete scenes and accessible trails fit safely.");
        return result;

        IEnumerable<int> Neighbours(int p)
        {
            int x = p % columns, y = p / columns;
            if (x > 0) yield return p - 1; if (x + 1 < columns) yield return p + 1;
            if (y > 0) yield return p - columns; if (y + 1 < rows) yield return p + columns;
        }
    }

}
