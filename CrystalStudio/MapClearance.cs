namespace CrystalStudio;

internal sealed class MapClearance(int width, int height, int[] sizes)
{
    internal int At(int x, int y) => x >= 0 && y >= 0 && x < width && y < height ? sizes[y * width + x] : 0;

    internal static MapClearance Build(MapDocument map, CancellationToken cancellation = default, bool[]? forbidden = null)
    {
        cancellation.ThrowIfCancellationRequested();
        int count = checked(map.Width * map.Height);
        if (forbidden != null && forbidden.Length != count) throw new ArgumentException("Invalid generation boundary mask.");
        if (count > MapRouteAnalysis.MaximumCells) throw new InvalidOperationException(LocalText.T("Route analysis supports up to 4,000,000 cells. Analyze a smaller map."));
        var sizes = new int[count];
        for (int row = map.Height - 1; row >= 0; row--)
        {
            cancellation.ThrowIfCancellationRequested();
            for (int column = map.Width - 1; column >= 0; column--)
            {
                int key = row * map.Width + column;
                if (forbidden?[key] == true || !MapRouteAnalysis.Walkable(map, column, row)) continue;
                sizes[key] = column + 1 == map.Width || row + 1 == map.Height ? 1 :
                    1 + Math.Min(sizes[key + 1], Math.Min(sizes[key + map.Width], sizes[key + map.Width + 1]));
            }
        }
        return new(map.Width, map.Height, sizes);
    }
}
