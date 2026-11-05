namespace CrystalStudio;

/// <summary>A continuous, irregular belt around the playable province.</summary>
public static class ProvinceBoundary
{
    public const int MinimumDepth = 10;
    public const int MaximumDepth = 15;
    private const int Blocked = 0x60000000;

    public static bool[] CreateMask(int width, int height, int seed)
    {
        var random = new Random(seed ^ 0x5B0ADE);
        int[] Profile(int length)
        {
            var knots = Enumerable.Range(0, length / 24 + 2).Select(_ => random.Next(MinimumDepth, MaximumDepth + 1)).ToArray();
            return Enumerable.Range(0, length).Select(i =>
            {
                double t = (i % 24) / 24.0;
                t = t * t * (3 - 2 * t);
                return (int)Math.Round(knots[i / 24] * (1 - t) + knots[i / 24 + 1] * t);
            }).ToArray();
        }
        var north = Profile(width); var south = Profile(width);
        var west = Profile(height); var east = Profile(height);
        var mask = new bool[width * height];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
            mask[y * width + x] = y < north[x] || height - 1 - y < south[x] ||
                x < west[y] || width - 1 - x < east[y];
        return mask;
    }

    public static int Plant(MapDocument map, bool[] boundary, IReadOnlyList<ProvinceScenery> scenery,
        ProvinceStyle style, int seed, CancellationToken cancellation,
        IReadOnlyList<bool>? widerSides = null, bool[]? settlementClearings = null, ProvinceSceneryGround? ground = null)
    {
        int width = map.Width, height = map.Height;
        var density = ForestDensity(width, height, seed, widerSides, style.Arid);
        var protectedCells = settlementClearings == null ? new bool[width * height] : (bool[])settlementClearings.Clone();
        // Native water and cliffs already provide the boundary. Keep their
        // artwork intact, but do not reserve paths through the perimeter.
        foreach (var stamp in map.PlacedStamps)
        foreach (var cell in stamp.Cells)
        {
            // Shore layouts also contain large areas of ordinary dry ground.
            // Trees may grow on that ground; reserve only the actual shore art
            // and water, otherwise a beach can leave a bare boundary strip.
            if (stamp.Wall == null && (cell.Cell.BackImage & Blocked) == 0 &&
                cell.Cell.ForegroundImages.Count == 0 && cell.Cell.MiddleImage == 0) continue;
            int x = stamp.X + cell.X, y = stamp.Y + cell.Y;
            for (int yy = Math.Max(0, y - 1); yy <= Math.Min(height - 1, y + 1); yy++)
            for (int xx = Math.Max(0, x - 1); xx <= Math.Min(width - 1, x + 1); xx++)
                protectedCells[yy * width + xx] = true;
        }
        var kind = style.Arid ? SpecialBrushKind.Rock : SpecialBrushKind.Tree;
        var trees = ProvinceSceneryPalette.Select(scenery, style, kind, boundary: true);
        if (trees.Length == 0) return 0;
        var random = new Random(seed ^ 0x7EEB0A);
        int count = 0;
        // Fill the protected belt and its irregular woodland fringe before
        // interior scenery. Offset passes close spaces in the dense core;
        // density falls away through the fringe into scattered small groves.
        int spacing = style.WallId == "mongchon-province" ? 2 : 3;
        int passes = style.WallId == "mongchon-province" ? 5 : 3;
        for (int pass = 0; pass < passes; pass++)
        {
            var history = new EditHistory();
            for (int sy = 0; sy < height; sy += spacing)
            {
                cancellation.ThrowIfCancellationRequested();
                for (int sx = 0; sx < width; sx += spacing)
                {
                    int x = sx + random.Next(spacing), y = sy + random.Next(spacing);
                    if (!map.Contains(x, y)) continue;
                    int key = y * width + x;
                    double chance = boundary[key] ? 1 : density[key];
                    if (chance <= 0 || random.NextDouble() >= chance * chance || protectedCells[key]) continue;
                    for (int attempt = 0; attempt < 4; attempt++)
                    {
                        var tree = trees[random.Next(trees.Length)];
                        if (ground != null && !ground.CanPlace(map, tree, x, y)) continue;
                        // Only the world edge may crop a stamp. Inside the map,
                        // retain complete trees and their original sprite anchors.
                        var cells = tree.Cells.Where(c => map.Contains(x + c.X, y + c.Y)).ToArray();
                        if (!cells.Any(c => c.Cell.ForegroundImages.Count > 0) ||
                            cells.Any(c => protectedCells[(y + c.Y) * width + x + c.X]) ||
                            !map.CanPlaceStamp(cells, x, y)) continue;
                        if (map.PlaceStamp(cells, x, y, tree.Name, history, (_, _) => { }) == null) continue;
                        count++;
                        break;
                    }
                }
            }
        }
        return count;
    }

    private static float[] ForestDensity(int width, int height, int seed, IReadOnlyList<bool>? widerSides, bool arid)
    {
        var random = new Random(seed ^ 0xC0A57);
        double size = Math.Min(width, height);
        double amplitude = Math.Clamp(size * .075, 10, 26) * (arid ? .4 : 1), feather = Math.Clamp(size * .035, 6, 14) * (arid ? .5 : 1);
        double[] Profile(int length, int side)
        {
            var values = new double[length];
            bool high = random.Next(2) == 0;
            double NextDepth()
            {
                high = !high;
                double depth = 12 + amplitude * (high ? .55 + random.NextDouble() * .45 : random.NextDouble() * .28);
                if (widerSides?[side] == true) depth += Math.Clamp(size * .055, 7, 21) * (.5 + random.NextDouble()) * (arid ? .3 : 1);
                return Math.Min(size * .28, depth);
            }
            int start = -random.Next(8, 24);
            double from = NextDepth();
            while (start < length)
            {
                int end = start + random.Next(16, 47);
                double to = NextDepth();
                for (int i = Math.Max(0, start); i < Math.Min(length, end); i++)
                {
                    double t = (double)(i - start) / (end - start);
                    t = t * t * (3 - 2 * t);
                    values[i] = from + (to - from) * t;
                }
                start = end; from = to;
            }
            return values;
        }
        var north = Profile(width, 0); var east = Profile(height, 1);
        var south = Profile(width, 2); var west = Profile(height, 3);
        // Blend adjoining woodland fronts into curved corners instead of
        // intersecting four straight strips at a square inside corner.
        static double Join(double a, double b)
        {
            double h = Math.Max(0, 12 - Math.Abs(a - b)) / 12;
            return Math.Max(a, b) + h * h * 3;
        }
        var result = new float[width * height];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            double edge = Join(Join(north[x] - y, south[x] - (height - 1 - y)),
                Join(west[y] - x, east[y] - (width - 1 - x)));
            double t = Math.Clamp((edge + feather) / feather, 0, 1);
            result[y * width + x] = (float)(t * t * (3 - 2 * t));
        }
        return result;
    }

    public static void Seal(MapDocument map, bool[] boundary, CancellationToken cancellation)
    {
        // A dense canopy is a visual boundary, but individual trunks do not
        // block every cell beneath it. Seal the entire belt, including corners,
        // shore seams and paths, using the same collision flags as native maps.
        for (int y = 0; y < map.Height; y++)
        {
            cancellation.ThrowIfCancellationRequested();
            for (int x = 0; x < map.Width; x++)
            {
                if (!boundary[y * map.Width + x]) continue;
                var cell = map.Get(x, y).Copy();
                cell.BackImage |= Blocked;
                map.Set(x, y, cell);
            }
        }
    }
}
