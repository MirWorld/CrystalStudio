namespace CrystalStudio;

internal sealed record NativeLoopReport(int Trials, int AcceptedLoops, int ReferenceFrom, int ReferenceTo,
    int InitialMainSteps, int FinalMainSteps, long PairStepsSaved, int FootprintSize, bool BudgetExhausted, bool Verified);

internal static class NativeDungeonLoops
{
    internal const int MaximumTrials = 6;

    internal static TemplePlan Refine(TempleOptions options, Func<TempleOptions, TemplePlan> build, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        int footprint = options.Design?.FootprintSize ?? 1;
        var current = build(options with { LoopEdges = [] });
        var original = Distances(current.Generated, current.RoomCentres, footprint, cancellation);
        int count = current.RoomCentres.Count, start = 0, goal = 0; var distances = original;
        if (!Connected(original)) return Finish(0, 0, 0, false, false);
        for (int first = 0; first < count; first++) for (int second = first + 1; second < count; second++)
            if (original[first, second] > original[start, goal]) { start = first; goal = second; }
        int initialMain = count == 0 ? 0 : original[start, goal];
        int minimumMain = (int)Math.Ceiling(initialMain * .75);
        var accepted = new List<(int A, int B)>();
        var available = current.LoopCandidates.ToList(); int trials = 0;
        while (accepted.Count < current.LoopBudget && available.Count > 0 && trials < MaximumTrials)
        {
            cancellation.ThrowIfCancellationRequested();
            var next = available.OrderByDescending(Estimate).ThenBy(edge => GenerationRandom.Bits(options.Seed, 0x2201, edge.A, edge.B))
                .ThenBy(edge => edge.A).ThenBy(edge => edge.B).First();
            available.Remove(next); trials++;
            TemplePlan candidate;
            try { candidate = build(options with { LoopEdges = accepted.Append(next).ToArray() }); }
            catch (System.IO.InvalidDataException exception) when (GenerationDiagnostics.Describe(exception, "Native loop verification").Kind == GenerationFailureKind.SeedRejected) { continue; }
            if (candidate.RoomCentres.Count != count) continue;
            var after = Distances(candidate.Generated, candidate.RoomCentres, footprint, cancellation);
            if (!Connected(after) || after[start, goal] < minimumMain || Sum(after) >= Sum(distances)) continue;
            accepted.Add(next); current = candidate; distances = after;
        }
        return Finish(trials, accepted.Count, Sum(original) - Sum(distances), true,
            trials == MaximumTrials && available.Count > 0 && accepted.Count < current.LoopBudget);

        long Estimate((int A, int B) edge)
        {
            var from = current.RoomCentres[edge.A]; var to = current.RoomCentres[edge.B];
            int length = Math.Abs(from.X - to.X) + Math.Abs(from.Y - to.Y); long benefit = 0;
            for (int first = 0; first < count; first++) for (int second = first + 1; second < count; second++)
                benefit += distances[first, second] - Math.Min(distances[first, second], Math.Min(
                    distances[first, edge.A] + length + distances[edge.B, second], distances[first, edge.B] + length + distances[edge.A, second]));
            return benefit;
        }

        TemplePlan Finish(int attempted, int added, long saved, bool verified, bool exhausted)
        {
            int before = count == 0 || !verified ? 0 : original[start, goal];
            int after = count == 0 || !verified ? 0 : distances[start, goal];
            return current with { Options = options, LoopVerification = new(attempted, added, start, goal, before, after, saved, footprint, exhausted, verified) };
        }
    }

    internal static int[,] Distances(MapDocument map, IReadOnlyList<(int X, int Y)> points, int footprint, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        if (points.Count > 64 || footprint is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(points));
        var clearance = MapClearance.Build(map, cancellation); int cells = checked(map.Width * map.Height);
        var distance = new int[cells]; var queue = new int[cells]; var result = new int[points.Count, points.Count];
        for (int origin = 0; origin < points.Count; origin++)
        {
            cancellation.ThrowIfCancellationRequested(); Array.Fill(distance, -1);
            var point = points[origin]; int head = 0, tail = 0;
            if (clearance.At(point.X, point.Y) >= footprint) { int index = point.Y * map.Width + point.X; queue[tail++] = index; distance[index] = 0; }
            while (head < tail)
            {
                if ((head & 4095) == 0) cancellation.ThrowIfCancellationRequested();
                int current = queue[head++];
                foreach (int neighbour in MapRouteAnalysis.Neighbours(current, map.Width, map.Height))
                    if (distance[neighbour] < 0 && clearance.At(neighbour % map.Width, neighbour / map.Width) >= footprint)
                    { distance[neighbour] = distance[current] + 1; queue[tail++] = neighbour; }
            }
            for (int target = 0; target < points.Count; target++)
            {
                var endpoint = points[target];
                result[origin, target] = map.Contains(endpoint.X, endpoint.Y) ? distance[endpoint.Y * map.Width + endpoint.X] : -1;
            }
        }
        return result;
    }

    private static bool Connected(int[,] distances)
    {
        foreach (int distance in distances) if (distance < 0) return false;
        return true;
    }

    private static long Sum(int[,] distances)
    {
        long sum = 0; int count = distances.GetLength(0);
        for (int first = 0; first < count; first++) for (int second = first + 1; second < count; second++) sum += distances[first, second];
        return sum;
    }
}
