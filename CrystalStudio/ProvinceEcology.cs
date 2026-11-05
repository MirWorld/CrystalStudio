namespace CrystalStudio;

internal static class ProvinceEcology
{
    // Low vegetation follows woodland margins, dry banks and trail shoulders.
    // It never repaints native banks or consumes the reserved walking lanes.
    internal static int PlantEdges(MapDocument map, TerrainCatalogue terrain, IReadOnlyList<ProvinceScenery> scenery,
        ProvinceStyle style, bool[] reserved, bool[] coast, IEnumerable<(int X, int Y)> roads,
        int seed, int vegetation, CancellationToken token)
    {
        if (vegetation == 0) return 0;
        var pool = ProvinceSceneryPalette.Select(scenery, style, SpecialBrushKind.Bush);
        if (pool.Length == 0) return 0;
        int width = map.Width, height = map.Height;
        var trees = ProvinceSceneryPalette.Select(scenery, style, SpecialBrushKind.Tree).Select(t => t.Name).ToHashSet();
        var canopy = Distances(map.PlacedStamps.Where(s => trees.Contains(s.Name))
            .SelectMany(s => s.Cells.Select(c => (X: s.X + c.X, Y: s.Y + c.Y))));
        var trails = Distances(roads);
        var banks = Distances(Enumerable.Range(0, coast.Length).Where(i => coast[i]).Select(i => (i % width, i / width)));
        var land = terrain.Sets.Where(s => s.Id == style.BaseId || s.Id == style.PatchId)
            .SelectMany(s => s.Shapes.Values.SelectMany(v => v).Select(v => (s.Library, Image: v.Index + 1))).ToHashSet();
        var ground = new ProvinceSceneryGround(terrain);
        var random = new Random(seed ^ 0xEC010);
        int count = 0;
        for (int sy = ProvinceBoundary.MaximumDepth + 2; sy < height - ProvinceBoundary.MaximumDepth - 2; sy += 3)
        {
            token.ThrowIfCancellationRequested();
            for (int sx = ProvinceBoundary.MaximumDepth + 2; sx < width - ProvinceBoundary.MaximumDepth - 2; sx += 3)
            {
                int x = sx + random.Next(3), y = sy + random.Next(3), key = y * width + x;
                double affinity = Math.Max(canopy[key] is >= 2 and <= 7 ? .38 : 0,
                    Math.Max(trails[key] is >= 4 and <= 8 ? .22 : 0, banks[key] is >= 1 and <= 7 ? .45 : 0));
                if (reserved[key] || random.NextDouble() >= affinity * vegetation / 100.0 * style.BushDensity) continue;
                var plant = pool[random.Next(pool.Length)];
                int left = x + plant.Cells.Min(c => c.X), right = x + plant.Cells.Max(c => c.X);
                int top = y + plant.Cells.Min(c => c.Y), bottom = y + plant.Cells.Max(c => c.Y);
                if (left < ProvinceBoundary.MaximumDepth || top < ProvinceBoundary.MaximumDepth ||
                    right >= width - ProvinceBoundary.MaximumDepth || bottom >= height - ProvinceBoundary.MaximumDepth ||
                    !ground.CanPlace(map, plant, x, y)) continue;
                bool fits = true;
                for (int yy = top; yy <= bottom && fits; yy++)
                for (int xx = left; xx <= right; xx++)
                {
                    var cell = map.Get(xx, yy);
                    var surface = map.Get(xx & ~1, yy & ~1);
                    if (reserved[yy * width + xx] || trails[yy * width + xx] < 4 || cell.HasForeground || cell.MiddleImage != 0 ||
                        (cell.BackImage & 0x60000000) != 0 || !land.Contains(((int)surface.BackLibrary, surface.BackImage & 0x1fffffff)))
                    { fits = false; break; }
                }
                if (!fits || map.PlaceStamp(plant.Cells, x, y, plant.Name, new(), (_, _) => { }) == null) continue;
                count++;
            }
        }
        return count;

        byte[] Distances(IEnumerable<(int X, int Y)> sources)
        {
            var distances = new byte[width * height]; Array.Fill(distances, byte.MaxValue);
            var queue = new Queue<int>();
            foreach (var p in sources)
            {
                token.ThrowIfCancellationRequested();
                if (!map.Contains(p.X, p.Y)) continue;
                int key = p.Y * width + p.X;
                if (distances[key] == 0) continue;
                distances[key] = 0; queue.Enqueue(key);
            }
            int visited = 0;
            while (queue.TryDequeue(out int key))
            {
                if ((visited++ & 4095) == 0) token.ThrowIfCancellationRequested();
                if (distances[key] >= 8) continue;
                int x = key % width, y = key / width;
                if (x > 0) Visit(key - 1); if (x + 1 < width) Visit(key + 1);
                if (y > 0) Visit(key - width); if (y + 1 < height) Visit(key + width);
                void Visit(int next)
                {
                    if (distances[next] <= distances[key] + 1) return;
                    distances[next] = (byte)(distances[key] + 1); queue.Enqueue(next);
                }
            }
            return distances;
        }
    }
}
