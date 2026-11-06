using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CrystalStudio.Rendering;

namespace CrystalStudio.Tests;

public sealed class ModuleJoiningTests
{
    private static JoinModule Room(ModuleEdge edge, string name = "Room", bool ground = false, int offset = 0)
    {
        var cells = Enumerable.Range(0, 4).SelectMany(y => Enumerable.Range(0, 4).Select(x =>
            new StampCell(x + offset, y + offset, new Cell { MiddleLibrary = 1, MiddleImage = 1, BackImage = ground && x % 2 == 0 && y % 2 == 0 ? 1 : 0 }))).ToList();
        var (px, py) = edge switch { ModuleEdge.North => (1, 0), ModuleEdge.East => (3, 1), ModuleEdge.South => (1, 3), _ => (0, 1) };
        return new(name, cells, new(1, [new("entry", px + offset, py + offset, edge, "path", 2)]));
    }
    private static ModuleJoinPlan Preview(MapDocument map, int x = 8, int y = 8) =>
        ModuleJoining.Preview(map, Room(ModuleEdge.East), "entry", Room(ModuleEdge.West), "entry", x, y);

    [Theory]
    [InlineData(ModuleEdge.North, ModuleEdge.South, 8, 4)]
    [InlineData(ModuleEdge.East, ModuleEdge.West, 12, 8)]
    [InlineData(ModuleEdge.South, ModuleEdge.North, 8, 12)]
    [InlineData(ModuleEdge.West, ModuleEdge.East, 4, 8)]
    public void OppositeConnectionsJoinWithoutGapsOrOverlap(ModuleEdge first, ModuleEdge second, int bx, int by)
    {
        var map = new MapDocument(32, 32);
        var plan = ModuleJoining.Preview(map, Room(first), "entry", Room(second), "entry", 8, 8);
        Assert.Equal(bx, plan.Placements[1].X); Assert.Equal(by, plan.Placements[1].Y);
        Assert.Equal(32, plan.Cells.Count); Assert.Equal(32, plan.Cells.Select(c => (c.X, c.Y)).Distinct().Count());
        var a = ModuleConnectionRules.Opening(plan.Openings[0]).ToArray();
        var b = ModuleConnectionRules.Opening(plan.Openings[1]).ToArray();
        Assert.All(a.Zip(b), pair => Assert.Equal(1, Math.Abs(pair.First.X - pair.Second.X) + Math.Abs(pair.First.Y - pair.Second.Y)));
        Assert.Empty(map.Cells); Assert.Empty(map.PlacedStamps); Assert.Equal(0, map.PickupRevision);
        Assert.Equal(2, plan.Generated.PlacedStamps.Count);
    }

    [Fact]
    public void LocalNegativeCoordinatesAreTranslatedWithoutMutatingModules()
    {
        var first = Room(ModuleEdge.East, offset: -2); var second = Room(ModuleEdge.West, offset: -6);
        var plan = ModuleJoining.Preview(new(32, 32), first, "entry", second, "entry", 8, 8);
        Assert.Equal(16, plan.Placements[1].X); Assert.Equal(12, plan.Placements[1].Y);
        first.Cells[0].Cell.MiddleImage = 9; first.Connections.Points.Clear();
        Assert.Equal((ushort)1, plan.Cells[0].Cell.MiddleImage);
        Assert.Single(plan.Placements[0].Module.Connections.Points);
        Assert.Equal(6, plan.Bounds.Left); Assert.Equal(13, plan.Bounds.Right);
    }

    [Theory]
    [InlineData("door", 2, ModuleEdge.West)]
    [InlineData("path", 1, ModuleEdge.West)]
    [InlineData("path", 2, ModuleEdge.East)]
    public void MismatchedConnectionsAreRejected(string kind, int width, ModuleEdge edge)
    {
        var second = Room(edge);
        second.Connections.Points[0] = second.Connections.Points[0] with { Kind = kind, Width = width };
        Assert.Throws<InvalidDataException>(() => ModuleJoining.Preview(new(32, 32), Room(ModuleEdge.East), "entry", second, "entry", 8, 8));
    }

    [Fact]
    public void MatchTypesAreCaseInsensitiveButUnknownIdsAndBlockedPassagesAreRejected()
    {
        var second = Room(ModuleEdge.West);
        second.Connections.Points[0] = second.Connections.Points[0] with { Kind = "PATH" };
        var map = new MapDocument(32, 32);
        ModuleJoining.Preview(map, Room(ModuleEdge.East), "entry", second, "entry", 8, 8);
        Assert.Throws<InvalidDataException>(() => ModuleJoining.Preview(map, Room(ModuleEdge.East), "missing", second, "entry", 8, 8));
        second.Cells.Single(c => c.X == 1 && c.Y == 1).Cell.BackImage = 0x40000000;
        Assert.Throws<InvalidDataException>(() => ModuleJoining.Preview(map, Room(ModuleEdge.East), "entry", second, "entry", 8, 8));
        Assert.Empty(map.Cells);
    }

