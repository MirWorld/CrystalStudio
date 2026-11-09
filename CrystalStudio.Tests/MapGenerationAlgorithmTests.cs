using System.IO;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;

namespace CrystalStudio.Tests;

public sealed class MapGenerationAlgorithmTests
{
    [Fact]
    public void SquareClearanceMatchesBruteForceForEveryFourByThreeCollisionLayout()
    {
        const int width = 4, height = 3;
        for (int mask = 0; mask < 1 << (width * height); mask++)
        {
            var map = new MapDocument(width, height);
            for (int index = 0; index < width * height; index++)
                if ((mask & (1 << index)) != 0) map.Set(index % width, index / width, new Cell { BackImage = index % 2 == 0 ? 0x20000000 : 0x40000000 });
            var clearance = MapClearance.Build(map);
            for (int row = 0; row < height; row++) for (int column = 0; column < width; column++)
            {
                int expected = 0;
                for (int size = 1; size <= Math.Min(width - column, height - row); size++)
                {
                    bool fits = true;
                    for (int dy = 0; dy < size; dy++) for (int dx = 0; dx < size; dx++)
                        if ((mask & (1 << ((row + dy) * width + column + dx))) != 0) fits = false;
                    if (fits) expected = size;
                }
                Assert.Equal(expected, clearance.At(column, row));
            }
            for (int size = 1; size <= 3; size++)
            {
                var from = new MapRoutePoint("Start", 0, 0); var to = new MapRoutePoint("Finish", width - size, height - size);
                var result = Assert.Single(MapRouteAnalysis.Analyze(map, [from, to], footprintSize: size).Legs);
                int expected = BruteRoute(mask, width, height, from, to, size);
                Assert.Equal(expected >= 0, result.Reachable);
                Assert.Equal(size, result.FootprintSize);
                if (expected < 0) continue;
                Assert.Equal(expected, result.Steps);
                Assert.True(result.MinimumSquareClearance >= size);
                Assert.All(result.Path, index => Assert.True(clearance.At(index % width, index / width) >= size));
            }
        }
    }

