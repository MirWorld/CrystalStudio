using System.IO;
namespace CrystalStudio;

public partial class TempleGeneratorWindow : EditorWindow
{
    private readonly MapDocument document;
    private readonly AssetStore assets;
    private readonly WallCatalogue walls;
    private readonly TerrainCatalogue terrain;
    private readonly DispatcherTimer timer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(350) };
    // Wall families cache compositions; keep workers serial even when an older
    // wall composition has not yet reached its cancellation checkpoint.
    private readonly SemaphoreSlim worker = new(1);
    private CancellationTokenSource? pending;
    private bool ready, closed;
    private int revision;
    private readonly DungeonThemeResources themes = new();
    private TempleDefinition? classicDefinition;
    private TemplePlan? cached;
    private RasterImage? overview, detailPreview;
    public TemplePlan? Plan { get; private set; }
    private readonly IReadOnlyDictionary<string, string>? savedParameters;

    public TempleGeneratorWindow(MapDocument document, AssetStore assets, WallCatalogue walls, TerrainCatalogue terrain, IReadOnlyDictionary<string, string>? parameters = null)
    {
        this.document = document; this.assets = assets; this.walls = walls; this.terrain = terrain; savedParameters = parameters;
        InitializeComponent();
        WidthList.SelectedIndex = 1; TempleList.ItemsSource = TempleDefinition.All; TempleList.SelectedIndex = 0;
        DungeonTypeList.ItemsSource = Enum.GetValues<DungeonType>(); DungeonTypeList.SelectedIndex = 0;
        ThemeList.SelectedIndex = 0;
        timer.Tick += GeneratePreview;
        Loaded += (_, _) => { ready = true; GeneratorPreferences.Restore(this, savedParameters); Schedule(); };
        Closed += (_, _) => { closed = true; timer.Stop(); pending?.Cancel(); };
    }

    private DungeonTheme SelectedTheme => (DungeonTheme)Math.Max(0, ThemeList.SelectedIndex);
    private TempleDefinition? SelectedDefinition => SelectedTheme == DungeonTheme.Classic
        ? TempleList.SelectedItem as TempleDefinition : DungeonThemes.Definition(SelectedTheme);
    private void ThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TempleList == null || DungeonTypeList == null || SizeSlider == null) return;
        bool classic = SelectedTheme == DungeonTheme.Classic;
        if (!classic && TempleList.SelectedItem is TempleDefinition current && TempleDefinition.All.Contains(current)) classicDefinition = current;
        TempleList.ItemsSource = classic ? TempleDefinition.All : new[] { DungeonThemes.Definition(SelectedTheme) };
        TempleList.SelectedItem = classic ? classicDefinition ?? TempleDefinition.All[0] : DungeonThemes.Definition(SelectedTheme);
        TempleList.IsEnabled = classic; DungeonTypeList.IsEnabled = classic;
        SizeSlider.Minimum = classic ? 0 : 3;
        if (!classic)
        {
            DungeonTypeList.SelectedItem = DungeonThemes.Type(SelectedTheme);
            SizeSlider.Value = 5; RoomsSlider.Value = SelectedTheme == DungeonTheme.CelestialPalace ? 9 : 8;
            WidthList.SelectedIndex = 1;
        }
        ThemeDescription.Text = DungeonThemes.Description(SelectedTheme);
        UpdateDoorOptions(); Schedule();
    }
    private void SettingsChanged(object sender, RoutedEventArgs e)
    {
        if (ThemeList != null && SelectedTheme != DungeonTheme.Classic && DungeonTypeList != null)
            DungeonTypeList.SelectedItem = DungeonThemes.Type(SelectedTheme);
        UpdateDoorOptions(); Schedule();
    }
    private void UpdateDoorOptions()
    {
        if (DoorCountSlider==null || SelectedDefinition is not TempleDefinition temple) return;
        var family=walls.Families.Single(f=>f.Id==temple.WallId);
        DoorCountSlider.IsEnabled=GeneratorEntrances.SupportsDungeon(family);
        DoorHint.Text = (family.Id=="stone-temple" ? LocalText.T("Places spaced stair entrances on clear floor space. 0 = none.") : (!family.SupportsDoors ? LocalText.T("This style has no matching wall entrance objects.") : LocalText.T("Total per dungeon. Random entrances stay apart and clear of corners. Small maps may fit fewer.")));
    }
    private void Schedule()
    {
        if (!ready || closed) return;
        revision++; timer.Stop(); pending?.Cancel(); Plan = null; ApplyButton.IsEnabled = false;
        PreviewImage.Opacity = .4; PreviewHint.IsVisible = true; PreviewHint.Text = LocalText.T("Updating preview…");
        timer.Start();
    }
    private void ShuffleClick(object sender, RoutedEventArgs e) => SeedBox.Text = Random.Shared.Next(int.MaxValue).ToString();
    private void RandomizeClick(object sender, RoutedEventArgs e)
    {
        if (SelectedTheme == DungeonTheme.Classic)
        {
            // Pick a different type so Randomize visibly changes the whole recipe.
            int next = Random.Shared.Next(TempleList.Items.Count - 1);
            TempleList.SelectedIndex = next >= TempleList.SelectedIndex ? next + 1 : next;
            DungeonTypeList.SelectedIndex = Random.Shared.Next(2);
        }
        RoomsSlider.Value = Random.Shared.Next(4, 25); SizeSlider.Value = SelectedTheme == DungeonTheme.Classic ? new[] { 0, 3, 4, 5 }[Random.Shared.Next(4)] : Random.Shared.Next(3, 6);
        DoorCountSlider.Value=Random.Shared.Next(1, 5);
        WidthList.SelectedIndex = Random.Shared.Next(2); ShuffleClick(sender, e);
    }
    private async void GeneratePreview(object? sender, EventArgs e)
    {
        timer.Stop();
        if (closed || SelectedDefinition is not TempleDefinition temple) return;
        if (!int.TryParse(SeedBox.Text, out int seed)) { PreviewHint.Text = ((FeedbackText.Text = LocalText.T("Enter a whole-number seed."))); return; }
        int request = revision;
        var options = new TempleOptions(temple, seed, (int)RoomsSlider.Value, (int)SizeSlider.Value, WidthList.SelectedIndex + 1,
            (DungeonType)DungeonTypeList.SelectedItem, DoorCountSlider.IsEnabled ? (int)DoorCountSlider.Value : 0, SelectedTheme);
        bool detail = DetailToggle.IsChecked == true;
        var job = new CancellationTokenSource(); pending = job;
        PreviewHint.Text = LocalText.T("Generating dungeon preview…");
        try
        {
            await worker.WaitAsync(job.Token);
            TemplePlan plan;
            RasterImage bitmap;
            try
            {
                (plan, bitmap) = await Task.Run(() =>
                {
                    var family = walls.Families.Single(f => f.Id == temple.WallId).WithMode(true);
                    var floor = terrain.Sets.Single(s => s.Id == temple.FloorId);
                    var result = cached;
                    if (result == null || result.Options != options || result.SourceRevision != document.PickupRevision)
                    {
                        string? error = family.Availability(assets);
                        if (error != null) throw new InvalidDataException(error);
                        job.Token.ThrowIfCancellationRequested();
                        if (!terrain.MatchesAssets(floor, assets)) throw new InvalidDataException($"The current libraries do not contain the matching {floor.Name} artwork for {temple.Name}.");
                        result = TempleGenerator.Plan(document, family, floor, options, job.Token, assets, themes);
                        cached = result; overview = detailPreview = null;
                    }
                    var preview = (detail ? detailPreview : overview) ?? TemplePreview.Create(result, assets, detail, job.Token);
                    if (detail) detailPreview = preview; else overview = preview;
                    return (result, preview);
                }, job.Token);
            }
            finally { worker.Release(); }
            if (closed || request != revision) return;
            cached = Plan = plan; PreviewImage.Source = bitmap; PreviewImage.Opacity = 1;
            PreviewHint.IsVisible = false; ApplyButton.IsEnabled = true; RandomizeButton.IsEnabled = true;
            int count = plan.RoomCentres.Count - plan.SideRooms.Count;
            FeedbackText.Text = string.Concat(string.Concat(string.Concat(string.Concat(string.Concat(((options.RoomSize == 0 ? LocalText.Format("Corridors Only · {0} connected junctions",count) : (options.Type == DungeonType.Cave ? LocalText.Format("Cave · {0} connected cavern pockets",count) : LocalText.Format("Temple · {0} connected rooms",plan.RoomCount)))), LocalText.Format(" · {0}×{1} cells · Seed {2}",document.Width,document.Height,seed)), ((count < options.Rooms ? LocalText.Format(". Layout limited to {0} to fit this map and native walls.",count) : ("")))), LocalText.Format(" · Entrances {0}/{1}",plan.Entrances.Count,options.DoorCount)), ((plan.SideRooms.Count > 0 ? LocalText.Format(" · {0} corridor side rooms",plan.SideRooms.Count) : ("")))), ((plan.Entrances.Count<options.DoorCount ? LocalText.T(". Fewer entrances fit with safe spacing.") : (""))));
            if (options.Theme != DungeonTheme.Classic)
                FeedbackText.Text = DungeonThemes.Description(options.Theme) + "\n" + FeedbackText.Text +
                    LocalText.Format(" · {0} scene details", plan.Decorations.Count);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!closed && request == revision) { Plan = null; ApplyButton.IsEnabled = false; PreviewImage.Source = null; PreviewHint.Text = LocalText.T("Preview unavailable"); FeedbackText.Text = (ex.Message); }
        }
        finally { if (ReferenceEquals(pending, job)) pending = null; job.Dispose(); }
    }
    private void ApplyClick(object sender, RoutedEventArgs e) { if (Plan != null && ApplyButton.IsEnabled) DialogResult = true; }
}

public static class TemplePreview
{
    public static RasterImage Create(TemplePlan plan, AssetStore assets, bool detail = false, CancellationToken cancellation = default)
    {
        var cells = plan.Generated.Cells.Select(p => new StampCell(p.Key % plan.Generated.Width, p.Key / plan.Generated.Width, p.Value))
            .Where(s => (s.Cell.BackImage & 0x1fffffff) != 0 || s.Cell.ForegroundImages.Count > 0);
        Rect? area = null;
        if (detail)
        {
            var centre = plan.RoomCentres[0];
            // Crop the rendered scene, not its anchors: a tall wall anchored
            // outside this rectangle can still cover floor inside it.
            area = new Rect((centre.X - 24) * 48, (centre.Y - 24) * 32, 48 * 48, 48 * 32);
        }
        return StampPreview.Create(cells.ToArray(), assets, 1600, 1000, cancellation, area, waitForAssets: true)!;
    }
}
