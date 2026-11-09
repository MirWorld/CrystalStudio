namespace CrystalStudio;

internal sealed class MapCreationReviewView : Grid
{
    private readonly TextBlock status = new() { Name = "ProductionCreationStatus", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock difference = new() { Name = "ProductionCreationDifference", TextWrapping = TextWrapping.Wrap };
    private readonly Image baseline = new(), current = new();
    private readonly ListBox changes = new() { Name = "ProductionCreationChanges", MaxHeight = 150 };
    private readonly Button compare = new() { Name = "ProductionCompareCreation", Content = LocalText.T("Compare with creation baseline") };
    private RasterImage? baselineBitmap, currentBitmap;
    private bool closed, busy;

    internal MapCreationReviewView(EditorWindow owner, MapDocument source, MapDesignSettings design, MapCreationReview review, AssetStore? assets, Action<int, int> locate)
    {
        RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*,Auto,Auto"); RowSpacing = 8;
        Children.Add(status); Grid.SetRow(compare, 1); Children.Add(compare); Grid.SetRow(difference, 2); Children.Add(difference);
        var images = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 8 };
        var navigation = new PreviewNavigation(); var left = new MapPreviewViewport(baseline, navigation); var right = new MapPreviewViewport(current, navigation);
        left.ViewChanged += right.InvalidateVisual; right.ViewChanged += left.InvalidateVisual;
        images.Children.Add(left); Grid.SetColumn(right, 1); images.Children.Add(right); Grid.SetRow(images, 3); Children.Add(images);
        Avalonia.Automation.AutomationProperties.SetName(left, LocalText.T("Creation baseline"));
        Avalonia.Automation.AutomationProperties.SetName(right, LocalText.T("Current working map"));
        changes.ItemTemplate = new FuncDataTemplate<MapCreationCellDifference>((cell, _) => new TextBlock { Text = cell?.ToString() });
        Grid.SetRow(changes, 4); Children.Add(changes);
        var locateChange = new Button { Name = "ProductionLocateCreationChange", Content = LocalText.T("Locate selected change"), IsEnabled = false };
        changes.SelectionChanged += (_, _) => locateChange.IsEnabled = changes.SelectedItem is MapCreationCellDifference;
        locateChange.Click += (_, _) => { if (changes.SelectedItem is MapCreationCellDifference cell) locate(cell.X, cell.Y); };
        Grid.SetRow(locateChange, 5); Children.Add(locateChange);
        compare.Click += async (_, _) =>
        {
            if (busy) return;
            busy = true; compare.IsEnabled = false; long revision = source.PickupRevision;
            RasterImage? nextBefore = null, nextAfter = null;
            try
            {
                var snapshot = MapRecoveryStore.Capture(source);
                var result = await Task.Run(() => review.Compare(snapshot));
                if (assets != null)
                {
                    nextBefore = await MinimapExporter.CreateAsync(review.Baseline, assets, 1024, 768);
                    nextAfter = await MinimapExporter.CreateAsync(snapshot, assets, 1024, 768);
                }
                if (closed || revision != source.PickupRevision) { difference.Text = LocalText.T("The map changed during comparison. Compare again."); return; }
                baseline.Source = current.Source = null; baselineBitmap?.Dispose(); currentBitmap?.Dispose();
                baseline.Source = baselineBitmap = nextBefore; current.Source = currentBitmap = nextAfter; nextBefore = nextAfter = null;
                changes.ItemsSource = result.Samples;
                difference.Text = LocalText.Format("Since creation: {0} changed cells; ground {1}, decoration {2}, collision {3}. The list shows up to 1,000 locations. Comparison never applies changes.", result.ChangedCells, result.GroundCells, result.DecorationCells, result.CollisionCells);
                comparedRevision = revision;
            }
            catch (Exception exception) { if (!closed) difference.Text = exception.Message; }
            finally { nextBefore?.Dispose(); nextAfter?.Dispose(); busy = false; if (!closed) compare.IsEnabled = true; }
        };
        void Refresh()
        {
            status.Text = LocalText.Format("Creation source: {0}; seed {1}; decoration seed {2}.", review.Recipe?.GeneratorVersion ?? "—", review.Recipe?.Parameters.GetValueOrDefault("SeedBox", "—") ?? "—", review.Recipe?.DecorationSeed?.ToString() ?? "—") + "\n" +
                LocalText.T(review.Current(source, design) ? review.Check.Accepted ? "Current map matches the latest layout check. This is not a gameplay acceptance." : "The latest layout check contains required issues." :
                    "The map or design changed. Previous checks are stale; analyze routes and reservations again.") + "\n" +
                string.Join("\n", review.Check.Findings.Take(8));
            if (comparedRevision >= 0 && comparedRevision != source.PickupRevision)
            {
                comparedRevision = -1; changes.ItemsSource = null; locateChange.IsEnabled = false;
                baseline.Source = current.Source = null; baselineBitmap?.Dispose(); baselineBitmap = null; currentBitmap?.Dispose(); currentBitmap = null;
                difference.Text = LocalText.T("The map changed. The previous creation comparison is stale; compare again.");
            }
        }
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) }; timer.Tick += (_, _) => Refresh();
        owner.Opened += (_, _) => timer.Start();
        owner.Closed += (_, _) => { closed = true; timer.Stop(); baseline.Source = current.Source = null; baselineBitmap?.Dispose(); currentBitmap?.Dispose(); };
        Refresh();
    }
    private long comparedRevision = -1;
}
