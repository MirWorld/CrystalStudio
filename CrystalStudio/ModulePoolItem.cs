using System.ComponentModel;
using System.IO;

namespace CrystalStudio;

internal sealed class ModulePoolItem : INotifyPropertyChanged, IDisposable
{
    private static readonly SemaphoreSlim readers = new(2);
    private readonly StampCategoryIndex catalogue;
    private readonly AssetStore? assets;
    private CancellationTokenSource? loading;
    private bool loaded, disposed, wanted, selected = true;
    private string summary;
    private RasterImage? thumbnail;
    internal CategorizedStamp Entry { get; }
    public string Name => Entry.Name;
    public bool Selected { get => selected; set { if (selected == value) return; selected = value; Notify(nameof(Selected)); SelectionChanged?.Invoke(); } }
    public string Summary => summary;
    public RasterImage? Thumbnail => thumbnail;
    public event PropertyChangedEventHandler? PropertyChanged;
    internal event Action? SelectionChanged;
    internal ModulePoolItem(StampCategoryIndex catalogue, CategorizedStamp entry, AssetStore? assets)
    {
        this.catalogue = catalogue; Entry = entry; this.assets = assets;
        summary = Description(entry.Connections?.Footprint is { } b ? $"{b.Width} × {b.Height}" : "…");
    }
    private string Description(string dimensions) => dimensions + " · " + LocalText.Format("{0} connections", Entry.Connections?.Points.Count ?? 0) + "\n" +
        string.Join(", ", Entry.Connections?.Points.Select(p => p.Kind).Distinct(StringComparer.OrdinalIgnoreCase) ?? []);
    internal bool Matches(string? query) => string.IsNullOrWhiteSpace(query) ||
        string.Join(' ', new[] { Entry.Name }.Concat(Entry.Tags).Concat(Entry.Connections?.Points.Select(p => p.Kind) ?? []))
            .Contains(query.Trim(), StringComparison.OrdinalIgnoreCase);
    private void Notify(string property) => PropertyChanged?.Invoke(this, new(property));
    internal async Task LoadAsync()
    {
        wanted = true;
        if (disposed || loaded || loading != null) return;
        using var job = new CancellationTokenSource(); loading = job;
        try
        {
            var result = await Task.Run(async () =>
            {
                await readers.WaitAsync(job.Token);
                try
                {
                    var cells = CellCodec.LoadStamp(StampCategories.Resolve(catalogue.ObjectRoot, Entry.File), job.Token);
                    var b = ModuleConnectionRules.GetBounds(cells, Entry.Connections);
                    var bitmap = assets == null ? null : StampPreview.Create(cells, assets, 100, 72, job.Token, waitForAssets: true);
                    return (Text: Description($"{b.Right - b.Left + 1} × {b.Bottom - b.Top + 1}"), Image: bitmap);
                }
                finally { readers.Release(); }
            }, job.Token);
            if (disposed || job.IsCancellationRequested) { result.Image?.Dispose(); return; }
            summary = result.Text; thumbnail = result.Image; loaded = true;
            Notify(nameof(Summary)); Notify(nameof(Thumbnail));
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
        { if (!disposed) { summary = "⚠ " + error.Message; loaded = true; Notify(nameof(Summary)); } }
        finally
        {
            if (ReferenceEquals(loading, job)) loading = null;
            if (!disposed && wanted && !loaded && job.IsCancellationRequested)
                Dispatcher.UIThread.Post(async () => { if (wanted && !disposed) await LoadAsync(); });
        }
    }
    internal void Pause() { wanted = false; loading?.Cancel(); }
    public void Dispose() { disposed = true; Pause(); thumbnail?.Dispose(); thumbnail = null; }
}
