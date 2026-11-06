using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CrystalStudio.Rendering;

namespace CrystalStudio.Tests;

public sealed class ModuleGenerationTests
{
    private static JoinModule Room(string name = "Room", bool ground = false, int offset = 0)
    {
        var cells = Enumerable.Range(0, 4).SelectMany(y => Enumerable.Range(0, 4).Select(x =>
            new StampCell(x + offset, y + offset, new Cell { MiddleLibrary = 1, MiddleImage = 1,
                BackImage = ground && x % 2 == 0 && y % 2 == 0 ? 1 : 0 }))).ToArray();
        return new(name, cells, new(1, [
            new("north", offset + 1, offset, ModuleEdge.North, "path"),
            new("east", offset + 3, offset + 1, ModuleEdge.East, "path"),
            new("south", offset + 1, offset + 3, ModuleEdge.South, "path"),
            new("west", offset, offset + 1, ModuleEdge.West, "path")]));
    }
    private static ModuleGenerationOptions Options(ModuleLayout layout = ModuleLayout.Compact, int count = 12, int seed = 42) =>
        new(count, seed, new(0, 0, 64, 64), layout);
    private static string Signature(ModuleGenerationPlan plan) => string.Join(";", plan.Placement.Placements.Select(p => $"{p.Module.Name}:{p.X},{p.Y}")) +
        "|" + string.Join(";", plan.Links);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void GeneratesTheRequestedCountWithinTheAreaWithReachableUniqueConnections(int layoutValue)
    {
        var layout = (ModuleLayout)layoutValue; var source = new MapDocument(64, 64); var options = Options(layout);
        var result = ModuleGeneration.Generate(source, [Room()], options);
        Assert.Equal(12, result.Placement.Placements.Count); Assert.Equal(11, result.Links.Count);
        Assert.Equal(192, result.WalkableCells);
        Assert.Empty(source.Cells); Assert.Empty(source.PlacedStamps); Assert.Equal(0, source.PickupRevision);
        var used = new HashSet<(int, int)>();
        var reachable = new HashSet<int> { 0 };
        foreach (var link in result.Links)
        {
            Assert.Contains(link.First, reachable); Assert.True(reachable.Add(link.Second));
            Assert.True(used.Add((link.First, link.FirstPort))); Assert.True(used.Add((link.Second, link.SecondPort)));
            var a = result.Placement.Placements[link.First]; var b = result.Placement.Placements[link.Second];
            var ap = a.Module.Connections.Points[link.FirstPort]; var bp = b.Module.Connections.Points[link.SecondPort];
            Assert.True(ModuleJoining.Matches(ap, bp));
            Assert.Equal(1, Math.Abs(a.X + ap.X - b.X - bp.X) + Math.Abs(a.Y + ap.Y - b.Y - bp.Y));
        }
        Assert.Equal(12, reachable.Count);
        Assert.Equal(result.Placement.Cells.Count, result.Placement.Cells.Select(c => (c.X, c.Y)).Distinct().Count());
        Assert.All(result.Placement.Cells, cell => { Assert.InRange(cell.X, 0, 63); Assert.InRange(cell.Y, 0, 63); });
        if (layout == ModuleLayout.Chain) Assert.All(result.Links, link => Assert.Equal(link.Second - 1, link.First));
        if (layout == ModuleLayout.Branching) Assert.True(result.Links.Count(link => link.First == 0) >= 3);
        Assert.InRange(result.SearchSteps, 1, options.SearchLimit);
    }

