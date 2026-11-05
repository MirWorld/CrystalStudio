using Avalonia.Controls;
using Avalonia.Headless.XUnit;

namespace CrystalStudio.Tests;

public sealed class ProvinceSceneTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(27)]
    public void CountrysideKeepsTownEntrancesAndConnectingRoadsOpen(int seed)
    {
        var source = new MapDocument(320, 320);
        var terrain = TerrainCatalogue.Load();
        var options = new ProvinceOptions(seed, ProvinceStyle.All[0], ForestChance: 0,
            CliffChance: 0, BeachChance: 0, RiverChance: 0, Vegetation: 0, TownCount: 2,
            Scene: ProvinceScene.VillageCountryside);
        ProvinceScenery[] scenery = [new(new BrushTree("Boundary tree", "", [new(0, 0,
            new Cell { Foreground = new(2, 1), BackImage = 0x60000000 })]), SpecialBrushKind.Tree, [ProvinceStyle.All[0].Region])];
        var plan = ProvinceGenerator.Plan(source, terrain, WallCatalogue.Read(WallCatalogue.ResolvePath()), scenery, options);
        Assert.Equal(2, plan.Towns.Count);
        Assert.Empty(plan.Ruins);
        Assert.Empty(source.Cells); Assert.Empty(source.PlacedStamps);
        var crops = plan.Towns.SelectMany(t => t.Plan.Buildings.Where(b => TownSceneDetails.Use(b.Piece) == "crop")).ToArray();
        Assert.NotEmpty(crops);
        foreach (var town in plan.Towns)
        {
            Assert.Equal(TownDecorations.Detailed, town.Plan.Options.Decorations);
            Assert.DoesNotContain(town.Plan.Buildings, b => b.Piece.Role is "prop" or "greenery" &&
                ProvinceSceneryPalette.GroundSetId(b.Piece.Id) == "unverified");
            foreach (var p in town.Plan.Roads.Select(p => (X: p.X + town.X, Y: p.Y + town.Y)).Concat(town.Approach))
            {
                Assert.Equal(0, plan.Generated.Get(p.X, p.Y).BackImage & 0x60000000);
                Assert.Null(plan.Generated.Get(p.X, p.Y).Foreground);
            }
        }
        var reachable = Reachable(plan.Generated, plan.Towns[0].X + plan.Towns[0].Plan.Buildings[0].AccessX,
            plan.Towns[0].Y + plan.Towns[0].Plan.Buildings[0].AccessY);
        foreach (var town in plan.Towns)
        foreach (var b in town.Plan.Buildings.Where(b => b.AccessX >= 0))
            Assert.Contains((town.Y + b.AccessY) * source.Width + town.X + b.AccessX, reachable);
        string path = Path.Combine(Path.GetTempPath(), "CrystalStudio-scene-" + Guid.NewGuid().ToString("N") + ".map");
        try
        {
            plan.Generated.Save(path);
            var loaded = MapDocument.Load(path);
            Assert.Equal(plan.Generated.Cells.OrderBy(c => c.Key), loaded.Cells.OrderBy(c => c.Key));
            Assert.Equal(plan.Generated.PlacedStamps.Count, loaded.PlacedStamps.Count);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void EdgeVegetationFollowsTrailsAndNeverOverwritesProtectedGround()
    {
        var terrain = TerrainCatalogue.Load();
        var basis = terrain.Sets.Single(s => s.Id == ProvinceStyle.All[0].BaseId);
        var map = new MapDocument(128, 128);
        for (int y = 0; y < 128; y += 2) for (int x = 0; x < 128; x += 2)
            map.Set(x, y, new Cell { BackLibrary = (short)basis.Library, BackImage = basis.Shapes[15][0].Index + 1 });
        map.Set(63, 63, new Cell { Foreground = new(2, 7), Door = 3, Light = 119 });
        var original = MapRecoveryStore.Capture(map);
        var reserved = new bool[128 * 128];
        for (int y = 30; y < 50; y++) for (int x = 20; x < 100; x++) reserved[y * 128 + x] = true;
        var coast = new bool[reserved.Length];
        var roads = Enumerable.Range(20, 88).Select(y => (X: 64, Y: y)).ToArray();
        var plant = new BrushTree("Test shrub", "", [new(0, 0, new Cell { Foreground = new(2, 1), BackImage = 0x60000000 })]);
        ProvinceScenery[] scenery = [new(plant, SpecialBrushKind.Bush, [ProvinceStyle.All[0].Region])];
        int count = ProvinceEcology.PlantEdges(map, terrain, scenery, ProvinceStyle.All[0], reserved, coast, roads, 27, 100, default);
        Assert.True(count > 0);
        Assert.All(map.PlacedStamps, stamp =>
        {
            Assert.InRange(roads.Min(p => Math.Abs(stamp.X - p.X) + Math.Abs(stamp.Y - p.Y)), 4, 8);
            Assert.False(reserved[stamp.Y * 128 + stamp.X]);
        });
        Assert.Equal(original.Get(63, 63), map.Get(63, 63));
        var again = MapRecoveryStore.Capture(original);
        Assert.Equal(count, ProvinceEcology.PlantEdges(again, terrain, scenery, ProvinceStyle.All[0], reserved, coast, roads, 27, 100, default));
        Assert.Equal(map.Cells.OrderBy(c => c.Key), again.Cells.OrderBy(c => c.Key));
        Assert.Equal(0, ProvinceEcology.PlantEdges(original, terrain, scenery, ProvinceStyle.All[0], reserved, coast, roads, 27, 0, default));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => ProvinceEcology.PlantEdges(original, terrain, scenery, ProvinceStyle.All[0],
            reserved, coast, roads, 27, 100, cancelled.Token));
    }

    [AvaloniaFact]
    public void SceneThemeSetsUsableDefaultsAndRecipeParametersRestoreThem()
    {
        using var assets = AssetStore.Load(Path.Combine(TestApplication.Root, "Data"));
        var window = new ProvinceGeneratorWindow(new MapDocument(320, 320), assets);
        var scene = window.FindControl<ComboBox>("SceneList")!;
        var towns = window.FindControl<Slider>("TownCountSlider")!;
        Assert.Equal(1, scene.SelectedIndex); Assert.True(towns.Value >= 1);
        var values = GeneratorPreferences.Capture(window);
        scene.SelectedIndex = 0; Assert.Equal(0, towns.Value);
        GeneratorPreferences.Restore(window, values);
        Assert.Equal(1, scene.SelectedIndex); Assert.True(towns.Value >= 1);
        Assert.Equal(values, GeneratorPreferences.Capture(window));
        window.Close();
        var legacy = new ProvinceGeneratorWindow(new MapDocument(320, 320), assets,
            parameters: new Dictionary<string, string> { ["TownCountSlider"] = "0" });
        Assert.Equal(0, legacy.FindControl<ComboBox>("SceneList")!.SelectedIndex);
        legacy.Close();
    }

    [Fact]
    public void CreationRecipeRecordsTheSceneAndItsReviewedArtwork()
    {
        var parameters = new Dictionary<string, string> { ["SceneList"] = "index:1", ["SeedBox"] = "27" };
        var recipe = MapCreationStore.Recipe(new MapDocument(320, 320), MapCreationKind.Province, parameters, new());
        Assert.Equal("index:1", recipe.Parameters["SceneList"]);
        Assert.Contains("quick-map-v5", recipe.GeneratorVersion);
        Assert.Contains("towns.json", recipe.CatalogueHashes.Keys);
        Assert.Contains("province-decoration-ground.json", recipe.CatalogueHashes.Keys);
    }

    [AvaloniaFact]
    public void ForestThemePreservesLegacyIndicesAndPinsWoodlandDuringRestore()
    {
        using var assets = AssetStore.Load(Path.Combine(TestApplication.Root, "Data"));
        var window = new ProvinceGeneratorWindow(new MapDocument(320, 320), assets);
        var scene = window.FindControl<ComboBox>("SceneList")!;
        scene.SelectedIndex = 2;
        Assert.True(window.FindControl<StackPanel>("RuinOptions")!.IsVisible);
        var style = window.FindControl<ComboBox>("StyleList")!;
        Assert.False(style.IsEnabled); Assert.Equal(0, style.SelectedIndex);
        style.SelectedIndex = 1; Assert.Equal(0, style.SelectedIndex);
        window.FindControl<Slider>("RuinCountSlider")!.Value = 3;
        var settings = GeneratorPreferences.Capture(window);
        scene.SelectedIndex = 0;
        Assert.True(style.IsEnabled); Assert.False(window.FindControl<StackPanel>("RuinOptions")!.IsVisible);
        GeneratorPreferences.Restore(window, settings);
        Assert.Equal(2, scene.SelectedIndex); Assert.Equal(3, window.FindControl<Slider>("RuinCountSlider")!.Value);
        window.Close();
    }

    private static HashSet<int> Reachable(MapDocument map, int x, int y)
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
