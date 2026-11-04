namespace CrystalStudio;

public enum StampBlockReason { OccupiedObject, MapBoundary, OccupiedCollision }
public sealed record StampBlockedCell(long X, long Y, StampBlockReason Reason);
public sealed record StampPlacementResult(IReadOnlyList<StampBlockedCell> BlockedCells, string? Error = null)
{
    public bool Allowed => Error == null && BlockedCells.Count == 0;
    public string Feedback => Allowed ? "Stamp can be placed" : "Placement blocked · " + string.Join(" · ", Details);
    public IEnumerable<string> Details
    {
        get
        {
            if (Error != null) yield return Error;
            foreach (var group in BlockedCells.GroupBy(c => c.Reason))
            {
                string label = group.Key switch
                {
                    StampBlockReason.OccupiedObject => "Occupied objects (red)",
                    StampBlockReason.OccupiedCollision => "Existing collisions (red)",
                    StampBlockReason.MapBoundary => "Outside map boundaries (amber)",
                    _ => throw new InvalidOperationException("Unknown placement blocker.")
                };
                int count = group.Count();
                string coordinates = string.Join(", ", group.Take(3).Select(c => $"({c.X}, {c.Y})"));
                yield return $"{label}: {count:N0} {(count == 1 ? "cell" : "cells")} · {coordinates}" + (count > 3 ? $", +{count - 3:N0} more" : "");
            }
        }
    }
}
