using System.IO;

namespace CrystalStudio;

internal enum MapPointRole { None, Entry, Target, Exit }
internal sealed record MapRoutePoint(string Name, int X, int Y)
{
    public MapPointRole Role { get; init; }
    public int ClearRadius { get; init; }
}
internal enum MapRegionPurpose { Protect, ClearObjects, Walkable }
internal sealed record MapDesignRegion(string Name, int X, int Y, int Width, int Height,
    MapRegionPurpose Purpose = MapRegionPurpose.Protect, bool Required = true)
{
    public bool Contains(int column, int row) => column >= X && row >= Y && (long)column < (long)X + Width && (long)row < (long)Y + Height;
    public override string ToString() => $"{Name} · ({X}, {Y}) {Width} × {Height} · {LocalText.T(Purpose.ToString())}" +
        (Purpose == MapRegionPurpose.Protect ? "" : " · " + LocalText.T(Required ? "Required" : "Preferred"));
}

internal sealed class MapDesignSettings
{
    public List<MapRoutePoint> Points { get; set; } = [];
    public List<MapDesignRegion> Regions { get; set; } = [];
    public bool RequireRoute { get; set; }
    public int FootprintSize { get; set; } = 1;
    public int MinimumRouteSteps { get; set; }
    public int MaximumRouteSteps { get; set; }
    public bool RequireTargetCrossing { get; set; }
    public bool IntentRequired { get; set; }
    public bool HasIntent => MinimumRouteSteps > 0 || MaximumRouteSteps > 0 || RequireTargetCrossing || Points.Any(point => point.ClearRadius > 0);
    public bool Empty => Points.Count == 0 && Regions.Count == 0 && !RequireRoute && FootprintSize == 1 && !HasIntent;
    public MapDesignSettings Copy() => new() { Points = [.. Points], Regions = [.. Regions], RequireRoute = RequireRoute, FootprintSize = FootprintSize,
        MinimumRouteSteps = MinimumRouteSteps, MaximumRouteSteps = MaximumRouteSteps, RequireTargetCrossing = RequireTargetCrossing, IntentRequired = IntentRequired };
    public bool SameAs(MapDesignSettings other) => RequireRoute == other.RequireRoute && FootprintSize == other.FootprintSize && Points.SequenceEqual(other.Points) && Regions.SequenceEqual(other.Regions) &&
        MinimumRouteSteps == other.MinimumRouteSteps && MaximumRouteSteps == other.MaximumRouteSteps && RequireTargetCrossing == other.RequireTargetCrossing && IntentRequired == other.IntentRequired;

    public void Validate(int width, int height)
    {
        if (FootprintSize is < 1 or > 64)
            throw GenerationDiagnostics.Reject(GenerationFailureKind.InputConflict, "Input validation", LocalText.T("Use a square footprint between 1 and 64 cells."));
        if (Points == null || Regions == null || Points.Count > 32 || Regions.Count > 64)
            throw GenerationDiagnostics.Reject(GenerationFailureKind.InputConflict, "Input validation", LocalText.T("Use at most 32 route points and 64 design regions."));
        if (Points.Any(point => point == null || string.IsNullOrWhiteSpace(point.Name) || point.Name.Length > 80 ||
            !Enum.IsDefined(point.Role) || point.ClearRadius is < 0 or > 32 || point.X < 0 || point.Y < 0 || point.X >= width || point.Y >= height) ||
            Regions.Any(region => region == null || string.IsNullOrWhiteSpace(region.Name) || region.Name.Length > 80 ||
                !Enum.IsDefined(region.Purpose) || region.X < 0 || region.Y < 0 || region.Width <= 0 || region.Height <= 0 ||
                (long)region.X + region.Width > width || (long)region.Y + region.Height > height))
            throw GenerationDiagnostics.Reject(GenerationFailureKind.InputConflict, "Input validation", LocalText.T("A design marker is invalid or outside this map. Adjust markers after resizing."));
        if (RequireRoute && Points.Count < 2)
            throw GenerationDiagnostics.Reject(GenerationFailureKind.InputConflict, "Input validation", LocalText.T("A required route needs at least two ordered points."));
        if (MinimumRouteSteps < 0 || MaximumRouteSteps < 0 || MinimumRouteSteps > MapRouteAnalysis.MaximumCells * 31L || MaximumRouteSteps > MapRouteAnalysis.MaximumCells * 31L ||
            MaximumRouteSteps > 0 && MinimumRouteSteps > MaximumRouteSteps || (MinimumRouteSteps > 0 || MaximumRouteSteps > 0) && Points.Count < 2)
            throw GenerationDiagnostics.Reject(GenerationFailureKind.InputConflict, "Input validation", LocalText.T("Route limits need at least two points and a valid minimum / maximum range. Zero disables a limit."));
        if (RequireTargetCrossing && new[] { MapPointRole.Entry, MapPointRole.Target, MapPointRole.Exit }.Any(role => Points.Count(point => point.Role == role) != 1))
            throw GenerationDiagnostics.Reject(GenerationFailureKind.InputConflict, "Input validation", LocalText.T("Target-crossing checks need exactly one entry, one target and one exit."));
    }
}

