using System.IO;
namespace CrystalStudio;

public partial class TerrainGeneratorWindow : EditorWindow
{
    private readonly MapDocument document;
    private readonly AssetStore assets;
    private readonly TerrainCatalogue catalogue;
    private readonly CancellationTokenSource cancellation = new();
    private readonly DispatcherTimer previewTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(350) };
    private CancellationTokenSource? previewCancellation;
    private IReadOnlyList<TerrainSet> available = [];
    private IReadOnlyList<RiverCoastLayout> coastLayouts = [];
    private RiverCoastPieces? coastPieces, bridgePieces;
    private RiverShoreComponents? shoreComponents;
    private IslandCoastObjects? islandCoast;
    private string? islandError;
    private bool IsIsland => BiomeList.SelectedItem as string == "Island";
    private string? coastError;
    private bool ready, closed, changingSettings, waterReady;
    private TerrainWaterMode WaterMode => RiverToggle.IsChecked == true
        ? BeachToggle.IsChecked == true ? TerrainWaterMode.RiverToSea : TerrainWaterMode.River
        : BeachToggle.IsChecked == true ? TerrainWaterMode.Beach : TerrainWaterMode.None;
    private int revision;
    private TerrainPlan? cachedPlan;
    private RasterImage? overview, detailPreview;
    private TerrainOptions? cachedOptions;
    private TerrainArea cachedArea;
    private Point? inspectionCentre;
    private Rect displayedRegion;
    internal GeneratorPreviewState PreviewState { get; } = new();
    public TerrainPlan? Plan { get; private set; }
    internal bool PreferencesRestored { get; private set; }
    private readonly IReadOnlyDictionary<string, string>? savedParameters;
    public TerrainGeneratorWindow(MapDocument document, AssetStore assets, TerrainCatalogue catalogue, IReadOnlyDictionary<string, string>? parameters = null)
    {
        this.document = document; this.assets = assets; this.catalogue = catalogue; savedParameters = parameters;
        InitializeComponent();
        RouteList.SelectedIndex = 0;
        previewTimer.Tick += PreviewTimerTick;
        Loaded += LoadAvailable;
        Closed += (_, _) => { closed = true; previewTimer.Stop(); previewCancellation?.Cancel(); cancellation.Cancel(); };
    }
    private async void LoadAvailable(object sender, RoutedEventArgs e)
    {
        PreviewHint.Text = LocalText.T("Checking terrain artwork\u2026");
        try
        {
            available = await Task.Run(() => catalogue.Sets.Where(s => { cancellation.Token.ThrowIfCancellationRequested(); return MapResourcePreflight.Available(() => catalogue.MatchesAssets(s, assets)); }).ToList(), cancellation.Token);
            if (closed) return;
            // Limit this window only; retain every biome for future full-map generation.
            BiomeList.ItemsSource = available.Where(s => s.Base && s.Biome is "Desert" or "Province").Select(s => s.Biome).Append("Island").Distinct().OrderBy(s => s).ToList();
            ready = true;
            PreviewState.Report(new([new("Base ground", true, available.Any(set => set.Base && set.Biome is "Desert" or "Province"))]));
            RandomizeButton.IsEnabled = BiomeList.Items.Count > 0;
            PreviewHint.Text = LocalText.T("The current libraries do not contain matching catalogue terrain artwork.");
            BiomeList.SelectedItem = available.Any(s => s.Base && s.Biome == "Province") ? "Province" : BiomeList.Items.Cast<string>().FirstOrDefault();
            // Make terrain controls usable before optional water artwork loads.
            waterReady=await Task.Run(()=>catalogue.Water?.MatchesAssets(assets)==true,cancellation.Token);
            var water=await TerrainWaterResources.Load(assets,cancellation.Token);
            if(closed)return;
            coastPieces=water.Banks;bridgePieces=water.Bridges;shoreComponents=water.Shore;coastError=water.Error;
            islandCoast=water.Island;islandError=water.IslandError;
            ReportResources();
            if(!closed){UpdateWaterChoices();if(IsIsland)SchedulePreview();}
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!closed) { PreviewHint.Text = LocalText.T("Terrain artwork could not be loaded."); FeedbackText.Text = (ex.Message); } }
        finally
        {
            if (ready && !closed)
            {
                GeneratorPreferences.Restore(this, savedParameters); PreferencesRestored = true;
                if (ApplyButton.IsEnabled && Plan != null && int.TryParse(SeedBox.Text, out int seed) && Plan.Seed == seed) PreviewState.Complete();
                else PreviewState.Notify();
            }
        }
    }
    private void BiomeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ready) return;
        WaterTab.IsVisible=IsIsland?false:true;
        if(IsIsland)TerrainTabs.SelectedIndex=0;
        BaseList.ItemsSource = available.Where(s => s.Base && (IsIsland
            ?s.Usage.Any(u=>u.FileName=="5")&&TerrainSurfaceRoute.Find(catalogue,s,catalogue.Sets.Single(t=>t.Id=="0:0:2300"))!=null
            :s.Biome == BiomeList.SelectedItem as string)).OrderBy(s => s.Name).ToList();
        BaseList.SelectedItem = BaseList.Items.Cast<TerrainSet>().FirstOrDefault(s => s.Id == "0:0:1450") ?? BaseList.Items.Cast<TerrainSet>().FirstOrDefault();
        // Categories can retain the same base object (Province -> Island).
        // Rebuild the settings and invalidate the old preview even without a selection event.
        BaseChanged(sender,e);
    }
    private void BaseChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ready) return;
        if (BaseList.SelectedItem is not TerrainSet basis) { SchedulePreview(); return; }
        var overlays = available.Where(s => (!IsIsland||s.Usage.Any(u=>u.FileName=="5"))&&catalogue.GenerationLayers(basis, s) is { } layers && layers.All(available.Contains)).OrderBy(s => s.Name).ToList();
        var choices = new object[] { "None" }.Concat(overlays).ToList();
        PatchList.ItemsSource = choices; PathList.ItemsSource = choices;
        PatchList.SelectedItem = (object?)overlays.FirstOrDefault(s => s.Material is "Grass" or "Mud") ?? choices[0];
        PathList.SelectedItem = (object?)overlays.FirstOrDefault(s => s.Material is "Dirt" or "Sand") ?? choices[0];
        UpdateWaterChoices();
        SettingsChanged(sender, e);
    }
    private void SettingsChanged(object sender, RoutedEventArgs e)
    {
        if(ReferenceEquals(sender,RiverToggle)||ReferenceEquals(sender,BeachToggle))UpdateBridgeChoice();
        SchedulePreview();
    }
    private void UpdateBridgeChoice()
    {
        bool enabled=bridgePieces!=null&&RiverToggle.IsChecked==true;
        BridgeToggle.IsEnabled=enabled;
        if(!enabled)BridgeToggle.IsChecked=false;
        ToolTip.SetTip(BridgeToggle, (enabled ? LocalText.T("Keep a Walkable Crossing in the Generated River.") : LocalText.T("No Reviewed Bridge with Gradual River Turns Fits This Area.")));
    }
    private void UpdateWaterChoices()
    {
        bool compatible=!IsIsland&&waterReady && catalogue.Water != null && BaseList.SelectedItem is TerrainSet basis && catalogue.Water.LandRoute(catalogue,basis) is {} route && route.All(available.Contains) && available.Any(s=>s.Id==catalogue.Water.WaterId) && Area.Width>=64 && Area.Height>=64;
        // Availability only describes loaded capabilities. The requested seed
        // checks exact fit on the preview worker; never generate sample maps here.
        RiverToggle.IsEnabled=compatible&&coastPieces!=null;
        BeachToggle.IsEnabled=compatible&&shoreComponents!=null&&Area.X==0&&Area.Y==0&&Area.Width==document.Width&&Area.Height==document.Height
            &&(long)Area.Width*Area.Height<=RiverPieceGroundAssembler.MaximumCoastCells;
        if(!RiverToggle.IsEnabled)RiverToggle.IsChecked=false;
        if(!BeachToggle.IsEnabled)BeachToggle.IsChecked=false;
        UpdateBridgeChoice();
    }
    private void SchedulePreview()
    {
        if (!ready || closed || changingSettings) return;
        revision++;
        previewTimer.Stop(); previewCancellation?.Cancel();
        Plan = null; ApplyButton.IsEnabled = false;
        PreviewHint.IsVisible = true;
        PreviewImage.Opacity = .45;
        if (BaseList.SelectedItem is not TerrainSet) { PreviewHint.Text = LocalText.T("Choose a base terrain."); return; }
        PreviewHint.Text = LocalText.T("Updating preview\u2026");
        previewTimer.Start();
    }
    private void ShuffleClick(object sender, RoutedEventArgs e)
    {
        SeedBox.Text = Random.Shared.Next(int.MaxValue).ToString();
        // A button click is complete input; only typing and slider changes need debounce.
        if (ready && !closed) PreviewTimerTick(sender, EventArgs.Empty);
    }
    private void RandomizeClick(object sender, RoutedEventArgs e)
    {
        if (!ready || BiomeList.Items.Count == 0) return;
        changingSettings = true;
        try
        {
            BiomeList.SelectedIndex = Random.Shared.Next(BiomeList.Items.Count);
            BaseList.SelectedIndex = Random.Shared.Next(BaseList.Items.Count);
            PatchList.SelectedIndex = Random.Shared.Next(PatchList.Items.Count);
            PathList.SelectedIndex = Random.Shared.Next(PathList.Items.Count);
            CoverageSlider.Value = Random.Shared.Next(21) / 20.0;
            ScaleSlider.Value = Random.Shared.Next((int)ScaleSlider.Minimum, (int)ScaleSlider.Maximum + 1);
            WidthSlider.Value = Random.Shared.Next((int)WidthSlider.Minimum, (int)WidthSlider.Maximum + 1);
            RouteList.SelectedIndex = Random.Shared.Next(RouteList.Items.Count);
            RiverToggle.IsChecked=RiverToggle.IsEnabled&&Random.Shared.Next(2)==1;
            BeachToggle.IsChecked=BeachToggle.IsEnabled&&Random.Shared.Next(2)==1;
            UpdateBridgeChoice();
            BridgeToggle.IsChecked=BridgeToggle.IsEnabled&&Random.Shared.Next(2)==1;
            SeedBox.Text = Random.Shared.Next(int.MaxValue).ToString();
        }
        finally { changingSettings = false; SchedulePreview(); }
    }
    private TerrainArea Area => new(0, 0, document.Width, document.Height);
    private async void PreviewTimerTick(object? sender, EventArgs e)
    {
        previewTimer.Stop();
        if (closed || BaseList.SelectedItem is not TerrainSet basis) return;
        if (!int.TryParse(SeedBox.Text, out int seed)) { PreviewHint.Text = ((FeedbackText.Text = LocalText.T("Enter a whole-number seed."))); return; }
        ReportResources();
        if (PreviewState.Resources?.CanGenerate == false)
        {
            PreviewHint.Text = LocalText.T("Preview unavailable"); FeedbackText.Text = PreviewState.Resources.Summary; return;
        }
        if(IsIsland&&islandCoast==null){PreviewHint.Text = LocalText.T("Island artwork is unavailable");FeedbackText.Text = (islandError ?? LocalText.T("Loading the original 5.map coast artwork\u2026"));return;}
        var options = new TerrainOptions(basis, PatchList.SelectedItem as TerrainSet, PathList.SelectedItem as TerrainSet, seed, CoverageSlider.Value, ScaleSlider.Value, WidthSlider.Value, RouteList.SelectedIndex + 1,WaterMode){BridgeCrossing=BridgeToggle.IsChecked==true,Island=IsIsland};
        var requestedPieces=options.BridgeCrossing?bridgePieces:coastPieces;
        var area = Area;
        bool detail = DetailToggle.IsChecked == true;
        Point? centre = inspectionCentre;
        int requestedRevision = revision;
        var job = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
        previewCancellation = job;
        PreviewHint.Text = LocalText.T("Generating preview\u2026"); PreviewHint.IsVisible = true;
        try
        {
            var plan = cachedPlan != null && cachedOptions == options && cachedArea == area && cachedPlan.SourceRevision == document.PickupRevision
                ? cachedPlan
                : await Task.Run(() => TerrainGenerator.Plan(document, area, catalogue, options, job.Token,coastLayouts,requestedPieces,shoreComponents,islandCoast), job.Token);
            if (closed || requestedRevision != revision) return;
            // Changing preview detail does not change the terrain or rerun river planning.
            if (!ReferenceEquals(cachedPlan, plan)) overview = detailPreview = null;
            cachedPlan = plan; cachedOptions = options; cachedArea = area;
            var bitmap = (detail ? detailPreview : overview) ?? await Task.Run(() =>
            {
                job.Token.ThrowIfCancellationRequested();
                var result = TerrainPreview.Create(plan, assets, detail,job.Token,centre);
                job.Token.ThrowIfCancellationRequested();
                return result;
            }, job.Token);
            if (closed || requestedRevision != revision) return;
            if (detail) detailPreview = bitmap; else overview = bitmap;
            PreviewImage.Source = bitmap; PreviewImage.Opacity = 1;
            Plan = plan; ApplyButton.IsEnabled = true; PreviewHint.IsVisible = false;
            displayedRegion = TerrainPreview.Region(plan, detail, inspectionCentre);
            FeedbackText.Text = string.Concat(string.Concat(string.Concat(string.Concat(LocalText.Format("{0:N0} ground tiles",plan.Tiles.Count), ((plan.Water==null ? ("") : LocalText.Format(" + {0:N0} native bank sprites",plan.Water.Banks.Count)))), (string.Concat(plan.CoastObjects.Select(c=>$" + {c.Layout.Name}")))), LocalText.Format(" · Area {0},{1} — {2}×{3} cells · Seed {4}. Apply creates one undo step; existing objects and collisions are preserved.",plan.Area.X,plan.Area.Y,plan.Area.Width,plan.Area.Height,seed)), ((plan.Notes.Count==0 ? ("") : string.Concat(("\n"), (string.Join(" ",plan.Notes))))));
            PreviewState.Complete();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!closed && requestedRevision == revision) { Plan = null; PreviewImage.Source = null; PreviewHint.Text = LocalText.T("Preview unavailable"); FeedbackText.Text = (ex.Message); } }
        finally { if (ReferenceEquals(previewCancellation, job)) previewCancellation = null; job.Dispose(); }
    }
    private void DetailChanged(object sender, RoutedEventArgs e)
    {
        SchedulePreview();
    }
    private void ReportResources()
    {
        PreviewState.Report(new([
            new("Base ground", true, BaseList.SelectedItem is TerrainSet basis && available.Contains(basis)),
            new("River banks", false, coastPieces != null && waterReady),
            new("Beach and shoreline", false, shoreComponents != null),
            new("River crossing", false, bridgePieces != null),
            new("Island coastline", IsIsland, islandCoast != null, IsIsland ? islandError : null)
        ]));
    }
    internal void Inspect(Point normalized)
    {
        if (PreviewImage.Source == null || !MapPreviewRegion.TrySelect(normalized, displayedRegion, PreviewImage.Source.Size, Plan?.CoastObjects.Count > 0, out var centre)) return;
        inspectionCentre = centre;
        detailPreview = null; DetailToggle.IsChecked = true; SchedulePreview();
    }
    private void ApplyClick(object sender, RoutedEventArgs e) { if (Plan != null && ApplyButton.IsEnabled && QuickMapPreview.Confirm(this)) DialogResult = true; }
}
