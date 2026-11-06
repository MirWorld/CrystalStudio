namespace CrystalStudio;

public sealed partial class MapDocument
{
    private readonly int[] layerCounts = new int[5];
    internal int LayerCount(MapLayer layer) => layerCounts[(int)layer];

    private void UpdateLayerCounts(int x, int y, Cell? previous, Cell next)
    {
        int before = LayerMask(previous), after = LayerMask(next);
        for (int layer = 0; layer < layerCounts.Length; layer++)
            layerCounts[layer] += ((after >> layer) & 1) - ((before >> layer) & 1);

        int LayerMask(Cell? cell)
        {
            if (cell == null) return 0;
            // Ground sprites occupy 2x2 cells and are drawn only at even anchors.
            int mask = (x & 1) == 0 && (y & 1) == 0 && (cell.BackImage & 0x1FFFFFFF) != 0 ? 1 : 0;
            if (cell.MiddleImage != 0) mask |= 2;
            if (cell.HasForeground) mask |= 4;
            if ((cell.BackImage & 0x20000000) != 0) mask |= 8;
            if ((cell.BackImage & 0x40000000) != 0) mask |= 16;
            return mask;
        }
    }
}
