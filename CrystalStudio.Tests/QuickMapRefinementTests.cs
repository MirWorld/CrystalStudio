using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using CrystalStudio.Rendering;

namespace CrystalStudio.Tests;

public sealed class QuickMapRefinementTests
{
    private static Dictionary<string, string> Parameters(int seed) => new() { ["SeedBox"] = seed.ToString(), ["RoomsSlider"] = "6" };

    private static async Task Until(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 500; attempt++)
        {
            Dispatcher.UIThread.RunJobs();
            if (condition()) return;
            await Task.Delay(20);
        }
        Assert.True(condition(), "The preview did not become ready.");
    }

    [AvaloniaFact]
    public void ProvincePreflightOmitsOptionalArtworkWithoutBlockingValidGround()
    {
        using var assets = AssetStore.Load(Path.Combine(TestApplication.Root, "Data"));
        var style = ProvinceStyle.All[0];
        var terrain = new TerrainCatalogue { Sets = new[] { style.BaseId, style.PatchId }.Select(id =>
            new TerrainSet { Id = id, Name = id, Library = 0, Shapes = new() { [15] = [new TerrainVariant { Index = 0 }] } }).ToList() };
        var requested = new ProvinceOptions(17, style, CliffChance: 100, BeachChance: 100, RiverChance: 100);
        var result = MapResourcePreflight.Province(terrain, new WallCatalogue(1, []), assets, [], null, requested, new());
        Assert.True(result.Report.CanGenerate);
        Assert.Equal(0, result.Options.CliffChance);
        Assert.Equal(0, result.Options.BeachChance);
        Assert.Equal(0, result.Options.RiverChance);
        Assert.NotEmpty(result.Report.Warnings);
        Assert.Equal(100, requested.CliffChance);
    }

    [AvaloniaFact]
    public void ProvincePreflightBlocksMissingGroundBeforePlanning()
    {
        using var assets = AssetStore.Load(Path.Combine(TestApplication.Root, "Data"));
        var result = MapResourcePreflight.Province(new TerrainCatalogue(), new WallCatalogue(1, []), assets, [], null,
            new ProvinceOptions(Style: ProvinceStyle.All[0], TownCount: 0), new());
        Assert.False(result.Report.CanGenerate);
        Assert.NotEmpty(result.Report.Errors);
    }

    [AvaloniaFact]
    public void ProvinceWithoutOptionalSceneryStillProducesGroundAndSealedBoundary()
    {
        var source = new MapDocument(128, 128);
        var plan = ProvinceGenerator.Plan(source, TerrainCatalogue.Load(), new WallCatalogue(1, []), [],
            new ProvinceOptions(19, ProvinceStyle.All[0], ForestChance: 0, CliffChance: 0, BeachChance: 0, RiverChance: 0, Vegetation: 0));
        Assert.NotEqual(0, plan.Generated.Get(64, 64).BackImage);
        Assert.NotEqual(0, plan.Generated.Get(0, 0).BackImage & 0x60000000);
        Assert.NotEmpty(plan.Notes);
    }

    [Fact]
    public void DetailSelectionIgnoresRendererPadding()
    {
        var region = new Rect(0, 0, 128, 128);
        Assert.False(MapPreviewRegion.TrySelect(new Point(.5, .001), region, new Size(1600, 1000), true, out _));
        Assert.True(MapPreviewRegion.TrySelect(new Point(.5, .5), region, new Size(1600, 1000), true, out var centre));
        Assert.Equal(region.Center, centre);
    }

    [Fact]
    public void CandidatesKeepParameterCopiesIgnoreViewModeAndEvictOnlyUnstarredItems()
    {
        var history = new QuickMapCandidates();
        var settings = new MapCreationSettings(MapCreationKind.Dungeon, 128, 128);
        var original = Parameters(1);
        var favorite = history.Remember(settings, original)!;
        history.SetFavorite(favorite, true);
        original["SeedBox"] = "999";
        Assert.Equal("1", favorite.Parameters["SeedBox"]);
        for (int seed = 2; seed <= 7; seed++) history.Remember(settings, Parameters(seed));
        Assert.Equal(5, history.Items.Count);
        Assert.Contains(favorite, history.Items);
        Assert.DoesNotContain(history.Items, item => item.Parameters["SeedBox"] == "2");
        var viewChange = Parameters(7); viewChange["DetailToggle"] = "True";
        Assert.Same(history.Items.Last(), history.Remember(settings with { Name = "Renamed" }, viewChange));
        Assert.Equal(5, history.Items.Count);
    }

    [Fact]
    public void FiveFavoritesAreNotSilentlyDiscarded()
    {
        var history = new QuickMapCandidates();
        var settings = new MapCreationSettings(MapCreationKind.Terrain);
        for (int seed = 1; seed <= 5; seed++) history.SetFavorite(history.Remember(settings, Parameters(seed))!, true);
        Assert.Null(history.Remember(settings, Parameters(6)));
        Assert.Equal(5, history.Items.Count);
        Assert.All(history.Items, item => Assert.True(item.IsFavorite));
        Assert.True(history.Items[0].CanRestore(settings));
        Assert.False(history.Items[0].CanRestore(settings with { Width = 128 }));
        Assert.False(history.Items[0].CanRestore(settings with { Kind = MapCreationKind.Dungeon }));
    }

    [Fact]
    public void PreviewZoomPreservesPointerLocationAndRejectsLetterboxClicks()
    {
        var navigation = new PreviewNavigation();
        var viewport = new Size(800, 600); var source = new Size(1600, 800);
        Assert.False(navigation.TryMap(new Point(200, 20), viewport, source, out _));
        var pointer = new Point(200, 250);
        Assert.True(navigation.TryMap(pointer, viewport, source, out var before));
        navigation.ZoomAt(pointer, 2, viewport, source);
        Assert.Equal(2, navigation.Zoom);
        Assert.True(navigation.TryMap(pointer, viewport, source, out var after));
        Assert.Equal(before.X, after.X, 8); Assert.Equal(before.Y, after.Y, 8);
        navigation.Pan(new Vector(100000, 100000), viewport, source);
        var bounds = navigation.ImageRect(viewport, source);
        Assert.True(bounds.X <= 0 && bounds.Y <= 0);
        navigation.Reset(); Assert.Equal(1, navigation.Zoom); Assert.Equal(default, navigation.Offset);
    }

    [Fact]
    public void SelectedDetailRegionStaysInMapAndOnGroundTileBoundaries()
    {
        var region = MapPreviewRegion.Detail(new Rect(0, 0, 128, 128), new Point(127, 127), 32, 24);
        Assert.Equal(new Rect(96, 104, 32, 24), region);
    }

    [AvaloniaFact]
    public void DungeonPreflightOmitsMissingOptionalStairsButKeepsRequiredChecks()
    {
        using var assets = AssetStore.Load(Path.Combine(TestApplication.Root, "Data"));
        var temple = TempleDefinition.All.Single(item => item.WallId == "stone-temple");
        var terrain = new TerrainCatalogue { Sets = [new TerrainSet { Id = temple.FloorId, Library = 0, Shapes = new() { [15] = [new TerrainVariant { Index = 0 }] } }] };
        var walls = new WallCatalogue(1, [new WallFamily { Id = temple.WallId, Name = temple.Name, Kind = "temple" }]);
        var result = MapResourcePreflight.Dungeon(new TempleOptions(temple, DoorCount: 3), walls, assets, new(terrain, assets));
        Assert.True(result.Report.CanGenerate); Assert.Equal(0, result.Options.DoorCount); Assert.NotEmpty(result.Report.Warnings);
        var missingWalls = MapResourcePreflight.Dungeon(new TempleOptions(temple), new WallCatalogue(1, []), assets, new(terrain, assets));
        Assert.False(missingWalls.Report.CanGenerate);
    }

    [AvaloniaFact]
    public void ExistingBrushEntranceSettingsDoNotTurnOptionalDoorsIntoRequiredWalls()
    {
        using var assets = AssetStore.Load(Path.Combine(TestApplication.Root, "Data"));
        var door = new WallPiece("door", "", "", "door", 0, [new StampCell(0, 0, new Cell { Foreground = new Front(999, 1) })]);
        var family = new WallFamily { Id = "test", Name = "Test wall", Kind = "test", RequestedDoorCount = 3,
            Entrances = [new WallEntrance("door", 1, 1, 0, 0, 0, door)] };
        var cache = new MapResourceCache(new TerrainCatalogue(), assets);
        Assert.True(cache.Wall(family, false));
        Assert.False(cache.Entrances(family, false));
        Assert.Equal(3, family.RequestedDoorCount);
    }

    [AvaloniaFact]
    public async Task ProvinceReportsMissingRequiredGroundBeforeOptionalResourceLoadingCompletes()
    {
        using var assets = AssetStore.Load(Path.Combine(TestApplication.Root, "Data"));
        var window = new ProvinceGeneratorWindow(new MapDocument(128, 128), assets, new WallCatalogue(1, []));
        bool earlyReport = false;
        window.PreviewState.Changed += (_, _) =>
        {
            if (window.PreviewState.Resources?.CanGenerate == false && !window.PreferencesRestored) earlyReport = true;
        };
        try
        {
            window.Show(); await Until(() => window.PreviewState.Resources != null);
            Assert.True(earlyReport);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void PreviewShowsOutputAndCanRejectFinalAcceptance()
    {
        var apply = new Button { Name = "ApplyButton" };
        var settings = new StackPanel { Children = { new ComboBox(), new TextBlock(), new ComboBox(), new TextBlock() } };
        var window = new EditorWindow { Content = new StackPanel { Children =
        { new ScrollViewer { Content = settings }, new DockPanel { Children = { apply } } } } };
        var session = new MapCreationSession(new(MapCreationKind.Terrain, 128, 256, "Forest", @"D:\Maps", true));
        int confirmations = 0;
        QuickMapPreview.Configure(window, session, _ => false, () => { confirmations++; return false; });
        var summary = window.GetLogicalDescendants().OfType<TextBlock>().Single(text => text.Name == "QuickMapSummary");
        Assert.Contains(Path.Combine(@"D:\Maps", "Forest.map"), summary.Text);
        Assert.Contains("128", summary.Text); Assert.Contains("256", summary.Text);
        Assert.False(QuickMapPreview.Confirm(window)); Assert.Equal(1, confirmations);
        window.Close();
    }

    [AvaloniaFact]
    public void NextDoesNotAskToReplaceAPresetBeforeTheMapIsCreated()
    {
        string root = Path.Combine(TestApplication.Root, Guid.NewGuid().ToString("N"));
        var store = new MapCreationStore(Path.Combine(root, "profile.json"));
        store.SavePreset("Existing", new(MapCreationKind.Terrain, 128, 128));
        var availability = Enum.GetValues<MapCreationKind>().ToDictionary(kind => kind, _ => (string?)null);
        var window = new NewMapWindow(root, availability, new(MapCreationKind.Terrain, 128, 128, SaveImmediately: false), store);
        int confirmations = 0;
        using var dialogs = Window.WindowOpenedEvent.AddClassHandler<Window>((dialog, _) =>
        {
            if (ReferenceEquals(dialog, window)) return;
            confirmations++; Dispatcher.UIThread.Post(() => dialog.Close(false));
        });
        try
        {
            window.Show(); window.PresetNameBox.Text = "Existing";
            window.CreateButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(0, confirmations); Assert.NotNull(window.Result); Assert.Single(store.Data.Presets);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task LivePreviewKeepsCandidatesRestoresSeedAndRedrawsDetailWithoutReplanning()
    {
        using var assets = AssetStore.Load(Path.Combine(TestApplication.Root, "Data"));
        var terrain = new TerrainCatalogue { Sets = [new TerrainSet { Id = "test", Name = "Fixture ground", Base = true, Biome = "Province", Library = 0,
            Shapes = new() { [15] = [new TerrainVariant { Index = 0, Weight = 1 }] } }] };
        var session = new MapCreationSession(new(MapCreationKind.Terrain, 128, 128, SaveImmediately: false));
        using var history = session.Candidates;
        var window = new TerrainGeneratorWindow(new MapDocument(128, 128), assets, terrain);
        bool allowCreation = false; int confirmations = 0;
        QuickMapPreview.Configure(window, session, _ => false, () => { confirmations++; return allowCreation; });
        try
        {
            window.Show();
            var controls = window.GetLogicalDescendants().ToArray();
            var seed = controls.OfType<TextBox>().Single(control => control.Name == "SeedBox");
            var apply = controls.OfType<Button>().Single(control => control.Name == "ApplyButton");
            await Until(() => window.PreferencesRestored && window.Plan != null && apply.IsEnabled);
            Assert.Single(history.Items);
            var original = window.Plan;
            var viewport = controls.OfType<MapPreviewViewport>().Single();
            viewport.ChangeZoom(2); Assert.Equal(2, viewport.Zoom); viewport.ResetView(); Assert.Equal(1, viewport.Zoom);
            window.Inspect(new Point(.8, .8));
            await Until(() => apply.IsEnabled);
            Assert.Same(original, window.Plan); Assert.Single(history.Items);
            var detail = controls.OfType<CheckBox>().Single(control => control.Name == "DetailToggle");
            Assert.True(detail.IsChecked);
            seed.Text = "572";
            await Until(() => window.Plan?.Seed == 572 && apply.IsEnabled);
            Assert.Equal(2, history.Items.Count);
            var restore = window.GetLogicalDescendants().OfType<Button>().Single(control => control.Name == "QuickMapCandidate0");
            restore.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => window.Plan?.Seed == 1 && apply.IsEnabled);
            Assert.Equal("1", seed.Text); Assert.Equal(2, history.Items.Count);
            string folder = Path.Combine(AppContext.BaseDirectory, "QuickMapScreenshots"); Directory.CreateDirectory(folder);
            Dispatcher.UIThread.RunJobs();
            using (var frame = window.CaptureRenderedFrame())
            {
                Assert.NotNull(frame); using var stream = File.Create(Path.Combine(folder, "refinement-candidates.png")); frame.Save(stream);
            }
            apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.Equal(1, confirmations); Assert.True(window.IsVisible);
            allowCreation = true; apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.Equal(2, confirmations); Assert.False(window.IsVisible);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ShufflingDoesNotCaptureAnOldImageUnderTheNewSeed()
    {
        using var assets = AssetStore.Load(Path.Combine(TestApplication.Root, "Data"));
        var terrain = new TerrainCatalogue { Sets = [new TerrainSet { Id = "test", Name = "Fixture ground", Base = true, Biome = "Province", Library = 0,
            Shapes = new() { [15] = [new TerrainVariant { Index = 0, Weight = 1 }] } }] };
        var session = new MapCreationSession(new(MapCreationKind.Terrain, 128, 128, SaveImmediately: false));
        using var history = session.Candidates;
        var window = new TerrainGeneratorWindow(new MapDocument(128, 128), assets, terrain);
        QuickMapPreview.Configure(window, session, _ => false);
        try
        {
            window.Show(); await Until(() => window.PreferencesRestored && window.Plan != null);
            Assert.Single(history.Items);
            var shuffle = window.GetLogicalDescendants().OfType<Button>().Single(button => Equals(button.Content, LocalText.T("Shuffle")));
            shuffle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Single(history.Items);
            var seed = window.GetLogicalDescendants().OfType<TextBox>().Single(control => control.Name == "SeedBox");
            int expected = int.Parse(seed.Text!);
            await Until(() => window.Plan?.Seed == expected);
            Assert.Equal(2, history.Items.Count);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ChineseRefinementControlsStayWithinTheMinimumWindow()
    {
        string root = Path.Combine(TestApplication.Root, Guid.NewGuid().ToString("N"));
        var language = new SettingsStore(Path.Combine(root, "settings.json"));
        string previous = LocalText.Language;
        language.Save(new(Path.Combine(TestApplication.Root, "Data"), "zh-CN")); LocalText.Initialize(language);
        using var assets = AssetStore.Load(Path.Combine(TestApplication.Root, "Data"));
        var window = new ProvinceGeneratorWindow(new MapDocument(128, 128), assets, WallCatalogue.Read(WallCatalogue.ResolvePath()));
        try
        {
            QuickMapPreview.Configure(window, new MapCreationSession(new(MapCreationKind.Province, 128, 128, "测试地图", @"D:\地图输出\新地图", true)), _ => false);
            window.Width = 850; window.Height = 650; window.Show(); await Task.Delay(150); Dispatcher.UIThread.RunJobs();
            var controls = window.GetLogicalDescendants().ToArray();
            var inspect = controls.OfType<Button>().Single(control => control.Name == "QuickMapInspectCentre");
            Assert.Equal("检查视图中心", inspect.Content);
            foreach (var button in controls.OfType<Button>().Where(control => control.Name is "QuickMapBack" or "ApplyButton" or "QuickMapOverview" or "QuickMapInspectCentre"))
            {
                var point = button.TranslatePoint(default, window)!.Value;
                Assert.InRange(point.X, 0, window.ClientSize.Width - button.Bounds.Width);
                Assert.InRange(point.Y, 0, window.ClientSize.Height - button.Bounds.Height);
            }
            string folder = Path.Combine(AppContext.BaseDirectory, "QuickMapScreenshots"); Directory.CreateDirectory(folder);
            using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame);
            using var stream = File.Create(Path.Combine(folder, "refinement-zh-minimum.png")); frame.Save(stream);
        }
        finally { window.Close(); language.Save(new(Path.Combine(TestApplication.Root, "Data"), previous)); LocalText.Initialize(language); }
    }
}
