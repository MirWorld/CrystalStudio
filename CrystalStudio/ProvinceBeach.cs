namespace CrystalStudio;

internal static class ProvinceBeach
{
    // A complete closed native bank course can form a sheltered cove where
    // it intersects the actual west/east map edge. Only off-map cells are
    // cropped; every inland bank, cover and collision remains native.
    internal static TerrainCoastPlacement? Cove(MapDocument map, RiverCoastPieces banks, int seed, CancellationToken cancellation)
    {
        int width = Math.Min(420, map.Width * 2 / 3), height = Math.Min(420, map.Height * 2 / 3);
        var built = RiverWindingPlanCache.Build(banks, seed, width, height, out _, cancellation);
        if (built == null) return null;
        var ground = built.PreparedGround ?? RiverPieceGroundAssembler.Assemble(banks, built.Chain, seed, cancellation);
        var wet = ground.Cells.Where(c => c.Cell.BackLibrary == 0 && (c.Cell.BackImage & 0x1fffffff) is >= 1801 and <= 1805).ToArray();
        if (wet.Length < 30) return null;
        var random = new Random(seed ^ 0xBEAC4);
        bool west = random.Next(2) == 0;
        int cut = ((wet.Min(c => c.X) + wet.Max(c => c.X)) / 2) & ~1;
        int dx = west ? -cut : (map.Width - cut) & ~1;
        int dy = ((map.Height - ground.Height) / 2 - ground.Y + random.Next(-map.Height / 8, map.Height / 8 + 1)) & ~1;
        var cells = ground.Cells.Where(c => map.Contains(c.X + dx, c.Y + dy)).Select(c =>
        {
            var cell = c.Cell.Copy();
            if (!ground.RequiredGroundAnchors.Contains((c.X, c.Y))) cell.BackImage &= 0x60000000;
            return new StampCell(c.X + dx, c.Y + dy, cell);
        }).Where(c => !c.Cell.Empty).ToArray();
        var edgeWater = cells.Where(c => c.X == (west ? 0 : (map.Width - 1) & ~1) &&
            c.Cell.BackLibrary == 0 && (c.Cell.BackImage & 0x1fffffff) is >= 1801 and <= 1805).ToArray();
        if (edgeWater.Length < 8 || cells.Min(c => c.Y) < 18 || cells.Max(c => c.Y) >= map.Height - 18) return null;
        var layout = new RiverCoastLayout("province-native-cove", "Sheltered beach cove", cells)
        {
            Width = map.Width, Height = map.Height, WaterMode = TerrainWaterMode.Beach,
            BoundaryEdges = [west ? "west" : "east"]
        };
        var result = new TerrainCoastPlacement(layout, 0, 0) { DiagonalAccess = true };
        try { TerrainCoastObjects.VerifyAccess(map, result, cancellation); }
        catch (ArgumentException) { return null; }
        return result;
    }
}
