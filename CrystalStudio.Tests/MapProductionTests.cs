using System.IO;
using System.IO.Compression;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Path = System.IO.Path;

namespace CrystalStudio.Tests;

public sealed class MapProductionTests
{
    [Fact]
    public void OrderedRoutesRespectBothCollisionBitsAndDoNotMutateSource()
    {
        var map = new MapDocument(8, 8);
        for (int row = 0; row < 7; row++) map.Set(3, row, new Cell { BackImage = row % 2 == 0 ? 0x20000000 : 0x40000000 });
        long revision = map.PickupRevision;
        var result = MapRouteAnalysis.Analyze(map, [new("Entry", 1, 1), new("Exit", 6, 1), new("Task", 6, 6)]);
        Assert.Equal(2, result.Legs.Count); Assert.All(result.Legs, leg => Assert.True(leg.Reachable));
        Assert.Equal(17, result.Legs[0].Steps); Assert.Equal(5, result.Legs[0].Manhattan);
        Assert.All(result.Legs.SelectMany(leg => leg.Path), index => Assert.True(MapRouteAnalysis.Walkable(map, index % 8, index / 8)));
        Assert.Equal(revision, map.PickupRevision);
    }

    [Fact]
    public void UnreachableAndCoincidentPointsAreReportedWithoutInventingPaths()
    {
        var map = new MapDocument(6, 4);
        for (int row = 0; row < 4; row++) map.Set(2, row, new Cell { BackImage = 0x60000000 });
        var route = MapRouteAnalysis.Analyze(map, [new("A", 0, 0), new("B", 5, 3), new("C", 5, 3), new("Blocked", 2, 0)]);
        Assert.False(route.Legs[0].Reachable); Assert.Empty(route.Legs[0].Path);
        Assert.True(route.Legs[1].Reachable); Assert.Equal(0, route.Legs[1].Steps); Assert.Single(route.Legs[1].Path);
        Assert.False(route.Legs[2].Reachable);
        var metrics = MapRouteAnalysis.Measure(map);
        Assert.Equal(20, metrics.WalkableCells); Assert.Equal(12, metrics.LargestArea); Assert.Equal(.6, metrics.ConnectedRatio);
    }

    [Fact]
    public void AnalysisValidatesBoundsBudgetsAndCancellation()
    {
        Assert.Throws<InvalidDataException>(() => MapRouteAnalysis.Analyze(new(16, 16), [new("Bad", -1, 0)]));
        Assert.Throws<InvalidOperationException>(() => MapRouteAnalysis.Measure(new(2048, 2048)));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => MapRouteAnalysis.Analyze(new(16, 16), [new("A", 0, 0), new("B", 15, 15)], cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => MapRouteAnalysis.Measure(new(16, 16), cancellation: cancellation.Token));
    }

    [Fact]
    public void MandatoryAndPreferredReservationsHaveDifferentAcceptanceSemantics()
    {
        var map = new MapDocument(16, 16); map.Set(5, 5, new Cell { BackImage = 0x20000000, Foreground = new(2, 1) });
        var design = new MapDesignSettings { Regions = [new("Keep", 5, 5, 1, 1), new("Plaza", 4, 4, 4, 4, MapRegionPurpose.ClearObjects, false)] };
        var preferred = MapDesignConstraints.Check(map, design); Assert.True(preferred.Accepted); Assert.Single(preferred.Findings);
        design.Regions.Add(new("Road", 4, 4, 4, 4, MapRegionPurpose.Walkable));
        var required = MapDesignConstraints.Check(map, design); Assert.False(required.Accepted); Assert.Equal(2, required.Findings.Count);
        design.Regions.Clear(); design.Points = [new("A", 0, 0), new("B", 15, 0)]; design.RequireRoute = true;
        for (int row = 0; row < 16; row++) map.Set(8, row, new Cell { BackImage = 0x40000000 });
        Assert.False(MapDesignConstraints.Check(map, design).Accepted);
    }