    [Theory]
    [InlineData(-1, 8)]
    [InlineData(26, 8)]
    [InlineData(8, 30)]
    [InlineData(int.MaxValue, 8)]
    [InlineData(int.MinValue, 8)]
    public void OutOfBoundsOrOverflowCannotModifyTheMap(int x, int y)
    {
        var map = new MapDocument(32, 32);
        Assert.Throws<InvalidDataException>(() => Preview(map, x, y));
        Assert.Empty(map.Cells); Assert.Empty(map.PlacedStamps);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void AnyExistingContentIncludingSparseHolesBlocksTheFootprint(int layer)
    {
        var map = new MapDocument(32, 32);
        var existing = layer switch
        {
            0 => new Cell { BackImage = 1 }, 1 => new Cell { MiddleImage = 2 },
            2 => new Cell { Foreground = new(2, 1) }, _ => new Cell { BackImage = 0x20000000 }
        };
        map.Set(9, 10, existing);
        var first = Room(ModuleEdge.East);
        first = first with { Cells = first.Cells.Where(c => c.X != 1 || c.Y != 2).ToArray() };
        Assert.Throws<InvalidDataException>(() => ModuleJoining.Preview(map, first, "entry", Room(ModuleEdge.West), "entry", 8, 8));
        Assert.Equal(existing, map.Get(9, 10)); Assert.Single(map.Cells);
    }

    [Fact]
    public void GroundGridParityIsPreservedAndMisalignmentIsRejected()
    {
        var map = new MapDocument(32, 32);
        var first = Room(ModuleEdge.East, ground: true); var second = Room(ModuleEdge.West, ground: true);
        var plan = ModuleJoining.Preview(map, first, "entry", second, "entry", 8, 8);
        Assert.All(plan.Cells.Where(c => c.Cell.BackImage != 0), c => { Assert.Equal(0, c.X % 2); Assert.Equal(0, c.Y % 2); });
        Assert.Throws<InvalidDataException>(() => ModuleJoining.Preview(map, first, "entry", second, "entry", 9, 8));
        Assert.Throws<InvalidDataException>(() => ModuleJoining.Preview(map, first, "entry", second, "entry", 8, 9));
        // A width-five first module forces the second ground module onto an odd X origin.
        var wider = first.Cells.Concat(Enumerable.Range(0, 4).Select(y => new StampCell(4, y, new Cell { MiddleImage = 1 }))).ToArray();
        first = first with { Cells = wider, Connections = new(1, [first.Connections.Points[0] with { X = 4 }]) };
        Assert.Throws<InvalidDataException>(() => ModuleJoining.Preview(map, first, "entry", second, "entry", 8, 8));
    }

    [Fact]
    public void CancellationAndInvalidMetadataLeaveSourceUntouched()
    {
        var map = new MapDocument(32, 32);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => ModuleJoining.Preview(map, Room(ModuleEdge.East), "entry", Room(ModuleEdge.West), "entry", 8, 8, cancellation.Token));
        var bad = Room(ModuleEdge.East) with { Connections = new(2, []) };
        Assert.Throws<InvalidDataException>(() => ModuleJoining.Preview(map, bad, "entry", Room(ModuleEdge.West), "entry", 8, 8));
        Assert.Empty(map.Cells); Assert.Empty(map.PlacedStamps);
    }

    [AvaloniaFact]
    public void ApplyUndoRedoAndMapSavePreserveBothInstancesAndUnrelatedContent()
    {
        var map = new MapDocument(32, 32);
        var prior = new EditHistory();
        map.PlaceStamp([new(0, 0, new Cell { MiddleImage = 3 })], 1, 1, "Existing", prior, (_, _) => { }); prior.Commit();
        var viewport = new MapViewport(); viewport.SetDocument(map);
        var plan = Preview(map);
        Assert.False(viewport.History.Dirty); Assert.Single(map.PlacedStamps);
        viewport.ApplyModuleJoin(plan);
        Assert.True(viewport.History.Dirty); Assert.Equal(3, map.PlacedStamps.Count); Assert.Equal(33, map.Cells.Count());
        Assert.Throws<InvalidOperationException>(() => viewport.ApplyModuleJoin(plan));
        viewport.Undo(); Assert.False(viewport.History.Dirty); Assert.Single(map.PlacedStamps); Assert.Single(map.Cells);
        Assert.Equal((ushort)3, map.Get(1, 1).MiddleImage);
        viewport.Redo(); Assert.True(viewport.History.Dirty); Assert.Equal(3, map.PlacedStamps.Count); Assert.Equal(33, map.Cells.Count());
        string file = Path.Combine(Path.GetTempPath(), "CrystalStudio-module-join-" + Guid.NewGuid().ToString("N") + ".map");
        try
        {
            map.Save(file); var loaded = MapDocument.Load(file);
            Assert.Null(loaded.MetadataWarning); Assert.Equal(3, loaded.PlacedStamps.Count);
            Assert.Equal((ushort)1, loaded.Get(12, 9).MiddleImage);
        }
        finally { File.Delete(file); }
    }

