using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CrystalStudio.Rendering;

namespace CrystalStudio.Tests;

public sealed class ModuleCaptureTests
{
    private static MapDocument Map(int width = 8, int height = 8)
    {
        var map = new MapDocument(width, height);
        map.Set(width / 2, height / 2, new Cell { MiddleLibrary = 1, MiddleImage = 1 });
        return map;
    }
    private static ModuleCaptureDraft Draft() => ModuleCapture.Capture(Map(), new(0, 0, 8, 8));

    [Fact]
    public void CapturesExactSelectionAndDetectsNonoverlappingBoundarySpansWithoutChangingTheMap()
    {
        var map = Map(); long revision = map.PickupRevision;
        var draft = ModuleCapture.Capture(map, new(0, 0, 8, 8));
        Assert.Single(draft.Cells); Assert.Equal(new(0, 0, 8, 8), draft.Connections.Footprint);
        Assert.Equal(4, draft.Connections.Points.Count);
        Assert.Equal(new[] { 8, 8, 6, 6 }, draft.Connections.Points.Select(p => p.Width));
        var openings = draft.Connections.Points.SelectMany(ModuleConnectionRules.Opening).ToArray();
        Assert.Equal(28, openings.Length); Assert.Equal(openings.Length, openings.Distinct().Count());
        ModuleConnectionRules.Validate(draft.Connections, draft.Cells);
        Assert.NotSame(map.Get(4, 4), draft.Cells[0].Cell);
        Assert.Equal(revision, map.PickupRevision); Assert.Single(map.Cells);
    }

