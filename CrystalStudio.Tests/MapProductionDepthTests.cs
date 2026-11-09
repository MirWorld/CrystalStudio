using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Path = System.IO.Path;

namespace CrystalStudio.Tests;

public sealed class MapProductionDepthTests
{
    [Fact]
    public void AllFourByThreeCollisionLayoutsAgreeWithIndependentAllPairsDistances()
    {
        const int width = 4, height = 3, count = width * height, infinity = 9999;
        for (int mask = 0; mask < 1 << count; mask++)
        {
            var map = new MapDocument(width, height); var open = new bool[count];
            var distance = new int[count, count];
            for (int index = 0; index < count; index++)
            {
                open[index] = (mask & (1 << index)) == 0;
                map.Set(index % width, index / width, new Cell { BackImage = open[index] ? unchecked((int)0x80000000) : index % 2 == 0 ? 0x20000000 : 0x40000000 });
            }
            for (int from = 0; from < count; from++) for (int to = 0; to < count; to++)
                distance[from, to] = open[from] && open[to] && (from == to || Adjacent(from, to)) ? from == to ? 0 : 1 : infinity;
            for (int via = 0; via < count; via++) for (int from = 0; from < count; from++) for (int to = 0; to < count; to++)
                distance[from, to] = Math.Min(distance[from, to], distance[from, via] + distance[via, to]);
            long revision = map.PickupRevision;
            var leg = Assert.Single(MapRouteAnalysis.Analyze(map, [new("Start", 0, 0), new("End", width - 1, height - 1)]).Legs);
            Assert.Equal(distance[0, count - 1] < infinity, leg.Reachable);
            if (leg.Reachable)
            {
                Assert.Equal(distance[0, count - 1], leg.Steps); Assert.Equal(leg.Steps + 1, leg.Path.Count);
                Assert.Equal(0, leg.Path[0]); Assert.Equal(count - 1, leg.Path[^1]);
                Assert.All(leg.Path, index => Assert.True(open[index]));
                for (int index = 1; index < leg.Path.Count; index++) Assert.True(Adjacent(leg.Path[index - 1], leg.Path[index]));
                Assert.Equal(leg.Path.Count(index => Degree(index) <= 2), leg.NarrowCells);
                Assert.Equal(leg.Path.Count(index => Degree(index) <= 1), leg.DeadEnds);
            }
            else Assert.Empty(leg.Path);
            var metrics = MapRouteAnalysis.Measure(map);
            Assert.Equal(open.Count(value => value), metrics.WalkableCells);
            Assert.Equal(Enumerable.Range(0, count).Where(index => open[index]).Select(index =>
                Enumerable.Range(0, count).Count(other => distance[index, other] < infinity)).DefaultIfEmpty(0).Max(), metrics.LargestArea);
            Assert.Equal(Enumerable.Range(0, count).Count(index => open[index] && Degree(index) <= 1), metrics.DeadEnds);
            Assert.Equal(revision, map.PickupRevision);

            bool Adjacent(int from, int to) => Math.Abs(from % width - to % width) + Math.Abs(from / width - to / width) == 1;
            int Degree(int index) => Enumerable.Range(0, count).Count(other => open[other] && Adjacent(index, other));
        }
    }

    [Fact]
    public void EmptyChecksAndRoutesHonorCancellation()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel(); var map = new MapDocument(8, 8);
        Assert.Throws<OperationCanceledException>(() => MapRouteAnalysis.Analyze(map, [], cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => MapDesignConstraints.Check(map, new(), cancellation.Token));
    }

    [Fact]
    public void ReservationFindingsIdentifyTheActualBlockedCellAndStampFootprint()
    {
        var map = new MapDocument(16, 16); map.Set(7, 6, new Cell { BackImage = 0x40000000 });
        map.PlaceStamp([new(0, 0, new Cell { Light = 1 })], 5, 7, "Recorded footprint", new(), (_, _) => { });
        long revision = map.PickupRevision;
        var result = MapDesignConstraints.Check(map, new() { Regions = [new("Road", 4, 4, 4, 4, MapRegionPurpose.Walkable), new("Plaza", 4, 4, 4, 4, MapRegionPurpose.ClearObjects, false)] });
        Assert.Equal((7, 6), (result.Findings[0].X, result.Findings[0].Y));
        Assert.Equal((5, 7), (result.Findings[1].X, result.Findings[1].Y));
        Assert.True(result.Findings[0].Required); Assert.False(result.Findings[1].Required); Assert.False(result.Accepted);
        Assert.Equal(revision, map.PickupRevision);
    }

