using System.IO;

namespace CrystalStudio;

public sealed class DungeonThemeResources
{
    private readonly Dictionary<DungeonTheme, IReadOnlyList<(string Role, BrushTree Tree)>> prepared = [];

    internal IReadOnlyList<(string Role, BrushTree Tree)> Load(DungeonTheme theme, AssetStore? assets, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (prepared.TryGetValue(theme, out var found)) return found;
        (string Role, int[] Ids)[] groups = theme switch
        {
            DungeonTheme.CelestialPalace => [("landmark", [788]), ("left", [2433]), ("right", [2434]), ("detail", [1643])],
            DungeonTheme.AbyssalReef => [("detail", [4626, 4630, 4642, 4646])],
            DungeonTheme.AlienPlanet => [("landmark", [8675]), ("detail", [1561, 1562, 1563, 7418, 8382])],
            _ => throw new ArgumentOutOfRangeException(nameof(theme))
        };
        var pieces = ProvinceObjectResources.Load(groups, assets, token);
        if (groups.Any(g => !pieces.Any(p => p.Role == g.Role))) throw new InvalidDataException(LocalText.T("No reviewed visible objects are available for this dungeon theme."));
        if (assets != null) prepared[theme] = pieces;
        return pieces;
    }
}

internal static class DungeonThemeDetails
{
    private static void PaintInlay(MapDocument map, IReadOnlyList<(int X, int Y)> centres,
        AssetStore? assets, CancellationToken token)
    {
        var terrain = TerrainCatalogue.Load(format: assets?.Format ?? AssetSourceFormat.Default);
        var tile = terrain.Sets.Single(s => s.Id == "0:0:2550");
        if (assets != null && !terrain.MatchesAssets(tile, assets)) throw new InvalidDataException("Celestial inlay artwork is unavailable.");
        foreach (var centre in centres)
        {
            token.ThrowIfCancellationRequested();
            for (int y = (centre.Y - 20) & ~1; y <= centre.Y + 20; y += 2)
            for (int x = (centre.X - 20) & ~1; x <= centre.X + 20; x += 2)
            {
                int distance = Math.Abs(x - centre.X) + Math.Abs(y - centre.Y);
                if (distance is < 12 or > 16) continue;
                bool clear = true;
                for (int dy = -2; clear && dy <= 3; dy++) for (int dx = -2; dx <= 3; dx++)
                    if (!map.Contains(x + dx, y + dy) || (map.Get(x + dx, y + dy).BackImage & 0x60000000) != 0 ||
                        map.Get(x + dx, y + dy).ForegroundImages.Count > 0) { clear = false; break; }
                if (!clear) continue;
                var cell = map.Get(x, y).Copy();
                // A deliberate mosaic band uses complete stone tiles; it is
                // architectural paving, not an unreviewed natural terrain blend.
                cell.BackLibrary = (short)tile.Library;
                cell.BackImage = tile.Shapes[15][0].Index + 1;
                map.Set(x, y, cell);
            }
        }
    }

