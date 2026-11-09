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
        {
            int extra = region.Purpose == MapRegionPurpose.Walkable ? design.FootprintSize - 1 : 0;
            Reserve(region.X, region.Y, region.Width + extra, region.Height + extra);
        }
        if (design.RequireRoute) foreach (var point in design.Points) Reserve(point.X, point.Y, design.FootprintSize, design.FootprintSize);

        void Reserve(int x, int y, int columns, int rows)
        {
            for (int row = y; row < Math.Min(height, y + rows); row++)
            {
                cancellation.ThrowIfCancellationRequested();
                Array.Fill(Reserved, true, row * width + x, Math.Min(columns, width - x));
            }
        }
    }

    internal static GenerationConstraints? Compile(MapDocument source, MapDesignSettings? design, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        if (design == null) return null;
        design.Validate(source.Width, source.Height);
        GenerationDiagnostics.ValidateInputs(source, design);
        return design.Empty ? null : new(source, design, cancellation);
    }

    internal void CheckForbidden(bool[] forbidden, CancellationToken cancellation, Func<int, int, bool>? permanentBoundary = null)
    {
        if (forbidden.Length != Reserved.Length) throw new ArgumentException("Invalid generation boundary mask.");
        if (permanentBoundary != null) Check(true);
        Check(false);

        void Check(bool permanentOnly)
        {
            foreach (var region in design.Regions.Where(region => region.Required && region.Purpose == MapRegionPurpose.Walkable))
            for (int row = region.Y; row < region.Y + region.Height + design.FootprintSize - 1; row++)
            {
                cancellation.ThrowIfCancellationRequested();
                for (int column = region.X; column < region.X + region.Width + design.FootprintSize - 1; column++)
                    if (Blocked(column, row)) Fail(region.Name, column, row, LocalText.Format("Required reservation {0} intersects the sealed boundary at ({1}, {2}).", region.Name, column, row));
            }
            if (design.RequireRoute) foreach (var point in design.Points)
            for (int row = point.Y; row < point.Y + design.FootprintSize; row++)
            for (int column = point.X; column < point.X + design.FootprintSize; column++)
                if (Blocked(column, row)) Fail(point.Name, column, row, LocalText.Format("Required route point {0} intersects the sealed boundary at ({1}, {2}).", point.Name, column, row));

            bool Blocked(int column, int row) => forbidden[row * width + column] && (!permanentOnly || permanentBoundary!(column, row));
            void Fail(string name, int column, int row, string message) => throw GenerationDiagnostics.Reject(
                permanentOnly ? GenerationFailureKind.InputConflict : GenerationFailureKind.SeedRejected, "Boundary reservations", message,
                [new(name, true, message, column, row), new(LocalText.T("Sealed boundary"), true, LocalText.T(permanentOnly ? "This boundary is sealed for every seed." : "This boundary shape belongs to the current seed."), column, row)]);
        }
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
        if (failures.Length > 0) throw GenerationDiagnostics.Reject(GenerationFailureKind.SeedRejected, "Layout validation", string.Join("\n", failures.Select(finding => finding.ToString())), failures);
    }

    internal void ReserveRoutes(MapDocument map, CancellationToken cancellation, bool[]? forbidden = null)
    {
        EnsureSatisfied(map, cancellation);
        if (forbidden != null && forbidden.Length != Reserved.Length) throw new ArgumentException("Invalid generation boundary mask.");
        if (!design.RequireRoute) return;
        var clearance = MapClearance.Build(map, cancellation, forbidden);
        bool Fits(int index) => clearance.At(index % width, index / width) >= design.FootprintSize;
        var previous = new int[Reserved.Length]; var queue = new int[Reserved.Length];
        for (int leg = 1; leg < design.Points.Count; leg++)
        {
            cancellation.ThrowIfCancellationRequested(); Array.Fill(previous, -1);
            var from = design.Points[leg - 1]; var to = design.Points[leg];
            int start = from.Y * width + from.X, goal = to.Y * width + to.X;
            if (!Fits(start) || !Fits(goal))
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
                    if (previous[next] >= 0 || !Fits(next)) return;
                    previous[next] = current; queue[tail++] = next;
                }
            }
            if (previous[goal] < 0) throw new InvalidDataException(new MapConstraintFinding(from.Name + " → " + to.Name, true, LocalText.T("Required route is unreachable."), to.X, to.Y).ToString());
            int visited = 0;
            for (int cursor = goal;; cursor = previous[cursor])
            {
                if ((visited++ & 4095) == 0) cancellation.ThrowIfCancellationRequested();
                int x = cursor % width, y = cursor / width;
                for (int row = Math.Max(0, y - 1); row <= Math.Min(height - 1, y + design.FootprintSize); row++)
                for (int column = Math.Max(0, x - 1); column <= Math.Min(width - 1, x + design.FootprintSize); column++) Reserved[row * width + column] = true;
                if (cursor == start) break;
            }
        }
    }
}