    [Fact]
    public void TwoByTwoSelectionAssignsCornersOnce()
    {
        var draft = ModuleCapture.Capture(Map(2, 2), new(0, 0, 2, 2));
        Assert.Equal(2, draft.Connections.Points.Count);
        Assert.All(draft.Connections.Points, p => Assert.Equal(2, p.Width));
        ModuleCapture.Prepare(draft, "Small", draft.Connections);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void BoundaryAndInwardCollisionsSplitOpenings(int collision)
    {
        var map = Map();
        Cell Block() => collision switch
        {
            0 => new() { BackImage = 0x20000000 },
            1 => new() { BackImage = 0x40000000 },
            _ => new() { Foreground = new(1, unchecked((short)0x8001)) }
        };
        map.Set(2, 0, Block()); map.Set(5, 1, Block());
        var draft = ModuleCapture.Capture(map, new(0, 0, 8, 8));
        var north = draft.Connections.Points.Where(p => p.Edge == ModuleEdge.North).ToArray();
        Assert.Equal(new[] { 0, 3, 6 }, north.Select(p => p.X));
        Assert.Equal(new[] { 2, 2, 2 }, north.Select(p => p.Width));
        ModuleCapture.Prepare(draft, "Room", draft.Connections);
    }

    [Fact]
    public void LongOpeningsAreSplitIntoSupportedWidths()
    {
        var draft = ModuleCapture.Capture(Map(130, 4), new(0, 0, 130, 4));
        Assert.Equal(new[] { 64, 64, 2 }, draft.Connections.Points.Where(p => p.Edge == ModuleEdge.North).Select(p => p.Width));
        Assert.All(draft.Connections.Points, p => Assert.InRange(p.Width, 1, 64));
        ModuleCapture.Prepare(draft, "Long", draft.Connections);
    }

    [Fact]
    public void TooManyProposalsAreRetainedForReviewInsteadOfSilentlyDropped()
    {
        var draft = ModuleCapture.Capture(Map(4096, 4), new(0, 0, 4096, 4));
        Assert.True(draft.Connections.Points.Count > 64);
        Assert.Throws<InvalidDataException>(() => ModuleCapture.Prepare(draft, "Long", draft.Connections));
        var reviewed = draft.Connections with { Points = draft.Connections.Points.Take(2).ToList() };
        Assert.Equal(2, ModuleCapture.Prepare(draft, "Long", reviewed).Connections!.Points.Count);
    }

    [Theory]
    [InlineData(-1, 0, 4, 4)]
    [InlineData(0, -1, 4, 4)]
    [InlineData(0, 0, 1, 4)]
    [InlineData(0, 0, 4, 1)]
    [InlineData(7, 7, 2, 2)]
    [InlineData(0, 0, int.MaxValue, 4)]
    public void InvalidSelectionsAreRejected(int x, int y, int width, int height) =>
        Assert.Throws<InvalidDataException>(() => ModuleCapture.Capture(Map(), new(x, y, width, height)));

    [Fact]
    public void EmptyAndExcessiveSelectionsAreRejected()
    {
        Assert.Throws<InvalidDataException>(() => ModuleCapture.Capture(new(8, 8), new(0, 0, 8, 8)));
        Assert.Throws<InvalidDataException>(() => ModuleCapture.Capture(Map(1100, 1100), new(0, 0, 1100, 1100)));
        Assert.Throws<InvalidDataException>(() => ModuleCapture.Capture(Map(4100, 4), new(0, 0, 4100, 4)));
    }

    [Fact]
    public void SelectionIgnoresOutsideCellsAndPreservesOddOriginGroundParity()
    {
        var map = new MapDocument(16, 16);
        map.Set(6, 6, new Cell { BackImage = 1, BackLibrary = 1 });
        map.Set(4, 4, new Cell { BackImage = 0x20000000 });
        var draft = ModuleCapture.Capture(map, new(5, 5, 4, 4));
        Assert.Single(draft.Cells); Assert.Equal((2, 2), (draft.Cells[0].X, draft.Cells[0].Y));
        var prepared = ModuleCapture.Prepare(draft, "Ground", draft.Connections);
        Assert.Equal(0, prepared.Cells[0].X & 1); Assert.Equal(0, prepared.Cells[0].Y & 1);
        Assert.Equal(new(-1, -1, 4, 4), prepared.Connections!.Footprint);
        var generated = ModuleGeneration.Generate(new(32, 32), [new("Ground", prepared.Cells, prepared.Connections)],
            new(4, 42, new(0, 0, 32, 32), ModuleLayout.Chain));
        Assert.Equal(64, generated.WalkableCells);
        Assert.All(generated.Placement.Cells, c => { Assert.Equal(0, c.X & 1); Assert.Equal(0, c.Y & 1); });
    }

    [Fact]
    public void ZeroExitsAndDisconnectedInteriorsCannotEnterTheGenerationPool()
    {
        var draft = Draft();
        Assert.Throws<InvalidDataException>(() => ModuleCapture.Prepare(draft, "Room", draft.Connections with { Points = [] }));
        var map = Map();
        for (int y = 0; y < 8; y++) map.Set(3, y, new Cell { BackImage = 0x20000000 });
        draft = ModuleCapture.Capture(map, new(0, 0, 8, 8));
        Assert.Throws<InvalidDataException>(() => ModuleCapture.Prepare(draft, "Room", draft.Connections));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("bad\nname")]
    public void InvalidNamesAreRejected(string name)
    {
        var draft = Draft(); Assert.Throws<InvalidDataException>(() => ModuleCapture.Prepare(draft, name, draft.Connections));
    }

    [Fact]
    public void StaleAndCancelledCapturesCannotBeSaved()
    {
        using var fixture = new CatalogueFixture(); var draft = Draft();
        draft.Source.Set(0, 0, new Cell { MiddleImage = 2 });
        Assert.Throws<InvalidOperationException>(() => ModuleCaptureStore.Save(fixture.IndexPath, draft, "Room", draft.Connections));
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => ModuleCapture.Capture(Map(), new(0, 0, 8, 8), cancel.Token));
        draft = Draft();
        Assert.Throws<OperationCanceledException>(() => ModuleCaptureStore.Save(fixture.IndexPath, draft, "Room", draft.Connections, cancel.Token));
        Assert.Empty(StampCategories.Read(fixture.IndexPath).Objects);
    }