internal sealed record MapDesignFile(int Version, int Width, int Height, MapDesignSettings Design);
internal static class MapDesignStore
{
    internal static void Save(string mapPath, MapDocument document, MapDesignSettings design)
    {
        if (design.Empty && !File.Exists(mapPath + ".studio.json")) return;
        design.Validate(document.Width, document.Height);
        AtomicFile.WriteJson(mapPath + ".studio.json", new MapDesignFile(1, document.Width, document.Height, design));
    }

    internal static MapDesignSettings Load(string mapPath, MapDocument document)
    {
        string path = mapPath + ".studio.json";
        if (!File.Exists(path)) return new();
        if (new FileInfo(path).Length > 1_048_576) throw new InvalidDataException("Design file is too large.");
        var file = StudioJson.Deserialize<MapDesignFile>(File.ReadAllText(path));
        if (file is not { Version: 1, Design: not null } || file.Width != document.Width || file.Height != document.Height)
            throw new InvalidDataException(LocalText.T("Design metadata does not match the map dimensions."));
        file.Design.Validate(document.Width, document.Height);
        return file.Design;
    }
}

internal sealed record MapConstraintFinding(string Name, bool Required, string Message, int X, int Y)
{
    public override string ToString() => LocalText.Format("{0} · {1} · ({2}, {3}): {4}", LocalText.T(Required ? "Required" : "Preferred"), Name, X, Y, Message);
}
internal sealed record MapConstraintResult(IReadOnlyList<MapConstraintFinding> Findings)
{
    public bool Accepted => Findings.All(finding => !finding.Required);
}

internal static class MapDesignConstraints
{
    internal static MapConstraintResult Check(MapDocument document, MapDesignSettings design, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        design.Validate(document.Width, document.Height);
        var findings = new List<MapConstraintFinding>();
        var clearance = design.FootprintSize > 1 ? MapClearance.Build(document, cancellation) : null;
        bool Fits(int column, int row) => clearance == null ? MapRouteAnalysis.Walkable(document, column, row) : clearance.At(column, row) >= design.FootprintSize;
        foreach (var point in design.Points)
            if (!Fits(point.X, point.Y))
                findings.Add(new(point.Name, design.RequireRoute, LocalText.T("Route point cannot fit the selected footprint."), point.X, point.Y));
        foreach (var region in design.Regions.Where(region => region.Purpose != MapRegionPurpose.Protect))
        {
            bool invalid = false; int issueX = region.X, issueY = region.Y;
            for (int row = region.Y; row < region.Y + region.Height && !invalid; row++)
            {
                cancellation.ThrowIfCancellationRequested();
                for (int column = region.X; column < region.X + region.Width; column++)
                {
                    var cell = document.Get(column, row);
                    if (region.Purpose == MapRegionPurpose.Walkable ? !Fits(column, row) :
                        cell.HasForeground || cell.MiddleImage != 0 || (cell.Door & 0x7F) != 0)
                    { invalid = true; issueX = column; issueY = row; break; }
                }
            }
            if (!invalid && region.Purpose == MapRegionPurpose.ClearObjects)
                foreach (var stamp in document.PlacedStamps)
                {
                    cancellation.ThrowIfCancellationRequested();
                    foreach (var position in stamp.Cells.Select(cell => (X: stamp.X + cell.X, Y: stamp.Y + cell.Y))
                        .Concat(stamp.Changes.Select(change => (X: stamp.X + change.X, Y: stamp.Y + change.Y))))
                        if (region.Contains(position.X, position.Y)) { invalid = true; issueX = position.X; issueY = position.Y; break; }
                    if (invalid) break;
                }
            if (invalid) findings.Add(new(region.Name, region.Required,
                LocalText.T(region.Purpose == MapRegionPurpose.Walkable ? "Reserved corridor cannot fit the selected footprint." : "Reserved area contains object anchors or stamp footprints."), issueX, issueY));
        }
        if (design.RequireRoute && findings.All(finding => !finding.Required))
        {
            var route = MapRouteAnalysis.Analyze(document, design.Points, cancellation, design.FootprintSize);
            foreach (var leg in route.Legs.Where(leg => !leg.Reachable))
                findings.Add(new(leg.From.Name + " → " + leg.To.Name, true, LocalText.T("Required route is unreachable."), leg.To.X, leg.To.Y));
        }
        if (design.HasIntent) findings.AddRange(MapDesignIntent.Check(document, design, cancellation));
        return new(findings);
    }
}