    [Fact]
    public void DesignMetadataRoundTripsWithoutChangingMapAndRejectsStaleDimensions()
    {
        string directory = Folder(); string path = Path.Combine(directory, "test.map"); var map = new MapDocument(16, 16);
        var design = new MapDesignSettings { Points = [new("Entry", 1, 2)], Regions = [new("Protected", 2, 2, 4, 4)] };
        MapDesignStore.Save(path, map, design); var restored = MapDesignStore.Load(path, map);
        Assert.Equal(design.Points, restored.Points); Assert.Equal(design.Regions, restored.Regions); Assert.False(File.Exists(path));
        Assert.Throws<InvalidDataException>(() => MapDesignStore.Load(path, new(18, 16)));
        MapDesignStore.Save(path, map, new()); Assert.True(MapDesignStore.Load(path, map).Empty);
    }

    [Fact]
    public void RecipeValidationAndCompatibilityDescribeChangesWithoutWritingFiles()
    {
        var recipe = Recipe(); MapRecipeRestore.Validate(recipe);
        var changed = recipe with { GeneratorVersion = "future", AssetFormat = AssetSourceFormat.Sanxiu,
            CatalogueHashes = new() { ["terrain.json"] = "changed" }, LibraryVersions = new() { ["0"] = "different" } };
        Assert.Equal(4, MapRecipeRestore.Differences(recipe, changed).Count);
        Assert.Empty(MapRecipeRestore.Differences(recipe, recipe));
        Assert.Throws<InvalidDataException>(() => MapRecipeRestore.Validate(recipe with { Version = 99 }));
        Assert.Throws<InvalidDataException>(() => MapRecipeRestore.Validate(recipe with { Width = int.MaxValue }));
        var portable = MapDeliveryPackage.PublicRecipe(recipe);
        Assert.Null(portable.LibraryFolder); Assert.DoesNotContain("private", portable.LibraryVersions["0"]);
        Assert.Contains("Tiles.lib|100|200", portable.LibraryVersions["0"]);
    }

    [AvaloniaFact]
    public void RecipeParametersSurviveTheFirstCreationPageInsteadOfUsingLastGeneratorSettings()
    {
        string directory = Folder(); var store = new MapCreationStore(Path.Combine(directory, "profile.json"));
        store.SetParameters(MapCreationKind.Terrain, new Dictionary<string, string> { ["SeedBox"] = "100" });
        var parameters = new Dictionary<string, string> { ["SeedBox"] = "7", ["CoverageSlider"] = "0.8" };
        var window = new NewMapWindow(directory, Enum.GetValues<MapCreationKind>().ToDictionary(kind => kind, _ => (string?)null),
            new(MapCreationKind.Terrain, 128, 128, "RecreatedMap", directory, false), store, initialParameters: parameters);
        Assert.Equal("7", window.SelectedParameters!["SeedBox"]); Assert.Equal("0.8", window.SelectedParameters["CoverageSlider"]);
        Assert.False(window.SaveToggle.IsChecked);
    }

    [Fact]
    public void LocalGroundPreservesProtectedTilesStampsCollisionsAndBoundaryCollar()
    {
        var source = new MapDocument(16, 16); var generated = new MapDocument(16, 16);
        for (int row = 0; row < 16; row += 2) for (int column = 0; column < 16; column += 2)
        { source.Set(column, row, new Cell { BackImage = 1 | 0x20000000 }); generated.Set(column, row, new Cell { BackLibrary = 3, BackImage = 2 }); }
        var history = new EditHistory(); source.PlaceStamp([new(0, 0, new Cell { Foreground = new(2, 1) })], 10, 10, "Keep", history, (_, _) => { });
        long revision = source.PickupRevision;
        var plan = PartialMapGeneration.Plan(source, generated, new Rect(0, 0, 16, 16), new() { Regions = [new("Partial tile", 5, 5, 1, 1)] }, PartialGenerationLayer.Ground);
        Assert.Equal(1 | 0x20000000, plan.Generated.Get(4, 4).BackImage);
        Assert.Equal(1 | 0x20000000, plan.Generated.Get(10, 10).BackImage);
        Assert.Equal(1 | 0x20000000, plan.Generated.Get(0, 0).BackImage);
        Assert.Equal(2 | 0x20000000, plan.Generated.Get(8, 8).BackImage);
        Assert.Single(plan.Generated.PlacedStamps); Assert.Equal(revision, source.PickupRevision);
        Assert.All(plan.Changed, index => Assert.InRange(index % 16, 2, 12));
    }

