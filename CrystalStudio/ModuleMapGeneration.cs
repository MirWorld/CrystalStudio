using System.IO;

namespace CrystalStudio;

internal sealed record ModuleMapOptions(int Count, int Seed, int ReferenceWidth, int ReferenceHeight, ModuleLayout Layout);

// Size hints affect the starting canvas, not whether the user's current map can contain the layout.
internal static class ModuleMapGeneration
{
    private const int MaximumSide = 4096;
    private const int MaximumAttempts = 8;
    private const int StepsPerAttempt = 25_000;

    internal static ModuleGenerationPlan Generate(IReadOnlyList<JoinModule> modules, ModuleMapOptions options,
        CancellationToken cancellation = default, Action<ModuleArea>? tryingArea = null)
    {
        cancellation.ThrowIfCancellationRequested();
        if (options.Count is < 2 or > 128)
            throw ModuleDiagnostics.Error("generation-count", LocalText.T("Enter 2–128 placed module instances. One selected module type can be reused."));
        if (options.ReferenceWidth <= 0 || options.ReferenceHeight <= 0)
            throw ModuleDiagnostics.Error("generation-reference", LocalText.T("Enter positive whole numbers for the reference width and height."));
        if (!Enum.IsDefined(options.Layout))
            throw ModuleDiagnostics.Error("generation-options", LocalText.T("Choose a valid layout and search limit."));
        if (modules.Count is < 1 or > 256)
            throw new InvalidDataException(LocalText.T("Select between 1 and 256 modules for automatic generation."));

        var sizes = modules.Select(module =>
        {
            cancellation.ThrowIfCancellationRequested();
            ModuleGeneration.ValidateModule(module, cancellation);
            var b = ModuleConnectionRules.GetBounds(module.Cells, module.Connections);
            return (Width: b.Right - b.Left + 1, Height: b.Bottom - b.Top + 1);
        }).OrderBy(s => (long)s.Width * s.Height).ToArray();
        long minimumCells = (long)sizes[0].Width * sizes[0].Height * options.Count;
        if (minimumCells > MapDimensions.MaximumGeneratedCells)
            throw ModuleDiagnostics.Error("generation-capacity", LocalText.Format(
                "Even the smallest module needs at least {0} footprint cells for {1} instances, exceeding the 1,000,000-cell limit. Reduce the count or use smaller modules.", minimumCells, options.Count));

        var reference = Normalize(options.ReferenceWidth, options.ReferenceHeight);
        var typical = sizes[sizes.Length / 2];
        double target = Math.Min(MapDimensions.MaximumGeneratedCells,
            Math.Max((long)reference.Width * reference.Height, (long)typical.Width * typical.Height * options.Count * 1.5));
        double ratio = (double)reference.Width / reference.Height;
        double width = Math.Max(sizes[0].Width + 2, Math.Sqrt(target * ratio));
        double height = Math.Max(sizes[0].Height + 2, target / Math.Max(1, width));
        var initial = Normalize(Math.Max(reference.Width, width), Math.Max(reference.Height, height));

        var areas = new List<ModuleArea>();
        void Add(ModuleArea area) { if (!areas.Contains(area)) areas.Add(area); }
        var current = initial;
        for (int i = 0; i < 5; i++)
        {
            Add(current);
            var larger = Normalize(current.Width * 2.0, current.Height * 2.0);
            if (larger == current) break;
            current = larger;
        }
        // Long corridors may require a different aspect ratio, not just a larger square.
        Add(Normalize(Math.Sqrt(MapDimensions.MaximumGeneratedCells * ratio), Math.Sqrt(MapDimensions.MaximumGeneratedCells / ratio)));
        Add(Normalize(MaximumSide, MapDimensions.MaximumGeneratedCells / (double)MaximumSide));
        Add(Normalize(MapDimensions.MaximumGeneratedCells / (double)MaximumSide, MaximumSide));

        int best = 0, attempts = 0;
        foreach (var area in areas.Take(MaximumAttempts))
        {
            cancellation.ThrowIfCancellationRequested(); tryingArea?.Invoke(area); attempts++;
            var result = ModuleGeneration.TryGenerate(new(area.Width, area.Height), modules,
                new(options.Count, options.Seed, area, options.Layout, StepsPerAttempt), cancellation);
            if (result.Plan != null) return result.Plan;
            best = Math.Max(best, result.BestCount);
            if (ModuleDiagnostics.Read(result.Failure)?.Code is not ("generation-area-size" or "generation-ground-alignment" or "generation-layout" or "generation-search-limit"))
                throw result.Failure!;
        }
        throw ModuleDiagnostics.Error("generation-auto-limit", LocalText.Format(
            "Tried {0} map sizes for {1} modules (best attempt: {2}), within 4096 per side and 1,000,000 cells. This bounded search does not prove no layout exists. Try fewer modules, another seed or compatible connections.", attempts, options.Count, best));
    }

    // Round to the ground grid and constrain huge hints without overflowing or allocating huge maps.
    private static ModuleArea Normalize(double width, double height)
    {
        width = Math.Clamp(width, 16, MaximumSide); height = Math.Clamp(height, 16, MaximumSide);
        double scale = Math.Min(1, Math.Sqrt(MapDimensions.MaximumGeneratedCells / (width * height)));
        int w = Math.Clamp((int)Math.Ceiling(width * scale / 2) * 2, 16, MaximumSide);
        int h = Math.Clamp((int)Math.Ceiling(height * scale / 2) * 2, 16, MaximumSide);
        if ((long)w * h > MapDimensions.MaximumGeneratedCells)
            h = (int)(MapDimensions.MaximumGeneratedCells / w / 2) * 2;
        return new(0, 0, w, h);
    }
}
