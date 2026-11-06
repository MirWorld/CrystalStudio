using System.IO;

namespace CrystalStudio;

public sealed record ProvinceRuin(int X, int Y, IReadOnlyList<(int X, int Y)> Approach,
    IReadOnlyList<TreePlacement> Objects);

public sealed class ProvinceRuinResources
{
    private IReadOnlyList<(string Role, BrushTree Tree)>? pieces;

    internal IReadOnlyList<(string Role, BrushTree Tree)> Load(AssetStore? assets, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (pieces != null) return pieces;
        var result = ProvinceObjectResources.Load([
            ("core", [6074, 6076, 6425, 6426]), ("wall", [3772, 3774, 3856, 4217, 4246]),
            ("statue", [154, 155, 156, 157]), ("debris", [1731, 1732, 6402, 6403, 6404]) ], assets, token);
        if (!result.Any(p => p.Role == "core") || !result.Any(p => p.Role == "wall"))
            throw new InvalidDataException("Forest ruin artwork is unavailable in the selected libraries.");
        return pieces = result;
    }
}

internal static class ProvinceRuins
{
    private const int Radius = 25;

    internal static IReadOnlyList<ProvinceRuin> Place(MapDocument map, TerrainCatalogue terrain, ProvinceStyle style,
        ProvinceOptions options, bool[] reserved, IEnumerable<(int X, int Y)> roads,
        ProvinceRuinResources resources, AssetStore? assets, List<string> notes, CancellationToken token)
    {
        var pieces = resources.Load(assets, token);
        if (pieces.Count < 18) notes.Add("Some optional forest ruin objects are unavailable; only complete visible objects were used.");
        return ProvinceClearings.Place(map, terrain, style, options.Seed, options.RuinCount, reserved, roads,
            "Forest ruin", Layout, notes, token).Select(s => new ProvinceRuin(s.X, s.Y, s.Approach, s.Objects)).ToArray();

        ProvinceClearingLayout Layout(Random random)
        {
            var objects = new List<TreePlacement>();
            foreach (var (role, dx, dy) in new[] { ("core", 0, -10), ("wall", -17, -1), ("wall", 17, -1),
                ("statue", -6, 9), ("statue", 6, 9), ("debris", -14, 16), ("debris", 14, 16) })
            {
                var pool = pieces.Where(p => p.Role == role).ToArray();
                if (pool.Length == 0) continue;
                var tree = pool[random.Next(pool.Length)].Tree;
                objects.Add(new(tree, dx, dy, tree.Cells));
            }
            return new(objects, Enumerable.Range(3, 24).Select(y => (X: 0, Y: y)).ToArray());
        }
    }

    internal static int PlantOvergrowth(MapDocument map, IReadOnlyList<ProvinceRuin> ruins,
        IReadOnlyList<ProvinceScenery> scenery, TerrainCatalogue terrain, ProvinceStyle style,
        bool[] reserved, ProvinceOptions options, CancellationToken token)
    {
        if (options.Vegetation == 0 || ruins.Count == 0) return 0;
        var shrubs = ProvinceSceneryPalette.Select(scenery, style, SpecialBrushKind.Bush)
            .Where(t => t.Cells.Max(c => c.X) - t.Cells.Min(c => c.X) <= 5 &&
                t.Cells.Max(c => c.Y) - t.Cells.Min(c => c.Y) <= 6).ToArray();
        if (shrubs.Length == 0) return 0;
        var ground = new ProvinceSceneryGround(terrain); var random = new Random(options.Seed ^ 0x0A34);
        var history = new EditHistory(); int count = 0;
        foreach (var ruin in ruins)
        {
            var forbidden = new HashSet<(int X, int Y)>();
            foreach (var p in ruin.Approach.Concat(ruin.Objects.SelectMany(o => o.Cells.Select(c => (X: o.X + c.X, Y: o.Y + c.Y)))))
                for (int dy = -2; dy <= 2; dy++) for (int dx = -2; dx <= 2; dx++) forbidden.Add((p.X + dx, p.Y + dy));
            // Small colonies around the remaining structure and rubble leave
            // the central entrance and every complete native object untouched.
            foreach (var obj in ruin.Objects)
            for (int attempt = 0; attempt < 8; attempt++)
            {
                token.ThrowIfCancellationRequested();
                if (random.Next(100) >= options.Vegetation) continue;
                int x = obj.X + random.Next(-8, 9), y = obj.Y + random.Next(-7, 8);
                var tree = shrubs[random.Next(shrubs.Length)];
                if (tree.Cells.Any(c => !map.Contains(x + c.X, y + c.Y) || Math.Abs(x + c.X - ruin.X) >= Radius ||
                    Math.Abs(y + c.Y - ruin.Y) >= Radius || forbidden.Contains((x + c.X, y + c.Y)) ||
                    (map.Get(x + c.X, y + c.Y).BackImage & 0x60000000) != 0 || map.Get(x + c.X, y + c.Y).MiddleImage != 0) ||
                    !ground.CanPlace(map, tree, x, y) || !map.CanPlaceStamp(tree.Cells, x, y)) continue;
                if (map.PlaceStamp(tree.Cells, x, y, tree.Name, history, (_, _) => { }) == null) continue;
                count++;
                foreach (var c in tree.Cells)
                {
                    reserved[(y + c.Y) * map.Width + x + c.X] = true;
                    for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++) forbidden.Add((x + c.X + dx, y + c.Y + dy));
                }
            }
        }
        return count;
    }
}
