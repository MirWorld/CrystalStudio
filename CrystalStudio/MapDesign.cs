using System.IO;

namespace CrystalStudio;

internal sealed record MapRoutePoint(string Name, int X, int Y);
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
    public bool Empty => Points.Count == 0 && Regions.Count == 0 && !RequireRoute;
    public MapDesignSettings Copy() => new() { Points = [.. Points], Regions = [.. Regions], RequireRoute = RequireRoute };

    public void Validate(int width, int height)
    {
        if (Points == null || Regions == null || Points.Count > 32 || Regions.Count > 64)
            throw new InvalidDataException(LocalText.T("Use at most 32 route points and 64 design regions."));
        if (Points.Any(point => point == null || string.IsNullOrWhiteSpace(point.Name) || point.Name.Length > 80 ||
            point.X < 0 || point.Y < 0 || point.X >= width || point.Y >= height) ||
            Regions.Any(region => region == null || string.IsNullOrWhiteSpace(region.Name) || region.Name.Length > 80 ||
                !Enum.IsDefined(region.Purpose) || region.X < 0 || region.Y < 0 || region.Width <= 0 || region.Height <= 0 ||
                (long)region.X + region.Width > width || (long)region.Y + region.Height > height))
            throw new InvalidDataException(LocalText.T("A design marker is invalid or outside this map. Adjust markers after resizing."));
        if (RequireRoute && Points.Count < 2)
            throw new InvalidDataException(LocalText.T("A required route needs at least two ordered points."));
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

internal sealed record MapConstraintFinding(string Name, bool Required, string Message, int X, int Y);
internal sealed record MapConstraintResult(IReadOnlyList<MapConstraintFinding> Findings)
{
    public bool Accepted => Findings.All(finding => !finding.Required);
}

internal static class MapDesignConstraints
{
    internal static MapConstraintResult Check(MapDocument document, MapDesignSettings design, CancellationToken cancellation = default)
    {
        design.Validate(document.Width, document.Height);
        var findings = new List<MapConstraintFinding>();
        foreach (var point in design.Points)
            if (!MapRouteAnalysis.Walkable(document, point.X, point.Y))
                findings.Add(new(point.Name, design.RequireRoute, LocalText.T("Route point is blocked."), point.X, point.Y));
        foreach (var region in design.Regions.Where(region => region.Purpose != MapRegionPurpose.Protect))
        {
            bool invalid = false;
            for (int row = region.Y; row < region.Y + region.Height && !invalid; row++)
            {
                cancellation.ThrowIfCancellationRequested();
                for (int column = region.X; column < region.X + region.Width; column++)
                {
                    var cell = document.Get(column, row);
                    if (region.Purpose == MapRegionPurpose.Walkable ? !MapRouteAnalysis.Walkable(document, column, row) :
                        cell.HasForeground || cell.MiddleImage != 0 || (cell.Door & 0x7F) != 0)
                    { invalid = true; break; }
                }
            }
            if (!invalid && region.Purpose == MapRegionPurpose.ClearObjects)
                invalid = document.PlacedStamps.Any(stamp => stamp.Cells.Any(cell => region.Contains(stamp.X + cell.X, stamp.Y + cell.Y)) ||
                    stamp.Changes.Any(change => region.Contains(stamp.X + change.X, stamp.Y + change.Y)));
            if (invalid) findings.Add(new(region.Name, region.Required,
                LocalText.T(region.Purpose == MapRegionPurpose.Walkable ? "Reserved corridor contains blocked cells." : "Reserved area contains object anchors or stamp footprints."), region.X, region.Y));
        }
        if (design.RequireRoute && findings.All(finding => !finding.Required))
        {
            var route = MapRouteAnalysis.Analyze(document, design.Points, cancellation);
            foreach (var leg in route.Legs.Where(leg => !leg.Reachable))
                findings.Add(new(leg.From.Name + " → " + leg.To.Name, true, LocalText.T("Required route is unreachable."), leg.To.X, leg.To.Y));
        }
        return new(findings);
    }
}
