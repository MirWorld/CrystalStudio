using System.IO;

namespace CrystalStudio;

public sealed record TreeBrushOptions(int Density = 35, int Spacing = 3, bool Round = false,
    bool Collisions = true, int Seed = 1729, bool RandomFromType = true, string TreeType = "All trees", bool UseCuratedList = false)
{
    public TreeBrushOptions Normalize() => this with { Density = Math.Clamp(Density, 0, 100), Spacing = Math.Clamp(Spacing, 1, 12) };
}

public sealed record BrushTree(string Name, string Path, IReadOnlyList<StampCell> Cells)
{
    internal string? ProvinceGroundSetId { get; init; }
    public static BrushTree Load(string name, string path, AssetStore assets)
        => FromCells(name, path, CellCodec.LoadStamp(path), assets);

    public static BrushTree FromCells(string name, string path, IEnumerable<StampCell> source, AssetStore? assets = null)
    {
        // Tree stamps may include terrain from their source map. Scatter only their objects and collision marks.
        var raw = source.ToList();
        if (raw.Count > 10_000 || raw.Any(s => s == null || s.Cell == null))
            throw new InvalidDataException($"{name}: invalid object cells.");
        var cells = raw.Select(s => new StampCell(s.X, s.Y,
            new Cell { BackImage = s.Cell.BackImage & 0x60000000,
                Foreground = s.Cell.Foreground is { } front && (front.Image & 0x7FFF) > 0 ? front : null }))
            .Where(s => !s.Cell.Empty).ToList();
        if (cells.Count == 0 || cells.Count > 10_000 || !cells.Any(s => s.Cell.ForegroundImages.Count > 0))
            throw new InvalidDataException($"{name}: choose an object stamp containing foreground sprites (up to 10,000 cells).");
        if (cells.Select(s => (s.X, s.Y)).Distinct().Count() != cells.Count)
            throw new InvalidDataException($"{name}: this stamp cannot be saved in a Crystal version 1 map.");
        if (assets != null && cells.SelectMany(s => s.Cell.ForegroundImages).Any(f => !assets.Libraries.TryGetValue(f.Library, out var lib) || (f.Image & 0x7FFF) > lib.Count))
            throw new InvalidDataException($"{name}: required object sprites are missing. Load the matching libraries in Settings or choose another object.");
        // Centre the stamp's occupied cells under the scattered anchor, including stamps with negative coordinates.
        int cx = (int)(((long)cells.Min(s => s.X) + cells.Max(s => s.X)) / 2);
        int cy = (int)(((long)cells.Min(s => s.Y) + cells.Max(s => s.Y)) / 2);
        if (cells.Any(s => Math.Abs((long)s.X - cx) > 256 || Math.Abs((long)s.Y - cy) > 256))
            throw new InvalidDataException($"{name}: object footprint is too large to scatter.");
        return new(name, path, cells.Select(s => s with { X = s.X - cx, Y = s.Y - cy }).ToList());
    }
}

public sealed record TreePlacement(BrushTree Tree, int X, int Y, IReadOnlyList<StampCell> Cells);

public static class TreeBrush
{
    private static uint Hash(int x, int y, int seed)
    {
        uint h = unchecked((uint)x * 0x9E3779B9u ^ (uint)y * 0x85EBCA6Bu ^ (uint)seed);
        h ^= h >> 16; h *= 0x7FEB352Du; h ^= h >> 15; h *= 0x846CA68Bu;
        return h ^ (h >> 16);
    }

    public static IReadOnlyList<TreePlacement> Plan(MapDocument document, IReadOnlyList<BrushTree> trees,
        TreeBrushOptions options, int size, int x, int y)
    {
        if (trees.Count == 0) return [];
        options = options.Normalize(); size = Math.Clamp(size | 1, 3, 31);
        int radius = size / 2, spacing = options.Spacing;
        var planned = new List<TreePlacement>();
        var reserved = new HashSet<(int X, int Y)>();
        // World-aligned sites keep density stable while dragging and make hover match painting.
        int left = Math.Max(0, x - radius), top = Math.Max(0, y - radius);
        int right = Math.Min(document.Width - 1, x + radius), bottom = Math.Min(document.Height - 1, y + radius);
        for (int sy = top + (spacing - top % spacing) % spacing; sy <= bottom; sy += spacing)
        for (int sx = left + (spacing - left % spacing) % spacing; sx <= right; sx += spacing)
        {
            if (options.Round && (long)(sx - x) * (sx - x) + (long)(sy - y) * (sy - y) > (radius + .5) * (radius + .5)) continue;
            uint hash = Hash(sx, sy, options.Seed);
            if (hash % 100 >= options.Density) continue;
            var tree = trees[(int)(Hash(sx, sy, options.Seed ^ 0x51ED270B) % (uint)trees.Count)];
            IReadOnlyList<StampCell> cells = options.Collisions ? tree.Cells : tree.Cells
                .Where(s => s.Cell.ForegroundImages.Count > 0).Select(s => s with { Cell = new Cell { Foreground = s.Cell.Foreground } }).ToList();
            if (cells.Count == 0 || cells.Any(s => reserved.Contains((sx + s.X, sy + s.Y))) ||
                !document.CanPlaceStamp(cells, sx, sy)) continue;
            planned.Add(new(tree, sx, sy, cells));
            foreach (var cell in cells) reserved.Add((sx + cell.X, sy + cell.Y));
        }
        return planned;
    }
}
