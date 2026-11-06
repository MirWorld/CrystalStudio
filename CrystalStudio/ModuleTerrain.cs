namespace CrystalStudio;

// Module editing and generation use four neighbours and both collision layers.
internal static class ModuleTerrain
{
    internal static bool Blocked(Cell cell) => (cell.BackImage & 0x60000000) != 0 ||
        cell.Foreground is { } front && (front.Image & 0x8000) != 0;

    internal static (int? X, int? Y) GroundParity(JoinModule module)
    {
        int? x = null, y = null;
        foreach (var cell in module.Cells)
        {
            if ((cell.Cell.BackImage & 0x1FFFFFFF) == 0) continue;
            // Explicit footprints are captured in map coordinates and CropStamp only
            // translates them by even offsets. Preserve *all* references, including
            // legacy references on non-rendered cells, by retaining that grid phase.
            if (module.Connections.MapGroundGrid) return (0, 0);
            if (x.HasValue && (x != (cell.X & 1) || y != (cell.Y & 1)))
            {
                // Captures made before the explicit flag can still contain legacy
                // references in several phases. Their footprint retains map parity.
                if (module.Connections.Footprint != null) return (0, 0);
                throw ModuleDiagnostics.Error("ground-phase", LocalText.T("Ground references use mixed grid phases without a captured footprint. Capture this module from the map again."), x: cell.X, y: cell.Y);
            }
            x = cell.X & 1; y = cell.Y & 1;
        }
        return (x, y);
    }

    internal static ModuleTerrainAnalysis Analyze(MapDocument source, CancellationToken token = default)
    {
        var labels = new int[checked(source.Width * source.Height)];
        for (int y = 0; y < source.Height; y++)
        {
            token.ThrowIfCancellationRequested();
            for (int x = 0; x < source.Width; x++)
                if (Blocked(source.Get(x, y))) labels[y * source.Width + x] = -1;
        }
        var regions = new List<ModuleTerrainRegion>(); var queue = new int[labels.Length];
        for (int first = 0; first < labels.Length; first++)
        {
            if (labels[first] != 0) continue;
            int id = regions.Count + 1, read = 0, write = 1;
            int left = source.Width, top = source.Height, right = 0, bottom = 0;
            queue[0] = first; labels[first] = id;
            void Visit(int n) { if (labels[n] == 0) { labels[n] = id; queue[write++] = n; } }
            while (read < write)
            {
                if ((read & 4095) == 0) token.ThrowIfCancellationRequested();
                int n = queue[read++], x = n % source.Width;
                int y = n / source.Width;
                left = Math.Min(left, x); top = Math.Min(top, y); right = Math.Max(right, x); bottom = Math.Max(bottom, y);
                if (x > 0) Visit(n - 1);
                if (x + 1 < source.Width) Visit(n + 1);
                if (n >= source.Width) Visit(n - source.Width);
                if (n + source.Width < labels.Length) Visit(n + source.Width);
            }
            regions.Add(new(id, write, first % source.Width, first / source.Width, new(left, top, right - left + 1, bottom - top + 1)));
        }
        // Manhattan distance to collision or the outside edge; this is a ranking
        // hint only. Complete four-neighbour validation still decides validity.
        var clearance = new int[labels.Length];
        for (int y = 0; y < source.Height; y++)
        {
            token.ThrowIfCancellationRequested();
            for (int x = 0; x < source.Width; x++)
            {
                int n = y * source.Width + x;
                if (labels[n] > 0) clearance[n] = Math.Min(x == 0 ? 0 : clearance[n - 1], y == 0 ? 0 : clearance[n - source.Width]) + 1;
            }
        }
        for (int y = source.Height - 1; y >= 0; y--)
        {
            token.ThrowIfCancellationRequested();
            for (int x = source.Width - 1; x >= 0; x--)
            {
                int n = y * source.Width + x;
                if (labels[n] > 0) clearance[n] = Math.Min(clearance[n], Math.Min(x + 1 == source.Width ? 0 : clearance[n + 1], y + 1 == source.Height ? 0 : clearance[n + source.Width]) + 1);
            }
        }
        return new(source.Width, source.Height, labels, regions, clearance);
    }
}

internal sealed record ModuleTerrainRegion(int Id, int Count, int X, int Y, ModuleArea Area)
{
    public override string ToString() => LocalText.Format("Region {0} · {1} cells", Id, Count);
}
internal sealed record ModuleTerrainAnalysis(int Width, int Height, int[] Labels, IReadOnlyList<ModuleTerrainRegion> Regions, int[] Clearance)
{
    internal int MainId => Regions.MaxBy(r => r.Count)?.Id ?? 0;
}
