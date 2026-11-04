namespace CrystalStudio;

public sealed record ProvinceCliffBorderLayout(int FirstSide, int SideCount, HashSet<(int X, int Y)> Positions);

/// <summary>One native cliff network, turning around shared province corners.</summary>
public static class ProvinceCliffBorders
{
    public static ProvinceCliffBorderLayout CreateLayout(int width, int height, WallFamily family, int seed)
    {
        var random = new Random(seed ^ 0xC11FB0);
        int count = random.Next(1, 5), first = random.Next(4);
        var sides = Enumerable.Range(0, count).Select(i => (first + i) % 4).ToHashSet();
        var positions = new HashSet<(int X, int Y)>();
        int extent = (width + height) / family.Step + 8;
        foreach (int side in sides.Order())
        {
            int length = side % 2 == 0 ? width : height;
            // Along coordinates run left-to-right or top-to-bottom, rather
            // than clockwise. Join each end to the correct neighbouring side.
            int lowNeighbour = side % 2 == 0 ? 3 : 0;
            int highNeighbour = side % 2 == 0 ? 1 : 2;
            bool joinLow = sides.Contains(lowNeighbour), joinHigh = sides.Contains(highNeighbour);
            double start = random.NextDouble() * .16 + .04, end = .80 + random.NextDouble() * .16;
            double size = Math.Min(width, height);
            double depth = Math.Min(size * .18, Math.Max(family.Step * 2, size * (.07 + random.NextDouble() * .06)));
            var knots = Enumerable.Range(0, length / 65 + 2).Select(_ => .7 + random.NextDouble() * .6).ToArray();
            for (int u = -extent; u <= extent; u++)
            for (int v = -extent; v <= extent; v++)
            {
                var p = family.GridPoint(u, v);
                if (p.X < -family.Step * 2 || p.Y < -family.Step * 2 ||
                    p.X > width + family.Step * 2 || p.Y > height + family.Step * 2) continue;
                double along = side % 2 == 0 ? p.X : p.Y;
                double distance = side switch { 0 => p.Y, 1 => width - 1 - p.X, 2 => height - 1 - p.Y, _ => p.X };
                double taper = Math.Min(joinLow ? 1 : Math.Clamp((along / length - start) * 9, 0, 1),
                    joinHigh ? 1 : Math.Clamp((end - along / length) * 9, 0, 1));
                double at = Math.Clamp(along, 0, length) / 65;
                int k = (int)at;
                double t = at - k; t = t * t * (3 - 2 * t);
                double edge = depth * (knots[k] * (1 - t) + knots[k + 1] * t) * taper;
                if (taper > 0 && distance < edge) positions.Add(p);
            }
        }
        return new(first, count, positions);
    }

    public static int Place(MapDocument map, WallFamily family, bool[] coastReserved, int seed, CancellationToken cancellation)
    {
        var layout = CreateLayout(map.Width, map.Height, family, seed);
        var positions = layout.Positions;
        var empty = new MapDocument(map.Width, map.Height);
        // Compose all sides together so the native brush resolves shared
        // corners. Only shore artwork can cut the border; terrain paths must
        // not punch arbitrary gaps through an otherwise closed cliff ring.
        for (int attempt = 0; attempt < 12 && positions.Count > 0; attempt++)
        {
            cancellation.ThrowIfCancellationRequested();
            // Resolve the native footprint before checking the occupied map:
            // shoreline sprites would otherwise reject the initial plan before
            // we have a chance to leave an opening for them.
            var ordered = positions.OrderBy(p => p.Y).ThenBy(p => p.X).ToArray();
            var wall = WallBrush.PlanStroke(empty, family, ordered, variationSeed: seed);
            if (!wall.Allowed)
            {
                var joins = WallJoinClearance.Conflicts(family, positions);
                if (joins.Count == 0) return 0;
                // Trimming around a shore can leave sections touching only at
                // a vertex. Widen that opening so complete shoulders can fit.
                foreach (var join in joins)
                foreach (var vertex in new[] { (join.U, join.V), (join.OtherU, join.OtherV) })
                    for (int u = 0; u <= 1; u++) for (int v = 0; v <= 1; v++)
                        positions.Remove(family.GridPoint(vertex.Item1 + u, vertex.Item2 + v));
                continue;
            }
            if (!wall.HasChanges) return 0;
            var conflicts = wall.Replacements.Where(r => r.Cells.Any(c => map.Contains(r.X + c.X, r.Y + c.Y) &&
                coastReserved[(r.Y + c.Y) * map.Width + r.X + c.X])).ToArray();
            if (conflicts.Length == 0)
            {
                var final = WallBrush.PlanStroke(map, family, ordered, variationSeed: seed);
                return final.Allowed && map.ApplyWallPlan(final, new(), (_, _) => { }) ? layout.SideCount : 0;
            }
            // Rebuild complete shoulders around water instead of clipping art.
            foreach (var conflict in conflicts)
                positions.RemoveWhere(p => Math.Abs(p.X - conflict.X) <= family.Step && Math.Abs(p.Y - conflict.Y) <= family.Step);
        }
        return 0;
    }
}
