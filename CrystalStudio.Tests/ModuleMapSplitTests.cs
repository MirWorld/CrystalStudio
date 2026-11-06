using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace CrystalStudio.Tests;

public sealed class ModuleMapSplitTests
{
    private static MapDocument Map(int width = 16, int height = 16)
    {
        var map = new MapDocument(width, height);
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
            map.Set(x, y, new Cell { MiddleLibrary = 1, MiddleImage = 1, BackLibrary = 1, BackImage = x % 2 == 0 && y % 2 == 0 ? 1 : 0 });
        return map;
    }
    private static ModuleCaptureRequest[] Requests(ModuleSplitResult result) => result.Parts.Where(p => p.Valid)
        .Select(p => new ModuleCaptureRequest(p.Draft!, $"Map-{p.Index}", p.Draft!.Connections, "from-map")).ToArray();

    [Fact]
    public void SplitsTheEntireMapAndPairsSharedEdgesWithoutChangingCells()
    {
        var map = Map(); long revision = map.PickupRevision;
        var result = ModuleMapSplit.Split(map, 8, 8);
        Assert.Equal(4, result.Parts.Count); Assert.All(result.Parts, p => Assert.True(p.Valid, p.Problem?.Message));
        Assert.Equal(256, result.Parts.Sum(p => p.Draft!.Cells.Count));
        var a = result.Parts[0]; var east = a.Draft!.Connections.Points.Single(p => p.Edge == ModuleEdge.East);
        var west = result.Parts[1].Draft!.Connections.Points.Single(p => p.Edge == ModuleEdge.West);
        Assert.True(ModuleJoining.Matches(east, west)); Assert.Equal(6, east.Width); Assert.Equal(1, east.Y);
        Assert.Equal(revision, map.PickupRevision); Assert.Equal(256, map.Cells.Count());
        Assert.NotSame(map.Get(0, 0), a.Draft.Cells[0].Cell);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void SharedOpeningsRespectBothBordersAndBothInwardCells(int kind)
    {
        var map = Map(16, 8);
        map.Set(kind == 2 ? 6 : 7, 3, kind == 1
            ? new Cell { Foreground = new(1, unchecked((short)0x8001)) }
            : new Cell { BackImage = 0x20000000 });
        var result = ModuleMapSplit.Split(map, 8, 8);
        Assert.All(result.Parts, p => Assert.True(p.Valid, p.Problem?.Message));
        var east = result.Parts[0].Draft!.Connections.Points; var west = result.Parts[1].Draft!.Connections.Points;
        Assert.Equal(new[] { 2, 3 }, east.Select(p => p.Width)); Assert.Equal(east.Count, west.Count);
        for (int i = 0; i < east.Count; i++) { Assert.True(ModuleJoining.Matches(east[i], west[i])); Assert.Equal(east[i].Y, west[i].Y); }
    }

    [Fact]
    public void SingleCellRemaindersAreMergedAndNoSourceCellsAreLost()
    {
        var result = ModuleMapSplit.Split(Map(17, 9), 8, 8);
        Assert.Equal(new[] { new ModuleArea(0, 0, 8, 9), new ModuleArea(8, 0, 9, 9) }, result.Parts.Select(p => p.Area));
        Assert.Equal(153, result.Parts.Sum(p => p.Draft!.Cells.Count));
        Assert.All(result.Parts, p => Assert.True(p.Valid, p.Problem?.Message));
    }

    [Fact]
    public void DisconnectedBlocksAreExcludedAndTheirNeighboursLoseOnlyOrphanedPorts()
    {
        var map = Map(24, 8); for (int y = 0; y < 8; y++) map.Set(3, y, new Cell { BackImage = 0x20000000 });
        var result = ModuleMapSplit.Split(map, 8, 8);
        Assert.False(result.Parts[0].Valid); Assert.Equal("connectivity", result.Parts[0].Problem!.Code);
        Assert.True(result.Parts[1].Valid); Assert.True(result.Parts[2].Valid);
        Assert.Equal(ModuleEdge.East, Assert.Single(result.Parts[1].Draft!.Connections.Points).Edge);
        Assert.Equal(ModuleEdge.West, Assert.Single(result.Parts[2].Draft!.Connections.Points).Edge);
    }

    [Fact]
    public void EmptyBlocksAreReportedAndInvalidDimensionsOrExcessiveBlockCountsAreRejected()
    {
        var map = new MapDocument(24, 8); map.Set(1, 1, new Cell { MiddleImage = 1 }); map.Set(9, 1, new Cell { MiddleImage = 1 });
        var result = ModuleMapSplit.Split(map, 8, 8);
        Assert.Equal(2, result.Parts.Count(p => p.Valid)); Assert.Equal("split-empty", result.Parts[2].Problem!.Code);
        Assert.Throws<InvalidDataException>(() => ModuleMapSplit.Split(map, 7, 8));
        Assert.Throws<InvalidDataException>(() => ModuleMapSplit.Split(new(512, 512), 4, 4));
        using var stop = new CancellationTokenSource(); stop.Cancel();
        Assert.Throws<OperationCanceledException>(() => ModuleMapSplit.Split(map, 8, 8, stop.Token));
    }

    [AvaloniaFact]
    public void MapFileBatchSaveThenRandomGenerationSupportsApplyUndoAndSaveReload()
    {
        using var files = new Files(); var original = Map(); string path = Path.Combine(files.Root, "source.map"); original.Save(path);
        byte[] before = File.ReadAllBytes(path); var loaded = MapDocument.Load(path);
        var split = ModuleMapSplit.Split(loaded, 8, 8);
        var saved = ModuleCaptureStore.SaveBatch(files.Index, Requests(split));
        var catalogue = StampCategories.Read(files.Index); Assert.Equal(4, saved.Count);
        Assert.All(saved, s => Assert.Contains("from-map", s.Tags));
        Assert.Equal("keep", JsonNode.Parse(File.ReadAllText(files.Index))!["extension"]!.GetValue<string>());
        var pool = ModuleGeneration.LoadPool(catalogue, saved);
        var plan = ModuleMapGeneration.Generate(pool, new(12, 17, 32, 32, ModuleLayout.Compact));
        Assert.Equal(12, plan.Placement.Placements.Count);
        var viewport = new MapViewport(); viewport.SetDocument(plan.Placement.Source); viewport.ApplyModuleJoin(plan.Placement);
        Assert.Equal(12, viewport.Document.PlacedStamps.Count); viewport.Undo(); Assert.Empty(viewport.Document.Cells);
        viewport.Redo(); Assert.Equal(12, viewport.Document.PlacedStamps.Count);
        string output = Path.Combine(files.Root, "generated.map"); viewport.Document.Save(output);
        Assert.Equal(12, MapDocument.Load(output).PlacedStamps.Count); Assert.Equal(before, File.ReadAllBytes(path));
        Assert.All(plan.Placement.Cells.Where(c => (c.Cell.BackImage & 0x1FFFFFFF) != 0), c => { Assert.Equal(0, c.X % 2); Assert.Equal(0, c.Y % 2); });
    }

    [Theory]
    [InlineData("object")]
    [InlineData("cancel")]
    [InlineData("stale")]
    [InlineData("concurrent")]
    public void BatchFailureRollsBackEveryNewFileAndPreservesExistingData(string failure)
    {
        using var files = new Files(); var map = Map(); var split = ModuleMapSplit.Split(map, 8, 8); var requests = Requests(split);
        var existing = ModuleCaptureStore.SaveBatch(files.Index, requests.Take(1).ToArray());
        byte[] before = File.ReadAllBytes(files.Index); using var stop = new CancellationTokenSource(); int writes = 0;
        Assert.ThrowsAny<Exception>(() => ModuleCaptureStore.SaveBatch(files.Index, requests, stop.Token, stage =>
        {
            if (stage != "object" || ++writes != 2) return;
            if (failure == "object") throw new IOException("injected");
            if (failure == "cancel") stop.Cancel();
            if (failure == "stale") map.Set(0, 0, new Cell { MiddleImage = 99 });
            if (failure == "concurrent") File.AppendAllText(files.Index, " ");
        }));
        var catalogue = StampCategories.Read(files.Index); Assert.Single(catalogue.Objects);
        Assert.Equal(existing[0].Id, catalogue.Objects[0].Id);
        Assert.Single(Directory.GetFiles(catalogue.ObjectRoot, "*.X", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(catalogue.ObjectRoot, "*.tmp", SearchOption.AllDirectories));
        Assert.Equal(failure == "concurrent" ? before.Concat(new byte[] { 32 }).ToArray() : before, File.ReadAllBytes(files.Index));
    }

    [AvaloniaFact]
    public async Task SplitPreviewSupportsCanvasSelectionCheckboxesAndInvalidatesBeforeSaving()
    {
        using var files = new Files(); var source = Map();
        using var assets = AssetStore.Load(Path.Combine(TestApplication.Root, "Data"));
        var window = new ModuleMapSplitWindow(source, "Sample.map", files.Index, assets); window.Show();
        try
        {
            Assert.Equal("8", window.WidthBox.Text); Assert.Equal("8", window.HeightBox.Text);
            window.WidthBox.Text = "8"; window.HeightBox.Text = "8"; await window.SplitAsync(); Dispatcher.UIThread.RunJobs();
            Assert.Equal(4, window.Choices.Count); Assert.True(window.GenerateButton.IsEnabled);
            var click = window.Preview.TranslatePoint(window.Preview.Screen(10.5, 10.5), window)!.Value;
            window.MouseDown(click, MouseButton.Left); window.MouseUp(click, MouseButton.Left);
            Assert.Same(window.Choices[3], window.PartsList.SelectedItem); Assert.True(window.Preview.Regions[3].Selected);
            var check = window.PartsList.GetVisualDescendants().OfType<CheckBox>().First();
            var p = check.TranslatePoint(new Point(check.Bounds.Width / 2, check.Bounds.Height / 2), window)!.Value;
            window.MouseDown(p, MouseButton.Left); window.MouseUp(p, MouseButton.Left);
            Assert.False(window.Choices[0].Selected); Assert.Equal(3, window.Choices.Count(c => c.Selected));
            if (Environment.GetEnvironmentVariable("CRYSTALSTUDIO_SPLIT_SCREENSHOT") is { Length: > 0 } screenshot)
            { await Task.Delay(100); Dispatcher.UIThread.RunJobs(); using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame); frame.Save(screenshot); }
            window.WidthBox.Text = "10"; Assert.Null(window.Result); Assert.False(window.SaveButton.IsEnabled);
            Assert.False(await window.SaveAsync(true)); Assert.Empty(StampCategories.Read(files.Index).Objects);
            window.WidthBox.Text = "8"; await window.SplitAsync();
            Dispatcher.UIThread.RunJobs();
            var savePoint = window.GenerateButton.TranslatePoint(new Point(window.GenerateButton.Bounds.Width / 2, window.GenerateButton.Bounds.Height / 2), window)!.Value;
            window.MouseDown(savePoint, MouseButton.Left); window.MouseUp(savePoint, MouseButton.Left);
            for (int i = 0; i < 500 && window.IsVisible; i++) { await Task.Delay(20); Dispatcher.UIThread.RunJobs(); }
            Assert.False(window.IsVisible); Assert.Equal(3, window.Saved!.Count); Assert.True(window.ContinueToGeneration);
            Assert.False(await window.SaveAsync(true)); Assert.Equal(256, source.Cells.Count());
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ClosingSplitPreviewDoesNotWriteAnyModules()
    {
        using var files = new Files(); var window = new ModuleMapSplitWindow(Map(), "Sample.map", files.Index, null); window.Show();
        window.WidthBox.Text = "8"; window.HeightBox.Text = "8"; await window.SplitAsync(); window.Close();
        Assert.Empty(StampCategories.Read(files.Index).Objects); Assert.Null(window.Saved);
    }

    [AvaloniaFact]
    public async Task SplitErrorsRemainVisibleAndInvalidBlocksCannotBeSelected()
    {
        using var files = new Files(); var map = Map(24, 8);
        for (int y = 0; y < 8; y++) map.Set(3, y, new Cell { BackImage = 0x20000000 });
        var window = new ModuleMapSplitWindow(map, "Sample.map", files.Index, null); window.Show();
        try
        {
            window.WidthBox.Text = "8"; window.HeightBox.Text = "8"; await window.SplitAsync();
            Assert.False(window.Choices[0].Part.Valid); window.Choices[0].Selected = true; Assert.False(window.Choices[0].Selected);
            window.WidthBox.Text = "7"; await window.SplitAsync();
            Assert.Null(window.Result); Assert.False(window.SaveButton.IsEnabled);
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == LocalText.T("Use even block dimensions from 4 to 512 cells."));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ClosingWhileSavingWaitsForAnAtomicOutcomeAndDoesNotContinueGeneration()
    {
        using var files = new Files(); var window = new ModuleMapSplitWindow(Map(), "Sample.map", files.Index, null); window.Show();
        await window.SplitAsync();
        var saving = window.SaveAsync(true); window.Close(); await saving;
        Assert.False(window.IsVisible); Assert.False(window.ContinueToGeneration);
        int count = StampCategories.Read(files.Index).Objects.Count;
        Assert.True(count is 0 or 4);
        Assert.Equal(count, Directory.GetFiles(Path.Combine(files.Root, "Objects"), "*.X", SearchOption.AllDirectories).Length);
        Assert.Empty(Directory.GetFiles(Path.Combine(files.Root, "Objects"), "*.tmp", SearchOption.AllDirectories));
    }

    private sealed class Files : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "CrystalStudio-split-" + Guid.NewGuid().ToString("N"));
        internal string Index => Path.Combine(Root, "catalogue", "palette.json");
        internal Files()
        {
            Directory.CreateDirectory(Path.Combine(Root, "catalogue")); Directory.CreateDirectory(Path.Combine(Root, "Objects"));
            File.WriteAllText(Index, "{\"schemaVersion\":1,\"objectRoot\":\"Objects\",\"previewRoot\":\"catalogue\",\"objects\":[],\"extension\":\"keep\"}");
        }
        public void Dispose() => Directory.Delete(Root, true);
    }
}
