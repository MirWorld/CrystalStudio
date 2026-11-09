namespace CrystalStudio;

internal sealed record GenerationImpact(int Width, int Height, int Affected, byte[] Coverage, string Description)
{
    internal const int Columns = 160, Rows = 100;

    internal static GenerationImpact Compare(MapDocument source, MapDocument generated, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        var coverage = new byte[Columns * Rows];
        int count = 0;
        void Check(int key, Cell original, Cell replacement)
        {
            cancellation.ThrowIfCancellationRequested();
            if (original.SameContents(replacement)) return;
            count++; Mark(coverage, key % source.Width, key / source.Width, source.Width, source.Height);
        }
        foreach (var (key, original) in source.Cells) Check(key, original, generated.Get(key % source.Width, key / source.Width));
        foreach (var (key, replacement) in generated.Cells)
        {
            var original = source.Get(key % source.Width, key / source.Width);
            if (original.Empty) Check(key, original, replacement);
        }
        return new(source.Width, source.Height, count, coverage,
            LocalText.Format("{0:N0} changed cells. Ground, middle, objects and collisions use the generated map. Stamp instances: {1:N0} → {2:N0}; instance contents also follow the generated map. Unchanged cells stay unchanged.", count, source.PlacedStamps.Count, generated.PlacedStamps.Count));
    }

    internal static GenerationImpact Terrain(TerrainPlan plan, CancellationToken cancellation = default)
    {
        var coverage = new byte[Columns * Rows];
        foreach (var tile in plan.Tiles)
        {
            cancellation.ThrowIfCancellationRequested();
            for (int row = 0; row < 2; row++) for (int column = 0; column < 2; column++) Mark(coverage, tile.X + column, tile.Y + row, plan.Source.Width, plan.Source.Height);
        }
        foreach (var bank in plan.Water?.Banks ?? []) Mark(coverage, bank.X, bank.Y, plan.Source.Width, plan.Source.Height);
        foreach (var coast in plan.CoastObjects)
            foreach (var cell in coast.Layout.Cells) Mark(coverage, coast.X + cell.X, coast.Y + cell.Y, plan.Source.Width, plan.Source.Height);
        return new(plan.Source.Width, plan.Source.Height, plan.Tiles.Count, coverage,
            LocalText.Format("Planned ground anchors: {0:N0} · Bank anchors: {1:N0} · Coast objects: {2:N0}. Ground is regenerated; generated banks and coast objects in the target area may be replaced. Other layers follow the existing terrain rules.",
                plan.Tiles.Count, plan.Water?.Banks.Count ?? 0, plan.CoastObjects.Count));
    }

    private static void Mark(byte[] coverage, int x, int y, int width, int height)
    {
        if (x < 0 || y < 0 || x >= width || y >= height) return;
        coverage[Math.Min(Rows - 1, (int)((long)y * Rows / height)) * Columns + Math.Min(Columns - 1, (int)((long)x * Columns / width))] = 1;
    }

    internal RasterImage CreateMap()
    {
        var pixels = new byte[Columns * Rows * 4];
        for (int index = 0; index < Coverage.Length; index++)
        {
            bool affected = Coverage[index] != 0;
            pixels[index * 4] = affected ? (byte)45 : (byte)48;
            pixels[index * 4 + 1] = affected ? (byte)150 : (byte)48;
            pixels[index * 4 + 2] = affected ? (byte)245 : (byte)48;
            pixels[index * 4 + 3] = 255;
        }
        return RasterImage.Create(Columns, Rows, 96, 96, RasterFormats.Bgra32, null, pixels, Columns * 4);
    }
}
