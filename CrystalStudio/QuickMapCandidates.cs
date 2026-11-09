namespace CrystalStudio;

internal sealed class QuickMapCandidate(MapCreationSettings settings, IReadOnlyDictionary<string, string> parameters, RasterImage? thumbnail, MapDesignSettings? design = null)
{
    private readonly MapDesignSettings? generationDesign = GenerationAlgorithms.UsesConstraints(GenerationAlgorithms.Read(parameters)) ? design?.Copy() ?? new() : null;
    internal MapDesignSettings? CopyDesign() => (evaluationDesign ?? generationDesign)?.Copy();
    public MapGenerationSettings Generation { get; } = new(settings.Kind, settings.Width, settings.Height);
    public IReadOnlyDictionary<string, string> Parameters { get; } = new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(new Dictionary<string, string>(parameters));
    public RasterImage? Thumbnail { get; internal set; } = thumbnail;
    public RasterImage? Comparison { get; internal set; }
    public IReadOnlyDictionary<string, string>? DisplayValues { get; internal set; }
    public bool IsFavorite { get; internal set; }
    public MapCandidateMetrics? Metrics { get; internal set; }
    private MapDesignSettings? evaluationDesign;
    internal bool SameEvaluation(MapDesignSettings design) => evaluationDesign?.SameAs(design) == true;
    internal void Evaluate(MapCandidateMetrics metrics, MapDesignSettings design) { Metrics = metrics; evaluationDesign = design.Copy(); }
    internal void RestoreDesignContext(MapDesignSettings design) { evaluationDesign = design.Copy(); Metrics = null; }
    public bool CanRestore(MapCreationSettings settings) => Generation.Kind == settings.Kind && Generation.Width == settings.Width && Generation.Height == settings.Height;
    public bool Matches(MapCreationSettings settings, IReadOnlyDictionary<string, string> parameters, MapDesignSettings? design = null)
    {
        var recordedDesign = evaluationDesign ?? generationDesign;
        return CanRestore(settings) && (recordedDesign == null || recordedDesign.SameAs(design ?? new())) &&
            parameters.Count(pair => QuickMapCandidates.IsGenerationParameter(pair.Key)) == Parameters.Count &&
            Parameters.All(pair => parameters.TryGetValue(pair.Key, out string? value) && value == pair.Value);
    }
}

internal sealed class QuickMapCandidates : IDisposable
{
    public const int Capacity = 5;
    private readonly List<QuickMapCandidate> items = [];
    public IReadOnlyList<QuickMapCandidate> Items => items;
    internal void Replace(IEnumerable<QuickMapCandidate> restored) { Dispose(); items.AddRange(restored); }
    internal static bool IsGenerationParameter(string name) => name != "DetailToggle" && !name.StartsWith("QuickMap", StringComparison.Ordinal) && !name.StartsWith("Generator", StringComparison.Ordinal);