    [AvaloniaFact]
    public void MarkerEditingPreservesBothSelectionsAndDisablesImpossibleActions()
    {
        var design = new MapDesignSettings { Points = [new("Entry", 1, 1), new("Exit", 12, 12)], Regions = [new("Keep", 4, 4, 2, 2)] };
        var window = new EditorWindow { Content = new MapDesignEditor(design, 16, 16) }; window.Show();
        try
        {
            Assert.False(Find<Button>(window, "QuickMapUpdatePoint").IsEnabled);
            var points = Find<ListBox>(window, "QuickMapDesignPoints"); var regions = Find<ListBox>(window, "QuickMapDesignRegions");
            points.SelectedIndex = 0; regions.SelectedIndex = 0;
            Assert.False(Find<Button>(window, "QuickMapMovePointUp").IsEnabled); Assert.True(Find<Button>(window, "QuickMapMovePointDown").IsEnabled);
            Find<TextBox>(window, "QuickMapPointName").Text = "Updated entry";
            Click(Find<Button>(window, "QuickMapUpdatePoint"));
            Assert.Equal("Updated entry", design.Points[0].Name); Assert.Equal(0, points.SelectedIndex); Assert.Equal(0, regions.SelectedIndex);
            Click(Find<Button>(window, "QuickMapMovePointDown"));
            Assert.Equal(1, points.SelectedIndex); Assert.Equal("Updated entry", design.Points[1].Name);
            Assert.False(Find<Button>(window, "QuickMapMovePointDown").IsEnabled);
            Click(Find<Button>(window, "QuickMapRemovePoint"));
            Assert.Equal(0, points.SelectedIndex); Assert.Equal(0, regions.SelectedIndex);
            Find<CheckBox>(window, "QuickMapRequireRoute").IsChecked = true;
            Assert.Equal(LocalText.T("A required route needs at least two ordered points."), Find<TextBlock>(window, "QuickMapDesignFeedback").Text);
            Assert.NotNull(Find<ComboBox>(window, "QuickMapRegionPurpose").ItemTemplate);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void RecipeSwitchIsAtomicWhenValidationOrCompatibilityFails()
    {
        var original = Recipe(7);
        var window = Production(new(128, 128), new(), original, recipe => recipe.Parameters["SeedBox"] == "9" ? throw new InvalidOperationException("Unavailable catalogue") : []);
        window.Show();
        try
        {
            string? description = Find<TextBlock>(window, "ProductionRecipeDetails").Text;
            string path = Path.Combine(Folder(), "import.creation.json");
            AtomicFile.WriteJson(path, Recipe(8) with { Version = 99 }); Assert.Throws<InvalidDataException>(() => window.LoadRecipe(path));
            Assert.Equal(description, Find<TextBlock>(window, "ProductionRecipeDetails").Text);
            AtomicFile.WriteJson(path, Recipe(9)); Assert.Throws<InvalidOperationException>(() => window.LoadRecipe(path));
            Assert.Equal(description, Find<TextBlock>(window, "ProductionRecipeDetails").Text);
            Assert.True(Find<Button>(window, "ProductionRestoreRecipe").IsEnabled);
            Click(Find<Button>(window, "ProductionRestoreRecipe")); Assert.Same(original, window.SelectedRecipe);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void LoadedRecipeShowsItsSourceAndOriginalDimensions()
    {
        var window = Production(new(32, 32), new()); window.Show();
        try
        {
            string path = Path.Combine(Folder(), "approved.creation.json"); AtomicFile.WriteJson(path, Recipe(11)); window.LoadRecipe(path);
            string? description = Find<TextBlock>(window, "ProductionRecipeDetails").Text;
            Assert.Contains("approved.creation.json", description); Assert.Contains("128 × 128", description);
            Assert.Contains(LocalText.T("The recipe size differs from this map. Restoration uses the recipe dimensions in a new map."), description);
            Click(Find<Button>(window, "ProductionRestoreRecipe")); Assert.Equal("11", window.SelectedRecipe!.Parameters["SeedBox"]);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(null)]
    [InlineData("Exported verified.zip")]
    public async Task CompletedAndDismissedOperationsNeverLeaveWorkingFeedback(string? outcome)
    {
        var completion = new TaskCompletionSource<string?>();
        var window = Production(new(16, 16), new(), export: (_, _) => completion.Task); window.Show();
        try
        {
            var tabs = Find<TabControl>(window, "ProductionTabs"); tabs.SelectedIndex = 2; Dispatcher.UIThread.RunJobs();
            Click(Find<Button>(window, "ProductionExportPackage")); Assert.False(tabs.IsEnabled);
            completion.SetResult(outcome);
            await EditorExperienceTests.WaitFor(() => tabs.IsEnabled);
            Assert.Equal(outcome ?? LocalText.T("Operation cancelled. No pending changes were applied."), Find<TextBlock>(window, "ProductionFeedback").Text);
            Assert.False(Find<Button>(window, "ProductionStop").IsEnabled);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task StoppingAnOperationWaitsForCancellationThenRestoresControls()
    {
        var window = Production(new(16, 16), new(), export: async (_, cancellation) => { await Task.Delay(Timeout.Infinite, cancellation); return "Unexpected completion"; }); window.Show();
        try
        {
            var tabs = Find<TabControl>(window, "ProductionTabs"); tabs.SelectedIndex = 2; Dispatcher.UIThread.RunJobs();
            Click(Find<Button>(window, "ProductionExportPackage")); Click(Find<Button>(window, "ProductionStop"));
            Assert.False(Find<Button>(window, "ProductionStop").IsEnabled);
            await EditorExperienceTests.WaitFor(() => tabs.IsEnabled);
            Assert.Equal(LocalText.T("Operation cancelled. No pending changes were applied."), Find<TextBlock>(window, "ProductionFeedback").Text);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task RouteResultsInvalidateWhenTheMapChangesWhileIdle()
    {
        var map = new MapDocument(32, 32); var design = new MapDesignSettings { Points = [new("Entry", 1, 1), new("Exit", 30, 30)] };
        var window = Production(map, design); window.Show();
        try
        {
            Click(Find<Button>(window, "ProductionAnalyzeRoute")); var legs = Find<ListBox>(window, "ProductionRouteLegs");
            await EditorExperienceTests.WaitFor(() => legs.ItemCount == 1); legs.SelectedIndex = 0;
            Assert.True(Find<Button>(window, "ProductionLocateRoute").IsEnabled);
            map.Set(5, 5, new Cell { BackImage = 0x20000000 });
            await EditorExperienceTests.WaitFor(() => legs.ItemCount == 0);
            Assert.Null(Find<Image>(window, "ProductionRouteImage").Source); Assert.False(Find<Button>(window, "ProductionLocateRoute").IsEnabled);
            Assert.Equal(LocalText.T("The map changed. Previous route analysis is stale; analyze again."), Find<TextBlock>(window, "ProductionFeedback").Text);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task AnalysisRejectsMarkersChangedBeforePublication()
    {
        var design = new MapDesignSettings { Points = [new("Entry", 1, 1), new("Exit", 120, 120)] };
        var window = Production(new(128, 128), design); window.Show();
        try
        {
            Click(Find<Button>(window, "ProductionAnalyzeRoute")); design.Points[1] = new("Changed exit", 30, 30);
            await EditorExperienceTests.WaitFor(() => Find<TabControl>(window, "ProductionTabs").IsEnabled);
            Assert.Equal(0, Find<ListBox>(window, "ProductionRouteLegs").ItemCount);
            Assert.Equal(LocalText.T("The map or design markers changed during analysis. Analyze again."), Find<TextBlock>(window, "ProductionFeedback").Text);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(7)]
    [InlineData(int.MaxValue)]
    public async Task BatchSearchLocksMutationEntrypointsAndMeasuresTheRequestedSeed(int requestedSeed)
    {
        using var assets = AssetStore.Load(Path.Combine(TestApplication.Root, "Data")); var source = new MapDocument(128, 128);
        var session = new MapCreationSession(new(MapCreationKind.Terrain, 128, 128)); using var candidates = session.Candidates;
        var window = new TerrainGeneratorWindow(source, assets, Terrain()); QuickMapPreview.Configure(window, session, _ => false); window.Show();
        try
        {
            await EditorExperienceTests.WaitFor(() => window.PreferencesRestored && Find<Button>(window, "ApplyButton").IsEnabled);
            Find<TextBox>(window, "SeedBox").Text = requestedSeed.ToString();
            await EditorExperienceTests.WaitFor(() => window.Plan?.Seed == requestedSeed && Find<Button>(window, "ApplyButton").IsEnabled);
            var production = window.GetLogicalDescendants().OfType<QuickMapProduction>().Distinct().Single();
            Find<NumericUpDown>(window, "QuickMapBatchCount").Value = 3;
            int firstSeed = int.Parse(Find<TextBox>(window, "SeedBox").Text!); long revision = source.PickupRevision;
            var completed = new List<int>();
            window.PreviewState.Completed += (_, _) =>
            {
                if (!session.BatchRunning) return;
                completed.Add(window.Plan!.Seed);
                Assert.False(Find<Button>(window, "GeneratorRecheck").IsEffectivelyEnabled);
                Assert.False(Find<ComboBox>(window, "QuickMapDensity").IsEffectivelyEnabled);
            };
            var search = production.SearchAsync();
            Assert.True(session.BatchRunning); Assert.False(Find<ComboBox>(window, "QuickMapDensity").IsEffectivelyEnabled);
            Assert.False(Find<Button>(window, "ApplyButton").IsEnabled);
            await production.SearchAsync(); await search;
            var rows = Find<ListBox>(window, "QuickMapBatchResults").Items.Cast<QuickMapBatchResult>().ToArray();
            Assert.Equal(new[] { firstSeed, unchecked(firstSeed + 1), unchecked(firstSeed + 2) }, rows.Select(row => row.Seed));
            Assert.All(rows, row => Assert.True(row.Accepted, row.Details));
            Assert.Contains(unchecked(firstSeed + 1), completed); Assert.Contains(unchecked(firstSeed + 2), completed);
            Assert.Equal(unchecked(firstSeed + 2), window.Plan!.Seed);
            var highlighted = window.GetLogicalDescendants().OfType<Button>().Distinct().Where(button => button.Name?.StartsWith("QuickMapCandidate", StringComparison.Ordinal) == true && button.BorderThickness.Left == 2).ToArray();
            Assert.Single(highlighted);
            Assert.Contains(unchecked(firstSeed + 2).ToString(), string.Join(" ", highlighted[0].GetLogicalDescendants().OfType<TextBlock>().Select(text => text.Text)));
            Assert.True(Find<ComboBox>(window, "QuickMapDensity").IsEffectivelyEnabled); Assert.Equal(revision, source.PickupRevision);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task BatchStopRetainsCompletedRowsAndNeverEvictsFavorites()
    {
        using var assets = AssetStore.Load(Path.Combine(TestApplication.Root, "Data")); var source = new MapDocument(128, 128);
        source.Set(8, 8, new Cell { BackImage = 0x20000000 });
        var session = new MapCreationSession(new(MapCreationKind.Terrain, 128, 128)) { Design = new() { Regions = [new("Preferred road", 8, 8, 1, 1, MapRegionPurpose.Walkable, false)] } };
        using var candidates = session.Candidates;
        var window = new TerrainGeneratorWindow(source, assets, Terrain()); QuickMapPreview.Configure(window, session, _ => false); window.Show();
        try
        {
            await EditorExperienceTests.WaitFor(() => window.PreferencesRestored && Find<Button>(window, "ApplyButton").IsEnabled);
            foreach (var candidate in candidates.Items) candidates.SetFavorite(candidate, true);
            for (int index = candidates.Items.Count; index < QuickMapCandidates.Capacity; index++)
            {
                var parameters = GeneratorPreferences.Capture(window); parameters["SeedBox"] = (-100 - index).ToString();
                var candidate = candidates.Remember(session.Settings, parameters)!; candidates.SetFavorite(candidate, true);
            }
            var favorites = candidates.Items.ToArray(); long revision = source.PickupRevision;
            Find<TextBox>(window, "SeedBox").Text = "500";
            await EditorExperienceTests.WaitFor(() => window.Plan?.Seed == 500 && Find<Button>(window, "ApplyButton").IsEnabled);
            Find<NumericUpDown>(window, "QuickMapBatchCount").Value = 10;
            var production = window.GetLogicalDescendants().OfType<QuickMapProduction>().Distinct().Single(); var search = production.SearchAsync();
            var rows = Find<ListBox>(window, "QuickMapBatchResults"); await EditorExperienceTests.WaitFor(() => rows.ItemCount > 0);
            Click(Find<Button>(window, "QuickMapBatchStop")); await search;
            Assert.InRange(rows.ItemCount, 1, 9); Assert.Equal(favorites, candidates.Items); Assert.False(session.BatchRunning);
            Assert.All(rows.Items.Cast<QuickMapBatchResult>(), row => Assert.Contains(LocalText.T("All five candidates are favorites. Unfavorite one to retain another result."), row.Details));
            Assert.All(rows.Items.Cast<QuickMapBatchResult>(), row => Assert.Contains("Preferred road", row.Details));
            Assert.Equal(revision, source.PickupRevision); Assert.True(Find<NumericUpDown>(window, "QuickMapBatchCount").IsEnabled);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ConfirmationLocksInputsAndRejectsChangedConstraints()
    {
        using var assets = AssetStore.Load(Path.Combine(TestApplication.Root, "Data")); var source = new MapDocument(128, 128);
        var session = new MapCreationSession(new(MapCreationKind.Terrain, 128, 128)) { Design = new() { Regions = [new("Keep", 4, 4, 4, 4)] } };
        using var candidates = session.Candidates;
        var window = new TerrainGeneratorWindow(source, assets, Terrain()); QuickMapPreview.Configure(window, session, _ => false); window.Show();
        try
        {
            await EditorExperienceTests.WaitFor(() => window.PreferencesRestored && Find<Button>(window, "ApplyButton").IsEnabled);
            var production = window.GetLogicalDescendants().OfType<QuickMapProduction>().Distinct().Single();
            var confirmation = production.ConfirmAsync();
            Assert.False(Find<ComboBox>(window, "QuickMapDensity").IsEffectivelyEnabled); Assert.False(Find<Button>(window, "GeneratorRecheck").IsEffectivelyEnabled);
            session.Design.Regions.Add(new("New protection", 12, 12, 2, 2));
            Assert.False(await confirmation);
            Assert.True(Find<Button>(window, "ApplyButton").IsEnabled); Assert.True(Find<ComboBox>(window, "QuickMapDensity").IsEffectivelyEnabled);
            Assert.Equal(LocalText.T("The preview or constraints changed. Check the current preview again."), Find<TextBlock>(window, "QuickMapProductionFeedback").Text);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task StaleLocalPlansAreRejectedBeforeRendering()
    {
        using var assets = AssetStore.Load(Path.Combine(TestApplication.Root, "Data")); var source = new MapDocument(16, 16);
        var generated = new MapDocument(16, 16); generated.Set(6, 6, new Cell { BackImage = 1 });
        var plan = PartialMapGeneration.Plan(source, generated, new Rect(0, 0, 16, 16), new(), PartialGenerationLayer.Ground);
        source.Set(2, 2, new Cell { BackImage = 1 });
        await Assert.ThrowsAsync<InvalidOperationException>(() => PartialMapGeneration.CreatePreviewAsync(plan, assets));
    }

    [AvaloniaFact]
    public void LocalGroundPreservesEveryCollisionObjectAndCompleteProtectedTileThroughUndoRedo()
    {
        var source = new MapDocument(16, 16); var generated = new MapDocument(16, 16);
        for (int row = 0; row < 16; row++) for (int column = 0; column < 16; column++)
        {
            int collision = (column + row) % 3 == 0 ? 0x20000000 : (column + row) % 3 == 1 ? 0x40000000 : 0;
            source.Set(column, row, new Cell { BackLibrary = 0, BackImage = collision | 1 });
            generated.Set(column, row, new Cell { BackLibrary = 1, BackImage = 2 });
        }
        source.PlaceStamp([new(0, 0, new Cell { Foreground = new(2, 1) })], 10, 10, "Existing object", new(), (_, _) => { });
        var before = source.Cells.ToDictionary(pair => pair.Key, pair => pair.Value.Copy()); var stampIds = source.PlacedStamps.Select(stamp => stamp.Id).ToArray();
        var plan = PartialMapGeneration.Plan(source, generated, new Rect(0, 0, 16, 16), new() { Regions = [new("Keep", 5, 5, 3, 3)] }, PartialGenerationLayer.Ground);
        var expected = new HashSet<int>();
        for (int row = 2; row < 14; row += 2) for (int column = 2; column < 14; column += 2)
        {
            bool protectedTile = column <= 7 && column + 1 >= 5 && row <= 7 && row + 1 >= 5;
            if (!protectedTile && (column != 10 || row != 10)) expected.Add(row * 16 + column);
        }
        Assert.True(expected.SetEquals(plan.Changed));
        var viewport = new MapViewport(); viewport.SetDocument(source);
        try
        {
            viewport.ApplyPartialGeneration(plan);
            foreach (var (index, cell) in before)
            {
                var actual = source.Get(index % 16, index / 16);
                Assert.Equal(cell.BackImage & unchecked((int)0xE0000000), actual.BackImage & unchecked((int)0xE0000000)); Assert.Equal(cell.Foreground, actual.Foreground);
                if (!expected.Contains(index)) Assert.True(cell.SameContents(actual));
                else { Assert.Equal(2, actual.BackImage & 0x1FFFFFFF); Assert.Equal(1, actual.BackLibrary); }
            }
            viewport.Undo(); Assert.False(viewport.History.CanUndo);
            Assert.All(before, pair => Assert.True(pair.Value.SameContents(source.Get(pair.Key % 16, pair.Key / 16))));
            Assert.Equal(stampIds, source.PlacedStamps.Select(stamp => stamp.Id));
            viewport.Redo(); Assert.True(viewport.History.CanUndo); Assert.False(viewport.History.CanRedo);
            Assert.All(plan.Changed, index => Assert.True(plan.Generated.Get(index % 16, index / 16).SameContents(source.Get(index % 16, index / 16))));
        }
        finally { viewport.StopBackgroundWork(); }
    }

    [Fact]
    public async Task DeliveryHashesMapAndDesignFindingsAreIndependentlyReconciledFromZip()
    {
        var source = new MapDocument(16, 16); source.Set(5, 5, new Cell { BackImage = 0x20000000 }); source.Set(2, 2, new Cell { BackImage = 1 });
        long revision = source.PickupRevision; var design = new MapDesignSettings { Regions = [new("Road", 4, 4, 4, 4, MapRegionPurpose.Walkable)] };
        string path = Path.Combine(Folder(), "delivery.zip"); await MapDeliveryPackage.ExportAsync(path, source, null, null, design, true);
        using var archive = ZipFile.OpenRead(path); using var reportStream = archive.GetEntry("report.json")!.Open(); using var report = JsonDocument.Parse(reportStream);
        foreach (var hash in report.RootElement.GetProperty("FileHashes").EnumerateObject())
        {
            var entry = archive.GetEntry(hash.Name); Assert.NotNull(entry); using var stream = entry.Open();
            Assert.Equal(hash.Value.GetString(), Convert.ToHexString(SHA256.HashData(stream)));
        }
        var layout = report.RootElement.GetProperty("LayoutCheck"); Assert.False(layout.GetProperty("Accepted").GetBoolean());
        var finding = Assert.Single(layout.GetProperty("Findings").EnumerateArray()); Assert.Equal(5, finding.GetProperty("X").GetInt32()); Assert.Equal(5, finding.GetProperty("Y").GetInt32());
        Assert.True(report.RootElement.GetProperty("LayoutCheckError").ValueKind is JsonValueKind.Null);
        string mapPath = Path.Combine(Folder(), "received.map"); archive.GetEntry("map.map")!.ExtractToFile(mapPath);
        var received = MapDocument.Load(mapPath); Assert.Equal(16, received.Width); Assert.Equal(16, received.Height);
        Assert.All(source.Cells, pair => Assert.True(pair.Value.SameContents(received.Get(pair.Key % 16, pair.Key / 16))));
        using var markerStream = archive.GetEntry("map.map.studio.json")!.Open(); using var markers = JsonDocument.Parse(markerStream);
        Assert.Equal("Road", markers.RootElement.GetProperty("Design").GetProperty("Regions")[0].GetProperty("Name").GetString());
        Assert.Equal(revision, source.PickupRevision);
    }

    [AvaloniaTheory]
    [InlineData("en-US")]
    [InlineData("zh-CN")]
    public async Task ProductionTabsRemainReadableWithRecipeProvenanceAndLongFindings(string language)
    {
        var store = new SettingsStore(); var saved = store.Load(); store.Save(saved with { Language = language }); LocalText.Initialize(store);
        var map = new MapDocument(32, 32); map.Set(7, 7, new Cell { BackImage = 0x20000000 });
        var design = new MapDesignSettings { Points = [new("Entry", 1, 1), new("Exit", 30, 30)],
            Regions = Enumerable.Range(0, 20).Select(index => new MapDesignRegion("Corridor " + index, 4, 4, 8, 8, MapRegionPurpose.Walkable)).ToList() };
        var window = Production(map, design, Recipe(7)); window.Show();
        try
        {
            window.Width = 780; window.Height = 520; Click(Find<Button>(window, "ProductionAnalyzeRoute"));
            var tabs = Find<TabControl>(window, "ProductionTabs"); await EditorExperienceTests.WaitFor(() => tabs.IsEnabled);
            window.UpdateLayout(); Assert.InRange(Find<ScrollViewer>(window, "ProductionFeedbackScroll").Bounds.Height, 1, 96);
            Assert.True(tabs.Bounds.Height > 200);
            await Capture(window, "routes-and-findings-" + language + ".png");
            tabs.SelectedIndex = 1; await Capture(window, "recipe-provenance-" + language + ".png");
            tabs.SelectedIndex = 2; await Capture(window, "regeneration-and-delivery-" + language + ".png");
        }
        finally { window.Close(); store.Save(saved); LocalText.Initialize(store); }
    }

    [AvaloniaFact]
    public async Task FailedOperationsRestoreControlsAndShowTheActualFailure()
    {
        var window = Production(new(16, 16), new(), export: (_, _) => Task.FromException<string?>(new IOException("Export destination is read-only"))); window.Show();
        try
        {
            var tabs = Find<TabControl>(window, "ProductionTabs"); tabs.SelectedIndex = 2; Dispatcher.UIThread.RunJobs();
            Click(Find<Button>(window, "ProductionExportPackage")); await EditorExperienceTests.WaitFor(() => tabs.IsEnabled);
            Assert.Equal("Export destination is read-only", Find<TextBlock>(window, "ProductionFeedback").Text);
            Assert.False(Find<Button>(window, "ProductionStop").IsEnabled);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task IdleRouteResultsAlsoInvalidateOnExternalMarkerChanges()
    {
        var design = new MapDesignSettings { Points = [new("Entry", 1, 1), new("Exit", 30, 30)] };
        var window = Production(new(32, 32), design); window.Show();
        try
        {
            Click(Find<Button>(window, "ProductionAnalyzeRoute")); var legs = Find<ListBox>(window, "ProductionRouteLegs");
            await EditorExperienceTests.WaitFor(() => legs.ItemCount == 1); design.Points.Reverse();
            await EditorExperienceTests.WaitFor(() => legs.ItemCount == 0);
            Assert.Null(Find<Image>(window, "ProductionRouteImage").Source);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ConstraintCancellationPreservesTheReadyPlanAndAllowsRetry()
    {
        using var assets = AssetStore.Load(Path.Combine(TestApplication.Root, "Data")); var source = new MapDocument(128, 128);
        var session = new MapCreationSession(new(MapCreationKind.Terrain, 128, 128)) { Design = new() { Regions = [new("Keep", 4, 4, 4, 4)] } };
        using var candidates = session.Candidates;
        var window = new TerrainGeneratorWindow(source, assets, Terrain()); QuickMapPreview.Configure(window, session, _ => false); window.Show();
        try
        {
            await EditorExperienceTests.WaitFor(() => window.PreferencesRestored && Find<Button>(window, "ApplyButton").IsEnabled);
            var production = window.GetLogicalDescendants().OfType<QuickMapProduction>().Distinct().Single(); var plan = window.Plan;
            var confirmation = production.ConfirmAsync(); Click(Find<Button>(window, "QuickMapBatchStop")); Assert.False(await confirmation);
            Assert.Same(plan, window.Plan); Assert.True(Find<Button>(window, "ApplyButton").IsEnabled);
            Assert.False(Find<Button>(window, "QuickMapBatchStop").IsEnabled);
            Assert.Equal(LocalText.T("Operation cancelled. No pending changes were applied."), Find<TextBlock>(window, "QuickMapProductionFeedback").Text);
            Assert.True(await production.ConfirmAsync()); Assert.Same(plan, window.Plan);
        }
        finally { window.Close(); }
    }

    private static MapProductionWindow Production(MapDocument map, MapDesignSettings design, MapCreationRecipe? recipe = null,
        Func<MapCreationRecipe, IReadOnlyList<string>>? compatibility = null, Func<EditorWindow, CancellationToken, Task<string?>>? export = null) =>
        new(map, null, design, recipe, compatibility ?? (_ => []), () => { }, (_, _) => { }, () => null,
            (_, _, _, _) => Task.FromResult<string?>(null), export ?? ((_, _) => Task.FromResult<string?>(null)));

    [AvaloniaFact]
    public void LocalActionsExplainMissingArtworkAndDisableUnsupportedDecoration()
    {
        using var assets = AssetStore.Load(Path.Combine(TestApplication.Root, "Data"));
        foreach (bool loaded in new[] { true, false })
        {
            var window = new MapProductionWindow(new(16, 16), loaded ? assets : null, new(), null, _ => [], () => { }, (_, _) => { },
                () => new Rect(0, 0, 16, 16), (_, _, _, _) => Task.FromResult<string?>(null), (_, _) => Task.FromResult<string?>(null)); window.Show();
            try
            {
                Find<TabControl>(window, "ProductionTabs").SelectedIndex = 2; Dispatcher.UIThread.RunJobs();
                Assert.Equal(loaded, Find<Button>(window, "ProductionRegenerateGround").IsEnabled);
                Assert.False(Find<Button>(window, "ProductionRegenerateDecoration").IsEnabled);
                if (loaded) Assert.Contains(LocalText.T("Choose a province or dungeon layout for complete decoration stamps. Terrain banks and coasts are protected, not clipped."), Find<TextBlock>(window, "ProductionLocalAvailability").Text);
                else Assert.Contains(LocalText.T("Load artwork libraries before local regeneration. Export remains available without artwork."), Find<TextBlock>(window, "ProductionLocalAvailability").Text);
                Find<ComboBox>(window, "ProductionGeneratorKind").SelectedItem = MapCreationKind.Province;
                Assert.Equal(loaded, Find<Button>(window, "ProductionRegenerateDecoration").IsEnabled);
                Assert.True(Find<Button>(window, "ProductionExportPackage").IsEnabled);
            }
            finally { window.Close(); }
        }
    }
    private static MapCreationRecipe Recipe(int seed) => new(1, DateTime.UtcNow, MapCreationKind.Terrain, 128, 128, AssetSourceFormat.Default, null,
        "fixture-v1", new() { ["SeedBox"] = seed.ToString() }, new());
    private static TerrainCatalogue Terrain() => new() { Sets = [new TerrainSet { Id = "test", Name = "Fixture ground", Base = true, Biome = "Province", Library = 0,
        Shapes = new() { [15] = [new TerrainVariant { Index = 0, Weight = 1 }] } }] };
    private static string Folder() { string path = Path.Combine(Path.GetTempPath(), "CrystalStudio-depth-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
    private static T Find<T>(EditorWindow window, string name) where T : Control => window.GetLogicalDescendants().OfType<T>().Distinct().Single(control => control.Name == name);
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static async Task Capture(EditorWindow window, string name)
    {
        if (Environment.GetEnvironmentVariable("CRYSTALSTUDIO_PRODUCTION_ARTIFACTS") is not { Length: > 0 } folder) return;
        Directory.CreateDirectory(folder); await Task.Delay(100); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
        using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame); frame.Save(Path.Combine(folder, name));
    }
}
