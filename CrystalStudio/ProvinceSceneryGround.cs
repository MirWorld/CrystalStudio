namespace CrystalStudio;

public sealed class ProvinceSceneryGround
{
    private readonly Dictionary<(int Library, int Image), HashSet<string>> solid = [];

    public ProvinceSceneryGround(TerrainCatalogue terrain)
    {
        // Different catalogue sets can contain identical full-ground artwork
        // (fine grass 1050/1450). Compare reviewed pixel hashes, not materials.
        var hashes = new Dictionary<string, HashSet<string>>();
        foreach (var set in terrain.Sets)
        foreach (var variant in set.Shapes[15])
        {
            if (!hashes.TryGetValue(variant.PixelSha256, out var sets)) hashes[variant.PixelSha256] = sets = [];
            sets.Add(set.Id);
        }
        foreach (var set in terrain.Sets)
        foreach (var variant in set.Shapes[15])
            solid[(set.Library, variant.Index + 1)] = hashes[variant.PixelSha256];
    }

    public bool CanPlace(MapDocument map, BrushTree tree, int x, int y)
        => CanPlace(map, tree.Cells, tree.ProvinceGroundSetId, x, y);

    internal bool CanPlace(MapDocument map, IReadOnlyList<StampCell> cells, string? setId, int x, int y)
    {
        if (setId == null) return true;
        if (setId == "unverified") return false;
        // Check the final map after coast, cliff and town terrain edits.
        // Background cards cover two by two cells; transition cards are not
        // solid matching terrain and cannot hide a rectangular baked base.
        int left = x + cells.Min(c => c.X), right = x + cells.Max(c => c.X);
        int top = y + cells.Min(c => c.Y), bottom = y + cells.Max(c => c.Y);
        for (int yy = Math.Max(0, top) & ~1; yy <= Math.Min(map.Height - 1, bottom); yy += 2)
        for (int xx = Math.Max(0, left) & ~1; xx <= Math.Min(map.Width - 1, right); xx += 2)
        {
            var cell = map.Get(xx, yy);
            if (!solid.TryGetValue((cell.BackLibrary, cell.BackImage & 0x1fffffff), out var actual) || !actual.Contains(setId))
                return false;
        }
        return true;
    }
}