    [AvaloniaFact]
    public void LayerLocksAndChangedOrDifferentMapsRejectApply()
    {
        var map = new MapDocument(32, 32); var viewport = new MapViewport(); viewport.SetDocument(map);
        var plan = Preview(map);
        foreach (var layer in Enum.GetValues<MapLayer>())
        {
            viewport.SetLayerLocked(layer, true);
            Assert.Throws<InvalidOperationException>(() => viewport.ApplyModuleJoin(plan));
            Assert.Empty(map.Cells); Assert.False(viewport.History.Dirty); viewport.SetLayerLocked(layer, false);
        }
        map.Set(0, 0, new Cell { MiddleImage = 9 });
        Assert.Throws<InvalidOperationException>(() => viewport.ApplyModuleJoin(plan)); Assert.Single(map.Cells);
        viewport.SetDocument(new(32, 32));
        Assert.Throws<InvalidOperationException>(() => viewport.ApplyModuleJoin(plan)); Assert.Empty(viewport.Document.Cells);
    }

    [AvaloniaFact]
    public async Task DialogLoadsSavedModulesFiltersMatchesAndInvalidatesChangedParameters()
    {
        using var fixture = new CatalogueFixture(); var map = new MapDocument(32, 32);
        var dialog = new ModuleJoinWindow(map, fixture.Index, null); dialog.Show();
        try
        {
            Assert.Single(dialog.SecondConnection.Items); Assert.False(dialog.TryAccept());
            await dialog.GeneratePreviewAsync();
            Assert.NotNull(dialog.Plan); Assert.True(dialog.ApplyButton.IsEnabled); Assert.True(dialog.TryAccept()); Assert.Empty(map.Cells);
            dialog.XBox.Text = "9";
            Assert.Null(dialog.Plan); Assert.False(dialog.ApplyButton.IsEnabled); Assert.False(dialog.TryAccept());
            await dialog.GeneratePreviewAsync(); Assert.NotNull(dialog.Plan);
            map.Set(0, 0, new Cell { MiddleImage = 1 });
            Assert.False(dialog.TryAccept()); Assert.Null(dialog.Plan);
            dialog.SecondModule.SelectedIndex = 0; Assert.Empty(dialog.SecondConnection.Items);
            await dialog.GeneratePreviewAsync(); Assert.Null(dialog.Plan); Assert.False(dialog.ApplyButton.IsEnabled);
            Assert.Single(map.Cells);
        }
        finally { dialog.Close(); }
    }

    [AvaloniaFact]
    public async Task CancellingDialogOrAnInFlightPreviewNeverWritesToMap()
    {
        using var fixture = new CatalogueFixture(); var map = new MapDocument(32, 32);
        var dialog = new ModuleJoinWindow(map, fixture.Index, null); dialog.Show();
        var work = dialog.GeneratePreviewAsync();
        dialog.XBox.Text = "invalid"; await work;
        Assert.Null(dialog.Plan); Assert.False(dialog.ApplyButton.IsEnabled);
        dialog.XBox.Text = "8";
        work = dialog.GeneratePreviewAsync(); dialog.Close(); await work;
        Assert.Null(dialog.Plan); Assert.Empty(map.Cells); Assert.Empty(map.PlacedStamps);
    }

    [AvaloniaFact]
    public async Task RepreviewRereadsTheObjectAndReportsInvalidFiles()
    {
        using var fixture = new CatalogueFixture(); var map = new MapDocument(32, 32);
        var dialog = new ModuleJoinWindow(map, fixture.Index, null); dialog.Show();
        try
        {
            await dialog.GeneratePreviewAsync(); Assert.NotNull(dialog.Plan);
            File.WriteAllText(Path.Combine(fixture.Root, "b.X"), "bad");
            await dialog.GeneratePreviewAsync(); Assert.Null(dialog.Plan); Assert.False(dialog.ApplyButton.IsEnabled);
            Assert.False(string.IsNullOrWhiteSpace(dialog.Feedback.Text)); Assert.Empty(map.Cells);
        }
        finally { dialog.Close(); }
    }