    internal static ((int X, int Y)[] Centres, IReadOnlyList<PlacedStamp> Decorations) Place(MapDocument map,
        IReadOnlyList<(int X, int Y)> intended, DungeonTheme theme, int seed, AssetStore? assets,
        DungeonThemeResources resources, CancellationToken token)
    {
        var pieces = resources.Load(theme, assets, token);
        if (theme == DungeonTheme.CelestialPalace) PaintInlay(map, intended, assets, token);
        int width = map.Width, height = map.Height;
        bool Open(int x, int y) => map.Contains(x, y) && (map.Get(x, y).BackImage & 0x60000000) == 0;
        var parent = new int[width * height]; Array.Fill(parent, -1);
        var queue = new Queue<int>();
        var start = Nearby(intended[0]).FirstOrDefault(p => Open(p.X, p.Y), (X: -1, Y: -1));
        if (start == (-1, -1)) throw new InvalidDataException("The themed entrance has no walkable floor.");
        int root = start.Y * width + start.X; parent[root] = root; queue.Enqueue(root);
        while (queue.TryDequeue(out int key))
        {
            token.ThrowIfCancellationRequested();
            int x = key % width, y = key / width;
            foreach (var p in new[] { (X: x - 1, Y: y), (X: x + 1, Y: y), (X: x, Y: y - 1), (X: x, Y: y + 1) })
            {
                if (!Open(p.X, p.Y) || parent[p.Y * width + p.X] != -1) continue;
                parent[p.Y * width + p.X] = key; queue.Enqueue(p.Y * width + p.X);
            }
        }
        var centres = intended.Select(c => Nearby(c).FirstOrDefault(p => map.Contains(p.X, p.Y) && parent[p.Y * width + p.X] >= 0, (X: -1, Y: -1))).ToArray();
        if (centres.Any(p => p == (-1, -1))) throw new InvalidDataException("A themed chamber is disconnected. Use a larger map or fewer chambers.");
        var reserved = new bool[parent.Length];
        foreach (var centre in centres)
        for (int key = centre.Y * width + centre.X;; key = parent[key])
        {
            Protect(key % width, key / width, 2);
            if (key == root) break;
        }
        foreach (var c in centres) Protect(c.X, c.Y, 3);
        var result = new List<PlacedStamp>(); var history = new EditHistory(); var random = new Random(seed ^ 0x7E4A);
        bool Fits(BrushTree tree, int x, int y)
        {
            for (int yy = y + tree.Cells.Min(c => c.Y); yy <= y + tree.Cells.Max(c => c.Y); yy++)
            for (int xx = x + tree.Cells.Min(c => c.X); xx <= x + tree.Cells.Max(c => c.X); xx++)
                if (!Open(xx, yy) || reserved[yy * width + xx] || map.Get(xx, yy).ForegroundImages.Count > 0 ||
                    map.Get(xx, yy).MiddleImage != 0 || (map.Get(xx & ~1, yy & ~1).BackImage & 0x1fffffff) == 0) return false;
            return true;
        }
        void Place(BrushTree tree, int x, int y)
        {
            var stamp = map.PlaceStamp(tree.Cells, x, y, "Dungeon theme · " + tree.Name, history, (_, _) => { });
            if (stamp == null) throw new InvalidDataException("A complete themed object could not be placed.");
            result.Add(stamp);
            foreach (var c in tree.Cells) Protect(x + c.X, y + c.Y, 1);
        }
        var landmark = pieces.FirstOrDefault(p => p.Role == "landmark").Tree;
        if (landmark != null)
        foreach (var centre in centres.Reverse())
        {
            bool placed = false;
            for (int dy = -16; !placed && dy <= 16; dy += 4) for (int dx = -16; dx <= 16; dx += 4)
                if (Fits(landmark, centre.X + dx, centre.Y + dy)) { Place(landmark, centre.X + dx, centre.Y + dy); placed = true; break; }
            if (placed) break;
        }
        if (theme == DungeonTheme.CelestialPalace)
        {
            var left = pieces.FirstOrDefault(p => p.Role == "left").Tree;
            var right = pieces.FirstOrDefault(p => p.Role == "right").Tree;
            if (left != null && right != null)
            foreach (var c in centres)
            foreach (int offset in new[] { 10, 16, 22 })
                if (Fits(left, c.X - offset, c.Y - offset) && Fits(right, c.X + offset, c.Y + offset))
                { Place(left, c.X - offset, c.Y - offset); Place(right, c.X + offset, c.Y + offset); break; }
            var tower = pieces.FirstOrDefault(p => p.Role == "detail").Tree;
            if (tower != null)
            foreach (var c in centres.Where((_, i) => i % 2 == 0))
            foreach (var offset in new[] { (X: -14, Y: 0), (X: 14, Y: 0), (X: 0, Y: -14), (X: 0, Y: 14) })
                if (Fits(tower, c.X + offset.X, c.Y + offset.Y)) Place(tower, c.X + offset.X, c.Y + offset.Y);
        }
        else
        {
            var detail = pieces.Where(p => p.Role == "detail").Select(p => p.Tree).ToArray();
            for (int y = 10; detail.Length > 0 && y < height - 10; y += 6)
            {
                token.ThrowIfCancellationRequested();
                for (int x = 10; x < width - 10; x += 6)
                {
                    int px = x + random.Next(3), py = y + random.Next(3);
                    // Colonies follow the rims of chambers and rock islands;
                    // the reserved backbone and central fighting space stay open.
                    bool edge = !Open(px - 5, py) || !Open(px + 5, py) || !Open(px, py - 5) || !Open(px, py + 5);
                    double colony = Math.Sin(px / 13.0 + seed) + Math.Cos(py / 17.0 - seed * .3);
                    if (colony < (edge ? -.35 : .8)) continue;
                    var tree = detail[random.Next(detail.Length)];
                    if (Fits(tree, px, py)) Place(tree, px, py);
                }
            }
        }
        token.ThrowIfCancellationRequested();
        return (centres, result);

        void Protect(int x, int y, int radius)
        {
            for (int yy = Math.Max(0, y - radius); yy <= Math.Min(height - 1, y + radius); yy++)
            for (int xx = Math.Max(0, x - radius); xx <= Math.Min(width - 1, x + radius); xx++) reserved[yy * width + xx] = true;
        }
        static IEnumerable<(int X, int Y)> Nearby((int X, int Y) p)
        {
            for (int distance = 0; distance <= 24; distance++)
            for (int dx = -distance; dx <= distance; dx++)
            {
                int dy = distance - Math.Abs(dx);
                yield return (p.X + dx, p.Y + dy);
                if (dy != 0) yield return (p.X + dx, p.Y - dy);
            }
        }
    }
}
