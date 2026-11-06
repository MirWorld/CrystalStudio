using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;

namespace CrystalStudio.Tests;

public sealed class QuickMapMergeCompatibilityTests
{
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
