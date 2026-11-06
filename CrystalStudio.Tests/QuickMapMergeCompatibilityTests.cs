using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using CrystalStudio.Rendering;

namespace CrystalStudio.Tests;

public sealed class QuickMapMergeCompatibilityTests
{
    [AvaloniaFact]
    public void ReturningBeforeArtworkLoadsKeepsEditsAndPendingResourceSelections()
    {
        using var assets = AssetStore.Load(Path.Combine(TestApplication.Root, "Data"));
        var parameters = new Dictionary<string, string> { ["SeedBox"] = "17", ["BaseList"] = "terrain:pending" };
        var session = new MapCreationSession(new(MapCreationKind.Terrain), parameters);
        using var candidates = session.Candidates;
        var window = new TerrainGeneratorWindow(new(256, 256), assets, new TerrainCatalogue(), parameters);
        try
        {
            QuickMapPreview.Configure(window, session, _ => false);
            window.FindControl<TextBox>("SeedBox")!.Text = "424242";
            window.FindControl<Slider>("CoverageSlider")!.Value = .63;
            window.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "QuickMapBack")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(window.PreferencesRestored);
            Assert.Equal("424242", session.Parameters["SeedBox"]);
            Assert.Equal("0.63", session.Parameters["CoverageSlider"]);
            Assert.Equal("terrain:pending", session.Parameters["BaseList"]);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(MapCreationKind.Terrain)]
    [InlineData(MapCreationKind.Province)]
    [InlineData(MapCreationKind.Dungeon)]
    public void QuickPreviewKeepsPrecisionControlsAndInspectionAfterWrappingTheImage(MapCreationKind kind)
    {
        using var assets = AssetStore.Load(Path.Combine(TestApplication.Root, "Data"));
        var terrain = TerrainCatalogue.Load();
        var walls = WallCatalogue.Read(WallCatalogue.ResolvePath());
        EditorWindow window = kind switch
        {
            MapCreationKind.Terrain => new TerrainGeneratorWindow(new(256, 256), assets, terrain),
            MapCreationKind.Province => new ProvinceGeneratorWindow(new(256, 256), assets, walls),
            _ => new TempleGeneratorWindow(new(256, 256), assets, walls, terrain)
        };
        using var overview = RasterImage.Create(128, 128, 96, 96, RasterFormats.Bgra32, null, new byte[128 * 128 * 4], 128 * 4);
        using var alternate = RasterImage.Create(128, 128, 96, 96, RasterFormats.Bgra32, null, new byte[128 * 128 * 4], 128 * 4);
        using var detail = RasterImage.Create(64, 64, 96, 96, RasterFormats.Bgra32, null, new byte[64 * 64 * 4], 64 * 4);
        var session = new MapCreationSession(new(kind, 256, 256, SaveImmediately: false));
        using var candidates = session.Candidates;
        try
        {
            QuickMapPreview.Configure(window, session, _ => false);
            var controls = window.GetLogicalDescendants().ToArray();
            var viewport = Assert.Single(controls.OfType<MapPreviewViewport>());
            Assert.NotEmpty(controls.OfType<GeneratorValueEditor>());
            Assert.Single(controls.OfType<Button>(), button => button.Name == "QuickMapBack");
            Assert.Single(controls.OfType<Button>(), button => button.Name == "QuickMapInspectCentre");
            Assert.Single(controls.OfType<TextBlock>(), text => text.Name == "QuickMapResourceFeedback");
            var parameters = GeneratorPreferences.Capture(window);
            var image = window.FindControl<Image>("PreviewImage")!;
            image.Source = overview;
            viewport.ChangeZoom(2);
            image.Source = alternate;
            Assert.Equal(2, viewport.Zoom);
            image.Source = detail;
            Assert.Equal(1, viewport.Zoom);
            Assert.Equal(parameters.OrderBy(pair => pair.Key), GeneratorPreferences.Capture(window).OrderBy(pair => pair.Key));
            Assert.Equal(0, session.GenerationRevision);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void DungeonPrimarySelectorsStayTogetherAndThemeParametersRoundTrip()
    {
        using var assets = AssetStore.Load(Path.Combine(TestApplication.Root, "Data"));
        var window = new TempleGeneratorWindow(new(320, 320), assets,
            WallCatalogue.Read(WallCatalogue.ResolvePath()), TerrainCatalogue.Load());
        try
        {
            QuickMapPreview.Configure(window, new(new(MapCreationKind.Dungeon)), _ => false);
            var theme = window.FindControl<ComboBox>("ThemeList")!;
            var type = window.FindControl<ComboBox>("DungeonTypeList")!;
            var style = window.FindControl<ComboBox>("TempleList")!;
            Assert.Same(theme.Parent, type.Parent);
            Assert.Same(theme.Parent, style.Parent);
            Assert.DoesNotContain(type.GetLogicalAncestors(), item => item is Expander);
            var panel = Assert.IsType<StackPanel>(type.Parent);
            Assert.IsType<TextBlock>(panel.Children[panel.Children.IndexOf(type) - 1]);
            foreach (int selected in new[] { 1, 2, 3 })
            {
                theme.SelectedIndex = selected;
                window.FindControl<Slider>("RoomsSlider")!.Value = 17;
                window.FindControl<Slider>("SizeSlider")!.Value = 4;
                window.FindControl<TextBox>("SeedBox")!.Text = "491";
                var values = GeneratorPreferences.Capture(window);
                theme.SelectedIndex = 0;
                GeneratorPreferences.Restore(window, values);
                Assert.Equal(selected, theme.SelectedIndex);
                Assert.Equal(17, window.FindControl<Slider>("RoomsSlider")!.Value);
                Assert.Equal(4, window.FindControl<Slider>("SizeSlider")!.Value);
                Assert.Equal("491", window.FindControl<TextBox>("SeedBox")!.Text);
                Assert.Equal(DungeonThemes.Definition((DungeonTheme)selected), style.SelectedItem);
            }
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void ProvincePrimarySelectorsAndSceneParametersSurviveQuickMapLayout()
    {
        using var assets = AssetStore.Load(Path.Combine(TestApplication.Root, "Data"));
        var window = new ProvinceGeneratorWindow(new(320, 320), assets);
        try
        {
            QuickMapPreview.Configure(window, new(new(MapCreationKind.Province)), _ => false);
            var scene = window.FindControl<ComboBox>("SceneList")!;
            var style = window.FindControl<ComboBox>("StyleList")!;
            Assert.Same(scene.Parent, style.Parent);
            Assert.DoesNotContain(style.GetLogicalAncestors(), item => item is Expander);
            foreach (int selected in new[] { 1, 2, 3 })
            {
                scene.SelectedIndex = selected;
                window.FindControl<Slider>("RuinCountSlider")!.Value = 3;
                window.FindControl<Slider>("MineCountSlider")!.Value = 4;
                window.FindControl<Slider>("VegetationSlider")!.Value = 67;
                window.FindControl<TextBox>("SeedBox")!.Text = "592";
                var values = GeneratorPreferences.Capture(window);
                scene.SelectedIndex = 0;
                GeneratorPreferences.Restore(window, values);
                Assert.Equal(selected, scene.SelectedIndex);
                Assert.Equal(3, window.FindControl<Slider>("RuinCountSlider")!.Value);
                Assert.Equal(4, window.FindControl<Slider>("MineCountSlider")!.Value);
                Assert.Equal(67, window.FindControl<Slider>("VegetationSlider")!.Value);
                Assert.Equal("592", window.FindControl<TextBox>("SeedBox")!.Text);
            }
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void OptionalResourceFallbackPreservesSelectedScenesAndCounts()
    {
        using var assets = AssetStore.Load(Path.Combine(TestApplication.Root, "Data"));
        foreach (var scene in new[] { ProvinceScene.VillageCountryside, ProvinceScene.ForestRuins, ProvinceScene.MiningQuarry })
        {
            var requested = new ProvinceOptions(Scene: scene, TownCount: 2, RuinCount: 3, MineCount: 4);
            var result = MapResourcePreflight.Province(new(), new(1, []), assets, [], null, requested, new());
            Assert.Equal(scene, result.Options.Scene);
            Assert.Equal(3, result.Options.RuinCount);
            Assert.Equal(4, result.Options.MineCount);
            Assert.Equal(0, result.Options.TownCount);
            Assert.Equal(0, result.Options.ForestChance);
            Assert.False(result.Report.CanGenerate);
        }
    }
}
