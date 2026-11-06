using System.IO;
namespace CrystalStudio;

public partial class TownStampWindow : EditorWindow
{
    private readonly AssetStore assets;
    private readonly TownCatalogue catalogue;
    private readonly TerrainCatalogue terrain;
    private TownWallResources? wallResources;
    private readonly Dictionary<string, TownFamily> prepared = [];
    private readonly Dictionary<string, int> skipped = [];
    private readonly Dictionary<(int Library, int Image), Sprite> sprites = [];
    private readonly DispatcherTimer debounce = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? pending;
    private TownPlan? cachedPlan;
    private RasterImage? overview, detailPreview;
    private int version;
    private bool ready, working, changingSettings;
    public TownPlan? Plan { get; private set; }
    public TownStampWindow(AssetStore assets, TownCatalogue catalogue, TownPlan? previous = null)
    {
        this.assets = assets; this.catalogue = catalogue; terrain = TerrainCatalogue.Load(format: assets.Format);
        InitializeComponent();
        TypeList.ItemsSource = catalogue.Families;
        TypeList.SelectedItem = catalogue.Families.FirstOrDefault(f => f.Id == previous?.Family.Id) ?? catalogue.Families[0];
        var options = previous?.Options ?? new();
        SizeList.SelectedIndex = options.Size switch { 64 => 0, 144 => 2, 192 => 3, _ => 1 };
        DensitySlider.Value = options.Density; GapSlider.Value = options.Spacing; WallChanceSlider.Value = options.WallChance;
        LayoutList.SelectedValue = options.Layout == TownLayout.Automatic ? TownLayout.WindingStreet : options.Layout; UpdateGroundChoices(options.Paths); DecorationList.SelectedIndex = (int)options.Decorations;
        SeedBox.Text = options.Seed.ToString(); GroundToggle.IsChecked = options.Ground; GreeneryToggle.IsChecked = options.Greenery;
        debounce.Tick += async (_, _) => { debounce.Stop(); await GenerateAsync(); };
        Loaded += (_, _) => { ready = true; RandomizeButton.IsEnabled = true; Schedule(); };
        Closed += (_, _) => { ready = false; debounce.Stop(); lifetime.Cancel(); };
    }
    private void UpdateGroundChoices(TownPathStyle requested)
    {
        bool wasChanging = changingSettings;
        changingSettings = true;
        try
        {
            var choices = TownGenerator.GroundChoices((TownFamily)TypeList.SelectedItem, terrain);
            PathList.ItemsSource = choices;
            PathList.SelectedItem = choices.FirstOrDefault(c => c.Style == requested) ?? choices.FirstOrDefault();
        }
        finally { changingSettings = wasChanging; }
    }
    private void SettingsChanged(object sender, RoutedEventArgs e)
    {
        if (!ready) return;
        if (ReferenceEquals(sender, TypeList)) UpdateGroundChoices(PathList.SelectedValue is TownPathStyle style ? style : TownPathStyle.Native);
        if (!changingSettings) Schedule();
    }
    private void Schedule()
    {
        version++; pending?.Cancel(); Plan = null; UseButton.IsEnabled = false;
        PreviewImage.Source = null; PreviewHint.IsVisible = true; PreviewHint.Text = LocalText.T("Preparing town preview…");
        debounce.Stop(); debounce.Start();
    }
    private async Task GenerateAsync()
    {
        if (!ready) return;
        // Serialize preparation of the shared sprite cache. A changed setting
        // invalidates this result and schedules the latest settings afterwards.
        if (working) { debounce.Start(); return; }
        int request = version; working = true;
        using var job = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        pending = job;
        try
        {
            if (TypeList.SelectedItem is not TownFamily selected || !int.TryParse(SeedBox.Text, out int seed)) throw new ArgumentException("Enter a whole-number seed.");
            int size = int.Parse(((ComboBoxItem)SizeList.SelectedItem).Tag.ToString()!);
            var options = new TownOptions(size, seed, (int)GapSlider.Value, (int)DensitySlider.Value, GroundToggle.IsChecked == true, GreeneryToggle.IsChecked == true,
                (TownLayout)LayoutList.SelectedValue, (TownPathStyle)PathList.SelectedValue, (TownDecorations)DecorationList.SelectedIndex, (int)WallChanceSlider.Value);
            bool detail = DetailToggle.IsChecked == true;
            var (plan, bitmap) = await Task.Run(() =>
            {
                var token = job.Token;
                if (!prepared.TryGetValue(selected.Id, out var family))
                {
                    family = TownCatalogue.Prepare(selected, assets, sprites, out int count, token);
                    prepared[selected.Id] = family; skipped[selected.Id] = count;
                }
                if (cachedPlan == null || cachedPlan.Family.Id != selected.Id || cachedPlan.Options != options)
                {
                    if (options.Ground)
                    {
                        var ground = TownGenerator.GroundChoice(family, terrain, options.Paths);
                        foreach (var set in new[] { ground.Base, ground.Path })
                        {
                            token.ThrowIfCancellationRequested();
                            if (!terrain.MatchesAssets(set, assets)) throw new InvalidDataException("Town ground needs matching terrain artwork. Load matching libraries or uncheck Include town ground and streets.");
                            foreach (var variant in set.Shapes.Values.SelectMany(v => v).DistinctBy(v => v.Index))
                                if (!sprites.ContainsKey((set.Library, variant.Index + 1))) sprites[(set.Library, variant.Index + 1)] = assets.Libraries[set.Library].Decode(variant.Index)!;
                        }
                    }
                    if (options.WallChance > 0) wallResources ??= TownWallResources.Load(assets, sprites, token);
                    cachedPlan = TownGenerator.Generate(family, options, terrain, token, wallResources);
                    overview = detailPreview = null;
                }
                var preview = (detail ? detailPreview : overview) ?? TownPreview.Create(cachedPlan, sprites, detail, token);
                if (detail) detailPreview = preview; else overview = preview;
                return (cachedPlan, preview);
            }, job.Token);
            if (!ready || request != version) return;
            Plan = plan; PreviewImage.Source = bitmap; PreviewHint.IsVisible = false; UseButton.IsEnabled = true;
            int houses = plan.Buildings.Count(b => b.Piece.Role == "house");
            string boundary = plan.Walls.Count == 0 ? "Open settlement" : string.Join(", ", plan.Walls.Select(w => $"{w.Purpose}: {w.Name}"));
            FeedbackText.Text = string.Concat(string.Concat(LocalText.Format("{0} · {1} · {2} houses · {3} market, civic and garden objects · {4} · Seed {5}",plan.LayoutName,plan.GroundName,houses,plan.Buildings.Count - houses,boundary,seed), ((skipped[selected.Id] > 0 ? LocalText.Format(" · {0} unavailable pieces skipped",skipped[selected.Id]) : ("")))), LocalText.T(". Click a clear area on the map to place the complete town. Ctrl+Z undoes it."));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (ready && request == version) { PreviewHint.Text = (ex.Message); FeedbackText.Text = LocalText.T("Town preview unavailable. Adjust the settings or libraries."); } }
        finally { if (ReferenceEquals(pending, job)) pending = null; working = false; if (ready && request != version) debounce.Start(); }
    }
    private void DetailChanged(object sender, RoutedEventArgs e) { if (ready) Schedule(); }
    private void ShuffleClick(object sender, RoutedEventArgs e) => SeedBox.Text = Random.Shared.Next().ToString();
    private void RandomizeClick(object sender, RoutedEventArgs e) => RandomizeSettings(Random.Shared);
    internal void RandomizeSettings(Random random)
    {
        if (!ready) return;
        changingSettings = true;
        try
        {
            // Change the style and layout visibly, then roll every other
            // generation setting. Preview magnification is a viewing choice.
            foreach (var list in new[] { TypeList, LayoutList })
            {
                if (list.Items.Count < 2) continue;
                int next = random.Next(list.Items.Count - 1);
                list.SelectedIndex = next >= list.SelectedIndex ? next + 1 : next;
            }
            foreach (var list in new[] { SizeList, PathList, DecorationList })
                list.SelectedIndex = random.Next(list.Items.Count);
            DensitySlider.Value = random.Next(5, 19) * 5;
            GapSlider.Value = random.Next(2, 9);
            WallChanceSlider.Value = random.Next(21) * 5;
            GroundToggle.IsChecked = true;
            GreeneryToggle.IsChecked = random.Next(2) == 0;
            SeedBox.Text = random.Next().ToString();
        }
        finally { changingSettings = false; Schedule(); }
    }
    private void UseClick(object sender, RoutedEventArgs e) { if (Plan != null && UseButton.IsEnabled) DialogResult = true; }
}
