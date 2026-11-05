using System.IO;

namespace CrystalStudio;

public sealed record ProvinceMine(int X, int Y, IReadOnlyList<(int X, int Y)> Approach,
    IReadOnlyList<TreePlacement> Objects, IReadOnlyList<(int X, int Y)> WorkPoints);

public sealed class ProvinceMineResources
{
    private IReadOnlyList<(string Role, BrushTree Tree)>? pieces;

    internal IReadOnlyList<(string Role, BrushTree Tree)> Load(AssetStore? assets, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (pieces != null) return pieces;
        var result = ProvinceObjectResources.Load([
            ("head", [1275]), ("face", [110, 6276, 6278]), ("spoil", [918, 919, 920, 921]),
            ("cart", [102, 103, 170, 171]), ("store", [817, 1166]), ("timber", [2035, 2037, 2038]),
            ("supply", [818, 975, 976]), ("bin", [977]) ], assets, token);
        if (new[] { "head", "face", "cart", "store", "timber", "bin" }.Any(role => !result.Any(p => p.Role == role)))
            throw new InvalidDataException(LocalText.T("Mining yard artwork is unavailable in the selected libraries."));
        return pieces = result;
    }
}

internal static class ProvinceMines
{
    // The endpoints serve the mine mouth, stone face, sorting bin and stores.
    private static readonly (int X, int Y)[] WorkPoints = [(-10, -4), (12, -4), (-20, -2), (-10, 18), (10, 18)];

    internal static IReadOnlyList<ProvinceMine> Place(MapDocument map, TerrainCatalogue terrain, ProvinceStyle style,
        ProvinceOptions options, bool[] reserved, IEnumerable<(int X, int Y)> roads,
        ProvinceMineResources resources, AssetStore? assets, List<string> notes, CancellationToken token)
    {
        var pieces = resources.Load(assets, token);
        if (pieces.Count < 21) notes.Add(LocalText.T("Some mining yard details are unavailable; only complete visible objects were used."));
        var sites = ProvinceClearings.Place(map, terrain, style, options.Seed ^ 0x61A3, options.MineCount, reserved, roads,
            "Mining yard", Layout, notes, token, laneRadius: 2, preferCliffs: true);
        return sites.Select(s => new ProvinceMine(s.X, s.Y, s.Approach, s.Objects,
            WorkPoints.Select(p => (X: s.X + p.X, Y: s.Y + p.Y)).ToArray())).ToArray();

        ProvinceClearingLayout Layout(Random random)
        {
            var objects = new List<TreePlacement>();
            // Fixed native orientations, with varied carts, stock and rock
            // groups. Working lanes separate extraction from dispatch/storage.
            foreach (var (role, x, y) in new[] {
                ("head", -10, -13), ("face", 12, -14), ("face", 19, -20), ("face", 5, -21), ("face", 20, -13),
                ("bin", -20, -8), ("cart", -17, 2), ("cart", 18, 1),
                ("store", -17, 18), ("store", -21, 20), ("store", -17, 22),
                ("timber", 17, 18), ("timber", 21, 21), ("timber", 17, 23), ("timber", 23, 23),
                ("supply", -21, 14), ("supply", 21, 14),
                ("spoil", 21, -7), ("spoil", 17, -14), ("spoil", 8, -16), ("spoil", 8, -10), ("spoil", -21, -18) })
            {
                var pool = pieces.Where(p => p.Role == role && (role != "cart" || x > 0 ||
                    p.Tree.Cells.Max(c => c.Y) - p.Tree.Cells.Min(c => c.Y) < 2)).ToArray();
                if (pool.Length == 0) continue;
                var tree = pool[random.Next(pool.Length)].Tree;
                objects.Add(new(tree, x, y, tree.Cells));
            }
            var paths = new HashSet<(int X, int Y)>();
            for (int y = 3; y <= 26; y++) paths.Add((0, y));
            for (int x = -20; x <= 20; x++) paths.Add((x, 8));
            for (int y = -4; y <= 18; y++) paths.Add((-10, y));
            for (int y = -4; y <= 8; y++) paths.Add((12, y));
            for (int y = 8; y <= 18; y++) paths.Add((10, y));
            for (int x = -20; x <= -10; x++) paths.Add((x, -2));
            return new(objects, paths.ToArray());
        }
    }
}
