namespace CrystalStudio;

internal sealed record MapCheckComparison(MapIssue[] Added, MapIssue[] Remaining, MapIssue[] Resolved)
{
    internal static MapCheckComparison? Compare(MapCheckResult? previous, MapCheckResult current)
    {
        if (previous == null || previous.Total != previous.Issues.Count || current.Total != current.Issues.Count) return null;
        var old = previous.Issues.ToHashSet(); var now = current.Issues.ToHashSet();
        return new(current.Issues.Where(issue => !old.Contains(issue)).ToArray(), current.Issues.Where(old.Contains).ToArray(),
            previous.Issues.Where(issue => !now.Contains(issue)).ToArray());
    }
}