    [Fact]
    public void AutomaticallyChoosesAlternatingCompatibleModuleTypesAndRepeatsInstances()
    {
        JoinModule Corridor(string name, string west, string east)
        {
            var room = Room(name);
            return room with { Connections = new(1, [
                new("west", 0, 1, ModuleEdge.West, west), new("east", 3, 1, ModuleEdge.East, east)]) };
        }
        var result = ModuleGeneration.Generate(new(64, 16), [Corridor("A", "a", "b"), Corridor("B", "b", "a")],
            new(8, 7, new(0, 0, 64, 16), ModuleLayout.Chain));
        Assert.Equal(8, result.Placement.Placements.Count);
        Assert.Equal(2, result.Placement.Placements.Select(p => p.Module.Name).Distinct().Count());
        Assert.All(result.Links, link => Assert.NotEqual(result.Placement.Placements[link.First].Module.Name, result.Placement.Placements[link.Second].Module.Name));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void SeedsReproduceTheLayoutAndDifferentSeedsProduceVariation(int layoutValue)
    {
        var layout = (ModuleLayout)layoutValue; var map = new MapDocument(64, 64);
        var a = ModuleGeneration.Generate(map, [Room()], Options(layout, seed: 123));
        var b = ModuleGeneration.Generate(map, [Room()], Options(layout, seed: 123));
        Assert.Equal(Signature(a), Signature(b)); Assert.Equal(a.SearchSteps, b.SearchSteps);
        var variants = Enumerable.Range(1, 4).Select(seed => Signature(ModuleGeneration.Generate(map, [Room()], Options(layout, seed: seed)))).Distinct().Count();
        Assert.True(variants > 1);
    }

    [Fact]
    public void RetriesTheRootPositionWhenACenteredChainCannotReachTheRequestedLength()
    {
        var room = Room();
        room = room with { Connections = new(1, room.Connections.Points.Where(p => p.Edge is ModuleEdge.East or ModuleEdge.West).ToList()) };
        var result = ModuleGeneration.Generate(new(40, 4), [room], new(10, 10, new(0, 0, 40, 4), ModuleLayout.Chain));
        Assert.Equal(10, result.Placement.Placements.Count); Assert.Equal(0, result.Placement.Bounds.Left); Assert.Equal(39, result.Placement.Bounds.Right);
    }

    [Fact]
    public void DenseLayoutsAndMaximumModuleCountReachTheExactTarget()
    {
        var dense = ModuleGeneration.Generate(new(16, 16), [Room()], new(16, 15, new(0, 0, 16, 16), ModuleLayout.Compact));
        Assert.Equal(16, dense.Placement.Placements.Count); Assert.Equal(256, dense.WalkableCells);
        var maximum = ModuleGeneration.Generate(new(64, 64), [Room()], Options(count: 128));
        Assert.Equal(128, maximum.Placement.Placements.Count); Assert.Equal(127, maximum.Links.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RejectsInternallyDisconnectedModulesEvenWhenTheirOpeningsAreLocallyValid(bool foregroundCollision)
    {
        var cells = Enumerable.Range(0, 5).SelectMany(y => Enumerable.Range(0, 7).Select(x => new StampCell(x, y,
            new Cell { MiddleImage = 1, BackImage = x == 3 && !foregroundCollision ? 0x40000000 : 0,
                Foreground = x == 3 && foregroundCollision ? new(2, unchecked((short)0x8001)) : null }))).ToArray();
        var module = new JoinModule("Split room", cells, new(1, [
            new("left", 0, 2, ModuleEdge.West, "path"), new("right", 6, 2, ModuleEdge.East, "path")]));
        ModuleConnectionRules.Validate(module.Connections, module.Cells); // Existing seam-only validation cannot detect the interior wall.
        var map = new MapDocument(64, 64);
        var error = Assert.Throws<InvalidDataException>(() => ModuleGeneration.Generate(map, [module], Options()));
        Assert.Contains("Split room", error.Message); Assert.Contains("inside a module", error.Message);
        Assert.Empty(map.Cells);
    }

    [Fact]
    public void RejectsAnIsolatedWalkablePocketNotJustDisconnectedPorts()
    {
        // Keep the edge port valid, but isolate (3, 3) from the rest of a larger module.
        var cells = Enumerable.Range(0, 7).SelectMany(y => Enumerable.Range(0, 7).Select(x => new StampCell(x, y,
            new Cell { MiddleImage = 1, BackImage = Math.Abs(x - 3) + Math.Abs(y - 3) == 1 ? 0x20000000 : 0 }))).ToArray();
        var module = new JoinModule("Pocket", cells, new(1, [new("west", 0, 1, ModuleEdge.West, "path")]));
        ModuleConnectionRules.Validate(module.Connections, module.Cells);
        Assert.Throws<InvalidDataException>(() => ModuleGeneration.Generate(new(64, 64), [module], Options()));
    }

    [Theory]
    [InlineData(8, 0)]
    [InlineData(4, 4)]
    public void GlobalConnectivityCannotBypassGapsOrDiagonalContactsThroughTheEmptyMap(int x, int y)
    {
        var map = new MapDocument(20, 20); var room = Room(); var history = new EditHistory();
        map.PlaceStamp(room.Cells, 0, 0, "A", history, (_, _) => { });
        map.PlaceStamp(room.Cells, x, y, "B", history, (_, _) => { });
        Assert.Throws<InvalidDataException>(() => ModuleGeneration.VerifyConnectivity(map, [new(room, 0, 0), new(room, x, y)], new(0, 0, 20, 20)));
    }

    [Fact]
    public void GenerationRespectsRegionAndPreservesExistingContentIncludingSparseHoles()
    {
        var map = new MapDocument(64, 64); map.Set(20, 20, new Cell { BackImage = 0x20000000 }); map.Set(1, 1, new Cell { MiddleImage = 9 });
        var sparse = Room(); sparse = sparse with { Cells = sparse.Cells.Where(c => c.X != 2 || c.Y != 2).ToArray() };
        var result = ModuleGeneration.Generate(map, [sparse], Options(count: 8) with { Area = new(8, 8, 24, 24) });
        foreach (var p in result.Placement.Placements)
        {
            Assert.InRange(p.X, 8, 28); Assert.InRange(p.Y, 8, 28);
            Assert.False(20 >= p.X && 20 < p.X + 4 && 20 >= p.Y && 20 < p.Y + 4);
        }
        Assert.Equal(2, map.Cells.Count()); Assert.Equal((ushort)9, result.Placement.Generated.Get(1, 1).MiddleImage);
        Assert.Equal(0x20000000, result.Placement.Generated.Get(20, 20).BackImage);
    }

    [Fact]
    public void NegativeLocalCoordinatesAndGroundParityAreMaintained()
    {
        var result = ModuleGeneration.Generate(new(64, 64), [Room(ground: true, offset: -3)], Options());
        Assert.All(result.Placement.Cells.Where(c => c.Cell.BackImage != 0), cell => { Assert.Equal(0, cell.X % 2); Assert.Equal(0, cell.Y % 2); });
        Assert.Equal(12, result.Placement.Placements.Count);
    }

    [Fact]
    public void ImpossibleTargetsAndExhaustedSearchNeverReturnPartialLayouts()
    {
        var room = Room();
        var leaf = room with { Connections = new(1, [room.Connections.Points[0]]) };
        var map = new MapDocument(64, 64);
        Assert.Throws<InvalidDataException>(() => ModuleGeneration.Generate(map, [leaf], Options(count: 3) with { SearchLimit = 100 }));
        Assert.Throws<InvalidDataException>(() => ModuleGeneration.Generate(map, [room], Options() with { SearchLimit = 1 }));
        Assert.Throws<InvalidDataException>(() => ModuleGeneration.Generate(map, [room], Options(count: 3) with { Area = new(0, 0, 4, 4) }));
        Assert.Empty(map.Cells); Assert.Empty(map.PlacedStamps); Assert.Equal(0, map.PickupRevision);
    }

    [Theory]
    [InlineData(1, 64, 64)]
    [InlineData(129, 64, 64)]
    [InlineData(12, 1, 64)]
    [InlineData(12, int.MaxValue, 64)]
    public void InvalidCountOrAreaIsRejected(int count, int width, int height) =>
        Assert.Throws<InvalidDataException>(() => ModuleGeneration.Generate(new(64, 64), [Room()], new(count, 1, new(0, 0, width, height), ModuleLayout.Compact)));

    [Fact]
    public void NoRootReportsAreaSizeInsteadOfSuggestingAnotherSeed()
    {
        var error = Assert.Throws<InvalidDataException>(() => ModuleGeneration.Generate(new(64, 64), [Room()],
            Options() with { Area = new(8, 10, 3, 3) }));
        var problem = Assert.IsType<ModuleProblem>(ModuleDiagnostics.Read(error));
        Assert.Equal("generation-area-size", problem.Code); Assert.Equal(8, problem.X); Assert.Equal(10, problem.Y);
        Assert.Contains("3 × 3", error.Message); Assert.Contains("Room: 4 × 4", error.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NoRootReportsExistingArtworkOrCollisionWithoutChangingIt(bool collision)
    {
        var map = new MapDocument(8, 8);
        for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++)
            map.Set(x, y, new Cell { BackImage = collision ? 0x20000000 : 1 });
        long revision = map.PickupRevision;
        var error = Assert.Throws<InvalidDataException>(() => ModuleGeneration.Generate(map, [Room()], Options() with { Area = new(0, 0, 8, 8) }));
        Assert.Equal("generation-area-occupied", ModuleDiagnostics.Read(error)?.Code);
        Assert.Contains("64 cells", error.Message); Assert.Contains("blank map", error.Message);
        Assert.Equal(revision, map.PickupRevision); Assert.Empty(map.PlacedStamps);
        Assert.All(map.Cells, c => Assert.Equal(collision ? 0x20000000 : 1, c.Value.BackImage));
    }

    [Fact]
    public void NoRootDistinguishesGroundAlignmentFromOccupiedArea()
    {
        var error = Assert.Throws<InvalidDataException>(() => ModuleGeneration.Generate(new(64, 64), [Room(ground: true)],
            Options() with { Area = new(1, 1, 4, 4) }));
        Assert.Equal("generation-ground-alignment", ModuleDiagnostics.Read(error)?.Code);
    }

    [Fact]
    public void UnsearchedOriginsAreNotReportedAsImpossibleOrOccupied()
    {
        var map = new MapDocument(64, 64); map.Set(30, 30, new Cell { MiddleImage = 1 });
        var error = Assert.Throws<InvalidDataException>(() => ModuleGeneration.Generate(map, [Room()], Options() with { SearchLimit = 1 }));
        Assert.Equal("generation-search-limit", ModuleDiagnostics.Read(error)?.Code);
        Assert.Contains("best attempt: 0", error.Message);
        Assert.Equal(12, ModuleGeneration.Generate(map, [Room()], Options()).Placement.Placements.Count);
    }

    [Fact]
    public void IncompatibleWidthsExplainWhySeedChangesCannotConnectThePool()
    {
        var module = Room() with { Connections = new(1, [
            new("north", 1, 0, ModuleEdge.North, "path", 1),
            new("south", 1, 3, ModuleEdge.South, "path", 2)]) };
        var error = Assert.Throws<InvalidDataException>(() => ModuleGeneration.Generate(new(64, 64), [module], Options()));
        Assert.Equal("generation-connections", ModuleDiagnostics.Read(error)?.Code);
        Assert.Contains("equal width", error.Message); Assert.Contains("Target: 12", error.Message);
    }

    [AvaloniaFact]
    public async Task ReferenceDimensionsInvalidatePreviewAndAdaptToSmallOrLargeHints()
    {
        using var fixture = new CatalogueFixture();
        var dialog = new ModuleGenerationWindow(fixture.Index, null); dialog.Show();
        try
        {
            dialog.WidthBox.Text = "64"; dialog.HeightBox.Text = "32";
            await dialog.GeneratePreviewAsync();
            var plan = Assert.IsType<ModuleGenerationPlan>(dialog.Plan);
            Assert.Equal(64, plan.Placement.Source.Width); Assert.Equal(32, plan.Placement.Source.Height);
            Assert.Empty(plan.Placement.Source.Cells); Assert.Equal(new ModuleArea(0, 0, 64, 32), plan.Options.Area);
            foreach (var dimensions in new[] { ("bad", "32"), ("0", "32"), ("-1", "32") })
            {
                dialog.WidthBox.Text = dimensions.Item1; dialog.HeightBox.Text = dimensions.Item2;
                Assert.Null(dialog.Plan); Assert.False(dialog.TryAccept());
                await dialog.GeneratePreviewAsync();
                Assert.Equal(ModuleGenerationViewState.Failed, dialog.ViewState);
                Assert.Same(plan, dialog.DisplayedPlan); Assert.False(dialog.ApplyButton.IsEnabled);
            }
            dialog.WidthBox.Text = "32"; dialog.HeightBox.Text = "64"; await dialog.GeneratePreviewAsync();
            Assert.True(dialog.TryAccept()); Assert.Equal(32, dialog.Plan!.Placement.Source.Width);
            Assert.NotSame(plan.Placement.Source, dialog.Plan.Placement.Source);
            foreach (string hint in new[] { "1", "4096" })
            {
                dialog.WidthBox.Text = hint; dialog.HeightBox.Text = hint; await dialog.GeneratePreviewAsync();
                Assert.True(dialog.TryAccept()); Assert.Equal(12, dialog.Plan!.Placement.Placements.Count);
                Assert.InRange((long)dialog.Plan.Placement.Source.Width * dialog.Plan.Placement.Source.Height, 256, 1_000_000);
                Assert.Contains("Actual new map:", dialog.Feedback.Text);
                Assert.Equal(hint, dialog.WidthBox.Text); // Keep the user's hint, show actual dimensions separately.
            }
        }
        finally { dialog.Close(); }
    }

    [Fact]
    public void CancellationAndInvalidPoolsAreRejectedBeforeModifyingTheMap()
    {
        var map = new MapDocument(64, 64);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => ModuleGeneration.Generate(map, [Room()], Options(), cancelled.Token));
        Assert.Throws<InvalidDataException>(() => ModuleGeneration.Generate(map, [], Options()));
        Assert.Throws<InvalidDataException>(() => ModuleGeneration.Generate(map, Enumerable.Repeat(Room(), 257).ToArray(), Options()));
        Assert.Throws<InvalidDataException>(() => ModuleGeneration.Generate(map, [Room()], Options() with { Layout = (ModuleLayout)100 }));
        Assert.Empty(map.Cells);
    }

    [AvaloniaFact]
    public void GeneratedLayoutAppliesAsOneUndoStepAndSurvivesSaveReload()
    {
        var map = new MapDocument(64, 64); map.Set(0, 0, new Cell { MiddleImage = 9 });
        var viewport = new MapViewport(); viewport.SetDocument(map);
        var result = ModuleGeneration.Generate(map, [Room()], Options() with { Area = new(8, 8, 48, 48) });
        Assert.False(viewport.History.Dirty);
        viewport.ApplyModuleJoin(result.Placement); Assert.Equal(12, map.PlacedStamps.Count); Assert.True(viewport.History.Dirty);
        Assert.Throws<InvalidOperationException>(() => viewport.ApplyModuleJoin(result.Placement));
        viewport.Undo(); Assert.Empty(map.PlacedStamps); Assert.Single(map.Cells); Assert.False(viewport.History.Dirty);
        viewport.Redo(); Assert.Equal(12, map.PlacedStamps.Count); Assert.Equal((ushort)9, map.Get(0, 0).MiddleImage);
        string file = Path.Combine(Path.GetTempPath(), "CrystalStudio-autogenerated-" + Guid.NewGuid().ToString("N") + ".map");
        try
        {
            map.Save(file); var loaded = MapDocument.Load(file);
            Assert.Null(loaded.MetadataWarning); Assert.Equal(12, loaded.PlacedStamps.Count);
            Assert.Equal(result.WalkableCells, ModuleGeneration.VerifyConnectivity(loaded, result.Placement.Placements, result.Options.Area));
        }
        finally { File.Delete(file); }
    }

    [AvaloniaFact]
    public void LocksChangedMapAndDifferentMapRejectGeneratedLayout()
    {
        var map = new MapDocument(64, 64); var viewport = new MapViewport(); viewport.SetDocument(map);
        var plan = ModuleGeneration.Generate(map, [Room()], Options());
        foreach (var layer in Enum.GetValues<MapLayer>())
        {
            viewport.SetLayerLocked(layer, true);
            Assert.Throws<InvalidOperationException>(() => viewport.ApplyModuleJoin(plan.Placement));
            Assert.Empty(map.Cells); viewport.SetLayerLocked(layer, false);
        }
        map.Set(0, 0, new Cell { MiddleImage = 9 });
        Assert.Throws<InvalidOperationException>(() => viewport.ApplyModuleJoin(plan.Placement)); Assert.Single(map.Cells);
        viewport.SetDocument(new(64, 64));
        Assert.Throws<InvalidOperationException>(() => viewport.ApplyModuleJoin(plan.Placement)); Assert.Empty(viewport.Document.Cells);
    }

    [AvaloniaFact]
    public async Task AutomaticDialogLoadsThePoolAndGeneratesWithoutManualModuleOrPortSelection()
    {
        using var fixture = new CatalogueFixture();
        var dialog = new ModuleGenerationWindow(fixture.Index, null); dialog.Show();
        try
        {
            dialog.SeedBox.Text = "17"; await dialog.GeneratePreviewAsync();
            Assert.NotNull(dialog.Plan); Assert.Equal(12, dialog.Plan.Placement.Placements.Count); Assert.True(dialog.TryAccept());
            Assert.Empty(dialog.Plan.Placement.Source.Cells); Assert.True(dialog.ApplyButton.IsEnabled);
            dialog.LayoutBox.SelectedItem = ModuleLayout.Chain; Assert.Null(dialog.Plan); Assert.False(dialog.ApplyButton.IsEnabled);
            await dialog.GeneratePreviewAsync(); Assert.NotNull(dialog.Plan);
            Assert.All(dialog.Plan.Links, link => Assert.Equal(link.Second - 1, link.First));
            dialog.CountBox.Text = "8"; Assert.Null(dialog.Plan);
            await dialog.GeneratePreviewAsync(); Assert.Equal(8, dialog.Plan!.Placement.Placements.Count);
            dialog.SelectModules([]); Assert.Null(dialog.Plan); await dialog.GeneratePreviewAsync(); Assert.Null(dialog.Plan);
        }
        finally { dialog.Close(); }
    }

    [AvaloniaFact]
    public async Task ChangesAndClosingCancelInflightGenerationAndInvalidInputsCannotApply()
    {
        using var fixture = new CatalogueFixture();
        var dialog = new ModuleGenerationWindow(fixture.Index, null); dialog.Show();
        var work = dialog.GeneratePreviewAsync(); dialog.SeedBox.Text = "21"; await work;
        Assert.Null(dialog.Plan); Assert.False(dialog.ApplyButton.IsEnabled);
        dialog.CountBox.Text = "bad"; await dialog.GeneratePreviewAsync(); Assert.False(dialog.TryAccept());
        dialog.CountBox.Text = "129"; await dialog.GeneratePreviewAsync(); Assert.Null(dialog.Plan);
        Assert.Contains("placed module instances", dialog.Feedback.Text);
        dialog.CountBox.Text = "12"; work = dialog.GeneratePreviewAsync(); dialog.Close(); await work;
        Assert.Null(dialog.Plan);
    }

    [AvaloniaFact]
    public async Task DialogRejectsMutatedPlanAndRereadsChangedModuleFiles()
    {
        using var fixture = new CatalogueFixture();
        var dialog = new ModuleGenerationWindow(fixture.Index, null); dialog.Show();
        try
        {
            await dialog.GeneratePreviewAsync(); Assert.NotNull(dialog.Plan);
            dialog.Plan.Placement.Source.Set(0, 0, new Cell { MiddleImage = 9 }); Assert.False(dialog.TryAccept()); Assert.Null(dialog.Plan);
            File.WriteAllText(Path.Combine(fixture.Root, "room.X"), "bad");
            await dialog.GeneratePreviewAsync(); Assert.Null(dialog.Plan); Assert.False(dialog.ApplyButton.IsEnabled);
            Assert.Contains("Room", dialog.Feedback.Text);
        }
        finally { dialog.Close(); }
    }

    [AvaloniaFact]
    public async Task AutomaticPreviewRendersArtworkAndAllConnections()
    {
        using var fixture = new CatalogueFixture(); using var assets = AssetStore.Load(Path.Combine(TestApplication.Root, "Data"));
        var dialog = new ModuleGenerationWindow(fixture.Index, assets); dialog.Show();
        try
        {
            dialog.WidthBox.Text = "16"; dialog.HeightBox.Text = "16";
            dialog.SeedBox.Text = "18"; await dialog.GeneratePreviewAsync(); Dispatcher.UIThread.RunJobs(); Assert.NotNull(dialog.Plan);
            for (int attempt = 0; attempt < 100 && assets.Get(1, 0) == null; attempt++) { await Task.Delay(20); Dispatcher.UIThread.RunJobs(); }
            dialog.GetVisualDescendants().OfType<MapViewport>().Single().RefreshAssets();
            await Task.Delay(100); Dispatcher.UIThread.RunJobs();
            using var frame = dialog.CaptureRenderedFrame(); Assert.NotNull(frame);
            using var stream = new MemoryStream(); frame.Save(stream); stream.Position = 0;
            using var image = RasterImage.Load(stream); byte[] pixels = Fixture.Pixels(image);
            Assert.Contains(Enumerable.Range(0, pixels.Length / 4), i => pixels[i * 4] == 80 && pixels[i * 4 + 1] == 160 && pixels[i * 4 + 2] == 20);
            if (Environment.GetEnvironmentVariable("CRYSTALSTUDIO_AUTO_MODULE_SCREENSHOT") is { Length: > 0 } path) frame.Save(path);
            Assert.Empty(dialog.GetVisualDescendants().OfType<TabControl>());
            var diagram = dialog.GetVisualDescendants().OfType<ModuleConnectionCanvas>().Single();
            Assert.Equal(22, diagram.Points.Count); Assert.True(diagram.Bounds.Width > 0);
        }
        finally { dialog.Close(); }
    }

    [AvaloniaFact]
    public async Task ToolsMenuIgnoresOriginalSelectionAndLocksAndCancelPreservesTheMap()
    {
        var window = new MainWindow(new AssetHistoryStore(Path.Combine(TestApplication.Root, "automatic-assets.json")),
            new RecentMapStore(Path.Combine(TestApplication.Root, "automatic-maps.json"))); window.Show();
        try
        {
            for (int i = 0; i < 500 && (window.FindControl<MapViewport>("Viewport")!.Assets == null || !window.FindControl<MenuItem>("NewButton")!.IsEnabled); i++)
            { await Task.Delay(20); Dispatcher.UIThread.RunJobs(); }
            var viewport = window.FindControl<MapViewport>("Viewport")!;
            var original = viewport.Document; original.Set(8, 10, new Cell { MiddleImage = 9 });
            var selection = new Rect(8, 10, 32, 24);
            viewport.SetLayerLocked(MapLayer.Ground, true);
            typeof(MapViewport).GetProperty("Selection")!.SetValue(viewport, selection);
            var tabs = window.GetVisualDescendants().OfType<TabControl>().Single(t => t.Name == "MapTabs");
            int tabCount = tabs.Items.Count;
            bool opened = false;
            using var handler = Window.WindowOpenedEvent.AddClassHandler<ModuleGenerationWindow>((dialog, _) => Dispatcher.UIThread.Post(() =>
            {
                Assert.Equal("256", dialog.WidthBox.Text); Assert.Equal("256", dialog.HeightBox.Text);
                Assert.Equal(LocalText.T("Create new map"), dialog.ApplyButton.Content);
                opened = true; dialog.DialogResult = false;
            }));
            var button = window.FindControl<MenuItem>("GenerateModulesButton")!;
            Assert.Null(window.FindControl<MenuItem>("JoinModulesButton"));
            Assert.NotNull(window.FindControl<MenuItem>("SplitMapModulesButton"));
            Assert.True(button.IsEnabled); button.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, button));
            for (int i = 0; i < 500 && !opened; i++) { await Task.Delay(20); Dispatcher.UIThread.RunJobs(); }
            Assert.True(opened); Assert.False(viewport.History.Dirty);
            Assert.Equal(tabCount, tabs.Items.Count); Assert.Same(original, viewport.Document);
            Assert.Equal(selection, viewport.Selection); Assert.True(viewport.IsLayerLocked(MapLayer.Ground));
            Assert.Equal((ushort)9, original.Get(8, 10).MiddleImage);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task CreatingGeneratedMapOpensANewTabWithIndependentUndoAndPreservesOriginalState()
    {
        using var fixture = new CatalogueFixture();
        var window = new MainWindow(new AssetHistoryStore(Path.Combine(TestApplication.Root, "new-module-assets.json")),
            new RecentMapStore(Path.Combine(TestApplication.Root, "new-module-maps.json"))); window.Show();
        try
        {
            for (int i = 0; i < 500 && !window.FindControl<MenuItem>("NewButton")!.IsEnabled; i++)
            { await Task.Delay(20); Dispatcher.UIThread.RunJobs(); }
            var viewport = window.FindControl<MapViewport>("Viewport")!;
            var original = viewport.Document; var originalHistory = viewport.History;
            original.Set(8, 10, new Cell { MiddleImage = 9 }); originalHistory.MarkUnsaved();
            long revision = original.PickupRevision;
            var selection = new Rect(8, 10, 32, 24);
            viewport.SetLayerLocked(MapLayer.Ground, true);
            typeof(MapViewport).GetProperty("Selection")!.SetValue(viewport, selection);
            var tabs = window.GetVisualDescendants().OfType<TabControl>().Single(t => t.Name == "MapTabs");
            var originalTab = tabs.SelectedItem; int tabCount = tabs.Items.Count;
            Exception? failure = null; bool created = false;
            using var handler = Window.WindowOpenedEvent.AddClassHandler<ModuleGenerationWindow>((dialog, _) => Dispatcher.UIThread.Post(async () =>
            {
                try
                {
                    dialog.WidthBox.Text = "64"; dialog.HeightBox.Text = "48";
                    await dialog.GeneratePreviewAsync(); Assert.True(dialog.TryAccept());
                    Dispatcher.UIThread.RunJobs();
                    var point = dialog.ApplyButton.TranslatePoint(new Point(dialog.ApplyButton.Bounds.Width / 2, dialog.ApplyButton.Bounds.Height / 2), dialog)!.Value;
                    Assert.InRange(point.Y, 0, dialog.Bounds.Height);
                    dialog.MouseDown(point, Avalonia.Input.MouseButton.Left); dialog.MouseUp(point, Avalonia.Input.MouseButton.Left);
                    Assert.False(dialog.IsVisible);
                    created = true;
                }
                catch (Exception error) { failure = error; dialog.DialogResult = false; }
            }));
            typeof(MainWindow).GetMethod("ShowModuleGeneration", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .Invoke(window, [fixture.Index]);
            Assert.Null(failure); Assert.True(created); Assert.Equal(tabCount + 1, tabs.Items.Count);
            var generated = viewport.Document;
            Assert.NotSame(original, generated); Assert.Equal(64, generated.Width); Assert.Equal(48, generated.Height);
            Assert.Equal(12, generated.PlacedStamps.Count); Assert.True(viewport.History.Dirty);
            Assert.False(viewport.AnyLayerLocked); Assert.Null(viewport.Selection);
            Assert.Equal(revision, original.PickupRevision); Assert.Single(original.Cells);
            viewport.Undo(); Assert.Empty(generated.Cells); Assert.Empty(generated.PlacedStamps);
            viewport.Redo(); Assert.Equal(12, generated.PlacedStamps.Count);
            viewport.History.Saved(); tabs.SelectedItem = originalTab;
            Assert.Same(original, viewport.Document); Assert.Same(originalHistory, viewport.History);
            Assert.True(viewport.History.Dirty); Assert.Equal(selection, viewport.Selection);
            Assert.True(viewport.IsLayerLocked(MapLayer.Ground)); Assert.Equal((ushort)9, original.Get(8, 10).MiddleImage);
        }
        finally { window.FindControl<MapViewport>("Viewport")!.History.Saved(); window.Close(); }
    }

    private sealed class CatalogueFixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "CrystalStudio-auto-pool-" + Guid.NewGuid().ToString("N"));
        internal StampCategoryIndex Index { get; }
        internal CatalogueFixture()
        {
            Directory.CreateDirectory(Root); var room = Room(); CellCodec.SaveStamp(Path.Combine(Root, "room.X"), room.Cells);
            var entry = new CategorizedStamp("room", "room.X", "Room", null, null, [], [], [], [], null, "unknown", null, false, "unreviewed")
                { Connections = room.Connections };
            Index = new(1, Root, Root, [entry]);
        }
        public void Dispose() => Directory.Delete(Root, true);
    }
}
