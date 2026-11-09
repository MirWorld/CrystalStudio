namespace CrystalStudio;

internal static class MapCandidateSelection
{
    internal static bool Dominates(MapCandidateMetrics first, MapCandidateMetrics second) =>
        first.PreferredConflicts <= second.PreferredConflicts && first.ReachableRatio + 1e-9 >= second.ReachableRatio && first.ReachableLegRatio + 1e-9 >= second.ReachableLegRatio &&
        (first.PreferredConflicts < second.PreferredConflicts || first.ReachableRatio > second.ReachableRatio + 1e-9 || first.ReachableLegRatio > second.ReachableLegRatio + 1e-9);

    internal static int Replacement(IReadOnlyList<MapCandidateMetrics> existing, IReadOnlyList<bool> favorites, MapCandidateMetrics incoming)
    {
        if (existing.Count != favorites.Count || existing.Count == 0) throw new ArgumentException("Candidate flags do not match metrics.");
        int[] editable = Enumerable.Range(0, existing.Count).Where(index => !favorites[index]).ToArray();
        if (editable.Length == 0 || existing.Any(item => Dominates(item, incoming))) return -1;
        int[] dominated = editable.Where(index => Dominates(incoming, existing[index])).ToArray();
        if (dominated.Length > 0) return dominated.OrderByDescending(index => existing[index].PreferredConflicts).ThenBy(index => existing[index].ReachableRatio).ThenBy(index => index).First();
        double best = Diversity(existing); int victim = -1;
        foreach (int index in editable)
        {
            var changed = existing.ToArray(); changed[index] = incoming; double score = Diversity(changed);
            if (score <= best + 1e-9) continue;
            best = score; victim = index;
        }
        return victim;
    }

    internal static double Diversity(IReadOnlyList<MapCandidateMetrics> metrics)
    {
        if (metrics.Count < 2) return 0;
        double score = 0;
        for (int first = 0; first < metrics.Count; first++)
        {
            double nearest = double.PositiveInfinity;
            for (int second = 0; second < metrics.Count; second++) if (first != second) nearest = Math.Min(nearest, Distance(metrics[first], metrics[second]));
            score += nearest;
        }
        return score / metrics.Count;
    }

    private static double Distance(MapCandidateMetrics first, MapCandidateMetrics second)
    {
        var left = Describe(first); var right = Describe(second); double distance = 0;
        for (int index = 0; index < left.Length; index++) distance += Math.Abs(left[index] - right[index]);
        return distance / left.Length;
    }

    private static double[] Describe(MapCandidateMetrics metrics)
    {
        double length = Math.Max(1, Math.Sqrt(metrics.TotalCells));
        return [Math.Min(1, Math.Log(1 + (metrics.RouteSteps ?? 0) / length) / Math.Log(9)),
            Math.Min(1, Math.Log(1 + metrics.MeanBranchDepth) / Math.Log(1 + length)),
            Math.Min(1, Math.Log(1 + metrics.MeanClearance) / Math.Log(1 + length)),
            metrics.WalkableRatio, Math.Min(1, metrics.WalkableCells == 0 ? 0 : 100.0 * metrics.DeadEnds / metrics.WalkableCells), Math.Min(1, (metrics.Rooms ?? 0) / 30.0)];
    }
}