    public QuickMapCandidate? Remember(MapCreationSettings settings, IReadOnlyDictionary<string, string> parameters, RasterImage? preview = null, bool isOverview = true, MapDesignSettings? design = null)
    {
        var values = parameters.Where(pair => IsGenerationParameter(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value);
        var existing = items.FirstOrDefault(item => item.Matches(settings, values, design));
        if (existing != null)
        {
            if (preview != null && isOverview && existing.Comparison == null)
            {
                var snapshots = Snapshots(preview, true);
                existing.Thumbnail?.Dispose(); existing.Thumbnail = snapshots.Thumbnail; existing.Comparison = snapshots.Comparison;
            }
            return existing;
        }
        if (items.Count >= Capacity)
        {
            var oldest = items.FirstOrDefault(item => !item.IsFavorite);
            if (oldest == null) return null;
            items.Remove(oldest); oldest.Thumbnail?.Dispose(); oldest.Comparison?.Dispose();
        }
        var images = preview == null ? default : Snapshots(preview, isOverview);
        var candidate = new QuickMapCandidate(settings, values, images.Thumbnail, design) { Comparison = images.Comparison };
        items.Add(candidate); return candidate;
    }

    internal CandidateRetention RememberMeasured(MapCreationSettings settings, IReadOnlyDictionary<string, string> parameters,
        MapCandidateMetrics metrics, RasterImage? preview, MapDesignSettings design)
    {
        var same = items.FirstOrDefault(item => item.Matches(settings, parameters, design));
        if (same != null)
        {
            Remember(settings, parameters, preview, design: design); same.Evaluate(metrics, design);
            return new(same, LocalText.T("Updated this candidate's measured results."));
        }
        var comparable = items.Where(item => item.CanRestore(settings) && item.Metrics != null && item.SameEvaluation(design)).ToArray();
        var duplicate = comparable.FirstOrDefault(item => metrics.TopologyFingerprint != null && item.Metrics!.TopologyFingerprint == metrics.TopologyFingerprint);
        if (duplicate != null && (duplicate.IsFavorite || !MapCandidateSelection.Dominates(metrics, duplicate.Metrics!)))
            return new(duplicate, LocalText.T("An equivalent passability layout is already retained. This seed remains in the search results."));
        QuickMapCandidate? victim = duplicate;
        if (victim == null && items.Count >= Capacity)
        {
            var eligible = items.Where(item => !item.IsFavorite).ToArray();
            if (eligible.Length == 0) return new(null, LocalText.T("All five candidates are favorites. Unfavorite one to retain another result."));
            victim = eligible.FirstOrDefault(item => !item.CanRestore(settings) || item.Metrics == null || !item.SameEvaluation(design));
            if (victim == null)
            {
                int[] indices = Enumerable.Range(0, items.Count).Where(index => comparable.Contains(items[index])).ToArray();
                int replace = MapCandidateSelection.Replacement(indices.Select(index => items[index].Metrics!).ToArray(), indices.Select(index => items[index].IsFavorite).ToArray(), metrics);
                if (replace < 0) return new(null, LocalText.T("Existing candidates offer equal or better quality and diversity. This seed remains in the search results."));
                victim = items[indices[replace]];
            }
        }
        if (victim != null) { items.Remove(victim); victim.Thumbnail?.Dispose(); victim.Comparison?.Dispose(); }
        var retained = Remember(settings, parameters, preview, design: design)!;
        retained.Evaluate(metrics, design);
        return new(retained, LocalText.T("Retained for measured quality and layout diversity; favorites were preserved."));
    }

    public void SetFavorite(QuickMapCandidate candidate, bool favorite)
    {
        if (items.Contains(candidate)) candidate.IsFavorite = favorite;
    }

    private static (RasterImage? Thumbnail, RasterImage? Comparison) Snapshots(RasterImage image, bool overview)
    {
        var source = System.Buffers.ArrayPool<byte>.Shared.Rent(checked(image.PixelWidth * image.PixelHeight * 4));
        try
        {
            image.CopyPixels(source, image.PixelWidth * 4, 0);
            return (Resize(image, source, 144, 80), overview ? Resize(image, source, 640, 400) : null);
        }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(source); }
    }

    private static RasterImage Resize(RasterImage image, byte[] source, int maximumWidth, int maximumHeight)
    {
        double scale = Math.Min(1, Math.Min((double)maximumWidth / image.PixelWidth, (double)maximumHeight / image.PixelHeight));
        int width = Math.Max(1, (int)(image.PixelWidth * scale)), height = Math.Max(1, (int)(image.PixelHeight * scale));
        var pixels = new byte[width * height * 4];
        for (int row = 0; row < height; row++) for (int column = 0; column < width; column++)
            source.AsSpan(((int)(row / scale) * image.PixelWidth + (int)(column / scale)) * 4, 4).CopyTo(pixels.AsSpan((row * width + column) * 4));
        return RasterImage.Create(width, height, 96, 96, RasterFormats.Bgra32, null, pixels, width * 4);
    }

    public void Dispose() { foreach (var item in items) { item.Thumbnail?.Dispose(); item.Comparison?.Dispose(); } items.Clear(); }
}

internal sealed record CandidateRetention(QuickMapCandidate? Candidate, string Explanation);
