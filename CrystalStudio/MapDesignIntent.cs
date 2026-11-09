namespace CrystalStudio;

internal static class MapDesignIntent
{
    internal static IReadOnlyList<MapConstraintFinding> Check(MapDocument map, MapDesignSettings design, CancellationToken cancellation = default)
    {
        var findings = new List<MapConstraintFinding>();
        var clearance = MapClearance.Build(map, cancellation);
        foreach (var point in design.Points.Where(point => point.ClearRadius > 0))
        {
            bool valid = true;
            for (int row = point.Y - point.ClearRadius; row < point.Y + design.FootprintSize + point.ClearRadius && valid; row++)
                for (int column = point.X - point.ClearRadius; column < point.X + design.FootprintSize + point.ClearRadius; column++)
                {
                    cancellation.ThrowIfCancellationRequested(); var cell = map.Get(column, row);
                    if (!map.Contains(column, row) || !MapRouteAnalysis.Walkable(map, column, row) || cell.HasForeground || cell.MiddleImage != 0 || cell.Door != 0)
                    { valid = false; break; }
                }
            if (valid) valid = !map.PlacedStamps.Any(stamp => stamp.Cells.Select(cell => new Point(stamp.X + cell.X, stamp.Y + cell.Y))
                .Concat(stamp.Changes.Select(delta => new Point(stamp.X + delta.X, stamp.Y + delta.Y)))
                .Any(cell => cell.X >= point.X - point.ClearRadius && cell.X < point.X + design.FootprintSize + point.ClearRadius &&
                    cell.Y >= point.Y - point.ClearRadius && cell.Y < point.Y + design.FootprintSize + point.ClearRadius));
            if (!valid) findings.Add(new(point.Name, design.IntentRequired, LocalText.T("The point's requested clear space is obstructed or outside the map."), point.X, point.Y));
        }
        if (design.MinimumRouteSteps > 0 || design.MaximumRouteSteps > 0)
        {
            var route = MapRouteAnalysis.Analyze(map, design.Points, cancellation, design.FootprintSize);
            var target = design.Points[^1]; int steps = route.Legs.Sum(leg => leg.Steps);
            if (route.Legs.Any(leg => !leg.Reachable)) findings.Add(new(target.Name, design.IntentRequired, LocalText.T("Route length cannot be accepted because a route leg is unreachable."), target.X, target.Y));
            else if (steps < design.MinimumRouteSteps || design.MaximumRouteSteps > 0 && steps > design.MaximumRouteSteps)
                findings.Add(new(target.Name, design.IntentRequired, LocalText.Format("Ordered route: {0} steps; requested range {1} to {2} (0 means unlimited).", steps, design.MinimumRouteSteps, design.MaximumRouteSteps), target.X, target.Y));
        }
        if (!design.RequireTargetCrossing) return findings;
        var entry = design.Points.Single(point => point.Role == MapPointRole.Entry);
        var targetPoint = design.Points.Single(point => point.Role == MapPointRole.Target);
        var exit = design.Points.Single(point => point.Role == MapPointRole.Exit);
        var routeToTarget = MapRouteAnalysis.Analyze(map, [entry, targetPoint, exit], cancellation, design.FootprintSize);
        if (routeToTarget.Legs.Any(leg => !leg.Reachable))
            findings.Add(new(targetPoint.Name, design.IntentRequired, LocalText.T("Entry, target and exit are not connected for the selected footprint."), targetPoint.X, targetPoint.Y));
        else if (CanBypass())
            findings.Add(new(targetPoint.Name, design.IntentRequired, LocalText.T("The exit can be reached without crossing the target footprint. This checks spatial routing, not quest completion."), targetPoint.X, targetPoint.Y));
        return findings;

        bool CanBypass()
        {
            int count = checked(map.Width * map.Height);
            if (count > MapRouteAnalysis.MaximumCells) throw new InvalidOperationException(LocalText.T("Route analysis supports up to 4,000,000 cells. Analyze a smaller map."));
            var visited = new bool[count]; var queue = new int[count]; int read = 0, write = 0;
            bool Allowed(int column, int row) => clearance.At(column, row) >= design.FootprintSize &&
                !(column < targetPoint.X + design.FootprintSize && column + design.FootprintSize > targetPoint.X && row < targetPoint.Y + design.FootprintSize && row + design.FootprintSize > targetPoint.Y);
            if (!Allowed(entry.X, entry.Y)) return false;
            int start = entry.Y * map.Width + entry.X; queue[write++] = start; visited[start] = true;
            while (read < write)
            {
                if ((read & 4095) == 0) cancellation.ThrowIfCancellationRequested();
                int index = queue[read++], column = index % map.Width, row = index / map.Width;
                if (column == exit.X && row == exit.Y) return true;
                Visit(column - 1, row); Visit(column + 1, row); Visit(column, row - 1); Visit(column, row + 1);
            }
            return false;
            void Visit(int column, int row)
            {
                if (!map.Contains(column, row) || !Allowed(column, row)) return;
                int index = row * map.Width + column; if (visited[index]) return;
                visited[index] = true; queue[write++] = index;
            }
        }
    }
}
