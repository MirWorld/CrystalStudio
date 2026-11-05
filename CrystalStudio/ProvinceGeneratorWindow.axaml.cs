using System.IO;
namespace CrystalStudio;

public partial class ProvinceGeneratorWindow : EditorWindow
{
    private readonly MapDocument document;
    private readonly AssetStore assets;
    private readonly WallCatalogue? suppliedWalls;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim worker = new(1);
    private readonly DispatcherTimer timer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(350) };
    private CancellationTokenSource? pending;
    private TerrainCatalogue terrain = null!;
    private WallCatalogue walls = null!;
    private IReadOnlyList<ProvinceScenery> scenery = [];
    private TerrainWaterResources? water;
    private readonly ProvinceTownResources towns = new();
    private ProvincePlan? cached;
    private RasterImage? overview, detailPreview;
    private bool ready, closed, changingSettings;
    private int revision;
    public ProvincePlan? Plan { get; private set; }
    private readonly IReadOnlyDictionary<string, string>? savedParameters;

    public ProvinceGeneratorWindow(MapDocument document, AssetStore assets, WallCatalogue? wallCatalogue = null, IReadOnlyDictionary<string, string>? parameters = null)
    {
        this.document = document; this.assets = assets; suppliedWalls = wallCatalogue; savedParameters = parameters;
        InitializeComponent();
        TownCountSlider.Maximum = Math.Clamp(document.Width * document.Height / (128 * 128), 1, 8);
        StyleList.ItemsSource = ProvinceStyle.All; StyleList.SelectedIndex = 0;
        SceneList.SelectedIndex = parameters is { Count: > 0 } && !parameters.ContainsKey("SceneList") ? 0 : 1;
        timer.Tick += Generate;
        Loaded += Load;
        Closed += (_, _) => { closed = true; timer.Stop(); pending?.Cancel(); lifetime.Cancel(); };
    }

    private async void Load(object sender, RoutedEventArgs e)
    {
        try
        {
            (terrain, walls, scenery) = await Task.Run(() => (TerrainCatalogue.Load(), suppliedWalls ?? WallCatalogue.Read(WallCatalogue.ResolvePath()),
                ProvinceGenerator.LoadScenery(assets, lifetime.Token)), lifetime.Token);
            water = await TerrainWaterResources.Load(assets, lifetime.Token);
            if (closed) return;
            ready = true; RandomizeButton.IsEnabled = true; GeneratorPreferences.Restore(this, savedParameters); UpdateDoorOptions(); Schedule();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!closed) { PreviewHint.Text = LocalText.T("Province artwork unavailable"); FeedbackText.Text = (ex.Message); } }
    }
    private void SceneChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TownCountSlider == null) return;
        bool village = SceneList.SelectedIndex == (int)ProvinceScene.VillageCountryside;
        TownCountSlider.Minimum = village ? 1 : 0;
        TownCountSlider.Value = village ? Math.Min(TownCountSlider.Maximum, Math.Max(1, document.Width * document.Height / 90000)) : 0;
        if (village)
        {
            ForestSlider.Value = 65; CliffSlider.Value = 35; BeachSlider.Value = 20;
            RiverSlider.Value = 45; VegetationSlider.Value = 70;
        }
        UpdateDoorOptions(); Schedule();
    }
    private void SettingsChanged(object sender, RoutedEventArgs e) { UpdateDoorOptions(); Schedule(); }
    private void UpdateDoorOptions()
    {
        if (DoorCountSlider==null || StyleList.SelectedItem is not ProvinceStyle style) return;
        var family=(walls ?? suppliedWalls)?.Families.Single(f=>f.Id==style.WallId);
        DoorCountSlider.IsEnabled=family?.SupportsDoors==true && CliffSlider.Value>0;
        DoorHint.Text = (family?.SupportsDoors!=true ? LocalText.T("This style has no matching cliff entrance objects.") : (CliffSlider.Value==0 ? LocalText.T("Enable cliffs to place entrances.") : LocalText.T("Total across generated cliffs. Random entrances stay apart. Maps without suitable cliffs may fit fewer.")));
    }
    private void Schedule()
    {
        if (!ready || closed || changingSettings) return;
        revision++; timer.Stop(); pending?.Cancel(); Plan = null; ApplyButton.IsEnabled = false;
        PreviewImage.Opacity = .4; PreviewHint.IsVisible = true; PreviewHint.Text = LocalText.T("Updating province…"); timer.Start();
    }
    private void ShuffleClick(object sender, RoutedEventArgs e) => SeedBox.Text = Random.Shared.Next(int.MaxValue).ToString();
    private void RandomizeClick(object sender, RoutedEventArgs e)
    {
        if (!ready || closed) return;
        changingSettings = true;
        try
        {
            int next = Random.Shared.Next(StyleList.Items.Count - 1);
            StyleList.SelectedIndex = next >= StyleList.SelectedIndex ? next + 1 : next;
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
        if (!int.TryParse(SeedBox.Text, out int seed)) { PreviewHint.Text = ((FeedbackText.Text = LocalText.T("Enter a whole-number seed."))); return; }
        var options = new ProvinceOptions(seed, StyleList.SelectedItem as ProvinceStyle, (int)ForestSlider.Value,
            (int)CliffSlider.Value, (int)BeachSlider.Value, (int)RiverSlider.Value, (int)VegetationSlider.Value, (int)TownCountSlider.Value,
            DoorCountSlider.IsEnabled ? (int)DoorCountSlider.Value : 0, (ProvinceScene)SceneList.SelectedIndex);
        int request = revision; bool detail = DetailToggle.IsChecked == true;
        var job = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); pending = job;
        try
        {
            await worker.WaitAsync(job.Token);
            ProvincePlan plan; RasterImage bitmap;
            try
            {
                (plan, bitmap) = await Task.Run(() =>
                {
                    var p = cached;
                    if (p == null || p.Options != options || p.SourceRevision != document.PickupRevision)
                    {
                        p = ProvinceGenerator.Plan(document, terrain, walls, scenery, options, job.Token, water?.Banks, water?.Shore, assets, towns: towns);
                        // Keep a finished plan even if a preview-mode change cancels
                        // rendering. Only generation options invalidate the map.
                        cached = p; overview = detailPreview = null;
                    }
                    var preview = detail ? detailPreview : overview;
                    preview ??= ProvincePreview.Create(p.Generated, assets, detail, job.Token);
                    if (detail) detailPreview = preview; else overview = preview;
                    return (p, preview);
                }, job.Token);
            }
            finally { worker.Release(); }
            if (closed || request != revision) return;
            Plan = plan; PreviewImage.Source = bitmap; PreviewImage.Opacity = 1;
            PreviewHint.IsVisible = false; ApplyButton.IsEnabled = true;
            FeedbackText.Text = string.Concat(string.Concat(LocalText.Format("{0} · {1}×{2} · Seed {3}\n",plan.Style.Name,document.Width,document.Height,seed), (string.Join(" · ", plan.Features))), ((plan.Notes.Count == 0 ? ("") : string.Concat(("\n"), (string.Join(" ", plan.Notes))))));
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

public static class ProvincePreview
{
    public static RasterImage Create(MapDocument map, AssetStore assets, bool detail = false, CancellationToken cancellation = default)
    {
        var cells = map.Cells.Select(p => new StampCell(p.Key % map.Width, p.Key / map.Width, p.Value)).ToArray();
        Rect area = detail ? new((map.Width / 2 - 32) * 48, (map.Height / 2 - 32) * 32, 64 * 48, 64 * 32)
            : new(0, 0, map.Width * 48, map.Height * 32);
        return StampPreview.Create(cells, assets, 1600, 1000, cancellation, area, waitForAssets: true, fastRaster: !detail)!;
    }
}
