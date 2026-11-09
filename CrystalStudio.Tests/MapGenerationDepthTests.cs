using System.IO;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;

namespace CrystalStudio.Tests;

public sealed class MapGenerationDepthTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void FootprintMetricsConstraintsAndRoutesAgreeWithDirectEnumeration(int footprint)
    {
        for (int mask = 0; mask < 4096; mask++)
        {
            var map = new MapDocument(4, 3);
            for (int index = 0; index < 12; index++) if ((mask & 1 << index) != 0) map.Set(index % 4, index / 4, new Cell { BackImage = 0x40000000 });
            var design = new MapDesignSettings { FootprintSize = footprint, RequireRoute = true, Points = [new("Start", 0, 0), new("Goal", 4 - footprint, 3 - footprint)] };
            var anchors = Anchors(map, footprint); var fromStart = Flood(anchors, 4, 0);
            var metrics = MapRouteAnalysis.Measure(map, design: design);
            Assert.Equal(anchors.Count(open => open), metrics.WalkableCells);
            Assert.Equal(fromStart.Count(distance => distance >= 0), metrics.StartArea);
            int target = (3 - footprint) * 4 + 4 - footprint;
            bool reachable = fromStart[target] >= 0;
            Assert.Equal(reachable, MapDesignConstraints.Check(map, design).Accepted);
            Assert.Equal(reachable, Assert.Single(MapRouteAnalysis.Analyze(map, design.Points, footprintSize: footprint).Legs).Reachable);
            Assert.Equal(reachable ? fromStart[target] : (int?)null, metrics.RouteSteps);
        }
    }

    [Fact]
    public void FirstPointCoverageRejectsAnIsolatedStartDespiteALargeConnectedArea()
    {
        var map = new MapDocument(10, 8);
        for (int row = 0; row < 8; row++) map.Set(1, row, new Cell { BackImage = 0x20000000 });
        var design = new MapDesignSettings { Points = [new("Entry", 0, 0)] };
        var measured = MapRouteAnalysis.Measure(map, design: design);
        Assert.True(measured.ConnectedRatio > .8); Assert.True(measured.ReachableRatio < .2);
        Assert.NotEmpty(new MapCandidateThresholds(0, 80, 0, false).Failures(measured));
        Assert.Empty(new MapCandidateThresholds(0, 80, 0, false).Failures(MapRouteAnalysis.Measure(map)));
    }

    [Theory]
    [InlineData(1, 80)]
    [InlineData(2, 63)]
    [InlineData(3, 48)]
    public void FullyOpenMapsCanMeetOneHundredPercentWalkabilityAtEveryFootprint(int footprint, int expectedAnchors)
    {
        var metrics = MapRouteAnalysis.Measure(new(10, 8), design: new() { FootprintSize = footprint });
        Assert.Equal(expectedAnchors, metrics.PossibleAnchors); Assert.Equal(expectedAnchors, metrics.WalkableCells);
        Assert.Equal(1, metrics.WalkableRatio); Assert.Empty(new MapCandidateThresholds(100, 100, 0, false).Failures(metrics));
    }

    [Fact]
    public void ReservationsProtectTheEntireSweptFootprintAndItsBuffer()
    {
        var map = new MapDocument(18, 10);
        var design = new MapDesignSettings { FootprintSize = 3, RequireRoute = true, Points = [new("Start", 2, 2), new("Goal", 12, 2)] };
        var constraints = GenerationConstraints.Compile(map, design)!; constraints.ReserveRoutes(map, default);
        for (int row = 1; row <= 5; row++) for (int column = 1; column <= 15; column++) Assert.True(constraints.Reserved[row * 18 + column]);
        Assert.False(constraints.Reserved[6 * 18 + 8]);
        var forbidden = new bool[180]; forbidden[4 * 18 + 14] = true;
        var exception = Assert.Throws<InvalidDataException>(() => constraints.CheckForbidden(forbidden, default));
        Assert.Equal(GenerationFailureKind.SeedRejected, GenerationDiagnostics.Describe(exception, "test").Kind);
        Assert.Contains("Goal", exception.Message);
    }

    [Fact]
    public void OutOfBoundsFootprintsExplainTheNamedInputConflictWithoutRelaxingPreferences()
    {
        var map = new MapDocument(8, 8);
        var design = new MapDesignSettings { FootprintSize = 2, Regions = [new("Exit lane", 6, 2, 2, 3, MapRegionPurpose.Walkable)] };
        var exception = Assert.Throws<InvalidDataException>(() => GenerationConstraints.Compile(map, design));
        var failure = GenerationDiagnostics.Describe(exception, "test");
        Assert.Equal(GenerationFailureKind.InputConflict, failure.Kind); Assert.Equal("Exit lane", Assert.Single(failure.Findings).Name);
        design.Regions[0] = design.Regions[0] with { Required = false };
        Assert.NotNull(GenerationConstraints.Compile(map, design)); Assert.True(MapDesignConstraints.Check(map, design).Accepted);
        Assert.Single(MapDesignConstraints.Check(map, design).Findings);
    }

    [Fact]
    public void SeedDependentBoundaryConflictsAreNotMisreportedAsPermanentInputConflicts()
    {
        var map = new MapDocument(128, 128); var mask = ProvinceBoundary.CreateMask(128, 128, 1);
        int variable = Enumerable.Range(0, mask.Length).First(index => mask[index] && index % 128 >= 10 && index % 128 < 118 && index / 128 >= 10 && index / 128 < 118);
        var design = new MapDesignSettings { Regions = [new("Variable fringe", variable % 128, variable / 128, 1, 1, MapRegionPurpose.Walkable)] };
        var constraints = GenerationConstraints.Compile(map, design)!;
        bool Permanent(int column, int row) => column < 10 || row < 10 || column >= 118 || row >= 118;
        var exception = Assert.Throws<InvalidDataException>(() => constraints.CheckForbidden(mask, default, Permanent));
        var failure = GenerationDiagnostics.Describe(exception, "test"); Assert.Equal(GenerationFailureKind.SeedRejected, failure.Kind); Assert.Equal(2, failure.Findings.Count);
        design.Regions = [new("Permanent belt", 2, 2, 1, 1, MapRegionPurpose.Walkable)];
        constraints = GenerationConstraints.Compile(map, design)!;
        exception = Assert.Throws<InvalidDataException>(() => constraints.CheckForbidden(mask, default, Permanent));
        Assert.Equal(GenerationFailureKind.InputConflict, GenerationDiagnostics.Describe(exception, "test").Kind);
    }

    [Fact]
    public void NarrowCellsUseTheSelectedFootprintAnchorGraphRatherThanSingleCellNeighbours()
    {
        var route = Assert.Single(MapRouteAnalysis.Analyze(new(5, 4), [new("A", 0, 0), new("B", 2, 1)], footprintSize: 3).Legs);
        Assert.True(route.Reachable); Assert.Equal(3, route.NarrowCells); Assert.Equal(0, route.DeadEnds);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(64)]
    public void DesignCopiesAndJsonRetainFootprints(int footprint)
    {
        var design = new MapDesignSettings { FootprintSize = footprint };
        var copy = StudioJson.Deserialize<MapDesignSettings>(System.Text.Encoding.UTF8.GetString(StudioJson.SerializeToUtf8Bytes(design)))!;
        Assert.True(copy.SameAs(design)); Assert.Equal(footprint, design.Copy().FootprintSize);
        Assert.Equal(1, StudioJson.Deserialize<MapDesignSettings>("{\"Points\":[],\"Regions\":[],\"RequireRoute\":false}")!.FootprintSize);
        copy.FootprintSize = footprint == 1 ? 2 : 1; Assert.False(copy.SameAs(design));
    }

    [Theory]
    [InlineData(1, 0, 20)]
    [InlineData(7, 1, 16)]
    public void NativeLoopVerificationRejectsOverShortcutsAndAcceptsRealCellSavings(int shortcutX, int expectedLoops, int expectedMain)
    {
        var options = new TempleOptions(TempleDefinition.All[2], 27) { AlgorithmVersion = 3 };
        var source = new MapDocument(12, 7);
        (int X, int Y)[] points = [(1, 1), (9, 1), (1, 5)];
        var result = NativeDungeonLoops.Refine(options, candidate =>
        {
            var map = UMap();
            if (candidate.LoopEdges!.Count > 0) for (int row = 1; row <= 5; row++) map.Set(shortcutX, row, new Cell());
            return new(source, source.PickupRevision, map, candidate, 3, points) { LoopCandidates = [(0, 2)], LoopBudget = 1 };
        }, default);
        var report = result.LoopVerification!;
        Assert.True(report.Verified); Assert.Equal(expectedLoops, report.AcceptedLoops); Assert.Equal(20, report.InitialMainSteps); Assert.Equal(expectedMain, report.FinalMainSteps);
        Assert.Equal(expectedMain, Flood(Anchors(result.Generated, 1), 12, 13)[61]);
        Assert.Equal(expectedLoops == 0 ? 0 : 4, report.PairStepsSaved); Assert.Equal(1, report.Trials);
        Assert.Empty(source.Cells);
    }

    [Fact]
    public void NativeLoopVerificationIsBoundedAndDoesNotClaimInfeasibility()
    {
        var options = new TempleOptions(TempleDefinition.All[2]) { AlgorithmVersion = 3 };
        (int X, int Y)[] points = [(1, 1), (3, 1), (5, 1), (7, 1), (9, 5)];
        var result = NativeDungeonLoops.Refine(options, candidate => new(new(12, 7), 0, UMap(), candidate, 5, points)
        {
            LoopCandidates = [(0, 2), (0, 3), (0, 4), (1, 3), (1, 4), (2, 4), (2, 3)], LoopBudget = 3
        }, default);
        Assert.Equal(NativeDungeonLoops.MaximumTrials, result.LoopVerification!.Trials);
        Assert.True(result.LoopVerification.BudgetExhausted); Assert.Equal(0, result.LoopVerification.AcceptedLoops);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(27)]
    [InlineData(1301)]
    public void RealNativeDungeonLoopReportsMatchIndependentCellSearch(int seed)
    {
        var terrain = TerrainCatalogue.Load(); var definition = TempleDefinition.All[2];
        var walls = WallCatalogue.Read(WallCatalogue.ResolvePath()).Families.Single(family => family.Id == definition.WallId).WithMode(true);
        var source = new MapDocument(256, 256); var options = new TempleOptions(definition, seed) { AlgorithmVersion = 3 };
        var baseline = TempleGenerator.Plan(source, walls, terrain.Sets.Single(set => set.Id == definition.FloorId), options with { LoopEdges = [] });
        var result = TempleGenerator.Plan(source, walls, terrain.Sets.Single(set => set.Id == definition.FloorId), options);
        var report = result.LoopVerification!; Assert.True(report.Verified);
        var from = result.RoomCentres[report.ReferenceFrom]; var to = result.RoomCentres[report.ReferenceTo];
        int actual = Flood(Anchors(result.Generated, 1), 256, from.Y * 256 + from.X)[to.Y * 256 + to.X];
        Assert.Equal(actual, report.FinalMainSteps); Assert.True(actual >= Math.Ceiling(report.InitialMainSteps * .75));
        Assert.InRange(report.Trials, 0, NativeDungeonLoops.MaximumTrials);
        var initialAnchors = Anchors(baseline.Generated, 1); long before = 0, after = 0;
        for (int index = 0; index < result.RoomCentres.Count; index++)
        {
            var first = baseline.RoomCentres[index]; var last = result.RoomCentres[index];
            var initial = Flood(initialAnchors, 256, first.Y * 256 + first.X); var final = Flood(Anchors(result.Generated, 1), 256, last.Y * 256 + last.X);
            for (int target = index + 1; target < result.RoomCentres.Count; target++)
            { var previous = baseline.RoomCentres[target]; var current = result.RoomCentres[target]; before += initial[previous.Y * 256 + previous.X]; after += final[current.Y * 256 + current.X]; }
        }
        Assert.Equal(before - after, report.PairStepsSaved); Assert.Empty(source.Cells);
    }

    [Fact]
    public void LargerCalibrationDungeonsExerciseBeneficialNativeLoopsWithoutChangingHoldoutSeeds()
    {
        var terrain = TerrainCatalogue.Load(); var definition = TempleDefinition.All[2];
        var walls = WallCatalogue.Read(WallCatalogue.ResolvePath()).Families.Single(family => family.Id == definition.WallId).WithMode(true);
        var reports = new List<NativeLoopReport>();
        foreach (int seed in new[] { 101, 211, 307 })
        {
            var result = TempleGenerator.Plan(new(512, 512), walls, terrain.Sets.Single(set => set.Id == definition.FloorId), new(definition, seed, Rooms: 20) { AlgorithmVersion = 3 });
            Assert.NotNull(result.LoopVerification); Assert.True(result.LoopVerification.Verified); reports.Add(result.LoopVerification);
        }
        if (Environment.GetEnvironmentVariable("CRYSTALSTUDIO_ALGORITHM_ARTIFACTS") is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory); File.WriteAllText(Path.Combine(directory, "native-loop-calibration.json"), System.Text.Json.JsonSerializer.Serialize(new { Seeds = new[] { 101, 211, 307 }, Width = 512, Rooms = 20, Reports = reports }));
        }
        Assert.Contains(reports, report => report.AcceptedLoops > 0 && report.PairStepsSaved > 0);
    }

    [Fact]
    public void CandidateSelectionPreservesFavoritesAndImprovesLayoutDiversity()
    {
        var metrics = Enumerable.Range(0, 5).Select(index => Profile(index)).ToArray();
        var diverse = Profile(100) with { RouteSteps = 1800, MeanClearance = 8, MeanBranchDepth = 20 };
        bool[] favorites = [true, false, false, false, false];
        int replacement = MapCandidateSelection.Replacement(metrics, favorites, diverse);
        Assert.InRange(replacement, 1, 4);
        double before = MapCandidateSelection.Diversity(metrics); metrics[replacement] = diverse;
        Assert.True(MapCandidateSelection.Diversity(metrics) > before);
        Assert.Equal(-1, MapCandidateSelection.Replacement(metrics, [true, true, true, true, true], Profile(101)));
        Assert.Equal(-1, MapCandidateSelection.Replacement(metrics, favorites, diverse with { PreferredConflicts = 1 }));
    }

    [Fact]
    public void MeasuredCandidatesDeduplicateTopologyAndKeepUnmeasuredFavoritesSafe()
    {
        using var candidates = new QuickMapCandidates(); var settings = new MapCreationSettings(MapCreationKind.Terrain, 128, 128); var design = new MapDesignSettings();
        var favorite = candidates.Remember(settings, Parameters(0))!; candidates.SetFavorite(favorite, true);
        var retained = candidates.RememberMeasured(settings, Parameters(1), Profile(1), null, design).Candidate!;
        var duplicate = candidates.RememberMeasured(settings, Parameters(2), Profile(1), null, design);
        Assert.Same(retained, duplicate.Candidate); Assert.Equal(2, candidates.Items.Count);
        for (int index = 3; index < 12; index++) candidates.RememberMeasured(settings, Parameters(index), Profile(index) with { MeanClearance = index }, null, design);
        Assert.Equal(5, candidates.Items.Count); Assert.Contains(favorite, candidates.Items); Assert.True(favorite.IsFavorite);
    }

    [Fact]
    public void MeasuredLegacyCandidatesRestoreTheirEvaluationContextWithoutChangingLegacyGeneration()
    {
        using var candidates = new QuickMapCandidates(); var settings = new MapCreationSettings(MapCreationKind.Terrain, 128, 128);
        var parameters = new Dictionary<string, string> { ["SeedBox"] = "1", [GenerationAlgorithms.Parameter] = "1" };
        var design = new MapDesignSettings { FootprintSize = 2, Points = [new("Entry", 20, 20)] };
        var candidate = candidates.RememberMeasured(settings, parameters, Profile(1) with { FootprintSize = 2 }, null, design).Candidate!;
        Assert.True(candidate.CopyDesign()!.SameAs(design)); Assert.True(candidate.Matches(settings, parameters, design));
        Assert.False(candidate.Matches(settings, parameters, new() { FootprintSize = 1 }));
        Assert.Equal(1, GenerationAlgorithms.Read(candidate.Parameters));
    }

    [Fact]
    public void FailureKindsHaveDifferentRecoveryAdviceAndKeepSourceFindings()
    {
        var finding = new MapConstraintFinding("Entry", true, "Blocked", 3, 4);
        var failure = GenerationDiagnostics.Describe(GenerationDiagnostics.Reject(GenerationFailureKind.SeedRejected, "Layout validation", finding.ToString(), [finding]), "other");
        Assert.Equal(finding, Assert.Single(failure.Findings)); Assert.Contains("Entry", failure.ToString());
        var missing = GenerationDiagnostics.Describe(new FileNotFoundException("missing.X"), "Artwork preflight");
        Assert.Equal(GenerationFailureKind.MissingAssets, missing.Kind); Assert.NotEqual(failure.Advice, missing.Advice);
        Assert.Equal(GenerationFailureKind.MissingAssets, GenerationDiagnostics.Describe(Assert.Throws<InvalidDataException>(() => new MapResourceReport([new("floor", true, false)]).EnsureReady()), "test").Kind);
        var state = new GeneratorPreviewState(); state.SetFailure(new FileNotFoundException("missing.X"), "Artwork preflight");
        Assert.NotNull(state.FailureDetails); state.SetPhase(GeneratorPreviewPhase.Waiting); Assert.Null(state.FailureDetails);
    }

    [Fact]
    public void NewMetricsAndNativeLoopVerificationObserveCancellation()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel(); var map = new MapDocument(8, 8);
        Assert.Throws<OperationCanceledException>(() => MapRouteAnalysis.Measure(map, cancellation: cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => NativeDungeonLoops.Distances(map, [(0, 0)], 1, cancellation.Token));
    }

    [AvaloniaFact]
    public void RestoringTheSameDesignRepairsAnInvalidFootprintControl()
    {
        var design = new MapDesignSettings { FootprintSize = 2 }; var editor = new MapDesignEditor(design, 128, 128);
        editor.Footprint.Value = 1.5M; Assert.False(editor.InputValid); editor.Restore(design.Copy());
        Assert.True(editor.InputValid); Assert.Equal(2M, editor.Footprint.Value);
    }

    [AvaloniaFact]
    public async Task TypedInputConflictsStopSeedSearchInsteadOfRetryingEverySeed()
    {
        using var assets = AssetStore.Load(Path.Combine(TestApplication.Root, "Data")); var terrain = SimpleTerrain();
        var session = new MapCreationSession(new(MapCreationKind.Terrain, 128, 128)); using var candidates = session.Candidates;
        var source = new MapDocument(128, 128); var window = new TerrainGeneratorWindow(source, assets, terrain); QuickMapPreview.Configure(window, session, _ => false); window.Show();
        try
        {
            await EditorExperienceTests.WaitFor(() => window.PreferencesRestored && window.PreviewState.Phase == GeneratorPreviewPhase.Ready);
            var controls = window.GetLogicalDescendants().OfType<Control>().Distinct().ToArray(); var production = controls.OfType<QuickMapProduction>().Single();
            production.RestoreDesign(new() { FootprintSize = 2, RequireRoute = true, Points = [new("Entry", 127, 127), new("Goal", 30, 30)] });
            await EditorExperienceTests.WaitFor(() => window.PreviewState.Phase == GeneratorPreviewPhase.Failed);
            await production.SearchAsync();
            var row = Assert.IsType<QuickMapBatchResult>(Assert.Single(controls.OfType<ListBox>().Single(control => control.Name == "QuickMapBatchResults").Items));
            Assert.False(row.Accepted); Assert.Equal(GenerationFailureKind.InputConflict, row.Failure!.Kind);
            Assert.Equal("Entry", Assert.Single(row.Failure.Findings).Name); Assert.False(session.BatchRunning);
            Assert.Empty(source.Cells);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task BatchFilteringDoesNotReinsertTheFinalRejectedPreview()
    {
        using var assets = AssetStore.Load(Path.Combine(TestApplication.Root, "Data"));
        var terrain = new TerrainCatalogue { Sets = [new TerrainSet { Id = "test", Name = "Fixture ground", Base = true, Biome = "Province", Library = 0, Shapes = new() { [15] = [new TerrainVariant { Index = 0, Weight = 1 }] } }] };
        var session = new MapCreationSession(new(MapCreationKind.Terrain, 128, 128)); using var candidates = session.Candidates;
        var window = new TerrainGeneratorWindow(new(128, 128), assets, terrain); QuickMapPreview.Configure(window, session, _ => false); window.Show();
        try
        {
            await EditorExperienceTests.WaitFor(() => window.PreferencesRestored && window.FindControl<Button>("ApplyButton")!.IsEnabled);
            var controls = window.GetLogicalDescendants().OfType<Control>().Distinct().ToArray();
            controls.OfType<NumericUpDown>().Single(control => control.Name == "QuickMapBatchCount").Value = 2;
            controls.OfType<NumericUpDown>().Single(control => control.Name == "QuickMapMinRooms").Value = 100;
            controls.OfType<NumericUpDown>().Single(control => control.Name == "QuickMapMinConnected").Value = 100;
            var production = controls.OfType<QuickMapProduction>().Single(); production.RestoreDesign(new() { Points = [new("Blocked for 2x2", 127, 127)], FootprintSize = 2 });
            await EditorExperienceTests.WaitFor(() => window.PreviewState.Phase == GeneratorPreviewPhase.Ready && window.FindControl<Button>("ApplyButton")!.IsEnabled);
            string[] existingSeeds = candidates.Items.Select(item => item.Parameters["SeedBox"]).ToArray();
            await production.SearchAsync();
            var rows = controls.OfType<ListBox>().Single(control => control.Name == "QuickMapBatchResults").Items.Cast<QuickMapBatchResult>().ToArray();
            Assert.Equal(2, rows.Length); Assert.All(rows, row => Assert.False(row.Accepted));
            Assert.Equal(existingSeeds, candidates.Items.Select(item => item.Parameters["SeedBox"]).ToArray());
            Assert.False(session.BatchRunning);
            controls.OfType<NumericUpDown>().Single(control => control.Name == "QuickMapDesignFootprint").Value = 1.5M;
            Assert.False(await production.ConfirmAsync());
        }
        finally { window.Close(); }
    }

    internal static bool[] Anchors(MapDocument map, int footprint)
    {
        var result = new bool[map.Width * map.Height];
        for (int row = 0; row + footprint <= map.Height; row++) for (int column = 0; column + footprint <= map.Width; column++)
        {
            bool open = true;
            for (int offsetY = 0; offsetY < footprint; offsetY++) for (int offsetX = 0; offsetX < footprint; offsetX++)
                if ((map.Get(column + offsetX, row + offsetY).BackImage & 0x60000000) != 0) open = false;
            result[row * map.Width + column] = open;
        }
        return result;
    }

    internal static int[] Flood(bool[] anchors, int width, int start)
    {
        var distance = Enumerable.Repeat(-1, anchors.Length).ToArray(); if (!anchors[start]) return distance;
        var queue = new Queue<int>(); queue.Enqueue(start); distance[start] = 0;
        while (queue.TryDequeue(out int current))
        {
            int column = current % width, row = current / width;
            foreach (var next in new[] { (Column: column - 1, Row: row), (Column: column + 1, Row: row), (Column: column, Row: row - 1), (Column: column, Row: row + 1) })
                if (next.Column >= 0 && next.Column < width && next.Row >= 0 && next.Row < anchors.Length / width)
                { int index = next.Row * width + next.Column; if (anchors[index] && distance[index] < 0) { distance[index] = distance[current] + 1; queue.Enqueue(index); } }
        }
        return distance;
    }

    private static MapDocument UMap()
    {
        var map = new MapDocument(12, 7);
        for (int row = 0; row < 7; row++) for (int column = 0; column < 12; column++) map.Set(column, row, new Cell { BackImage = 0x60000000 });
        for (int column = 1; column <= 9; column++) { map.Set(column, 1, new Cell()); map.Set(column, 5, new Cell()); }
        for (int row = 1; row <= 5; row++) map.Set(9, row, new Cell());
        return map;
    }

    private static Dictionary<string, string> Parameters(int seed) => new() { ["SeedBox"] = seed.ToString(), [GenerationAlgorithms.Parameter] = "3" };
    private static TerrainCatalogue SimpleTerrain() => new() { Sets = [new TerrainSet { Id = "test", Name = "Fixture ground", Base = true, Biome = "Province", Library = 0,
        Shapes = new() { [15] = [new TerrainVariant { Index = 0, Weight = 1 }] } }] };
    private static MapCandidateMetrics Profile(int index) => new(10000, 10000, 16384, 5, 2, 12)
    { RouteSteps = 100 + index, MeanBranchDepth = 1, MeanClearance = 1 + index * .001, TopologyFingerprint = "layout-" + index };
}
