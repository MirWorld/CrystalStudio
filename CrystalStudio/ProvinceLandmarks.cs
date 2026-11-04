namespace CrystalStudio;

internal static class ProvinceLandmarks
{
    internal static int PlaceCliffs(MapDocument map, WallFamily family, bool[] reserved, int seed, CancellationToken cancellation)
    {
        var random = new Random(seed ^ 0xC11F5);
        int desired = random.Next(1, Math.Clamp(map.Width * map.Height / 90000 + 2, 2, 9));
        int placed = 0;
        var empty = new MapDocument(map.Width, map.Height);
        for (int attempt = 0; attempt < desired * 35 && placed < desired; attempt++)
        {
            cancellation.ThrowIfCancellationRequested();
            var origin = family.Snap(random.Next(32, map.Width - 32), random.Next(32, map.Height - 32));
            int length = random.Next(2, 5), breadth = random.Next(1, 3);
            bool alongU = random.Next(2) == 0, recess = random.Next(2) == 0;
            var nodes = new List<(int X, int Y)>();
            for (int u = 0; u < length; u++) for (int v = 0; v < breadth; v++)
            {
                if (recess && length > 2 && breadth > 1 && u == length - 1 && v == 0) continue;
                var p = family.GridPoint(alongU ? u : v, alongU ? v : u);
                nodes.Add((origin.X + p.X, origin.Y + p.Y));
            }
            // Fit a complete standalone formation before touching the existing
            // network. Native shoulders must remain clear of roads and water.
            var candidate = WallBrush.PlanStroke(empty, family, nodes, variationSeed: seed, clipToMap: false);
            if (!candidate.Allowed || !candidate.HasChanges || candidate.Cells.Any(p =>
                p.Key.X < 24 || p.Key.Y < 24 || p.Key.X >= map.Width - 24 || p.Key.Y >= map.Height - 24 ||
                reserved[p.Key.Y * map.Width + p.Key.X] || map.Get(p.Key.X, p.Key.Y).ForegroundImages.Count > 0)) continue;
            // Keep it separate from border cliffs and previously placed hills.
            if (nodes.Any(p => map.PlacedStamps.Any(s => s.Wall != null &&
                Math.Abs(p.X - s.X) < family.Step * 3 && Math.Abs(p.Y - s.Y) < family.Step * 3))) continue;
            var plan = WallBrush.PlanStroke(map, family, nodes, variationSeed: seed);
            if (!plan.Allowed || !map.ApplyWallPlan(plan, new(), (_, _) => { })) continue;
            placed++;
            foreach (var p in candidate.Cells.Keys)
                for (int y = Math.Max(0, p.Y - 5); y <= Math.Min(map.Height - 1, p.Y + 5); y++)
                for (int x = Math.Max(0, p.X - 5); x <= Math.Min(map.Width - 1, p.X + 5); x++) reserved[y * map.Width + x] = true;
        }
        return placed;
    }

    internal static (int Groves, int Trees) PlantGroves(MapDocument map, IReadOnlyList<ProvinceScenery> scenery,
        ProvinceStyle style, bool[] reserved, int seed, int vegetation, CancellationToken cancellation, ProvinceSceneryGround? ground = null)
    {
        if (vegetation == 0) return (0, 0);
        vegetation = Math.Max(1, (int)Math.Round(vegetation * style.TreeDensity));
        var pool = ProvinceSceneryPalette.Select(scenery, style, SpecialBrushKind.Tree);
        if (pool.Length == 0) return (0, 0);
        var random = new Random(seed ^ 0xF0AE57);
        int desired = 1 + (int)Math.Round(vegetation / 100.0 * Math.Clamp(map.Width * map.Height / 120000 + 2, 2, 10));
        var centres = new List<(int X, int Y)>();
        int trees = 0;
        for (int attempt = 0; attempt < desired * 20 && centres.Count < desired; attempt++)
        {
            cancellation.ThrowIfCancellationRequested();
            int cx = random.Next(32, map.Width - 32), cy = random.Next(32, map.Height - 32);
            if (reserved[cy * map.Width + cx] || (map.Get(cx, cy).BackImage & 0x60000000) != 0 ||
                centres.Any(p => Math.Abs(p.X - cx) < 50 && Math.Abs(p.Y - cy) < 50)) continue;
            double radius = Math.Clamp(Math.Min(map.Width, map.Height) * .06, 10, 32) * style.GroveScale;
            double rx = radius * (.7 + random.NextDouble()), ry = radius * (.6 + random.NextDouble());
            double phase = random.NextDouble() * Math.Tau;
            var preferred = pool[random.Next(pool.Length)];
            var history = new EditHistory();
            int count = 0;
            for (int pass = 0; pass < 2; pass++)
            for (int sy = Math.Max(18, cy - (int)(ry * 1.3)); sy < Math.Min(map.Height - 18, cy + ry * 1.3); sy += 3)
            {
                cancellation.ThrowIfCancellationRequested();
                for (int sx = Math.Max(18, cx - (int)(rx * 1.3)); sx < Math.Min(map.Width - 18, cx + rx * 1.3); sx += 3)
                {
                    int x = sx + random.Next(3), y = sy + random.Next(3);
                    double dx = (x - cx) / rx, dy = (y - cy) / ry, angle = Math.Atan2(dy, dx);
                    double distance = Math.Sqrt(dx * dx + dy * dy) / (1 + .17 * Math.Sin(angle * 3 + phase) + .1 * Math.Sin(angle * 5 - phase));
                    double density = Math.Clamp((1.1 - distance) / .5, 0, 1);
                    if (random.NextDouble() >= density) continue;
                    for (int trial = 0; trial < 3; trial++)
                    {
                        var tree = trial == 0 && random.Next(3) != 0 ? preferred : pool[random.Next(pool.Length)];
                        if (ground != null && !ground.CanPlace(map, tree, x, y)) continue;
                        if (tree.Cells.Any(c => !map.Contains(x + c.X, y + c.Y) || reserved[(y + c.Y) * map.Width + x + c.X]) ||
                            !map.CanPlaceStamp(tree.Cells, x, y)) continue;
                        if (map.PlaceStamp(tree.Cells, x, y, tree.Name, history, (_, _) => { }) == null) continue;
                        count++; break;
                    }
                }
            }
            trees += count;
            if (count >= 5) centres.Add((cx, cy));
        }
        return (centres.Count, trees);
    }
}
