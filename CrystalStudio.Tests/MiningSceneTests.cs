using Avalonia.Controls;
using Avalonia.Headless.XUnit;

namespace CrystalStudio.Tests;

public sealed class MiningSceneTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(27)]
    public void WorkingAreasAndHaulLanesSurviveVegetationAndSave(int seed)
    {
        var source = new MapDocument(320, 320);
        var terrain = TerrainCatalogue.Load(); var walls = WallCatalogue.Read(WallCatalogue.ResolvePath());
        ProvinceScenery[] scenery = [new(new BrushTree("Boundary tree", "", [new(0, 0,
            new Cell { Foreground = new(2, 1), BackImage = 0x60000000 })]), SpecialBrushKind.Tree, [ProvinceStyle.All[0].Region]),
            new(new BrushTree("Shrub", "", [new(0, 0, new Cell { Foreground = new(2, 2), BackImage = 0x60000000 })]),
                SpecialBrushKind.Bush, [ProvinceStyle.All[0].Region])];
        var options = new ProvinceOptions(seed, ProvinceStyle.All[0], ForestChance: 100, CliffChance: 0,
            BeachChance: 0, RiverChance: 0, Vegetation: 100, Scene: ProvinceScene.MiningQuarry, MineCount: 2);
        var plan = ProvinceGenerator.Plan(source, terrain, walls, scenery, options);
        Assert.Equal(2, plan.Mines.Count);
        Assert.Empty(plan.Ruins); Assert.Empty(plan.Towns); Assert.Empty(source.Cells); Assert.Empty(source.PlacedStamps);
        var reachable = Flood(plan.Generated, plan.Mines[0].X, plan.Mines[0].Y + 3);
        foreach (var mine in plan.Mines)
        {
            Assert.Equal(22, mine.Objects.Count);
            Assert.Contains(mine.Objects, o => o.Tree.Name == "mir/mir_01275");
            foreach (var p in mine.WorkPoints.Concat(mine.Approach))
            {
                Assert.Contains(p.Y * source.Width + p.X, reachable);
                Assert.Empty(plan.Generated.Get(p.X, p.Y).ForegroundImages);
            }
            // Five-cell haul lanes remain clear all the way to each site.
            for (int y = mine.Y + 3; y <= mine.Y + 26; y++) for (int dx = -2; dx <= 2; dx++)
                Assert.Contains((mine.X + dx, y), mine.Approach);
            foreach (var obj in mine.Objects)
            {
                var stamp = Assert.Single(plan.Generated.PlacedStamps,
                    s => s.X == obj.X && s.Y == obj.Y && s.Name.StartsWith("Mining yard"));
                Assert.Equal(obj.Cells, stamp.Cells);
            }
        }
        var again = ProvinceGenerator.Plan(source, terrain, walls, scenery, options);
        Assert.Equal(plan.Generated.Cells.OrderBy(c => c.Key), again.Generated.Cells.OrderBy(c => c.Key));
        string path = Path.Combine(Path.GetTempPath(), "CrystalStudio-mining-" + Guid.NewGuid().ToString("N") + ".map");
        try
        {
            plan.Generated.Save(path); var loaded = MapDocument.Load(path);
            Assert.Equal(plan.Generated.Cells.OrderBy(c => c.Key), loaded.Cells.OrderBy(c => c.Key));
            Assert.Equal(plan.Generated.PlacedStamps.Count, loaded.PlacedStamps.Count);
        }
        finally { File.Delete(path); }
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => ProvinceGenerator.Plan(source, terrain, walls, scenery, options, cancelled.Token));
    }

    [Fact]
    public void MiningRequiresReviewedCoreArtworkAndReportsMissingRoads()
    {
        var resources = new ProvinceMineResources();
        Assert.Equal(21, resources.Load(null, default).Count);
        Assert.Null(ProvinceSceneryPalette.GroundSetId("mir/mir_01275"));
        var map = new MapDocument(128, 128); var notes = new List<string>();
        Assert.Empty(ProvinceMines.Place(map, TerrainCatalogue.Load(), ProvinceStyle.All[0], new(Scene: ProvinceScene.MiningQuarry),
            new bool[128 * 128], [], resources, null, notes, default));
        Assert.Empty(map.Cells); Assert.Empty(map.PlacedStamps);
        Assert.Contains(notes, n => n.Contains("No accessible trail"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public void InvalidMiningCountsAreRejected(int count)
    {
        Assert.Throws<ArgumentException>(() => ProvinceGenerator.Plan(new(128, 128), TerrainCatalogue.Load(),
            WallCatalogue.Read(WallCatalogue.ResolvePath()), [], new(Scene: ProvinceScene.MiningQuarry, MineCount: count)));
    }

    [AvaloniaFact]
    public void MiningThemeRestoresCountAndKeepsWoodlandArtwork()
    {
        using var assets = AssetStore.Load(Path.Combine(TestApplication.Root, "Data"));
        var window = new ProvinceGeneratorWindow(new MapDocument(320, 320), assets);
        var theme = window.FindControl<ComboBox>("SceneList")!; theme.SelectedIndex = 3;
        Assert.True(window.FindControl<StackPanel>("MineOptions")!.IsVisible);
        Assert.False(window.FindControl<StackPanel>("RuinOptions")!.IsVisible);
        var style = window.FindControl<ComboBox>("StyleList")!;
        Assert.False(style.IsEnabled); style.SelectedIndex = 1; Assert.Equal(0, style.SelectedIndex);
        window.FindControl<Slider>("MineCountSlider")!.Value = 3;
        var settings = GeneratorPreferences.Capture(window); theme.SelectedIndex = 1;
        Assert.True(style.IsEnabled); Assert.False(window.FindControl<StackPanel>("MineOptions")!.IsVisible);
        GeneratorPreferences.Restore(window, settings);
        Assert.Equal(3, theme.SelectedIndex); Assert.Equal(3, window.FindControl<Slider>("MineCountSlider")!.Value);
        var recipe = MapCreationStore.Recipe(new(320, 320), MapCreationKind.Province, settings, new());
        Assert.Contains("quick-map-v5", recipe.GeneratorVersion);
        Assert.Equal("index:3", recipe.Parameters["SceneList"]);
        window.Close();
        using var unavailable = AssetStore.Load(Path.Combine(TestApplication.Root, "Data"));
        Assert.Throws<InvalidDataException>(() => new ProvinceMineResources().Load(unavailable, default));
    }

    private static HashSet<int> Flood(MapDocument map, int x, int y)
    {
        var result = new HashSet<int>(); var queue = new Queue<(int X, int Y)>(); queue.Enqueue((x, y));
        while (queue.TryDequeue(out var p))
        {
            if (!map.Contains(p.X, p.Y) || (map.Get(p.X, p.Y).BackImage & 0x60000000) != 0 || !result.Add(p.Y * map.Width + p.X)) continue;
            queue.Enqueue((p.X - 1, p.Y)); queue.Enqueue((p.X + 1, p.Y)); queue.Enqueue((p.X, p.Y - 1)); queue.Enqueue((p.X, p.Y + 1));
        }
        return result;
    }
}
