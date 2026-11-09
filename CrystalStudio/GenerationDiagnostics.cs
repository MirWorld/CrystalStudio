using System.IO;

namespace CrystalStudio;

internal enum GenerationFailureKind { InputConflict, SeedRejected, SearchExhausted, MissingAssets, Unexpected }

internal sealed record GenerationFailure(GenerationFailureKind Kind, string Stage, string Message,
    IReadOnlyList<MapConstraintFinding> Findings)
{
    internal string Advice => LocalText.T(Kind switch
    {
        GenerationFailureKind.InputConflict => "Adjust the named conditions or map bounds. Changing the seed will not resolve this conflict.",
        GenerationFailureKind.SeedRejected => "This seed failed validation. Try another seed or adjust the named conditions; no constraint was relaxed.",
        GenerationFailureKind.SearchExhausted => "The search budget was exhausted. This does not prove that no valid layout exists.",
        GenerationFailureKind.MissingAssets => "Load the matching reviewed artwork or choose a supported style. Changing the seed will not supply missing assets.",
        _ => "An unexpected error occurred. Keep the seed and parameters when reporting this failure."
    });
    public override string ToString() => LocalText.T(Kind switch
    {
        GenerationFailureKind.InputConflict => "Input conflict", GenerationFailureKind.SeedRejected => "Seed rejected",
        GenerationFailureKind.SearchExhausted => "Search limit reached", GenerationFailureKind.MissingAssets => "Required artwork unavailable",
        _ => "Unexpected generation error"
    }) + " · " + LocalText.T(Stage) + "\n" + Message + "\n" + Advice;
}

internal static class GenerationDiagnostics
{
    private const string Key = "CrystalStudio.GenerationFailure";

    internal static InvalidDataException Reject(GenerationFailureKind kind, string stage, string message,
        IReadOnlyList<MapConstraintFinding>? findings = null)
    {
        var exception = new InvalidDataException(message);
        exception.Data[Key] = new GenerationFailure(kind, stage, message, findings?.ToArray() ?? []);
        return exception;
    }

    internal static ArgumentException MissingArgument(string message)
    {
        var exception = new ArgumentException(message);
        exception.Data[Key] = new GenerationFailure(GenerationFailureKind.MissingAssets, "Artwork preflight", message, []);
        return exception;
    }

    internal static GenerationFailure Describe(Exception exception, string stage)
    {
        if (exception.Data[Key] is GenerationFailure failure) return failure;
        if (exception.InnerException != null && exception is AggregateException) return Describe(exception.InnerException, stage);
        var kind = exception switch
        {
            FileNotFoundException or DirectoryNotFoundException => GenerationFailureKind.MissingAssets,
            ArgumentException => GenerationFailureKind.InputConflict,
            InvalidDataException => GenerationFailureKind.SeedRejected,
            _ => GenerationFailureKind.Unexpected
        };
        return new(kind, stage, exception.Message, []);
    }

    internal static void ValidateInputs(MapDocument map, MapDesignSettings design)
    {
        design.Validate(map.Width, map.Height);
        var findings = new List<MapConstraintFinding>();
        if (design.RequireRoute) foreach (var point in design.Points)
            if ((long)point.X + design.FootprintSize > map.Width || (long)point.Y + design.FootprintSize > map.Height)
                findings.Add(new(point.Name, true, LocalText.T("The selected footprint extends outside the map."), point.X, point.Y));
        foreach (var region in design.Regions.Where(region => region.Required && region.Purpose == MapRegionPurpose.Walkable))
            if ((long)region.X + region.Width + design.FootprintSize - 1 > map.Width || (long)region.Y + region.Height + design.FootprintSize - 1 > map.Height)
                findings.Add(new(region.Name, true, LocalText.T("The selected footprint extends outside the map."), region.X + region.Width - 1, region.Y + region.Height - 1));
        if (findings.Count > 0) throw Reject(GenerationFailureKind.InputConflict, "Input validation", string.Join("\n", findings), findings);
    }
}

internal sealed record MapCandidateThresholds(int WalkablePercent, int ReachablePercent, int Rooms, bool IsDungeon)
{
    internal IReadOnlyList<string> Failures(MapCandidateMetrics metrics)
    {
        var failures = new List<string>();
        if (metrics.WalkableRatio * 100 + 1e-9 < WalkablePercent)
            failures.Add(LocalText.Format("Walkable anchors {0:P1} are below {1}%.", metrics.WalkableRatio, WalkablePercent));
        if (metrics.ReachableRatio * 100 + 1e-9 < ReachablePercent)
            failures.Add(LocalText.Format("Reachable anchors {0:P1} are below {1}% ({2}).", metrics.ReachableRatio, ReachablePercent,
                LocalText.T(metrics.StartArea != null ? "from first point" : "largest area")));
        if (IsDungeon && (metrics.Rooms ?? 0) < Rooms)
            failures.Add(LocalText.Format("Room count {0} is below {1}.", metrics.Rooms ?? 0, Rooms));
        return failures;
    }
}