    [Fact]
    public void SavedModuleRetainsMarginsRoundTripsAndGeneratesAConnectedMap()
    {
        using var fixture = new CatalogueFixture(); var draft = Draft();
        var entry = ModuleCaptureStore.Save(fixture.IndexPath, draft, "  自定义模块  ", draft.Connections);
        var index = StampCategories.Read(fixture.IndexPath); var saved = Assert.Single(index.Objects);
        Assert.Equal("自定义模块", saved.Name); Assert.Equal(entry.Id, saved.Id);
        var pool = ModuleGeneration.LoadPool(index, index.Objects);
        var bounds = ModuleConnectionRules.GetBounds(pool[0].Cells, pool[0].Connections);
        Assert.Equal(new(-4, -4, 3, 3), bounds); Assert.Single(pool[0].Cells);
        var plan = ModuleGeneration.Generate(new(64, 64), pool, new(12, 42, new(0, 0, 64, 64), ModuleLayout.Compact));
        Assert.Equal(12, plan.Placement.Placements.Count); Assert.Equal(11, plan.Links.Count); Assert.Equal(768, plan.WalkableCells);
        Assert.Single(draft.Source.Cells); Assert.Equal(draft.Revision, draft.Source.PickupRevision);
        Assert.Equal("keep", JsonNode.Parse(File.ReadAllText(fixture.IndexPath))!["extension"]!.GetValue<string>());
        var again = ModuleConnectionRules.PrepareSave(pool[0].Cells, pool[0].Connections);
        Assert.Equal(saved.Connections!.Footprint, again.Connections!.Footprint);
        Assert.Equal(saved.Connections.Points, again.Connections.Points);
    }