    [AvaloniaFact]
    public void LocalGenerationAppliesOneUndoStepAndRejectsStaleOrLockedDocuments()
    {
        var source = new MapDocument(16, 16); var generated = new MapDocument(16, 16); generated.Set(6, 6, new Cell { BackImage = 1 });
        var plan = PartialMapGeneration.Plan(source, generated, new Rect(0, 0, 16, 16), new(), PartialGenerationLayer.Ground);
        var viewport = new MapViewport(); viewport.SetDocument(source);
        try
        {
            viewport.ApplyPartialGeneration(plan); Assert.Equal(1, source.Get(6, 6).BackImage); Assert.Equal("Regenerate selected area", viewport.History.UndoName);
            viewport.Undo(); Assert.Equal(0, source.Get(6, 6).BackImage); Assert.False(viewport.History.CanUndo);
            viewport.Redo(); Assert.Equal(1, source.Get(6, 6).BackImage);
            Assert.Throws<InvalidOperationException>(() => viewport.ApplyPartialGeneration(plan));
            var next = PartialMapGeneration.Plan(source, generated, new Rect(0, 0, 16, 16), new(), PartialGenerationLayer.Ground);
            viewport.SetLayerLocked(MapLayer.Objects, true); Assert.Throws<InvalidOperationException>(() => viewport.ApplyPartialGeneration(next));
        }
        finally { viewport.StopBackgroundWork(); }
    }

    [Fact]
    public void LocalDecorationKeepsWholeTrackedObjectsAndSkipsClippedOrUntrackedArtwork()
    {
        var source = new MapDocument(16, 16); source.Set(8, 8, new Cell { Foreground = new(2, 1) });
        var generated = new MapDocument(16, 16); var history = new EditHistory();
        generated.PlaceStamp([new(0, 0, new Cell { Foreground = new(2, 1) }), new(1, 0, new Cell { Light = 1 })], 4, 4, "Complete", history, (_, _) => { });
        generated.PlaceStamp([new(0, 0, new Cell { Foreground = new(2, 1) }), new(1, 0, new Cell { Foreground = new(2, 1) })], 12, 12, "Crosses protection", history, (_, _) => { });
        generated.Set(7, 7, new Cell { Foreground = new(2, 1) });
        var plan = PartialMapGeneration.Plan(source, generated, new Rect(0, 0, 16, 16), new() { Regions = [new("Keep", 13, 12, 1, 1)] }, PartialGenerationLayer.Decoration);
        Assert.Single(plan.Generated.PlacedStamps); Assert.Equal("Complete", plan.Generated.PlacedStamps[0].Name);
        Assert.True(plan.Generated.Get(4, 4).HasForeground); Assert.Equal(1, plan.Generated.Get(5, 4).Light);
        Assert.False(plan.Generated.Get(12, 12).HasForeground); Assert.False(plan.Generated.Get(7, 7).HasForeground);
        Assert.True(plan.Generated.Get(8, 8).HasForeground); Assert.True(plan.SkippedStamps > 0);
        string path = Path.Combine(Folder(), "local.map"); plan.Generated.Save(path);
        var loaded = MapDocument.Load(path); Assert.Null(loaded.MetadataWarning); Assert.Single(loaded.PlacedStamps);
    }