    [Fact]
    public void OneCellJunctionDoesNotImplyTwoByTwoClearance()
    {
        var map = new MapDocument(9, 9);
        for (int row = 0; row < 9; row++) for (int column = 0; column < 9; column++)
            if (row != 4 && column != 4) map.Set(column, row, new Cell { BackImage = 0x60000000 });
        MapRoutePoint[] points = [new("West", 0, 4), new("East", 8, 4)];
        Assert.True(Assert.Single(MapRouteAnalysis.Analyze(map, points).Legs).Reachable);
        Assert.False(Assert.Single(MapRouteAnalysis.Analyze(map, points, footprintSize: 2).Legs).Reachable);
        Assert.Equal(1, MapClearance.Build(map).At(4, 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => MapRouteAnalysis.Analyze(map, points, footprintSize: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => MapRouteAnalysis.Analyze(map, points, footprintSize: 65));
    }

    [Fact]
    public void LocalCornerPropagationIsMonotoneDeterministicAndIdempotent()
    {
        for (int mask = 0; mask < 512; mask++) foreach (bool fill in new[] { false, true })
        {
            var original = Enumerable.Range(0, 9).Select(index => (mask & (1 << index)) != 0).ToArray();
            var field = (bool[])original.Clone(); var repeat = (bool[])original.Clone();
            int changed = TerrainFieldNormalizer.Normalize(field, 2, 2, fill, (_, _) => true, 27);
            Assert.Equal(changed, TerrainFieldNormalizer.Normalize(repeat, 2, 2, fill, (_, _) => true, 27));
            Assert.Equal(field, repeat); Assert.InRange(changed, 0, 9);
            for (int index = 0; index < 9; index++) if (field[index] != original[index]) Assert.Equal(fill, field[index]);
            for (int row = 0; row < 2; row++) for (int column = 0; column < 2; column++)
            {
                int native = (field[row * 3 + column] ? 1 : 0) | (field[row * 3 + column + 1] ? 2 : 0) |
                    (field[(row + 1) * 3 + column] ? 4 : 0) | (field[(row + 1) * 3 + column + 1] ? 8 : 0);
                Assert.DoesNotContain(native, new[] { 6, 9 });
            }
            Assert.Equal(0, TerrainFieldNormalizer.Normalize(field, 2, 2, fill, (_, _) => true, 27));
        }
        bool[] diagonal = [true, false, false, true];
        Assert.Equal(1, TerrainFieldNormalizer.Normalize(diagonal, 1, 1, true, (_, _) => true, 1));
        Assert.Throws<InvalidDataException>(() => TerrainFieldNormalizer.Normalize([true, false, false, true], 1, 1, true, (_, _) => false, 1));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(27)]
    [InlineData(1729)]
    public void LoopSelectionPreservesMainRouteAndImprovesDetours(int seed)
    {
        (int A, int B)[] backbone = [(0, 1), (1, 2), (2, 3), (3, 4), (2, 5), (5, 6), (6, 7), (7, 8)];
        (int A, int B)[] candidates = [(0, 4), (0, 5), (1, 6), (3, 7), (4, 8), (5, 8)];
        var before = GraphDistances(9, backbone);
        var loops = DungeonTopology.SelectLoops(9, backbone, candidates, 3, seed);
        Assert.NotEmpty(loops); Assert.InRange(loops.Count, 1, 3);
        Assert.Equal(loops, DungeonTopology.SelectLoops(9, backbone, candidates.Reverse(), 3, seed));
        Assert.All(loops, edge => Assert.Contains(edge, candidates));
        int start = 0, goal = 0;
        for (int first = 0; first < 9; first++) for (int second = first + 1; second < 9; second++)
            if (before[first, second] > before[start, goal]) { start = first; goal = second; }
        var after = GraphDistances(9, backbone.Concat(loops));
        Assert.True(after[start, goal] >= (int)Math.Ceiling(before[start, goal] * .75));
        int saved = 0;
        for (int first = 0; first < 9; first++) for (int second = 0; second < 9; second++)
        { Assert.InRange(after[first, second], 0, before[first, second]); saved += before[first, second] - after[first, second]; }
        Assert.True(saved > 0);
        Assert.Throws<ArgumentException>(() => DungeonTopology.SelectLoops(3, [(0, 1)], [(1, 2)], 1, seed));
    }

    [Fact]
    public void SpatialBucketsAgreeWithIndependentAllPairsSpacingChecks()
    {
        var random = new Random(1729); var spacing = new ScenerySpacing();
        var accepted = new List<(double X, double Y, double Radius)>();
        for (int attempt = 0; attempt < 2000; attempt++)
        {
            double x = random.NextDouble() * 200 - 100, y = random.NextDouble() * 200 - 100, radius = .5 + random.NextDouble() * 5;
            bool expected = accepted.All(point => Math.Pow(point.X - x, 2) + Math.Pow(point.Y - y, 2) >= Math.Pow(point.Radius + radius, 2));
            Assert.Equal(expected, spacing.Fits(x, y, radius));
            if (expected) { spacing.Add(x, y, radius); accepted.Add((x, y, radius)); }
        }
        Assert.True(accepted.Count > 100);
    }

    [Fact]
    public void CandidateKeysDoNotDependOnOtherStreamsOrPreviewSize()
    {
        var small = ProvinceScenerySampling.Sites(80, 60, 4, 27, 0x4001, default);
        var large = ProvinceScenerySampling.Sites(120, 100, 4, 27, 0x4001, default).Where(point => point.X < 80 && point.Y < 60).ToArray();
        Assert.Equal(small, large); Assert.Equal(small.Count, small.Distinct().Count());
        double key = GenerationRandom.Unit(27, 0x4002, 18, 22, 2);
        var unrelated = GenerationRandom.Stream(27, 0x4001);
        for (int count = 0; count < 10000; count++) unrelated.Next();
        Assert.Equal(key, GenerationRandom.Unit(27, 0x4002, 18, 22, 2));
        Assert.NotEqual(key, GenerationRandom.Unit(27, 0x4001, 18, 22, 2));
        var tree = new BrushTree("Canopy", "", [new(0, 0, new Cell { BackImage = 0x60000000 }), new(40, 40, new Cell { Foreground = new(2, 1) })]);
        Assert.Equal(1.0, ScenerySpacing.Footprint(tree, 1).Radius);
    }

    [Fact]
    public void CompiledConstraintsKeepClearanceAndWalkabilitySemanticsSeparate()
    {
        var map = new MapDocument(12, 10); map.Set(4, 4, new Cell { BackImage = 0x60000000 });
        var design = new MapDesignSettings { Regions = [new("Empty but solid", 4, 4, 1, 1, MapRegionPurpose.ClearObjects), new("Preference", 7, 7, 1, 1, MapRegionPurpose.ClearObjects, false)] };
        var compiled = GenerationConstraints.Compile(map, design)!;
        compiled.EnsureSatisfied(map, default);
        Assert.True(compiled.Reserved[4 * 12 + 4]); Assert.False(compiled.Reserved[7 * 12 + 7]);
        design.Regions.Clear(); compiled.EnsureSatisfied(map, default);
        map.Set(4, 4, new Cell { Foreground = new(2, 1) });
        Assert.Throws<InvalidDataException>(() => compiled.EnsureSatisfied(map, default));
        Assert.Null(GenerationConstraints.Compile(map, new()));
        Assert.Throws<InvalidDataException>(() => GenerationConstraints.Compile(map, new() { Points = null! }));
    }

    [Fact]
    public void RequiredRoutesReserveEveryCellEvenWhenDisplayPathsWouldBeSampled()
    {
        var map = new MapDocument(5008, 3);
        var design = new MapDesignSettings { RequireRoute = true, Points = [new("A", 1, 1), new("B", 5006, 1)] };
        var compiled = GenerationConstraints.Compile(map, design)!; compiled.ReserveRoutes(map, default);
        for (int column = 1; column <= 5006; column++) Assert.True(compiled.Reserved[map.Width + column]);
        var forbidden = new bool[map.Width * map.Height];
        for (int row = 0; row < map.Height; row++) forbidden[row * map.Width + 2500] = true;
        Assert.Throws<InvalidDataException>(() => compiled.ReserveRoutes(map, default, forbidden));
        Array.Fill(forbidden, false); forbidden[map.Width + 1] = true;
        Assert.Throws<InvalidDataException>(() => compiled.ReserveRoutes(map, default, forbidden));
        Assert.Throws<ArgumentException>(() => compiled.ReserveRoutes(map, default, [false]));
        Assert.Empty(map.Cells);
    }

    [Fact]
    public void ImpossibleProvinceBoundaryConstraintFailsBeforeAnyGenerationStage()
    {
        var source = new MapDocument(128, 128); int stages = 0;
        var design = new MapDesignSettings { Regions = [new("Entry", 0, 0, 2, 2, MapRegionPurpose.Walkable)] };
        var options = Province(1) with { Design = design };
        var error = Assert.Throws<InvalidDataException>(() => ProvinceGenerator.Plan(source, TerrainCatalogue.Load(), new(1, []), [], options, timing: (_, _) => stages++));
        Assert.Contains("Entry", error.Message); Assert.Contains("(0, 0)", error.Message); Assert.Equal(0, stages); Assert.Empty(source.Cells);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(27)]
    [InlineData(1729)]
    public void RefinedProvinceIsDeterministicAndKeepsRequiredRoutesAndEmptyRegions(int seed)
    {
        var source = new MapDocument(192, 192); var terrain = TerrainCatalogue.Load();
        var walls = WallCatalogue.Read(WallCatalogue.ResolvePath()); var scenery = Scenery();
        var design = new MapDesignSettings { RequireRoute = true, Points = [new("A", 40, 50), new("B", 150, 140)], Regions = [new("Plaza", 70, 70, 20, 20, MapRegionPurpose.ClearObjects)] };
        var options = Province(seed) with { Design = design, Vegetation = 100 };
        var first = ProvinceGenerator.Plan(source, terrain, walls, scenery, options);
        var repeat = ProvinceGenerator.Plan(source, terrain, walls, scenery.Reverse().ToArray(), options);
        Assert.True(MapDesignConstraints.Check(first.Generated, design).Accepted);
        Assert.Equal(first.Generated.Cells.OrderBy(pair => pair.Key), repeat.Generated.Cells.OrderBy(pair => pair.Key));
        Assert.Equal(first.Generated.PlacedStamps.Select(stamp => (stamp.Name, stamp.X, stamp.Y)), repeat.Generated.PlacedStamps.Select(stamp => (stamp.Name, stamp.X, stamp.Y)));
        Assert.Empty(source.Cells); Assert.Empty(source.PlacedStamps);
        var bare = ProvinceGenerator.Plan(source, terrain, walls, scenery, options with { Vegetation = 0 });
        for (int row = 0; row < source.Height; row += 2) for (int column = 0; column < source.Width; column += 2)
        {
            Assert.Equal(first.Generated.Get(column, row).BackLibrary, bare.Generated.Get(column, row).BackLibrary);
            Assert.Equal(first.Generated.Get(column, row).BackImage & 0x1fffffff, bare.Generated.Get(column, row).BackImage & 0x1fffffff);
        }
    }

    [Theory]
    [InlineData(1, DungeonType.Temple)]
    [InlineData(27, DungeonType.Temple)]
    [InlineData(1729, DungeonType.Temple)]
    [InlineData(1, DungeonType.Cave)]
    [InlineData(27, DungeonType.Cave)]
    public void RefinedDungeonPreservesNativeCollisionConnectivityAndSource(int seed, DungeonType type)
    {
        var source = new MapDocument(256, 256); var terrain = TerrainCatalogue.Load(); var definition = TempleDefinition.All[2];
        var walls = WallCatalogue.Read(WallCatalogue.ResolvePath()).Families.Single(family => family.Id == definition.WallId).WithMode(true);
        var options = new TempleOptions(definition, seed, Type: type) { AlgorithmVersion = GenerationAlgorithms.Current };
        var first = TempleGenerator.Plan(source, walls, terrain.Sets.Single(set => set.Id == definition.FloorId), options);
        var repeat = TempleGenerator.Plan(source, walls, terrain.Sets.Single(set => set.Id == definition.FloorId), options);
        Assert.Equal(first.Generated.Cells.OrderBy(pair => pair.Key), repeat.Generated.Cells.OrderBy(pair => pair.Key));
        var route = MapRouteAnalysis.Analyze(first.Generated, first.RoomCentres.Select((point, index) => new MapRoutePoint(index.ToString(), point.X, point.Y)).ToArray());
        Assert.NotEmpty(route.Legs); Assert.All(route.Legs, leg => Assert.True(leg.Reachable));
        Assert.Empty(source.Cells); Assert.Empty(source.PlacedStamps);
    }

    [Fact]
    public void TerrainVariantOrderingCannotChangeRefinedGeneration()
    {
        var source = new MapDocument(128, 128); var terrain = TerrainCatalogue.Load();
        var basis = terrain.Sets.Single(set => set.Id == "0:0:0"); var patch = terrain.Sets.Single(set => set.Id == "0:0:950");
        var options = new TerrainOptions(basis, patch, null, 27) { AlgorithmVersion = GenerationAlgorithms.Current };
        var first = TerrainGenerator.Plan(source, new(0, 0, 128, 128), terrain, options);
        foreach (var set in terrain.Sets) foreach (var variants in set.Shapes.Values) variants.Reverse();
        var repeat = TerrainGenerator.Plan(source, new(0, 0, 128, 128), terrain, options);
        Assert.Equal(first.Tiles, repeat.Tiles); Assert.Empty(source.Cells);
        Assert.Throws<InvalidDataException>(() => TerrainGenerator.Plan(source, new(0, 0, 128, 128), terrain, options with { AlgorithmVersion = 99 }));
    }

    [AvaloniaFact]
    public void NewWindowsRecordVersionAndOldRecipeParametersRestoreLegacyWithoutChangingPartialEdits()
    {
        using var assets = AssetStore.Load(Path.Combine(TestApplication.Root, "Data"));
        var window = new TerrainGeneratorWindow(new(128, 128), assets, TerrainCatalogue.Load());
        try
        {
            Assert.Equal("2", GeneratorPreferences.Capture(window)[GenerationAlgorithms.Parameter]);
            var choice = window.GetLogicalDescendants().OfType<ComboBox>().Single(control => control.Name == "GeneratorAlgorithm");
            Assert.Equal(1, choice.SelectedIndex);
            GeneratorPreferences.Restore(window, new Dictionary<string, string> { ["CoverageSlider"] = ".5" });
            Assert.Equal(2, GenerationAlgorithms.Version(window));
            GeneratorPreferences.Restore(window, new Dictionary<string, string> { ["SeedBox"] = "27" });
            Assert.Equal("1", GeneratorPreferences.Capture(window)[GenerationAlgorithms.Parameter]);
            Assert.Equal(0, choice.SelectedIndex); choice.SelectedIndex = 1;
            Assert.Equal(2, GenerationAlgorithms.Version(window));
            GeneratorPreferences.Restore(window, new Dictionary<string, string> { [GenerationAlgorithms.Parameter] = "2", ["SeedBox"] = "17" });
            Assert.Equal("2", GeneratorPreferences.Capture(window)[GenerationAlgorithms.Parameter]);
            var before = GeneratorPreferences.Capture(window);
            Assert.Throws<InvalidDataException>(() => GeneratorPreferences.Restore(window, new Dictionary<string, string> { [GenerationAlgorithms.Parameter] = "99", ["SeedBox"] = "999" }));
            Assert.Equal(before.OrderBy(pair => pair.Key), GeneratorPreferences.Capture(window).OrderBy(pair => pair.Key));
            var recipe = MapCreationStore.Recipe(new(128, 128), MapCreationKind.Terrain, before, new());
            Assert.Contains("algorithm-v2", recipe.GeneratorVersion); MapRecipeRestore.Validate(recipe);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void AlgorithmSelectionIsGlobalRatherThanPartOfPerSceneDrafts()
    {
        using var assets = AssetStore.Load(Path.Combine(TestApplication.Root, "Data"));
        var window = new ProvinceGeneratorWindow(new(256, 256), assets);
        try
        {
            var choice = window.GetLogicalDescendants().OfType<ComboBox>().Single(control => control.Name == "GeneratorAlgorithm");
            var scene = window.FindControl<ComboBox>("SceneList")!;
            scene.SelectedIndex = 0; scene.SelectedIndex = 2;
            choice.SelectedIndex = 0; scene.SelectedIndex = 0;
            Assert.Equal(0, choice.SelectedIndex); Assert.Equal(1, GenerationAlgorithms.Version(window));
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(1, false)]
    [InlineData(2, true)]
    public void FailedRefinedSeedsCanStartAnotherBoundedSearch(int version, bool enabled)
    {
        using var assets = AssetStore.Load(Path.Combine(TestApplication.Root, "Data"));
        var parameters = new Dictionary<string, string> { [GenerationAlgorithms.Parameter] = version.ToString() };
        var session = new MapCreationSession(new(MapCreationKind.Terrain, 128, 128), parameters);
        using var candidates = session.Candidates;
        var window = new TerrainGeneratorWindow(new(128, 128), assets, TerrainCatalogue.Load(), parameters);
        try
        {
            QuickMapPreview.Configure(window, session, _ => false);
            window.PreviewState.SetPhase(GeneratorPreviewPhase.Failed, "Fixture constraint mismatch");
            Assert.Equal(enabled, window.GetLogicalDescendants().OfType<Button>().Single(control => control.Name == "QuickMapBatchStart").IsEnabled);
            Assert.Contains("Fixture constraint mismatch", window.GetLogicalDescendants().OfType<TextBlock>().Single(control => control.Name == "FeedbackText").Text);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task FootprintControlInvalidatesAndRecomputesActualOccupancyRoutes()
    {
        var map = new MapDocument(8, 8);
        for (int row = 0; row < 8; row++) if (row != 4) map.Set(4, row, new Cell { BackImage = 0x60000000 });
        var design = new MapDesignSettings { Points = [new("A", 1, 2), new("B", 6, 2)] };
        var window = new MapProductionWindow(map, null, design, null, _ => [], () => { }, (_, _) => { }, () => null,
            (_, _, _, _) => Task.FromResult<string?>(null), (_, _) => Task.FromResult<string?>(null));
        window.Show();
        try
        {
            var controls = window.GetLogicalDescendants().OfType<Control>().Distinct().ToArray();
            var analyze = controls.OfType<Button>().Single(control => control.Name == "ProductionAnalyzeRoute");
            var legs = controls.OfType<ListBox>().Single(control => control.Name == "ProductionRouteLegs");
            var footprint = controls.OfType<NumericUpDown>().Single(control => control.Name == "ProductionRouteFootprint");
            analyze.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await EditorExperienceTests.WaitFor(() => legs.Items.Count == 1);
            Assert.True(Assert.IsType<MapRouteLeg>(legs.Items[0]).Reachable);
            footprint.Value = 2; Assert.Empty(legs.Items);
            analyze.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await EditorExperienceTests.WaitFor(() => legs.Items.Count == 1);
            var result = Assert.IsType<MapRouteLeg>(legs.Items[0]); Assert.False(result.Reachable); Assert.Equal(2, result.FootprintSize);
            if (Environment.GetEnvironmentVariable("CRYSTALSTUDIO_ALGORITHM_ARTIFACTS") is { Length: > 0 } directory)
            {
                Directory.CreateDirectory(directory); await Task.Delay(100); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame); frame.Save(Path.Combine(directory, "square-footprint-analysis.png"));
            }
            footprint.Value = 1.5M; analyze.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var feedback = controls.OfType<TextBlock>().Single(control => control.Name == "ProductionFeedback");
            await EditorExperienceTests.WaitFor(() => feedback.Text == LocalText.T("Enter a whole number."));
            Assert.Empty(legs.Items);
        }
        finally { window.Close(); }
    }

    [Theory]
    [InlineData(DungeonTheme.CelestialPalace)]
    [InlineData(DungeonTheme.AbyssalReef)]
    [InlineData(DungeonTheme.AlienPlanet)]
    public void RefinedThemesKeepCompleteObjectsAndReachableChambers(DungeonTheme theme)
    {
        var source = new MapDocument(256, 256); var terrain = TerrainCatalogue.Load(); var definition = DungeonThemes.Definition(theme);
        var walls = WallCatalogue.Read(WallCatalogue.ResolvePath()).Families.Single(family => family.Id == definition.WallId).WithMode(true);
        var options = new TempleOptions(definition, 101, Rooms: 9, Type: DungeonThemes.Type(theme), Theme: theme) { AlgorithmVersion = GenerationAlgorithms.Current };
        var plan = TempleGenerator.Plan(source, walls, terrain.Sets.Single(set => set.Id == definition.FloorId), options);
        Assert.NotEmpty(plan.Decorations);
        var routes = MapRouteAnalysis.Analyze(plan.Generated, plan.RoomCentres.Select((point, index) => new MapRoutePoint(index.ToString(), point.X, point.Y)).ToArray());
        Assert.All(routes.Legs, leg => Assert.True(leg.Reachable));
        foreach (var stamp in plan.Decorations) Assert.All(stamp.Cells, cell => Assert.True(plan.Generated.Contains(stamp.X + cell.X, stamp.Y + cell.Y)));
        Assert.Empty(source.Cells); Assert.Empty(source.PlacedStamps);
    }

    [Theory]
    [InlineData(ProvinceScene.VillageCountryside)]
    [InlineData(ProvinceScene.ForestRuins)]
    [InlineData(ProvinceScene.MiningQuarry)]
    public void RefinedProvinceScenesRetainTheirRequestedProductionSites(ProvinceScene scene)
    {
        var source = new MapDocument(320, 320); var terrain = TerrainCatalogue.Load();
        var options = Province(101) with { Scene = scene, TownCount = scene == ProvinceScene.VillageCountryside ? 1 : 0 };
        var plan = ProvinceGenerator.Plan(source, terrain, WallCatalogue.Read(WallCatalogue.ResolvePath()), Scenery(), options);
        if (scene == ProvinceScene.VillageCountryside) Assert.Single(plan.Towns);
        if (scene == ProvinceScene.ForestRuins) Assert.Equal(options.RuinCount, plan.Ruins.Count);
        if (scene == ProvinceScene.MiningQuarry) Assert.Equal(options.MineCount, plan.Mines.Count);
        Assert.Empty(source.Cells); Assert.Empty(source.PlacedStamps);
    }

    [Fact]
    public void NewAlgorithmHelpersObserveCancellation()
    {
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => MapClearance.Build(new(8, 8), cancelled.Token));
        Assert.Throws<OperationCanceledException>(() => TerrainFieldNormalizer.Normalize(new bool[9], 2, 2, true, (_, _) => true, 1, cancelled.Token));
        Assert.Throws<OperationCanceledException>(() => DungeonTopology.SelectLoops(2, [(0, 1)], [], 1, 1, cancelled.Token));
        Assert.Throws<OperationCanceledException>(() => ProvinceScenerySampling.Sites(8, 8, 3, 1, 1, cancelled.Token));
        Assert.Throws<OperationCanceledException>(() => GenerationConstraints.Compile(new(8, 8), new(), cancelled.Token));
    }

    [Fact]
    public void RefinedCandidateIdentityIncludesDefensiveLayoutSnapshots()
    {
        using var candidates = new QuickMapCandidates(); var settings = new MapCreationSettings(MapCreationKind.Province);
        var parameters = new Dictionary<string, string> { ["SeedBox"] = "27", [GenerationAlgorithms.Parameter] = "2" };
        var firstDesign = new MapDesignSettings { Regions = [new("A", 20, 20, 10, 10, MapRegionPurpose.ClearObjects)] };
        var secondDesign = new MapDesignSettings { Regions = [new("B", 50, 50, 10, 10, MapRegionPurpose.ClearObjects)] };
        var first = candidates.Remember(settings, parameters, design: firstDesign)!; candidates.SetFavorite(first, true);
        var second = candidates.Remember(settings, parameters, design: secondDesign)!;
        Assert.NotSame(first, second); Assert.Equal(2, candidates.Items.Count); Assert.True(first.IsFavorite);
        Assert.True(first.Matches(settings, parameters, firstDesign)); Assert.False(first.Matches(settings, parameters, secondDesign));
        firstDesign.Regions.Clear(); var copy = first.CopyDesign()!; Assert.Single(copy.Regions); copy.Regions.Clear();
        Assert.Single(first.CopyDesign()!.Regions);
        parameters[GenerationAlgorithms.Parameter] = "1";
        var legacy = candidates.Remember(settings, parameters, design: firstDesign);
        Assert.Same(legacy, candidates.Remember(settings, parameters, design: secondDesign)); Assert.Null(legacy!.CopyDesign());
    }

    [AvaloniaTheory]
    [InlineData(0, 2)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    public void ReturningBeforeArtworkLoadsPreservesAlgorithmIdentity(int supplied, int expected)
    {
        using var assets = AssetStore.Load(Path.Combine(TestApplication.Root, "Data"));
        var parameters = supplied == 0 ? null : new Dictionary<string, string> { [GenerationAlgorithms.Parameter] = supplied.ToString() };
        var session = new MapCreationSession(new(MapCreationKind.Terrain, 128, 128), parameters); using var candidates = session.Candidates;
        var window = new TerrainGeneratorWindow(new(128, 128), assets, new TerrainCatalogue(), parameters);
        try
        {
            QuickMapPreview.Configure(window, session, _ => false);
            window.GetLogicalDescendants().OfType<Button>().Single(control => control.Name == "QuickMapBack").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(window.PreferencesRestored); Assert.Equal(expected.ToString(), session.Parameters[GenerationAlgorithms.Parameter]);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task CandidateRestorationUpdatesTheDesignModelEditorAndGeneratorSnapshotTogether()
    {
        using var assets = AssetStore.Load(Path.Combine(TestApplication.Root, "Data"));
        var original = new MapDesignSettings { RequireRoute = true, Points = [new("Entry", 20, 20), new("Goal", 90, 90)], Regions = [new("A", 30, 30, 8, 8, MapRegionPurpose.ClearObjects)] };
        var changed = new MapDesignSettings { Regions = [new("B", 70, 70, 8, 8, MapRegionPurpose.ClearObjects)] };
        var session = new MapCreationSession(new(MapCreationKind.Terrain, 128, 128)) { Design = original.Copy() }; using var candidates = session.Candidates;
        var window = new TerrainGeneratorWindow(new(128, 128), assets, SyntheticTerrain());
        try
        {
            QuickMapPreview.Configure(window, session, _ => false);
            window.Show();
            await EditorExperienceTests.WaitFor(() => window.PreferencesRestored && window.FindControl<Button>("ApplyButton")!.IsEnabled);
            var production = window.GetLogicalDescendants().OfType<QuickMapProduction>().Single(); production.RestoreDesign(changed);
            Assert.True(session.Design.SameAs(changed)); window.PreviewState.Notify();
            window.GetLogicalDescendants().OfType<Button>().Single(control => control.Name == "QuickMapCandidate0").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(session.Design.SameAs(original)); Assert.True(GenerationAlgorithms.Design(window)!.SameAs(original));
            Assert.True(window.GetLogicalDescendants().OfType<CheckBox>().Single(control => control.Name == "QuickMapRequireRoute").IsChecked);
            Assert.Equal(2, window.GetLogicalDescendants().OfType<ListBox>().Single(control => control.Name == "QuickMapDesignPoints").Items.Count);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void ComparisonReportsLayoutDifferencesEvenForTheSameSeedAndParameters()
    {
        using var candidates = new QuickMapCandidates(); var settings = new MapCreationSettings(MapCreationKind.Province);
        var parameters = new Dictionary<string, string> { ["SeedBox"] = "27", [GenerationAlgorithms.Parameter] = "2" };
        var first = candidates.Remember(settings, parameters)!;
        var second = candidates.Remember(settings, parameters, design: new() { Regions = [new("A", 20, 20, 10, 10, MapRegionPurpose.ClearObjects)] })!;
        var window = new QuickMapComparisonWindow([first, second], second);
        try
        {
            Assert.Contains(LocalText.T("Layout constraints differ between these candidates."),
                window.GetLogicalDescendants().OfType<TextBlock>().Single(control => control.Name == "QuickMapParameterDifferences").Text);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task BatchResultRestoresItsRecordedDesignInsteadOfTheCurrentDesign()
    {
        using var assets = AssetStore.Load(Path.Combine(TestApplication.Root, "Data"));
        var original = new MapDesignSettings { Regions = [new("A", 20, 20, 10, 10, MapRegionPurpose.ClearObjects)] };
        var changed = new MapDesignSettings { Regions = [new("B", 50, 50, 10, 10, MapRegionPurpose.ClearObjects)] };
        var session = new MapCreationSession(new(MapCreationKind.Terrain, 128, 128)) { Design = original.Copy() }; using var candidates = session.Candidates;
        var terrain = SyntheticTerrain();
        var window = new TerrainGeneratorWindow(new(128, 128), assets, terrain); QuickMapPreview.Configure(window, session, _ => false); window.Show();
        try
        {
            await EditorExperienceTests.WaitFor(() => window.PreferencesRestored && window.FindControl<Button>("ApplyButton")!.IsEnabled);
            var production = window.GetLogicalDescendants().OfType<QuickMapProduction>().Single();
            window.GetLogicalDescendants().OfType<NumericUpDown>().Single(control => control.Name == "QuickMapBatchCount").Value = 2;
            await production.SearchAsync();
            var results = window.GetLogicalDescendants().OfType<ListBox>().Single(control => control.Name == "QuickMapBatchResults");
            var rows = results.Items.Cast<QuickMapBatchResult>().ToArray(); Assert.Equal(2, rows.Length);
            Assert.All(rows, row => { Assert.True(row.Accepted, row.Details); Assert.True(row.Design!.SameAs(original)); });
            production.RestoreDesign(changed); Assert.True(session.Design.SameAs(changed));
            results.SelectedIndex = 0;
            window.GetLogicalDescendants().OfType<Button>().Single(control => control.Name == "QuickMapBatchRestore").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(session.Design.SameAs(original));
            await EditorExperienceTests.WaitFor(() => window.Plan?.Seed == rows[0].Seed && window.FindControl<Button>("ApplyButton")!.IsEnabled);
        }
        finally { window.Close(); }
    }

    private static TerrainCatalogue SyntheticTerrain() => new() { Sets = [new TerrainSet { Id = "test", Name = "Fixture ground", Base = true, Biome = "Province", Library = 0,
        Shapes = new() { [15] = [new TerrainVariant { Index = 0, Weight = 1 }] } }] };
    private static ProvinceOptions Province(int seed) => new(seed, ProvinceStyle.All[0], ForestChance: 0, CliffChance: 0, BeachChance: 0, RiverChance: 0)
        { AlgorithmVersion = GenerationAlgorithms.Current };
    private static ProvinceScenery[] Scenery() => Enum.GetValues<SpecialBrushKind>().Select(kind => new ProvinceScenery(new(kind.ToString(), "", [new(0, 0,
        new Cell { Foreground = new(2, (short)((int)kind + 1)), BackImage = 0x60000000 })]), kind, [ProvinceStyle.All[0].Region])).ToArray();

    private static int BruteRoute(int mask, int width, int height, MapRoutePoint from, MapRoutePoint to, int size)
    {
        var free = new bool[width * height];
        for (int row = 0; row + size <= height; row++) for (int column = 0; column + size <= width; column++)
        {
            bool fits = true;
            for (int dy = 0; dy < size; dy++) for (int dx = 0; dx < size; dx++)
                fits &= (mask & (1 << ((row + dy) * width + column + dx))) == 0;
            free[row * width + column] = fits;
        }
        int start = from.Y * width + from.X, goal = to.Y * width + to.X;
        if (!free[start] || !free[goal]) return -1;
        var distances = Enumerable.Repeat(-1, width * height).ToArray(); var queue = new Queue<int>(); queue.Enqueue(start); distances[start] = 0;
        while (queue.TryDequeue(out int current))
        {
            if (current == goal) return distances[current];
            foreach (int next in Enumerable.Range(0, width * height).Where(next => Math.Abs(next % width - current % width) + Math.Abs(next / width - current / width) == 1))
                if (free[next] && distances[next] < 0) { distances[next] = distances[current] + 1; queue.Enqueue(next); }
        }
        return -1;
    }

    private static int[,] GraphDistances(int count, IEnumerable<(int A, int B)> edges)
    {
        var adjacency = Enumerable.Range(0, count).Select(_ => new List<int>()).ToArray();
        foreach (var edge in edges) { adjacency[edge.A].Add(edge.B); adjacency[edge.B].Add(edge.A); }
        var distances = new int[count, count];
        for (int start = 0; start < count; start++)
        {
            for (int index = 0; index < count; index++) distances[start, index] = index == start ? 0 : count;
            var queue = new Queue<int>(); queue.Enqueue(start);
            while (queue.TryDequeue(out int current))
                foreach (int next in adjacency[current])
                    if (distances[start, next] == count) { distances[start, next] = distances[start, current] + 1; queue.Enqueue(next); }
        }
        return distances;
    }
}
