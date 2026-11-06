using System.IO;

namespace CrystalStudio;

internal static class ModulePartitions
{
    internal static bool Intersects(ModuleArea a, ModuleArea b) => a.X < b.X + b.Width && b.X < a.X + a.Width && a.Y < b.Y + b.Height && b.Y < a.Y + a.Height;
    internal static bool Contains(ModuleArea a, int x, int y) => x >= a.X && y >= a.Y && x < a.X + a.Width && y < a.Y + a.Height;
    internal static string Key(ModuleArea a) => $"{a.X}:{a.Y}:{a.Width}:{a.Height}";

    internal static (ModuleArea A, ModuleArea B) Cut(ModuleArea area, bool vertical, int position, IReadOnlyList<ModuleArea>? protectedAreas = null)
    {
        int origin = vertical ? area.X : area.Y, length = vertical ? area.Width : area.Height;
        if ((position & 1) != 0 || position - origin < 2 || origin + length - position < 2)
            throw new InvalidDataException(LocalText.T("Cuts must preserve the two-cell ground grid and leave at least two cells on each side."));
        if (protectedAreas?.Any(p => Intersects(area, p) && (vertical
            ? position > p.X && position < p.X + p.Width
            : position > p.Y && position < p.Y + p.Height)) == true)
            throw new InvalidDataException(LocalText.T("This cut crosses a protected region. Move the cut or remove the protection."));
        return vertical ? (area with { Width = position - area.X }, area with { X = position, Width = area.X + area.Width - position })
            : (area with { Height = position - area.Y }, area with { Y = position, Height = area.Y + area.Height - position });
    }

    internal static ModuleArea Merge(ModuleArea a, ModuleArea b)
    {
        bool vertical = a.Y == b.Y && a.Height == b.Height && (a.X + a.Width == b.X || b.X + b.Width == a.X);
        bool horizontal = a.X == b.X && a.Width == b.Width && (a.Y + a.Height == b.Y || b.Y + b.Height == a.Y);
        if (!vertical && !horizontal)
            throw new InvalidDataException(LocalText.T("Select two adjacent blocks whose union is a rectangle; no other block may be included."));
        return new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), vertical ? a.Width + b.Width : a.Width, horizontal ? a.Height + b.Height : a.Height);
    }

    internal static IReadOnlyList<ModuleArea> ObjectProtection(MapDocument map) => map.PlacedStamps.Where(p => p.Cells.Count > 0).Select(p =>
    {
        int left = p.Cells.Min(c => c.X) + p.X, top = p.Cells.Min(c => c.Y) + p.Y;
        return new ModuleArea(left, top, p.Cells.Max(c => c.X) + p.X - left + 1, p.Cells.Max(c => c.Y) + p.Y - top + 1);
    }).ToArray();
}

internal static class ModuleChannelSplit
{
    private sealed record Stats(int Count, int Largest, int Components);
    private sealed record Candidate(bool Vertical, int Position, double Score);

