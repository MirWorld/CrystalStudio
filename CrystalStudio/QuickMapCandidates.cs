namespace CrystalStudio;

internal sealed class QuickMapCandidate(MapCreationSettings settings, IReadOnlyDictionary<string, string> parameters, RasterImage? thumbnail)
{
    public MapGenerationSettings Generation { get; } = new(settings.Kind, settings.Width, settings.Height);
    public IReadOnlyDictionary<string, string> Parameters { get; } = new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(new Dictionary<string, string>(parameters));
    public RasterImage? Thumbnail { get; } = thumbnail;
    public bool IsFavorite { get; internal set; }
    public bool CanRestore(MapCreationSettings settings) => Generation.Kind == settings.Kind && Generation.Width == settings.Width && Generation.Height == settings.Height;
}

internal sealed class QuickMapCandidates : IDisposable
{
    public const int Capacity = 5;
    private readonly List<QuickMapCandidate> items = [];
    public IReadOnlyList<QuickMapCandidate> Items => items;

    public QuickMapCandidate? Remember(MapCreationSettings settings, IReadOnlyDictionary<string, string> parameters, RasterImage? preview = null)
    {
        var values = parameters.Where(pair => pair.Key != "DetailToggle" && !pair.Key.StartsWith("QuickMap", StringComparison.Ordinal))
            .ToDictionary(pair => pair.Key, pair => pair.Value);
        var existing = items.FirstOrDefault(item => item.CanRestore(settings) && item.Parameters.Count == values.Count &&
            values.All(pair => item.Parameters.TryGetValue(pair.Key, out string? value) && value == pair.Value));
        if (existing != null) return existing;
        if (items.Count >= Capacity)
        {
            var oldest = items.FirstOrDefault(item => !item.IsFavorite);
            if (oldest == null) return null;
            items.Remove(oldest); oldest.Thumbnail?.Dispose();
        }
        var candidate = new QuickMapCandidate(settings, values, preview == null ? null : Thumbnail(preview));
        items.Add(candidate); return candidate;
    }

    public void SetFavorite(QuickMapCandidate candidate, bool favorite)
    {
        if (items.Contains(candidate)) candidate.IsFavorite = favorite;
    }

    private static RasterImage Thumbnail(RasterImage image)
    {
        double scale = Math.Min(144.0 / image.PixelWidth, 80.0 / image.PixelHeight);
        int width = Math.Max(1, (int)(image.PixelWidth * scale)), height = Math.Max(1, (int)(image.PixelHeight * scale));
        var source = new byte[image.PixelWidth * image.PixelHeight * 4]; image.CopyPixels(source, image.PixelWidth * 4, 0);
        var pixels = new byte[width * height * 4];
        for (int row = 0; row < height; row++) for (int column = 0; column < width; column++)
            source.AsSpan(((int)(row / scale) * image.PixelWidth + (int)(column / scale)) * 4, 4).CopyTo(pixels.AsSpan((row * width + column) * 4));
        return RasterImage.Create(width, height, 96, 96, RasterFormats.Bgra32, null, pixels, width * 4);
    }

    public void Dispose() { foreach (var item in items) item.Thumbnail?.Dispose(); items.Clear(); }
}
