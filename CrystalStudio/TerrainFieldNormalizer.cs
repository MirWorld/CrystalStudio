using System.IO;

namespace CrystalStudio;

internal static class TerrainFieldNormalizer
{
    internal static int Normalize(bool[] field, int width, int height, bool fill, Func<int, int, bool> allowed,
        int seed, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        if (width < 1 || height < 1 || field.Length != checked((width + 1) * (height + 1))) throw new ArgumentException("Invalid terrain vertex field.");
        int stride = width + 1, changed = 0;
        var queued = new bool[width * height]; var pending = new Queue<int>();
        for (int tile = 0; tile < queued.Length; tile++) Enqueue(tile);
        while (pending.TryDequeue(out int tile))
        {
            cancellation.ThrowIfCancellationRequested(); queued[tile] = false;
            int x = tile % width, y = tile / width;
            if (Mask(x, y) is not (6 or 9)) continue;
            int best = -1, bestConflicts = int.MaxValue; ulong bestTie = ulong.MaxValue;
            foreach (int vertex in new[] { y * stride + x, y * stride + x + 1, (y + 1) * stride + x, (y + 1) * stride + x + 1 })
            {
                int vx = vertex % stride, vy = vertex / stride;
                if (field[vertex] == fill || fill && !allowed(vx, vy)) continue;
                field[vertex] = fill;
                int conflicts = 0;
                for (int row = Math.Max(0, vy - 1); row <= Math.Min(height - 1, vy); row++)
                for (int column = Math.Max(0, vx - 1); column <= Math.Min(width - 1, vx); column++)
                    if (Mask(column, row) is 6 or 9) conflicts++;
                field[vertex] = !fill;
                ulong tie = GenerationRandom.Bits(seed, fill ? 0x3101 : 0x3102, vx, vy);
                if (conflicts > bestConflicts || conflicts == bestConflicts && tie >= bestTie) continue;
                best = vertex; bestConflicts = conflicts; bestTie = tie;
            }
            if (best < 0) throw new InvalidDataException("A native terrain corner cannot be resolved inside the permitted area.");
            field[best] = fill; changed++;
            int bestX = best % stride, bestY = best / stride;
            for (int row = Math.Max(0, bestY - 1); row <= Math.Min(height - 1, bestY); row++)
            for (int column = Math.Max(0, bestX - 1); column <= Math.Min(width - 1, bestX); column++) Enqueue(row * width + column);
        }
        return changed;

        int Mask(int x, int y) => (field[y * stride + x] ? 1 : 0) | (field[y * stride + x + 1] ? 2 : 0) |
            (field[(y + 1) * stride + x] ? 4 : 0) | (field[(y + 1) * stride + x + 1] ? 8 : 0);
        void Enqueue(int tile)
        {
            if (queued[tile]) return;
            queued[tile] = true; pending.Enqueue(tile);
        }
    }
}