    [Fact]
    public void NamesCannotChoosePathsAndRepeatedSavesNeverOverwriteExistingFilesOrEntries()
    {
        using var fixture = new CatalogueFixture(); var draft = Draft();
        var first = ModuleCaptureStore.Save(fixture.IndexPath, draft, "../../room", draft.Connections);
        var root = JsonNode.Parse(File.ReadAllText(fixture.IndexPath))!;
        root["objects"]![0]!["custom"] = 123; File.WriteAllText(fixture.IndexPath, root.ToJsonString());
        string path = StampCategories.Resolve(StampCategories.Read(fixture.IndexPath).ObjectRoot, first.File);
        byte[] bytes = File.ReadAllBytes(path);
        var second = ModuleCaptureStore.Save(fixture.IndexPath, draft, "../../room", draft.Connections);
        Assert.NotEqual(first.Id, second.Id); Assert.NotEqual(first.File, second.File); Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Equal(2, StampCategories.Read(fixture.IndexPath).Objects.Count);
        Assert.Equal(123, JsonNode.Parse(File.ReadAllText(fixture.IndexPath))!["objects"]![0]!["custom"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("object")]
    [InlineData("index")]
    [InlineData("cancel")]
    [InlineData("concurrent")]
    [InlineData("stale")]
    public void FailedSaveRollsBackOnlyItsNewFile(string failure)
    {
        using var fixture = new CatalogueFixture(); var draft = Draft();
        var existing = ModuleCaptureStore.Save(fixture.IndexPath, draft, "Existing", draft.Connections);
        byte[] before = File.ReadAllBytes(fixture.IndexPath); using var cancel = new CancellationTokenSource();
        Assert.ThrowsAny<Exception>(() => ModuleCaptureStore.Save(fixture.IndexPath, draft, "New", draft.Connections, cancel.Token, stage =>
        {
            if (stage == failure) throw new IOException("Injected failure");
            if (stage != "index") return;
            if (failure == "cancel") cancel.Cancel();
            if (failure == "concurrent") File.AppendAllText(fixture.IndexPath, " ");
            if (failure == "stale") draft.Source.Set(0, 0, new Cell { MiddleImage = 5 });
        }));
        var index = StampCategories.Read(fixture.IndexPath);
        Assert.Equal(existing.Id, Assert.Single(index.Objects).Id);
        Assert.Single(Directory.GetFiles(index.ObjectRoot, "*.X", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(index.ObjectRoot, "*.tmp", SearchOption.AllDirectories));
        Assert.Equal(failure == "concurrent" ? before.Concat(new byte[] { 32 }).ToArray() : before, File.ReadAllBytes(fixture.IndexPath));
    }

    [Fact]
    public void DeclaredFootprintIsOptionalForLegacyMetadataAndRejectsOverflow()
    {
        var legacy = JsonSerializer.Deserialize<ModuleConnections>("{\"Version\":1,\"Points\":[]}")!;
        Assert.Null(legacy.Footprint);
        Assert.Equal(new(4, 4, 4, 4), ModuleConnectionRules.GetBounds(Draft().Cells, legacy));
        var draft = Draft();
        Assert.Throws<InvalidDataException>(() => ModuleConnectionRules.Validate(draft.Connections with
            { Footprint = new(int.MaxValue, 0, 2, 4) }, draft.Cells));
    }

    [AvaloniaFact]
    public async Task ReviewWindowUpdatesPreviewRejectsEmptySelectionAndSavesTheReviewedPorts()
    {
        using var fixture = new CatalogueFixture(); var draft = Draft();
        var dialog = new ModuleCaptureWindow(draft, fixture.IndexPath, null); dialog.Show();
        try
        {
            Dispatcher.UIThread.RunJobs(); Assert.True(dialog.SaveButton.IsEnabled);
            var diagram = dialog.GetVisualDescendants().OfType<ModuleConnectionCanvas>().Single();
            Assert.Equal(4, diagram.Points.Count); Assert.True(diagram.Bounds.Width > 0);
            dialog.SelectConnections([]); Assert.False(dialog.SaveButton.IsEnabled);
            Assert.False(await dialog.SaveAsync()); Assert.Null(dialog.Saved); Assert.Empty(StampCategories.Read(fixture.IndexPath).Objects);
            dialog.SelectConnections(["north-1", "south-1"]); Assert.True(dialog.SaveButton.IsEnabled);
            Assert.Equal(2, diagram.Points.Count); dialog.NameBox.Text = "Reviewed";
            Assert.True(await dialog.SaveAsync()); Assert.NotNull(dialog.Saved);
            Assert.Equal(2, Assert.Single(StampCategories.Read(fixture.IndexPath).Objects).Connections!.Points.Count);
            Assert.False(await dialog.SaveAsync());
            var generator = new ModuleGenerationWindow(new(64, 64), StampCategories.Read(fixture.IndexPath), null);
            generator.Show();
            try
            {
                generator.CountBox.Text = "4"; await generator.GeneratePreviewAsync();
                Assert.NotNull(generator.Plan); Assert.Equal(4, generator.Plan.Placement.Placements.Count);
                Assert.True(generator.TryAccept());
            }
            finally { generator.Close(); }
        }
        finally { dialog.Close(); }
    }

    [AvaloniaFact]
    public async Task ClosingDuringSaveWaitsUntilTheCatalogueOutcomeIsKnown()
    {
        using var fixture = new CatalogueFixture();
        var dialog = new ModuleCaptureWindow(Draft(), fixture.IndexPath, null); dialog.Show();
        var save = dialog.SaveAsync(); dialog.Close(); await save;
        Assert.False(dialog.IsVisible);
        var entries = StampCategories.Read(fixture.IndexPath).Objects;
        if (dialog.Saved == null) Assert.Empty(entries);
        else Assert.Equal(dialog.Saved.Id, Assert.Single(entries).Id);
        Assert.False(await dialog.SaveAsync());
    }

    [AvaloniaFact]
    public async Task CapturePreviewRendersArtworkAndCanEditDetectedConnections()
    {
        using var fixture = new CatalogueFixture(); using var assets = AssetStore.Load(Path.Combine(TestApplication.Root, "Data"));
        var dialog = new ModuleCaptureWindow(Draft(), fixture.IndexPath, assets); dialog.Show();
        try
        {
            for (int i = 0; i < 100 && assets.Get(1, 0) == null; i++) { await Task.Delay(20); Dispatcher.UIThread.RunJobs(); }
            var preview = dialog.GetVisualDescendants().OfType<MapViewport>().Single(); preview.RefreshAssets();
            await Task.Delay(100); Dispatcher.UIThread.RunJobs();
            using var frame = dialog.CaptureRenderedFrame(); Assert.NotNull(frame);
            using var stream = new MemoryStream(); frame.Save(stream); stream.Position = 0;
            using var image = RasterImage.Load(stream); byte[] pixels = Fixture.Pixels(image);
            Assert.Contains(Enumerable.Range(0, pixels.Length / 4), i => pixels[i * 4] == 80 && pixels[i * 4 + 1] == 160 && pixels[i * 4 + 2] == 20);
            if (Environment.GetEnvironmentVariable("CRYSTALSTUDIO_CAPTURE_SCREENSHOT") is { Length: > 0 } path) frame.Save(path);
            using var handler = Window.WindowOpenedEvent.AddClassHandler<ModuleConnectionsWindow>((editor, _) => Dispatcher.UIThread.Post(() =>
            {
                editor.ConnectionsList.SelectedIndex = 0; editor.WidthBox.Text = "2";
                Assert.True(editor.TryApply()); editor.DialogResult = true;
            }));
            var edit = dialog.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, LocalText.T("Edit module connections…")));
            edit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, edit));
            Assert.Equal(2, dialog.Reviewed.Points[0].Width); Assert.True(dialog.SaveButton.IsEnabled);
            dialog.GetVisualDescendants().OfType<TabControl>().Single().SelectedIndex = 1; Dispatcher.UIThread.RunJobs();
            var diagram = dialog.GetVisualDescendants().OfType<ModuleConnectionCanvas>().Single();
            Assert.Equal(2, diagram.Points[0].Width); Assert.Equal(4, diagram.Points.Count);
            if (Environment.GetEnvironmentVariable("CRYSTALSTUDIO_CAPTURE_SCREENSHOT") is { Length: > 0 } diagramPath)
            { using var diagramFrame = dialog.CaptureRenderedFrame(); diagramFrame!.Save(Path.ChangeExtension(diagramPath, ".connections.png")); }
        }
        finally { dialog.Close(); }
    }

    [AvaloniaFact]
    public async Task CancelledWindowDoesNotWriteAndTheConnectionEditorPreservesTheFootprint()
    {
        using var fixture = new CatalogueFixture(); var draft = Draft();
        var dialog = new ModuleCaptureWindow(draft, fixture.IndexPath, null); dialog.Show(); dialog.Close();
        Assert.False(await dialog.SaveAsync()); Assert.Empty(StampCategories.Read(fixture.IndexPath).Objects);
        var editor = new ModuleConnectionsWindow(draft.Cells, draft.Connections); editor.Show();
        try
        {
            editor.ConnectionsList.SelectedIndex = 0; editor.KindBox.Text = "road";
            Assert.True(editor.TryApply()); Assert.Equal(draft.Connections.Footprint, editor.Result!.Footprint);
            Assert.Equal("road", editor.Result.Points[0].Kind); Assert.Equal("path", draft.Connections.Points[0].Kind);
        }
        finally { editor.Close(); }
    }

    [AvaloniaFact]
    public async Task ToolsMenuRequiresSelectionAndOpensCaptureWithoutEditingTheMap()
    {
        var window = new MainWindow(new AssetHistoryStore(Path.Combine(TestApplication.Root, "capture-assets.json")),
            new RecentMapStore(Path.Combine(TestApplication.Root, "capture-maps.json"))); window.Show();
        try
        {
            for (int i = 0; i < 500 && !window.FindControl<MenuItem>("NewButton")!.IsEnabled; i++)
            { await Task.Delay(20); Dispatcher.UIThread.RunJobs(); }
            var viewport = window.FindControl<MapViewport>("Viewport")!;
            var menu = window.FindControl<MenuItem>("CaptureModuleButton")!; Assert.True(menu.IsEnabled);
            bool opened = false;
            using var handler = Window.WindowOpenedEvent.AddClassHandler<ModuleCaptureWindow>((dialog, _) => Dispatcher.UIThread.Post(() =>
            {
                Assert.Equal(new(0, 0, 8, 8), dialog.Reviewed.Footprint); opened = true; dialog.DialogResult = false;
            }));
            typeof(MapViewport).GetProperty("Selection")!.SetValue(viewport, null);
            menu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, menu)); Assert.False(opened);
            Assert.Equal(LocalText.T("Press M and drag a rectangular map selection, then use Tools → Create module from selection."), window.FindControl<TextBlock>("Status")!.Text);
            viewport.Document.Set(12, 14, new Cell { MiddleImage = 1 }); long revision = viewport.Document.PickupRevision;
            typeof(MapViewport).GetProperty("Selection")!.SetValue(viewport, new Rect(8, 10, 8, 8));
            menu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, menu));
            for (int i = 0; i < 500 && !opened; i++) { await Task.Delay(20); Dispatcher.UIThread.RunJobs(); }
            Assert.True(opened); Assert.Equal(revision, viewport.Document.PickupRevision); Assert.False(viewport.History.Dirty);
        }
        finally { window.Close(); }
    }

    private sealed class CatalogueFixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "CrystalStudio-module-capture-" + Guid.NewGuid().ToString("N"));
        internal string IndexPath => Path.Combine(Root, "catalogue", "palette.json");
        internal CatalogueFixture()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(IndexPath)!);
            File.WriteAllText(IndexPath, "{\"schemaVersion\":1,\"objectRoot\":\"objects\",\"previewRoot\":\"previews\",\"objects\":[],\"extension\":\"keep\"}");
        }
        public void Dispose() => Directory.Delete(Root, true);
    }
}