    [Fact]
    public async Task DeliveryUsesSnapshotFreshChecksAndSanitizedDependenciesWithoutArtworkFiles()
    {
        string directory = Folder(); string path = Path.Combine(directory, "delivery.zip");
        var map = new MapDocument(16, 16); map.Set(2, 2, new Cell { BackImage = 1, BackLibrary = 0, Foreground = new(2, 1), Door = 1, DoorOffset = 2 });
        long revision = map.PickupRevision;
        await MapDeliveryPackage.ExportAsync(path, map, null, Recipe(), new(), true);
        using var archive = ZipFile.OpenRead(path);
        Assert.NotNull(archive.GetEntry("map.map")); Assert.Null(archive.GetEntry("minimap.png"));
        Assert.DoesNotContain(archive.Entries, entry => entry.FullName.EndsWith(".lib", StringComparison.OrdinalIgnoreCase));
        using var stream = archive.GetEntry("report.json")!.Open(); var report = StudioJson.Deserialize<MapDeliveryReport>(stream)!;
        Assert.True(report.IncludedUnsavedChanges); Assert.False(report.ArtworkChecked); Assert.False(report.Truncated);
        Assert.True(report.RecipeIncluded); Assert.False(report.RecipeDimensionsMatch);
        Assert.Contains(report.References, reference => reference.Library == 2 && reference.Image == 3);
        Assert.Contains("map.map", report.FileHashes.Keys); Assert.All(report.Libraries, library => Assert.Null(library.FileName));
        using var reader = new StreamReader(archive.GetEntry("map.map.creation.json")!.Open()); Assert.DoesNotContain("private", await reader.ReadToEndAsync());
        Assert.Equal(revision, map.PickupRevision);
    }

