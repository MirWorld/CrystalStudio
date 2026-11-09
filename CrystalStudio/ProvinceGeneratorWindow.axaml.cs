using System.IO;
namespace CrystalStudio;

public partial class ProvinceGeneratorWindow : EditorWindow
{
    private readonly MapDocument document;
    private readonly AssetStore assets;
    internal AssetStore PreviewAssets => assets;
    private readonly WallCatalogue? suppliedWalls;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim worker = new(1);
    private readonly DispatcherTimer timer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(350) };
    private CancellationTokenSource? pending;
    private TerrainCatalogue terrain = null!;
    private WallCatalogue walls = null!;
    private IReadOnlyList<ProvinceScenery> scenery = [];
    private TerrainWaterResources? water;
    private Task<(IReadOnlyList<ProvinceScenery> Scenery, TerrainWaterResources Water)>? optionalResources;
    private readonly ProvinceTownResources towns = new();
    private readonly ProvinceRuinResources ruins = new();
    private readonly ProvinceMineResources mines = new();
    private ProvincePlan? cached;
    private GenerationImpact? cachedImpact;
    private ProvinceOptions? cachedRequest;
    private MapResourceCache resourceCache = null!;
    private Point? inspectionCentre;
    private Point? detailCentre;
    private Rect displayedRegion;
    internal (Rect Region, bool Padded) DesignFrame => (displayedRegion, true);
    internal GeneratorPreviewState PreviewState { get; } = new();
    internal GeneratorWorkflow Workflow { get; }
    private RasterImage? overview, detailPreview;
    internal RasterImage? OverviewPreview => overview;
    private bool ready, closed, changingSettings;
    private int revision;
    private readonly GeneratorChoiceDrafts sceneDrafts = new("SceneList");
    public ProvincePlan? Plan { get; private set; }
    internal bool PreferencesRestored { get; private set; }
    private readonly IReadOnlyDictionary<string, string>? savedParameters;

    public ProvinceGeneratorWindow(MapDocument document, AssetStore assets, WallCatalogue? wallCatalogue = null, IReadOnlyDictionary<string, string>? parameters = null)
    {
        this.document = document; this.assets = assets; suppliedWalls = wallCatalogue; savedParameters = parameters;
        PreviewState.SetSource(assets);
        InitializeComponent();
        PreviewMapInfo.Text = LocalText.Format("Apply to current map · {0} × {1} cells · One undo step. This does not create a new map.", document.Width, document.Height);
        ApplyButton.Content = LocalText.T("Apply to current map");
        TownCountSlider.Maximum = Math.Clamp(document.Width * document.Height / (128 * 128), 1, 8);
        StyleList.ItemsSource = ProvinceStyle.All; StyleList.SelectedIndex = 0;
        GenerationAlgorithms.Register(this, parameters, Schedule);
        SceneList.SelectedIndex = parameters is { Count: > 0 } && !parameters.ContainsKey("SceneList") ? 0 : 1;
        GeneratorPreferences.Restore(this, savedParameters);
        timer.Tick += Generate;
        Loaded += Load;
        Workflow = new(this, PreviewState, CancelPreview, () => { resourceCache?.Clear(); Schedule(); }, () => pending != null || timer.IsEnabled);
        Closed += (_, _) => { closed = true; timer.Stop(); pending?.Cancel(); lifetime.Cancel(); };
    }

    private async void Load(object sender, RoutedEventArgs e)
    {
        try
        {
            (terrain, walls) = await Task.Run(() =>
            {
                var ground = TerrainCatalogue.Load(format: assets.Format);
                var cliffs = suppliedWalls;
                if (cliffs == null)
                {
                    try { cliffs = WallCatalogue.Read(WallCatalogue.ResolvePath(), assets.Format); }
                    catch (IOException) { cliffs = new(1, []); }
                }
                return (ground, cliffs);
            }, lifetime.Token);
            if (closed) return;
            resourceCache = new(terrain, assets);
            var style = (ProvinceStyle)StyleList.SelectedItem!;
            var required = await Task.Run(() => MapResourcePreflight.ProvinceGround(style, resourceCache), lifetime.Token);
            if (closed) return;
            PreviewState.Report(required);
            ready = true; RandomizeButton.IsEnabled = true; PreferencesRestored = true; UpdateDoorOptions(); Schedule();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!closed) { PreviewHint.Text = LocalText.T("Province artwork unavailable"); FeedbackText.Text = (ex.Message); PreviewState.SetPhase(GeneratorPreviewPhase.Failed, ex.Message); } }
    }
    private void SceneChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TownCountSlider == null) return;
        bool wasChanging = changingSettings;
        changingSettings = true;
        try { sceneDrafts.Switch(this, SceneList.SelectedIndex, ConfigureScene); }
        finally { changingSettings = wasChanging; }
        UpdateDoorOptions(); Schedule();
    }
    private void ConfigureScene()
    {
        bool village = SceneList.SelectedIndex == (int)ProvinceScene.VillageCountryside;
        bool forestRuins = SceneList.SelectedIndex == (int)ProvinceScene.ForestRuins;
        bool mining = SceneList.SelectedIndex == (int)ProvinceScene.MiningQuarry;
        if (MineOptions != null) MineOptions.IsVisible = mining;
        if (RuinOptions != null) RuinOptions.IsVisible = forestRuins;
        StyleList.IsEnabled = !forestRuins && !mining;
        if (forestRuins || mining) StyleList.SelectedIndex = 0;
        TownCountSlider.Minimum = village ? 1 : 0;
        TownCountSlider.Value = village ? Math.Min(TownCountSlider.Maximum, Math.Max(1, document.Width * document.Height / 90000)) : 0;
        if (forestRuins)
        {
            ForestSlider.Value = 100; CliffSlider.Value = 35; BeachSlider.Value = 0;
            RiverSlider.Value = 40; VegetationSlider.Value = 85;
            if (RuinCountSlider != null) RuinCountSlider.Value = Math.Clamp(document.Width * document.Height / 90000, 1, 4);
        }
        if (mining)
        {
            ForestSlider.Value = 55; CliffSlider.Value = 100; BeachSlider.Value = 0;
            RiverSlider.Value = 25; VegetationSlider.Value = 35;
            if (MineCountSlider != null) MineCountSlider.Value = Math.Clamp(document.Width * document.Height / 100000, 1, 4);
        }
        if (village)
        {
            ForestSlider.Value = 65; CliffSlider.Value = 35; BeachSlider.Value = 20;
            RiverSlider.Value = 45; VegetationSlider.Value = 70;
        }
    }
    private void SettingsChanged(object sender, RoutedEventArgs e)
    {
        if (SceneList?.SelectedIndex is (int)ProvinceScene.ForestRuins or (int)ProvinceScene.MiningQuarry && StyleList.SelectedIndex != 0) StyleList.SelectedIndex = 0;
        UpdateDoorOptions(); Schedule();
    }
    private Task<(IReadOnlyList<ProvinceScenery> Scenery, TerrainWaterResources Water)> LoadOptionalResources() => optionalResources ??= Task.Run(async () =>
    {
        IReadOnlyList<ProvinceScenery> decoration;
        try { decoration = ProvinceGenerator.LoadScenery(assets, lifetime.Token); }
        catch (Exception exception) when (exception is IOException or InvalidDataException or System.Text.Json.JsonException) { decoration = []; }
        TerrainWaterResources shoreline;
        try { shoreline = await TerrainWaterResources.Load(assets, lifetime.Token); }
        catch (Exception exception) when (exception is IOException or InvalidDataException or System.Text.Json.JsonException)
        { shoreline = new(null, null, null, exception.Message, null, exception.Message); }
        return (decoration, shoreline);
    }, lifetime.Token);
    private void UpdateDoorOptions()
    {
        if (DoorCountSlider==null || StyleList.SelectedItem is not ProvinceStyle style) return;
        var family=(walls ?? suppliedWalls)?.Families.FirstOrDefault(f=>f.Id==style.WallId);
        DoorCountSlider.IsEnabled=family?.SupportsDoors==true && CliffSlider.Value>0;
        DoorHint.Text = (family?.SupportsDoors!=true ? LocalText.T("This style has no matching cliff entrance objects.") : (CliffSlider.Value==0 ? LocalText.T("Enable cliffs to place entrances.") : LocalText.T("Total across generated cliffs. Random entrances stay apart. Maps without suitable cliffs may fit fewer.")));
    }
    private void Schedule()
    {
        if (!ready || closed || changingSettings) return;
        revision++; timer.Stop(); pending?.Cancel(); Plan = null; ApplyButton.IsEnabled = false;
        PreviewState.SetPhase(GeneratorPreviewPhase.Waiting);
        PreviewImage.Opacity = .4; PreviewHint.IsVisible = true; PreviewHint.Text = LocalText.T("Updating province…"); timer.Start();
    }
    internal void CancelPreview()
    {
        timer.Stop(); Plan = null; ApplyButton.IsEnabled = false;
        PreviewState.SetPhase(pending == null ? GeneratorPreviewPhase.Cancelled : GeneratorPreviewPhase.Cancelling);
        pending?.Cancel();
    }
    private void ShuffleClick(object sender, RoutedEventArgs e) => SeedBox.Text = Random.Shared.Next(int.MaxValue).ToString();
    private void RandomizeClick(object sender, RoutedEventArgs e)
    {
        if (!ready || closed) return;
        changingSettings = true;
        try
        {
            int next = Random.Shared.Next(StyleList.Items.Count - 1);
            if (SceneList.SelectedIndex is not ((int)ProvinceScene.ForestRuins or (int)ProvinceScene.MiningQuarry)) StyleList.SelectedIndex = next >= StyleList.SelectedIndex ? next + 1 : next;
            foreach (var slider in new[] { ForestSlider, CliffSlider, BeachSlider, RiverSlider, VegetationSlider, TownCountSlider })
                slider.Value = Random.Shared.Next((int)slider.Minimum, (int)slider.Maximum + 1);
            DoorCountSlider.Value=Random.Shared.Next(1, 5);
            ShuffleClick(sender, e);
        }
        finally { changingSettings = false; Schedule(); }
    }
    private async void Generate(object? sender, EventArgs e)
    {
        timer.Stop();
        if (!ready || closed) return;
        if (!int.TryParse(SeedBox.Text, out int seed)) { PreviewHint.Text = ((FeedbackText.Text = LocalText.T("Enter a whole-number seed."))); PreviewState.SetPhase(GeneratorPreviewPhase.Failed, FeedbackText.Text); return; }
        PreviewState.SetPhase(GeneratorPreviewPhase.Checking);
        var options = new ProvinceOptions(seed, StyleList.SelectedItem as ProvinceStyle, (int)ForestSlider.Value,
            (int)CliffSlider.Value, (int)BeachSlider.Value, (int)RiverSlider.Value, (int)VegetationSlider.Value, (int)TownCountSlider.Value,
            DoorCountSlider.IsEnabled ? (int)DoorCountSlider.Value : 0, (ProvinceScene)SceneList.SelectedIndex, (int)RuinCountSlider.Value, (int)MineCountSlider.Value)
        { AlgorithmVersion = GenerationAlgorithms.Version(this), Design = GenerationAlgorithms.Design(this) };
        int request = revision; bool detail = DetailToggle.IsChecked == true;
        var job = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); pending = job;
        PreviewState.SetStage("Checking required and optional artwork…");
        try
        {
            await worker.WaitAsync(job.Token);
            ProvincePlan plan; RasterImage bitmap;
            try
            {
                var required = await Task.Run(() => MapResourcePreflight.ProvinceGround(options.Style!, resourceCache), job.Token);
                if (closed || request != revision) return;
                PreviewState.Report(required); required.EnsureReady();
                if (optionalResources?.IsCompleted != true) PreviewHint.Text = LocalText.T("Checking optional scenery and water artwork…");
                PreviewState.SetStage("Loading optional scenery and water artwork…");
                (scenery, water) = await LoadOptionalResources().WaitAsync(job.Token);
                if (closed || request != revision) return;
                var preflight = await Task.Run(() => MapResourcePreflight.Province(terrain, walls, assets, scenery, water, options, towns, resourceCache, job.Token), job.Token);
                if (closed || request != revision) return;
                PreviewState.Report(preflight.Report); preflight.Report.EnsureReady();
                PreviewState.SetPhase(GeneratorPreviewPhase.Generating);
                PreviewState.SetStage("Planning layout and filling artwork…");
                Point? centre = inspectionCentre;
                (plan, bitmap) = await Task.Run(() =>
                {
                    var p = cached;
                    if (p == null || cachedRequest != options || p.SourceRevision != document.PickupRevision)
                    {
                        p = ProvinceGenerator.Plan(document, terrain, walls, scenery, preflight.Options, job.Token, water?.Banks, water?.Shore, assets, towns: towns, ruins: ruins, mines: mines);
                        // Keep a finished plan even if a preview-mode change cancels
                        // rendering. Only generation options invalidate the map.
                        cached = p; cachedRequest = options; overview = detailPreview = null; cachedImpact = null;
                    }
                    cachedImpact ??= GenerationImpact.Compare(document, p.Generated, job.Token);
                    Dispatcher.UIThread.Post(() => { if (!closed && request == revision && !job.IsCancellationRequested) PreviewState.SetStage("Rendering preview…"); });
                    var preview = detail ? detailCentre == centre ? detailPreview : null : overview;
                    preview ??= ProvincePreview.Create(p.Generated, assets, detail, job.Token, centre);
                    if (detail) { detailPreview = preview; detailCentre = centre; } else overview = preview;
                    return (p, preview);
                }, job.Token);
            }
            finally { worker.Release(); }
            job.Token.ThrowIfCancellationRequested();
            if (closed || request != revision) return;
            Plan = plan; PreviewImage.Source = bitmap; PreviewImage.Opacity = 1;
            displayedRegion = ProvincePreview.Region(plan.Generated, detail, inspectionCentre);
            PreviewHint.IsVisible = false; ApplyButton.IsEnabled = true;
            FeedbackText.Text = string.Concat(string.Concat(LocalText.Format("{0} · {1}×{2} · Seed {3}\n",plan.Style.Name,document.Width,document.Height,seed), (string.Join(" · ", plan.Features))), ((plan.Notes.Count == 0 ? ("") : string.Concat(("\n"), (string.Join(" ", plan.Notes))))));
            PreviewState.SetImpact(cachedImpact!); PreviewState.Complete();
        }
        catch (OperationCanceledException) { if (!closed && request == revision) PreviewState.SetPhase(GeneratorPreviewPhase.Cancelled); }
        catch (Exception ex)
        {
            if (!closed && request == revision) { Plan = null; ApplyButton.IsEnabled = false; PreviewImage.Source = null; PreviewHint.Text = LocalText.T("Preview unavailable"); PreviewState.SetFailure(ex, "Province generation"); FeedbackText.Text = PreviewState.Failure; }
        }
        finally { if (ReferenceEquals(pending, job)) { pending = null; if (PreviewState.Phase == GeneratorPreviewPhase.Cancelling) PreviewState.SetPhase(GeneratorPreviewPhase.Cancelled); else PreviewState.Notify(); } job.Dispose(); }
    }
    internal void Inspect(Point normalized)
    {
        if (PreviewImage.Source == null || !MapPreviewRegion.TrySelect(normalized, displayedRegion, PreviewImage.Source.Size, true, out var centre)) return;
        inspectionCentre = centre;
        detailPreview = null; DetailToggle.IsChecked = true; Schedule();
    }
    private async void ApplyClick(object sender, RoutedEventArgs e) { var plan = Plan; if (plan != null && ApplyButton.IsEnabled && await QuickMapPreview.ConfirmAsync(this) && ReferenceEquals(Plan, plan) && PreviewState.Phase == GeneratorPreviewPhase.Ready) DialogResult = true; }
}

public static class ProvincePreview
{
    internal static Rect Region(MapDocument map, bool detail, Point? centre) => detail
        ? MapPreviewRegion.Detail(new Rect(0, 0, map.Width, map.Height), centre ?? new Point(map.Width / 2.0, map.Height / 2.0), 32, 24)
        : new Rect(0, 0, map.Width, map.Height);
    public static RasterImage Create(MapDocument map, AssetStore assets, bool detail = false, CancellationToken cancellation = default, Point? centre = null)
    {
        var cells = map.Cells.Select(p => new StampCell(p.Key % map.Width, p.Key / map.Width, p.Value)).ToArray();
        Rect region = Region(map, detail, centre);
        Rect area = new(region.X * 48, region.Y * 32, region.Width * 48, region.Height * 32);
        return StampPreview.Create(cells, assets, 1600, 1000, cancellation, area, waitForAssets: true, fastRaster: !detail)!;
    }
}
