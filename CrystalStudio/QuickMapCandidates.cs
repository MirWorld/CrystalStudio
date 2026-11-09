namespace CrystalStudio;

internal sealed class QuickMapCandidate(MapCreationSettings settings, IReadOnlyDictionary<string, string> parameters, RasterImage? thumbnail)
{
    public MapGenerationSettings Generation { get; } = new(settings.Kind, settings.Width, settings.Height);
    public IReadOnlyDictionary<string, string> Parameters { get; } = new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(new Dictionary<string, string>(parameters));
    public RasterImage? Thumbnail { get; internal set; } = thumbnail;
    public RasterImage? Comparison { get; internal set; }
    public IReadOnlyDictionary<string, string>? DisplayValues { get; internal set; }
    public bool IsFavorite { get; internal set; }
    public bool CanRestore(MapCreationSettings settings) => Generation.Kind == settings.Kind && Generation.Width == settings.Width && Generation.Height == settings.Height;
}

internal sealed class QuickMapCandidates : IDisposable
{
    public const int Capacity = 5;
    private readonly List<QuickMapCandidate> items = [];
    public IReadOnlyList<QuickMapCandidate> Items => items;

    public QuickMapCandidate? Remember(MapCreationSettings settings, IReadOnlyDictionary<string, string> parameters, RasterImage? preview = null, bool isOverview = true)
    {
        var values = parameters.Where(pair => pair.Key != "DetailToggle" && !pair.Key.StartsWith("QuickMap", StringComparison.Ordinal) && !pair.Key.StartsWith("Generator", StringComparison.Ordinal))
            .ToDictionary(pair => pair.Key, pair => pair.Value);
        var existing = items.FirstOrDefault(item => item.CanRestore(settings) && item.Parameters.Count == values.Count &&
            values.All(pair => item.Parameters.TryGetValue(pair.Key, out string? value) && value == pair.Value));
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
        var candidate = new QuickMapCandidate(settings, values, images.Thumbnail) { Comparison = images.Comparison };
        items.Add(candidate); return candidate;
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