    [AvaloniaFact]
    public async Task ArtworkPreviewAndConnectionDiagramCanBothBeOpened()
    {
        using var fixture = new CatalogueFixture();
        using var assets = AssetStore.Load(Path.Combine(TestApplication.Root, "Data"));
        var dialog = new ModuleJoinWindow(new(32, 32), fixture.Index, assets); dialog.Show();
        try
        {
            await dialog.GeneratePreviewAsync(); Dispatcher.UIThread.RunJobs();
            Assert.NotNull(dialog.Plan); Assert.True(dialog.TryAccept());
            for (int attempt = 0; attempt < 100 && assets.Get(1, 0) == null; attempt++)
            { await Task.Delay(20); Dispatcher.UIThread.RunJobs(); }
            Assert.NotNull(assets.Get(1, 0));
            var viewport = dialog.GetVisualDescendants().OfType<MapViewport>().Single();
            viewport.RefreshAssets();
            await Task.Delay(100); Dispatcher.UIThread.RunJobs();
            using var frame = dialog.CaptureRenderedFrame(); Assert.NotNull(frame);
            using var stream = new MemoryStream(); frame.Save(stream); stream.Position = 0;
            using var image = RasterImage.Load(stream); byte[] pixels = Fixture.Pixels(image);
            Assert.True(Enumerable.Range(0, pixels.Length / 4).Any(i => pixels[i * 4] == 80 && pixels[i * 4 + 1] == 160 && pixels[i * 4 + 2] == 20),
                "The map preview must render the test module artwork.");
            // Opt-in artifact for checking the actual dialog layout, without writing to the repository.
            if (Environment.GetEnvironmentVariable("CRYSTALSTUDIO_JOIN_SCREENSHOT") is { Length: > 0 } path) frame.Save(path);
            dialog.GetVisualDescendants().OfType<TabControl>().Single().SelectedIndex = 1;
            Dispatcher.UIThread.RunJobs();
            var schematic = dialog.GetVisualDescendants().OfType<ModuleConnectionCanvas>().Single();
            Assert.Equal(2, schematic.Points.Count); Assert.True(schematic.Bounds.Width > 0);
        }
        finally { dialog.Close(); }
    }

    [AvaloniaFact]
    public async Task ToolsMenuOpensTheJoinDialogAndCancelKeepsTheMapClean()
    {
        var window = new MainWindow(new AssetHistoryStore(Path.Combine(TestApplication.Root, "join-assets.json")),
            new RecentMapStore(Path.Combine(TestApplication.Root, "join-maps.json")));
        window.Show();
        try
        {
            for (int i = 0; i < 500 && (window.FindControl<MapViewport>("Viewport")!.Assets == null || !window.FindControl<MenuItem>("NewButton")!.IsEnabled); i++)
            { await Task.Delay(20); Dispatcher.UIThread.RunJobs(); }
            bool opened = false;
            using var handler = Window.WindowOpenedEvent.AddClassHandler<ModuleJoinWindow>((dialog, _) =>
                Dispatcher.UIThread.Post(() => { opened = true; dialog.DialogResult = false; }));
            var button = window.FindControl<MenuItem>("JoinModulesButton")!;
            Assert.True(button.IsEnabled); button.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, button));
            for (int i = 0; i < 500 && !opened; i++) { await Task.Delay(20); Dispatcher.UIThread.RunJobs(); }
            Assert.True(opened);
            Assert.False(window.FindControl<MapViewport>("Viewport")!.History.Dirty);
        }
        finally { window.Close(); }
    }

    private sealed class CatalogueFixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "CrystalStudio-join-catalogue-" + Guid.NewGuid().ToString("N"));
        internal StampCategoryIndex Index { get; }
        internal CatalogueFixture()
        {
            Directory.CreateDirectory(Root);
            var a = Room(ModuleEdge.East, "A"); var b = Room(ModuleEdge.West, "B");
            CellCodec.SaveStamp(Path.Combine(Root, "a.X"), a.Cells); CellCodec.SaveStamp(Path.Combine(Root, "b.X"), b.Cells);
            CategorizedStamp Entry(string file, JoinModule module) => new(file, file, module.Name, null, null, [], [], [], [], null, "unknown", null, false, "unreviewed") { Connections = module.Connections };
            Index = new(1, Root, Root, [Entry("a.X", a), Entry("b.X", b)]);
        }
        public void Dispose() => Directory.Delete(Root, true);
    }
}