    internal static ModuleSplitResult Split(MapDocument source, int width, int height,
        IReadOnlyList<ModuleArea>? protection = null, CancellationToken token = default)
    {
        if (width is < 4 or > 512 || height is < 4 or > 512 || (width & 1) != 0 || (height & 1) != 0)
            throw new InvalidDataException(LocalText.T("Use even block dimensions from 4 to 512 cells."));
        ModuleArea whole = new(0, 0, source.Width, source.Height);
        ModuleMapSplit.ValidatePartition(source, [whole]);
        long revision = source.PickupRevision;
        var analysis = ModuleTerrain.Analyze(source, token);
        var protectedAreas = ModulePartitions.ObjectProtection(source).Concat(protection ?? []).ToArray();
        var visited = new int[analysis.Labels.Length]; var queue = new int[visited.Length]; int visitId = 0;
        bool Open(int x, int y) => source.Contains(x, y) && analysis.Labels[y * source.Width + x] > 0;
        var cache = new Dictionary<ModuleArea, Stats>();
        Stats Measure(ModuleArea area)
        {
            if (cache.TryGetValue(area, out var known)) return known;
            int stamp = ++visitId, count = 0, largest = 0, components = 0;
            for (int y = area.Y; y < area.Y + area.Height; y++)
            {
                token.ThrowIfCancellationRequested();
                for (int x = area.X; x < area.X + area.Width; x++)
                {
                    int first = y * source.Width + x;
                    if (analysis.Labels[first] <= 0 || visited[first] == stamp) continue;
                    int read = 0, write = 1; queue[0] = first; visited[first] = stamp;
                    void Visit(int n) { if (analysis.Labels[n] > 0 && visited[n] != stamp) { visited[n] = stamp; queue[write++] = n; } }
                    while (read < write)
                    {
                        if ((read & 4095) == 0) token.ThrowIfCancellationRequested();
                        int n = queue[read++], cx = n % source.Width, cy = n / source.Width;
                        if (cx > area.X) Visit(n - 1);
                        if (cx + 1 < area.X + area.Width) Visit(n + 1);
                        if (cy > area.Y) Visit(n - source.Width);
                        if (cy + 1 < area.Y + area.Height) Visit(n + source.Width);
                    }
                    components++; count += write; largest = Math.Max(largest, write);
                }
            }
            var result = new Stats(count, largest, components);
            if (cache.Count < 12000) cache[area] = result;
            return result;
        }
        double Seam(ModuleArea a, bool vertical, int at)
        {
            int start = vertical ? a.Y : a.X, length = vertical ? a.Height : a.Width;
            int open = 0, runs = 0, unstable = 0, widest = 0; bool previous = false;
            for (int n = start; n < start + length; n++)
            {
                bool Near(int offset) => vertical ? Open(at + offset, n) : Open(n, at + offset);
                bool crossing = Near(-1) && Near(0), safe = crossing && Near(-2) && Near(1);
                if (crossing && (!safe || n == start || n == start + length - 1)) unstable++;
                if (safe)
                {
                    open++; if (!previous) runs++;
                    widest = Math.Max(widest, analysis.Clearance[(vertical ? n : at) * source.Width + (vertical ? at : n)]);
                }
                previous = safe;
            }
            // Narrow stable passages outrank wide chambers and multiple nearby corridors.
            return open + runs * 12 + unstable * 80 + widest * 2;
        }
        var leaves = new List<ModuleArea>(); var pending = new Stack<(ModuleArea Area, int Depth)>(); pending.Push((whole, 0));
        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var (area, depth) = pending.Pop(); var stats = Measure(area);
            if (stats.Count == 0 || (stats.Components == 1 && area.Width <= width && area.Height <= height) ||
                depth >= 16 || leaves.Count + pending.Count >= 255)
            { leaves.Add(area); continue; }
            var candidates = new List<Candidate>();
            foreach (bool vertical in new[] { true, false })
            {
                int origin = vertical ? area.X : area.Y, length = vertical ? area.Width : area.Height;
                int step = Math.Max(2, (length / 48) & ~1);
                for (int at = origin + 4; at <= origin + length - 4; at += step)
                {
                    token.ThrowIfCancellationRequested();
                    try { ModulePartitions.Cut(area, vertical, at, protectedAreas); }
                    catch (InvalidDataException) { continue; }
                    double balance = Math.Abs(at - origin - length / 2.0) / length;
                    candidates.Add(new(vertical, at, Seam(area, vertical, at) + balance * 30));
                }
            }
            Candidate? best = null; double bestScore = double.MaxValue;
            foreach (var candidate in candidates.OrderBy(c => c.Score).Take(16))
            {
                var (a, b) = ModulePartitions.Cut(area, candidate.Vertical, candidate.Position);
                var sa = Measure(a); var sb = Measure(b);
                double overflow = Math.Max(0, a.Width - width) + Math.Max(0, a.Height - height) + Math.Max(0, b.Width - width) + Math.Max(0, b.Height - height);
                double score = (sa.Count - sa.Largest + sb.Count - sb.Largest) * 8 +
                    (Math.Max(0, sa.Components - 1) + Math.Max(0, sb.Components - 1)) * 160 + candidate.Score + overflow * 1.5;
                // Avoid repeatedly trimming tiny empty strips from an otherwise connected room.
                if ((sa.Count == 0 || sb.Count == 0) && stats.Components == 1) score += 100;
                if (score < bestScore) { bestScore = score; best = candidate; }
            }
            if (best == null) { leaves.Add(area); continue; }
            var split = ModulePartitions.Cut(area, best.Vertical, best.Position);
            pending.Push((split.B, depth + 1)); pending.Push((split.A, depth + 1));
        }
        token.ThrowIfCancellationRequested();
        if (source.PickupRevision != revision)
            throw new InvalidOperationException(LocalText.T("The map changed after the module preview. Generate a fresh preview."));
        return ModuleMapSplit.Build(source, leaves.OrderBy(a => a.Y).ThenBy(a => a.X).ToArray(), token);
    }
}
