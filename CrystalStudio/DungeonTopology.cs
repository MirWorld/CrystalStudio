namespace CrystalStudio;

internal static class DungeonTopology
{
    internal static IReadOnlyList<(int A, int B)> SelectLoops(int count, IReadOnlyList<(int A, int B)> backbone,
        IEnumerable<(int A, int B)> candidates, int budget, int seed, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        if (count is < 1 or > 64 || budget < 0) throw new ArgumentOutOfRangeException(nameof(count));
        var edges = backbone.Select(Normalize).ToHashSet();
        var available = candidates.Select(Normalize).Distinct().Where(edge => !edges.Contains(edge)).OrderBy(edge => edge.A).ThenBy(edge => edge.B).ToList();
        if (edges.Concat(available).Any(edge => edge.A < 0 || edge.B >= count || edge.A == edge.B)) throw new ArgumentException("Invalid dungeon edge.");
        var distances = Distances();
        if (Enumerable.Range(0, count * count).Any(index => distances[index / count, index % count] >= count))
            throw new ArgumentException("The dungeon backbone must be connected.");
        int start = 0, goal = 0;
        for (int first = 0; first < count; first++) for (int second = first + 1; second < count; second++)
            if (distances[first, second] > distances[start, goal]) { start = first; goal = second; }
        int minimumMain = (int)Math.Ceiling(distances[start, goal] * .75);
        var selected = new List<(int A, int B)>();
        while (selected.Count < budget && available.Count > 0)
        {
            cancellation.ThrowIfCancellationRequested();
            (int A, int B)? best = null; long bestBenefit = 0; ulong bestTie = ulong.MaxValue;
            foreach (var edge in available)
            {
                cancellation.ThrowIfCancellationRequested();
                int After(int first, int second) => Math.Min(distances[first, second], Math.Min(
                    distances[first, edge.A] + 1 + distances[edge.B, second], distances[first, edge.B] + 1 + distances[edge.A, second]));
                if (After(start, goal) < minimumMain || distances[edge.A, edge.B] < 3) continue;
                long benefit = 0;
                for (int first = 0; first < count; first++) for (int second = first + 1; second < count; second++)
                    benefit += distances[first, second] - After(first, second);
                ulong tie = GenerationRandom.Bits(seed, 0x2101, edge.A, edge.B);
                if (benefit < bestBenefit || benefit == bestBenefit && tie >= bestTie) continue;
                best = edge; bestBenefit = benefit; bestTie = tie;
            }
            if (best == null || bestBenefit == 0) break;
            selected.Add(best.Value); edges.Add(best.Value); available.Remove(best.Value); distances = Distances();
        }
        return selected;

        int[,] Distances()
        {
            var result = new int[count, count];
            for (int first = 0; first < count; first++) for (int second = 0; second < count; second++) result[first, second] = first == second ? 0 : count;
            foreach (var edge in edges) result[edge.A, edge.B] = result[edge.B, edge.A] = 1;
            for (int middle = 0; middle < count; middle++)
            {
                cancellation.ThrowIfCancellationRequested();
                for (int first = 0; first < count; first++) for (int second = 0; second < count; second++)
                    result[first, second] = Math.Min(result[first, second], result[first, middle] + result[middle, second]);
            }
            return result;
        }
        static (int A, int B) Normalize((int A, int B) edge) => edge.A < edge.B ? edge : (edge.B, edge.A);
    }
}
