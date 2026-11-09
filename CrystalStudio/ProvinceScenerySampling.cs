namespace CrystalStudio;

internal sealed class ScenerySpacing
{
    private readonly Dictionary<(int X, int Y), List<(double X, double Y, double Radius)>> buckets = [];
    private double maximumRadius;

    internal bool Fits(double x, double y, double radius)
    {
        int left = (int)Math.Floor((x - radius - maximumRadius) / 4), right = (int)Math.Floor((x + radius + maximumRadius) / 4);
        int top = (int)Math.Floor((y - radius - maximumRadius) / 4), bottom = (int)Math.Floor((y + radius + maximumRadius) / 4);
        for (int row = top; row <= bottom; row++) for (int column = left; column <= right; column++)
            if (buckets.TryGetValue((column, row), out var points))
                foreach (var point in points)
                    if ((point.X - x) * (point.X - x) + (point.Y - y) * (point.Y - y) < (point.Radius + radius) * (point.Radius + radius)) return false;
        return true;
    }

    internal void Add(double x, double y, double radius)
    {
        var key = ((int)Math.Floor(x / 4), (int)Math.Floor(y / 4));
        if (!buckets.TryGetValue(key, out var points)) buckets[key] = points = [];
        points.Add((x, y, radius)); maximumRadius = Math.Max(maximumRadius, radius);
    }

    internal static (double X, double Y, double Radius) Footprint(BrushTree tree, double minimum)
    {
        var blocked = tree.Cells.Where(cell => (cell.Cell.BackImage & 0x60000000) != 0).ToArray();
        if (blocked.Length == 0) return (0, 0, minimum);
        int left = blocked.Min(cell => cell.X), right = blocked.Max(cell => cell.X);
        int top = blocked.Min(cell => cell.Y), bottom = blocked.Max(cell => cell.Y);
        double width = right - left + 1, height = bottom - top + 1;
        return ((left + right) / 2.0, (top + bottom) / 2.0, Math.Max(minimum, Math.Sqrt(width * width + height * height) / 2));
    }
}

internal static class ProvinceScenerySampling
{
    internal static IReadOnlyList<(int X, int Y)> Sites(int width, int height, int spacing, int seed, int domain, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (width < 1 || height < 1 || spacing < 1) throw new ArgumentOutOfRangeException(nameof(spacing));
        var sites = new List<(int X, int Y)>();
        for (int row = 0; row < height; row += spacing)
        {
            cancellation.ThrowIfCancellationRequested();
            for (int column = 0; column < width; column += spacing)
            {
                int x = column + GenerationRandom.Index(seed, domain, column, row, spacing);
                int y = row + GenerationRandom.Index(seed, domain, column, row, spacing, 1);
                if (x < width && y < height) sites.Add((x, y));
            }
        }
        var ordered = sites.OrderBy(site => GenerationRandom.Bits(seed, domain, site.X, site.Y, 4)).ThenBy(site => site.Y).ThenBy(site => site.X).ToArray();
        cancellation.ThrowIfCancellationRequested(); return ordered;
    }

    internal static int Place(MapDocument map, BrushTree[] pool, ProvinceSceneryGround ground, bool[] reserved,
        int spacing, int seed, int domain, Func<int, int, double> density, CancellationToken cancellation)
    {
        var candidates = pool.OrderBy(tree => tree.Name, StringComparer.Ordinal).ThenBy(tree => tree.Path, StringComparer.Ordinal).ToArray();
        var footprints = candidates.Distinct().ToDictionary(tree => tree, tree => ScenerySpacing.Footprint(tree, spacing * .4));
        var separation = new ScenerySpacing(); var history = new EditHistory(); int count = 0;
        foreach (var site in Sites(map.Width, map.Height, spacing, seed, domain, cancellation))
        {
            cancellation.ThrowIfCancellationRequested();
            int x = site.X, y = site.Y;
            if (x < 2 || y < 2 || x >= map.Width - 2 || y >= map.Height - 2 || reserved[y * map.Width + x] ||
                GenerationRandom.Unit(seed, domain, x, y, 2) >= density(x, y)) continue;
            var tree = candidates[GenerationRandom.Index(seed, domain, x, y, candidates.Length, 3)];
            var footprint = footprints[tree]; double cx = x + footprint.X, cy = y + footprint.Y;
            if (!separation.Fits(cx, cy, footprint.Radius) || !ground.CanPlace(map, tree, x, y)) continue;
            int left = x + tree.Cells.Min(cell => cell.X), right = x + tree.Cells.Max(cell => cell.X);
            int top = y + tree.Cells.Min(cell => cell.Y), bottom = y + tree.Cells.Max(cell => cell.Y);
            if (left < 1 || top < 1 || right >= map.Width - 1 || bottom >= map.Height - 1) continue;
            bool blocked = false;
            for (int row = top; row <= bottom && !blocked; row++) for (int column = left; column <= right; column++)
                if (reserved[row * map.Width + column]) { blocked = true; break; }
            if (blocked || tree.Cells.Any(cell =>
            {
                var existing = map.Get(x + cell.X, y + cell.Y);
                return (existing.BackImage & 0x60000000) != 0 || existing.HasForeground || existing.MiddleImage != 0 || (existing.Door & 0x7f) != 0;
            })) continue;
            if (map.PlaceStamp(tree.Cells, x, y, tree.Name, history, (_, _) => { }) == null) continue;
            separation.Add(cx, cy, footprint.Radius); count++;
        }
        return count;
    }
}
