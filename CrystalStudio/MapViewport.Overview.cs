namespace CrystalStudio;

public sealed partial class MapViewport
{
    private CancellationTokenSource? overviewCancellation;
    private Task? overviewTask;
    private int overviewGeneration, overviewSettings = -1, overviewLevel = -1;
    private AssetStore? overviewAssets;
    private double overviewDepth = 1;
    private bool overviewDirty = true;
    private bool overviewReady;
    private bool overviewBusy;
    private OverviewBatch.Result? overviewResult;
    private readonly HashSet<int> overviewEditedBands = [];
    private void ResetOverview()
    {
        overviewCancellation?.Cancel(); overviewCancellation?.Dispose(); overviewCancellation = null;
        overviewGeneration++; overviewTask = null; overviewReady = false; overviewDirty = true; overviewBusy = false;
        overviewResult = null; overviewEditedBands.Clear();
         sceneDirty = true; MinimapInvalidated?.Invoke();
    }
    private void PauseOverview() { overviewCancellation?.Cancel(); overviewDirty = true; overviewBusy = false; }
    private void InvalidateOverviewCell(int x, int y) { overviewEditedBands.Add(y / OverviewBatch.RowsPerBatch); }
    private void RefreshOverviewAssets()
    {
        if (!ReferenceEquals(overviewAssets, Assets)) { ResetOverview(); overviewAssets = Assets; WarmOverview(); }
    }
    private void DrawOverview(SceneContext dc, int x0, int y0, int x1, int y1)
    {
        int settings = RenderLayerSettings;
        int level = Math.Clamp((int)Math.Floor(-Math.Log2(Zoom)), 2, 16);
        // An existing sharper atlas remains valid at every farther zoom-out. Only the
        // camera changes; do not rebuild the whole map at every mip boundary.
        if (!overviewDirty && overviewSettings == settings && ReferenceEquals(overviewAssets, Assets) && overviewLevel >= 2 && level > overviewLevel)
            level = overviewLevel;
        if (overviewDirty || settings != overviewSettings || level != overviewLevel || !ReferenceEquals(overviewAssets, Assets))
            StartOverview(settings, level);
        else if (!overviewBusy && overviewEditedBands.Count > 0) StartOverview(settings, level, editsOnly: true);
        if (overviewReady) dc.DrawDrawing(overviewResult!.Scene);
        drawn += overviewReady ? overviewResult!.Parts.Count : 0;
    }
    private void WarmOverview()
    {
        if (Assets == null || !Document.Cells.Any()) return;
        int settings = RenderLayerSettings;
        StartOverview(settings, 4);
    }
    private void StartOverview(int settings, int level, bool editsOnly = false)
    {
        overviewSettings = settings; overviewLevel = level; overviewAssets = Assets; overviewDirty = false;
        // Editor writes replace cell instances; retaining the old references gives a stable,
        // inexpensive snapshot without cloning every cell on the UI thread.
        // Effect backgrounds include unchanged placements retained by the previous
        // batch, so edits only need a snapshot of their changed row bands.
        var previous = editsOnly ? overviewResult : null;
        int[] bands = overviewEditedBands.Order().ToArray(); overviewEditedBands.Clear();
        KeyValuePair<int, Cell>[] cells;
        if (previous == null) cells = Document.Cells.ToArray();
        else
        {
            var changed = new List<KeyValuePair<int, Cell>>();
            foreach (int band in bands)
                for (int y = band * OverviewBatch.RowsPerBatch; y < Math.Min(Document.Height, (band + 1) * OverviewBatch.RowsPerBatch); y++)
                    for (int x = 0; x < Document.Width; x++)
                    {
                        var cell = Document.Get(x, y);
                        if (!cell.Empty) changed.Add(new(y * Document.Width + x, cell));
                    }
            cells = changed.ToArray();
        }
        // Unchanged effects are recorded in the previous batch. Only the edited
        // rows can introduce new ones; never scan the entire map on the UI thread.
        bool recomposite = previous != null && (previous.HasBlends || cells.Any(p =>
            MapSpriteDrawing.MiddleBlend(p.Value.MiddleFrames) || p.Value.Foreground is { } front && MapSpriteDrawing.FrontBlend(front.Frames)));
        int width = Document.Width, height = Document.Height, generation = ++overviewGeneration;
        overviewCancellation?.Cancel(); overviewCancellation?.Dispose();
        overviewCancellation = new(); var token = overviewCancellation.Token;
        var store = Assets; overviewBusy = true; var background = (paper as ImmutableSolidColorBrush)?.Color ?? Colors.Gray;
        overviewTask = Task.Run(async () =>
        {
            try
            {
                var model = await OverviewBatch.BuildAsync(cells, width, height, settings, level, store, token, previous, bands, background, recomposite);
                token.ThrowIfCancellationRequested();
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (generation != overviewGeneration || token.IsCancellationRequested) return;
                    overviewResult = model;
                    overviewDepth = model.Depth; overviewReady = true; sceneDirty = true; InvalidateVisual();
                    overviewBusy = false;
                    // Never restart an in-flight edit for every pointer movement. Publish
                    // this update, then process edits made while it was being prepared.
                    if (overviewEditedBands.Count > 0) StartOverview(settings, level, editsOnly: true);
                    MinimapInvalidated?.Invoke();
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (generation == overviewGeneration) { overviewBusy = false; overviewDirty = true; Message?.Invoke($"Overview: {ex.Message}"); }
                });
            }
        }, token);
    }
}