    [Fact]
    public async Task CancelledDeliveryCannotReplaceAnExistingArchive()
    {
        string path = Path.Combine(Folder(), "delivery.zip"); File.WriteAllText(path, "keep");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MapDeliveryPackage.ExportAsync(path, new(16, 16), null, null, new(), false, cancellation.Token));
        Assert.Equal("keep", File.ReadAllText(path));
    }

    [AvaloniaFact]
    public async Task BatchSearchUsesActualMetricsRetainsBoundedCandidatesAndDoesNotApplyMaps()
    {
        using var assets = AssetStore.Load(Path.Combine(TestApplication.Root, "Data")); var map = new MapDocument(128, 128);
        long revision = map.PickupRevision; var session = new MapCreationSession(new(MapCreationKind.Terrain, 128, 128));
        using var candidates = session.Candidates;
        var window = new TerrainGeneratorWindow(map, assets, Terrain()); QuickMapPreview.Configure(window, session, _ => false); window.Show();
        try
        {
            await EditorExperienceTests.WaitFor(() => window.PreferencesRestored && Find<Button>(window, "ApplyButton").IsEnabled);
            var production = window.GetLogicalDescendants().OfType<QuickMapProduction>().Single();
            Find<NumericUpDown>(window, "QuickMapBatchCount").Value = 2;
            await production.SearchAsync();
            var rows = Find<ListBox>(window, "QuickMapBatchResults").Items.Cast<QuickMapBatchResult>().ToArray();
            Assert.Equal(2, rows.Length); Assert.All(rows, row => { Assert.True(row.Accepted, row.Details); Assert.NotNull(row.Metrics); Assert.Equal(1, row.Metrics.WalkableRatio); });
            Assert.NotEqual(rows[0].Seed, rows[1].Seed); Assert.InRange(candidates.Items.Count, 2, 5);
            Assert.False(session.BatchRunning); Assert.Equal(revision, map.PickupRevision); Assert.All(candidates.Items, candidate => Assert.NotNull(candidate.Comparison));
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData("en-US")]
    [InlineData("zh-CN")]
    public async Task ProductionWindowProvidesReadableRoutesAndMetadataAtMinimumSize(string language)
    {
        var store = new SettingsStore(); var saved = store.Load(); store.Save(saved with { Language = language }); LocalText.Initialize(store);
        var map = new MapDocument(32, 32); var design = new MapDesignSettings { Points = [new("Entry", 1, 1), new("Exit", 30, 30)] };
        var window = new MapProductionWindow(map, null, design, Recipe(), _ => [], () => { }, (_, _) => { }, () => new Rect(0, 0, 16, 16),
            (_, _, _, _) => Task.CompletedTask, (_, _) => Task.CompletedTask);
        window.Show();
        try
        {
            window.Width = 780; window.Height = 520;
            Find<Button>(window, "ProductionAnalyzeRoute").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await EditorExperienceTests.WaitFor(() => Find<ListBox>(window, "ProductionRouteLegs").ItemCount == 1);
            Assert.True(Find<ListBox>(window, "ProductionRouteLegs").Bounds.Width > 100);
            if (Environment.GetEnvironmentVariable("CRYSTALSTUDIO_PRODUCTION_ARTIFACTS") is { Length: > 0 } folder)
            {
                Directory.CreateDirectory(folder); await Task.Delay(100); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame); frame.Save(Path.Combine(folder, "production-" + language + ".png"));
            }
        }
        finally { window.Close(); store.Save(saved); LocalText.Initialize(store); }
    }

    private static TerrainCatalogue Terrain() => new() { Sets = [new TerrainSet { Id = "test", Name = "Fixture ground", Base = true, Biome = "Province", Library = 0,
        Shapes = new() { [15] = [new TerrainVariant { Index = 0, Weight = 1 }] } }] };
    private static MapCreationRecipe Recipe() => new(1, DateTime.UtcNow, MapCreationKind.Terrain, 128, 128, AssetSourceFormat.Default,
        @"C:\private\art", "generator-v1", new() { ["SeedBox"] = "7" }, new() { ["terrain.json"] = "hash" }) { LibraryVersions = new() { ["0"] = @"C:\private\art\Tiles.lib|100|200" } };
    private static string Folder() { string path = Path.Combine(Path.GetTempPath(), "CrystalStudio-production-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
    private static T Find<T>(EditorWindow window, string name) where T : Control => window.GetLogicalDescendants().OfType<T>().Distinct().Single(control => control.Name == name);

    [Fact]
    public void CompleteDecorationMetadataNeverClaimsTheExistingGroundOrCollisionFlags()
    {
        var source = new MapDocument(16, 16); source.Set(4, 4, new Cell { BackImage = 1 | 0x40000000, BackLibrary = 4 });
        var generated = new MapDocument(16, 16); generated.PlaceStamp([new(0, 0, new Cell { Foreground = new(2, 1) })], 4, 4, "Decoration", new(), (_, _) => { });
        var plan = PartialMapGeneration.Plan(source, generated, new Rect(0, 0, 16, 16), new(), PartialGenerationLayer.Decoration);
        var stamp = Assert.Single(plan.Generated.PlacedStamps);
        Assert.Equal(0, stamp.Cells[0].Cell.BackImage); Assert.Equal(0, stamp.Cells[0].Cell.BackLibrary);
        Assert.Equal(1 | 0x40000000, plan.Generated.Get(4, 4).BackImage);
        Assert.Equal(stamp.Changes[0].Before.BackImage, stamp.Changes[0].After.BackImage);
        plan.Generated.DeleteStamp(stamp.Id, new(), (_, _) => { }); Assert.True(source.Get(4, 4).SameContents(plan.Generated.Get(4, 4)));
    }

    [Fact]
    public void RecoveryCarriesDesignMarkersAlongsideTheMapSnapshot()
    {
        string directory = Folder(); var map = new MapDocument(16, 16);
        var design = new MapDesignSettings { Points = [new("Entry", 1, 1)], Regions = [new("Keep", 2, 2, 4, 4)] };
        using (var writer = new MapRecoveryStore(directory))
        {
            Guid id = Guid.NewGuid();
            writer.Write(id, map, null, "Test", null, new(1, writer.SessionId, id, "", null, "Test", DateTime.UtcNow, AssetSourceFormat.Default, null, Design: design));
        }
        using var reader = new MapRecoveryStore(directory); var entry = Assert.Single(reader.List());
        Assert.Equal(design.Points, entry.Design!.Points); Assert.Equal(design.Regions, entry.Design.Regions); Assert.Equal(16, reader.Load(entry).Width);
    }

    [Fact]
    public void MaterializedGeneratorMapsPreserveTheSourceLibraryFormat()
    {
        var source = new MapDocument(128, 128) { SourceFormat = AssetSourceFormat.Sanxiu };
        var generated = new MapDocument(128, 128);
        var plan = new ProvincePlan(source, source.PickupRevision, generated, new(), ProvinceStyle.All[0], [], []);
        Assert.Equal(AssetSourceFormat.Sanxiu, QuickMapProduction.Materialize(plan, default).SourceFormat);
        Assert.Equal(AssetSourceFormat.Default, generated.SourceFormat);
    }

    [AvaloniaFact]
    public async Task DeliveryIncludesMinimapAndVerifiableHashesWithLoadedArtwork()
    {
        using var assets = AssetStore.Load(Path.Combine(TestApplication.Root, "Data"));
        var map = new MapDocument(16, 16); map.Set(0, 0, new Cell { BackLibrary = 0, BackImage = 1 });
        string path = Path.Combine(Folder(), "artwork-delivery.zip");
        await MapDeliveryPackage.ExportAsync(path, map, assets, null, new(), false);
        using var archive = ZipFile.OpenRead(path); Assert.NotNull(archive.GetEntry("minimap.png"));
        using var reportStream = archive.GetEntry("report.json")!.Open(); var report = StudioJson.Deserialize<MapDeliveryReport>(reportStream)!;
        Assert.True(report.ArtworkChecked); Assert.Equal(0, report.TotalIssues);
        foreach (var pair in report.FileHashes)
        {
            using var stream = archive.GetEntry(pair.Key)!.Open();
            Assert.Equal(pair.Value, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)));
        }
        Assert.All(report.Libraries, library => Assert.Equal(Path.GetFileName(library.FileName), library.FileName));
    }

    [AvaloniaFact]
    public async Task FilteredBatchCannotEvictFavoritesOrModifyTheWorkingDocument()
    {
        using var assets = AssetStore.Load(Path.Combine(TestApplication.Root, "Data")); var map = new MapDocument(128, 128);
        map.Set(7, 7, new Cell { BackImage = 0x20000000 }); long revision = map.PickupRevision;
        var session = new MapCreationSession(new(MapCreationKind.Terrain, 128, 128)); using var candidates = session.Candidates;
        for (int seed = 100; seed < 105; seed++) candidates.SetFavorite(candidates.Remember(session.Settings, new Dictionary<string, string> { ["SeedBox"] = seed.ToString() })!, true);
        var favorites = candidates.Items.ToArray();
        var window = new TerrainGeneratorWindow(map, assets, Terrain()); QuickMapPreview.Configure(window, session, _ => false); window.Show();
        try
        {
            await EditorExperienceTests.WaitFor(() => window.PreferencesRestored && Find<Button>(window, "ApplyButton").IsEnabled);
            Find<NumericUpDown>(window, "QuickMapBatchCount").Value = 2; Find<NumericUpDown>(window, "QuickMapMinWalkable").Value = 100;
            await window.GetLogicalDescendants().OfType<QuickMapProduction>().Single().SearchAsync();
            Assert.All(Find<ListBox>(window, "QuickMapBatchResults").Items.Cast<QuickMapBatchResult>(), result => Assert.False(result.Accepted));
            Assert.Equal(favorites, candidates.Items); Assert.Equal(revision, map.PickupRevision);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task StoppingBatchSearchRetainsCompletedResultsAndReleasesTheControls()
    {
        using var assets = AssetStore.Load(Path.Combine(TestApplication.Root, "Data")); var map = new MapDocument(128, 128);
        var session = new MapCreationSession(new(MapCreationKind.Terrain, 128, 128)); using var candidates = session.Candidates;
        var window = new TerrainGeneratorWindow(map, assets, Terrain()); QuickMapPreview.Configure(window, session, _ => false); window.Show();
        try
        {
            await EditorExperienceTests.WaitFor(() => window.PreferencesRestored && Find<Button>(window, "ApplyButton").IsEnabled);
            Find<NumericUpDown>(window, "QuickMapBatchCount").Value = 3;
            window.PreviewState.Changed += (_, _) =>
            {
                if (session.BatchRunning && window.PreviewState.Phase == GeneratorPreviewPhase.Generating)
                    Find<Button>(window, "QuickMapBatchStop").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            };
            await window.GetLogicalDescendants().OfType<QuickMapProduction>().Single().SearchAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.False(session.BatchRunning); Assert.True(Find<Button>(window, "QuickMapBack").IsEnabled);
            Assert.False(Find<Button>(window, "QuickMapBatchStop").IsEnabled); Assert.NotEmpty(candidates.Items); Assert.Empty(map.Cells);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task RecipePreviewReportsUnavailableOptionsRatherThanClaimingAnExactRestore()
    {
        using var assets = AssetStore.Load(Path.Combine(TestApplication.Root, "Data"));
        var parameters = new Dictionary<string, string> { ["SeedBox"] = "7", ["BaseList"] = "terrain:unavailable" };
        var session = new MapCreationSession(new(MapCreationKind.Terrain, 128, 128), parameters) { ExpectedRecipeParameters = parameters };
        using var candidates = session.Candidates;
        var window = new TerrainGeneratorWindow(new(128, 128), assets, Terrain(), parameters);
        QuickMapPreview.Configure(window, session, _ => false); window.Show();
        try
        {
            await EditorExperienceTests.WaitFor(() => window.PreferencesRestored && Find<Button>(window, "ApplyButton").IsEnabled);
            var feedback = Find<TextBlock>(window, "QuickMapRecipeFeedback"); Assert.True(feedback.IsVisible); Assert.Contains("terrain:unavailable", feedback.Text);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task LocalReviewCanRenderAnEmptyOriginalAndKeepsBothViewsSynchronized()
    {
        using var assets = AssetStore.Load(Path.Combine(TestApplication.Root, "Data"));
        var source = new MapDocument(16, 16); var generated = new MapDocument(16, 16); generated.Set(6, 6, new Cell { BackLibrary = 0, BackImage = 1 });
        var plan = PartialMapGeneration.Plan(source, generated, new Rect(0, 0, 16, 16), new(), PartialGenerationLayer.Ground);
        var images = await PartialMapGeneration.CreatePreviewAsync(plan, assets);
        using var before = images.Before; using var after = images.After;
        Assert.Equal(1024, before.PixelWidth); Assert.Equal(768, after.PixelHeight);
        var window = new PartialGenerationReviewWindow(plan, before, after); window.Show();
        try
        {
            window.Width = 720; window.Height = 460; Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var left = Find<MapPreviewViewport>(window, "PartialGenerationBefore"); var right = Find<MapPreviewViewport>(window, "PartialGenerationAfter");
            left.ChangeZoom(2); Assert.Equal(2, right.Zoom); left.ResetView(); Assert.Equal(1, right.Zoom);
            Assert.True(Find<Button>(window, "PartialGenerationApply").IsEnabled); Assert.Empty(source.Cells);
            if (Environment.GetEnvironmentVariable("CRYSTALSTUDIO_PRODUCTION_ARTIFACTS") is { Length: > 0 } folder)
            {
                Directory.CreateDirectory(folder); using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame); frame.Save(Path.Combine(folder, "local-review-minimum.png"));
            }
        }
        finally { window.Close(); }
    }
}
