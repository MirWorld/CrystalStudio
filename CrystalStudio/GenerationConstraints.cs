using System.IO;

namespace CrystalStudio;

internal sealed class GenerationConstraints
{
    private readonly MapDesignSettings design;
    private readonly int width, height;
    internal bool[] Reserved { get; }

    private GenerationConstraints(MapDocument source, MapDesignSettings settings, CancellationToken cancellation)
    {
        design = settings.Copy(); width = source.Width; height = source.Height;
        design.Validate(width, height); Reserved = new bool[checked(width * height)];
        foreach (var region in design.Regions.Where(region => region.Required && region.Purpose != MapRegionPurpose.Protect))
        for (int row = region.Y; row < region.Y + region.Height; row++)
        {
            cancellation.ThrowIfCancellationRequested();
            Array.Fill(Reserved, true, row * width + region.X, region.Width);
        }
        if (design.RequireRoute) foreach (var point in design.Points) Reserved[point.Y * width + point.X] = true;
    }

    internal static GenerationConstraints? Compile(MapDocument source, MapDesignSettings? design, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        if (design == null) return null;
        design.Validate(source.Width, source.Height);
        return design.Empty ? null : new(source, design, cancellation);
    }

    internal void CheckForbidden(bool[] forbidden, CancellationToken cancellation)
    {
        if (forbidden.Length != Reserved.Length) throw new ArgumentException("Invalid generation boundary mask.");
        foreach (var region in design.Regions.Where(region => region.Required && region.Purpose == MapRegionPurpose.Walkable))
        for (int row = region.Y; row < region.Y + region.Height; row++)
        {
            cancellation.ThrowIfCancellationRequested();
            for (int column = region.X; column < region.X + region.Width; column++)
                if (forbidden[row * width + column]) throw new InvalidDataException(LocalText.Format("Required reservation {0} intersects the sealed boundary at ({1}, {2}).", region.Name, column, row));
        }
        if (design.RequireRoute)
            foreach (var point in design.Points)
                if (forbidden[point.Y * width + point.X]) throw new InvalidDataException(LocalText.Format("Required route point {0} intersects the sealed boundary at ({1}, {2}).", point.Name, point.X, point.Y));
    }

    internal void MergeInto(bool[] target, CancellationToken cancellation)
    {
        if (target.Length != Reserved.Length) throw new ArgumentException("Invalid generation reservation mask.");
        for (int index = 0; index < target.Length; index++)
        {
            if ((index & 4095) == 0) cancellation.ThrowIfCancellationRequested();
            target[index] |= Reserved[index];
        }
    }

    internal void EnsureSatisfied(MapDocument map, CancellationToken cancellation) => EnsureSatisfied(map, design, cancellation);

    internal static void EnsureSatisfied(MapDocument map, MapDesignSettings design, CancellationToken cancellation)
    {
        var check = MapDesignConstraints.Check(map, design, cancellation);
        var failures = check.Findings.Where(finding => finding.Required).ToArray();
        if (failures.Length > 0) throw new InvalidDataException(string.Join("\n", failures.Select(finding => finding.ToString())));
    }

    internal void ReserveRoutes(MapDocument map, CancellationToken cancellation, bool[]? forbidden = null)
    {
        EnsureSatisfied(map, cancellation);
        if (forbidden != null && forbidden.Length != Reserved.Length) throw new ArgumentException("Invalid generation boundary mask.");
        if (!design.RequireRoute) return;
        var previous = new int[Reserved.Length]; var queue = new int[Reserved.Length];
        for (int leg = 1; leg < design.Points.Count; leg++)
        {
            cancellation.ThrowIfCancellationRequested(); Array.Fill(previous, -1);
            var from = design.Points[leg - 1]; var to = design.Points[leg];
            int start = from.Y * width + from.X, goal = to.Y * width + to.X;
            if (forbidden?[start] == true || forbidden?[goal] == true)
                throw new InvalidDataException(new MapConstraintFinding(from.Name + " → " + to.Name, true, LocalText.T("Required route is unreachable."), to.X, to.Y).ToString());
            int head = 0, tail = 1; queue[0] = start; previous[start] = start;
            while (head < tail && previous[goal] < 0)
            {
                if ((head & 4095) == 0) cancellation.ThrowIfCancellationRequested();
                int current = queue[head++], x = current % width, y = current / width;
                if (x > 0) Visit(current - 1); if (x + 1 < width) Visit(current + 1);
                if (y > 0) Visit(current - width); if (y + 1 < height) Visit(current + width);
                void Visit(int next)
                {
                    if (previous[next] >= 0 || forbidden?[next] == true || !MapRouteAnalysis.Walkable(map, next % width, next / width)) return;
                    previous[next] = current; queue[tail++] = next;
                }
            }
            if (previous[goal] < 0) throw new InvalidDataException(new MapConstraintFinding(from.Name + " → " + to.Name, true, LocalText.T("Required route is unreachable."), to.X, to.Y).ToString());
            int visited = 0;
            for (int cursor = goal;; cursor = previous[cursor])
            {
                if ((visited++ & 4095) == 0) cancellation.ThrowIfCancellationRequested();
                int x = cursor % width, y = cursor / width;
                for (int row = Math.Max(0, y - 1); row <= Math.Min(height - 1, y + 1); row++)
                for (int column = Math.Max(0, x - 1); column <= Math.Min(width - 1, x + 1); column++) Reserved[row * width + column] = true;
                if (cursor == start) break;
            }
        }
    }
}
