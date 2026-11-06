using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Threading;
using CrystalStudio.Rendering;
using System.Security.Cryptography;
using System.Text.Json;

namespace CrystalStudio.Tests;

public sealed class ModuleChannelSplitTests
{
    private static MapDocument Maze(string shape)
    {
        var map = new MapDocument(64, 64);
        for (int y = 0; y < 64; y++) for (int x = 0; x < 64; x++)
        {
            bool h = y is >= 28 and <= 33, v = x is >= 28 and <= 33;
            bool open = shape switch
            {
                "straight" => h,
                "L" => h && x < 34 || v && y >= 28,
                "T" => h || v && y >= 28,
                "cross" => h || v,
                "S" => (y is >= 12 and <= 17 && x <= 33) || v && y is >= 12 and <= 49 || (y is >= 44 and <= 49 && x >= 28),
                "parallel" => y is >= 12 and <= 17 or >= 44 and <= 49,
                "ring" => x is >= 12 and <= 49 && y is >= 12 and <= 49 && !(x is >= 18 and <= 43 && y is >= 18 and <= 43),
                "island" => h || x == 19 && y == 19,
                "dead-end" => h && x <= 45,
                _ => h && x < 32 || y is >= 24 and <= 39 && x >= 32
            };
            map.Set(x, y, new Cell { BackLibrary = 1, BackImage = (open ? 0 : 0x20000000) | 1, MiddleImage = open ? (ushort)1 : (ushort)0 });
        }
        return map;
    }
    [Theory]
    [InlineData("straight")]
    [InlineData("L")]
    [InlineData("T")]
    [InlineData("cross")]
    [InlineData("S")]
    [InlineData("parallel")]
    [InlineData("ring")]
    [InlineData("island")]
    [InlineData("dead-end")]
    [InlineData("width")]
    public void CorridorPartitionsPreserveEveryCellAndEveryRetainedPortHasAPartner(string shape)
    {
        var map = Maze(shape); long revision = map.PickupRevision;
        var result = ModuleChannelSplit.Split(map, 24, 24);
        Assert.InRange(result.Parts.Count, 2, 256); Assert.Contains(result.Parts, p => p.Valid);
        var covered = new HashSet<(int, int)>();
        foreach (var part in result.Parts)
        {
            for (int y = part.Area.Y; y < part.Area.Y + part.Area.Height; y++)
                for (int x = part.Area.X; x < part.Area.X + part.Area.Width; x++) Assert.True(covered.Add((x, y)));
            if (part.Draft is not { } draft) continue;
            var footprint = draft.Connections.Footprint!;
            foreach (var cell in draft.Cells)
                Assert.True(cell.Cell.SameContents(map.Get(cell.X - footprint.X + part.Area.X, cell.Y - footprint.Y + part.Area.Y)));
            if (!part.Valid) continue;
            ModuleGeneration.ValidateModule(new("part", draft.Cells, draft.Connections));
            foreach (var p in draft.Connections.Points)
            {
                int px = p.X - footprint.X + part.Area.X, py = p.Y - footprint.Y + part.Area.Y;
                var (dx, dy) = p.Edge switch { ModuleEdge.North => (0, -1), ModuleEdge.South => (0, 1), ModuleEdge.East => (1, 0), _ => (-1, 0) };
                Assert.Contains(result.Parts.Where(other => other != part && other.Valid), other => other.Draft!.Connections.Points.Any(q =>
                    ModuleJoining.Matches(p, q) && q.X - other.Draft.Connections.Footprint!.X + other.Area.X == px + dx && q.Y - other.Draft.Connections.Footprint.Y + other.Area.Y == py + dy));
            }
        }
        Assert.Equal(4096, covered.Count); Assert.Equal(revision, map.PickupRevision);
    }

    [Fact]
    public void ProtectionCancellationAndPartitionGeometryAreEnforced()
    {
        var map = Maze("T"); ModuleArea junction = new(22, 22, 20, 20);
        var result = ModuleChannelSplit.Split(map, 24, 24, [junction]);
        Assert.Single(result.Parts, p => ModulePartitions.Intersects(p.Area, junction));
        Assert.Throws<InvalidDataException>(() => ModulePartitions.Cut(new(0, 0, 64, 64), true, 32, [junction]));
        Assert.Throws<InvalidDataException>(() => ModulePartitions.Cut(new(0, 0, 8, 8), true, 3));
        Assert.Throws<InvalidDataException>(() => ModulePartitions.Merge(new(0, 0, 8, 8), new(8, 0, 8, 4)));
        Assert.Throws<InvalidDataException>(() => ModuleMapSplit.Build(map, [new(0, 0, 32, 64)]));
        Assert.Throws<InvalidDataException>(() => ModuleMapSplit.Build(map, [new(0, 0, 32, 64), new(30, 0, 34, 64)]));
        using var stop = new CancellationTokenSource(); stop.Cancel();
        Assert.Throws<OperationCanceledException>(() => ModuleChannelSplit.Split(map, 24, 24, token: stop.Token));
    }

    private static Point At(ModuleMapSplitWindow window, double x, double y) => window.Preview.TranslatePoint(window.Preview.Screen(x, y), window)!.Value;
    private static void Click(ModuleMapSplitWindow window, Control control)
    {
        var p = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(p, MouseButton.Left); window.MouseUp(p, MouseButton.Left); Dispatcher.UIThread.RunJobs();
    }
    private static async Task Settle(ModuleMapSplitWindow window)
    {
        for (int i = 0; i < 500 && !window.ToolBox.IsEnabled; i++) { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); }
        Dispatcher.UIThread.RunJobs();
    }
    private static byte[] Screenshot(ModuleMapSplitWindow window, string name)
    {
        Dispatcher.UIThread.RunJobs(); using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame);
        if (Environment.GetEnvironmentVariable("CRYSTALSTUDIO_MODULE_UX_ARTIFACTS") is { Length: > 0 } directory)
        { Directory.CreateDirectory(directory); frame.Save(Path.Combine(directory, name + ".png")); }
        using var stream = new MemoryStream(); frame.Save(stream); return stream.ToArray();
    }

    [AvaloniaFact]
    public async Task MouseCutsMoveMergeProtectionUndoAndStaleFeedbackAreReliable()
    {
        var map = new MapDocument(24, 16);
        for (int y = 0; y < map.Height; y++) for (int x = 0; x < map.Width; x++) map.Set(x, y, new Cell { BackImage = 1 });
        var window = new ModuleMapSplitWindow(map, "editing.map", "unused.json", null); window.Show();
        try
        {
            window.WidthBox.Text = "12"; window.HeightBox.Text = "16"; await window.SplitAsync(); Dispatcher.UIThread.RunJobs();
            Assert.Equal(2, window.Choices.Count); window.Choices[1].Selected = false;
            var before = Screenshot(window, "channel-before");
            window.ToolBox.SelectedIndex = 1; window.MouseMove(At(window, 6, 6));
            Assert.NotNull(window.Preview.CutPreview); Assert.Equal(2, window.Preview.CutOpenings.Count); Assert.True(ModuleJoining.Matches(window.Preview.CutOpenings[0], window.Preview.CutOpenings[1])); Assert.False(window.SaveButton.IsEnabled);
            var ghost = Screenshot(window, "channel-cut-draft"); Assert.False(before.SequenceEqual(ghost));
            window.MouseDown(At(window, 8, 6), MouseButton.Left); window.MouseMove(At(window, 6, 6), RawInputModifiers.LeftMouseButton); window.MouseUp(At(window, 6, 6), MouseButton.Left);
            await Settle(window); Assert.Equal(3, window.Choices.Count); Assert.Null(window.Preview.CutPreview);
            Assert.False(window.Choices.Single(c => c.Part.Area.X == 12).Selected);
            Screenshot(window, "channel-cut-applied");
            Click(window, window.UndoButton); Assert.Equal(2, window.Choices.Count);
            Click(window, window.RedoButton); Assert.Equal(3, window.Choices.Count);
            window.PartsList.Focus(); window.KeyPressQwerty(PhysicalKey.Z, RawInputModifiers.Control); window.KeyReleaseQwerty(PhysicalKey.Z, RawInputModifiers.Control);
            Assert.Equal(2, window.Choices.Count);
            window.KeyPressQwerty(PhysicalKey.Y, RawInputModifiers.Control); window.KeyReleaseQwerty(PhysicalKey.Y, RawInputModifiers.Control);
            Assert.Equal(3, window.Choices.Count);
            window.NameBox.Focus(); window.KeyPressQwerty(PhysicalKey.Z, RawInputModifiers.Control); window.KeyReleaseQwerty(PhysicalKey.Z, RawInputModifiers.Control);
            Assert.Equal(3, window.Choices.Count); window.NameBox.Text = "editing";
            window.ToolBox.SelectedIndex = 3;
            window.MouseDown(At(window, 6, 6), MouseButton.Left); window.MouseMove(At(window, 8, 6), RawInputModifiers.LeftMouseButton); window.MouseUp(At(window, 8, 6), MouseButton.Left);
            await Settle(window); Assert.Contains(window.Choices, c => c.Part.Area == new ModuleArea(0, 0, 8, 16));
            window.ToolBox.SelectedIndex = 4;
            window.MouseDown(At(window, 2, 6), MouseButton.Left); window.MouseUp(At(window, 2, 6), MouseButton.Left);
            window.MouseDown(At(window, 10, 6), MouseButton.Left); window.MouseUp(At(window, 10, 6), MouseButton.Left);
            await Settle(window); Assert.Equal(2, window.Choices.Count);
            window.ToolBox.SelectedIndex = 5;
            window.MouseDown(At(window, 8, 10), MouseButton.Left); window.MouseMove(At(window, 3, 3), RawInputModifiers.LeftMouseButton); window.MouseUp(At(window, 3, 3), MouseButton.Left);
            Assert.Single(window.Preview.ProtectedRegions);
            window.ToolBox.SelectedIndex = 1;
            window.MouseDown(At(window, 6, 6), MouseButton.Left); window.MouseUp(At(window, 6, 6), MouseButton.Left);
            Assert.True(window.Preview.CutPreview?.Invalid); Assert.False(window.SaveButton.IsEnabled);
            Screenshot(window, "channel-cut-rejected");
            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None); window.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Assert.Null(window.Preview.CutPreview); Assert.True(window.SaveButton.IsEnabled);
            window.Preview.Viewport.ZoomPreview(window.Preview.Screen(6, 6), 1.3); window.Preview.Viewport.PanPreview(new(10, -5));
            window.MouseDown(At(window, 16, 6), MouseButton.Left); window.MouseUp(At(window, 16, 6), MouseButton.Left);
            Assert.Equal(12, ((ModuleSplitChoice)window.PartsList.SelectedItem!).Part.Area.X);
            window.WidthBox.Text = "10"; Assert.Null(window.Result); Assert.Equal(2, window.Preview.Regions.Count);
            Assert.False(window.SaveButton.IsEnabled); Assert.False(window.UndoButton.IsEnabled);
            Assert.Equal(384, map.Cells.Count()); Assert.False(window.Preview.Viewport.History.Dirty);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task FailedTopologyCutStaysRedUntilEscapeAndCaptureLossCancelsDrag()
    {
        var window = new ModuleMapSplitWindow(Maze("L"), "L.map", "unused.json", null); window.Show();
        try
        {
            window.WidthBox.Text = "32"; window.HeightBox.Text = "64"; await window.SplitAsync(); Dispatcher.UIThread.RunJobs();
            window.ToolBox.SelectedIndex = 2;
            window.MouseDown(At(window, 10, 6), MouseButton.Left); window.MouseUp(At(window, 10, 6), MouseButton.Left); await Settle(window);
            Assert.True(window.Preview.CutPreview?.Invalid); Assert.False(window.SaveButton.IsEnabled);
            Assert.Equal(2, window.Choices.Count);
            window.ToolBox.SelectedIndex = 1;
            window.MouseDown(At(window, 10, 10), MouseButton.Left);
            window.MouseMove(At(window, 12, 10), RawInputModifiers.LeftMouseButton);
            // Releasing capture through the real pointer must cancel, not apply, the drag.
            var overlay = window.Preview.Children.Last();
            overlay.RaiseEvent(new PointerCaptureLostEventArgs(overlay, new Pointer(42, PointerType.Mouse, true)));
            Assert.Null(window.Preview.CutPreview); Assert.Equal(2, window.Choices.Count);
        }
        finally { window.Close(); }
    }

    private static void SaveRaster(RasterImage image, string path) { using var stream = File.Create(path); image.Save(stream); }

    [AvaloniaFact]
    public async Task SourceMapBatchAcceptanceSupportsGenerationAndRenderedRoundTrip()
    {
        string root = Path.Combine(Path.GetTempPath(), "CrystalStudio-channel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "catalogue")); Directory.CreateDirectory(Path.Combine(root, "Objects"));
        string index = Path.Combine(root, "catalogue", "palette.json");
        File.WriteAllText(index, "{\"schemaVersion\":1,\"objectRoot\":\"Objects\",\"previewRoot\":\"catalogue\",\"objects\":[]}");
        string? real = Environment.GetEnvironmentVariable("CRYSTALSTUDIO_REAL_SPLIT_MAP");
        string input = real ?? Path.Combine(root, "source.map");
        if (real == null) Maze("cross").Save(input);
        var format = real == null ? AssetSourceFormat.Default : AssetSourceFormat.Sanxiu;
        var map = MapDocument.Load(input, format); var before = SHA256.HashData(File.ReadAllBytes(input)); long revision = map.PickupRevision;
        string? artifact = Environment.GetEnvironmentVariable("CRYSTALSTUDIO_MODULE_UX_ARTIFACTS");
        var store = new SettingsStore(); var savedSettings = store.Load(); store.Save(savedSettings with { Language = "zh-CN" }); LocalText.Initialize(store);
        using var assets = AssetStore.Load(Environment.GetEnvironmentVariable("CRYSTALSTUDIO_REAL_SPLIT_ASSETS") ?? Path.Combine(TestApplication.Root, "Data"), format);
        var window = new ModuleMapSplitWindow(map, input, index, assets); window.Show();
        try
        {
            window.ModeBox.SelectedIndex = 1; window.WidthBox.Text = "48"; window.HeightBox.Text = "32";
            await window.SplitAsync(); Assert.NotNull(window.Result);
            var result = window.Result!; var valid = result.Parts.Where(p => p.Valid).ToArray(); Assert.True(valid.Length >= 2);
            // Resolve the actual artwork, then compare a captured rectangle with its saved/reloaded .X.
            var sample = valid.MaxBy(p => p.Draft!.Cells.Count)!; var draft = sample.Draft!;
            var prepared = ModuleCapture.Prepare(draft, "sample", draft.Connections);
            string stamp = Path.Combine(root, "sample.X"); CellCodec.SaveStamp(stamp, prepared.Cells);
            var reload = CellCodec.LoadStamp(stamp); var fp = draft.Connections.Footprint!;
            using var originalImage = await Task.Run(() => StampPreview.Create(draft.Cells, assets, 640, 440, waitForAssets: true, mapGroundGrid: true,
                area: new Rect(fp.X * 48, fp.Y * 32, fp.Width * 48, fp.Height * 32)));
            var moved = prepared.Connections!.Footprint!;
            using var reloadedImage = await Task.Run(() => StampPreview.Create(reload, assets, 640, 440, waitForAssets: true, mapGroundGrid: true,
                area: new Rect(moved.X * 48, moved.Y * 32, moved.Width * 48, moved.Height * 32)));
            Assert.NotNull(originalImage); Assert.NotNull(reloadedImage); Assert.Equal(Fixture.Pixels(originalImage), Fixture.Pixels(reloadedImage));
            Assert.Contains(Fixture.Pixels(originalImage).Where((_, n) => n % 4 == 3), alpha => alpha != 0);
            if (artifact != null) { SaveRaster(originalImage, Path.Combine(artifact, "channel-source-module.png")); SaveRaster(reloadedImage, Path.Combine(artifact, "channel-reloaded-module.png")); }
            var allCells = map.Cells.Select(c => new StampCell(c.Key % map.Width, c.Key / map.Width, c.Value)).ToArray();
            using var full = await Task.Run(() => StampPreview.Create(allCells, assets, 1000, 760, waitForAssets: true, mapGroundGrid: true));
            window.Preview.Viewport.RefreshAssets(); await Task.Delay(150); Dispatcher.UIThread.RunJobs();
            Screenshot(window, "channel-real-map-preview");
            // Exercise the real selection gesture, then save exactly the remaining checked candidates.
            var first = valid[0]; var click = At(window, first.Area.X + first.Area.Width / 2.0, first.Area.Y + first.Area.Height / 2.0);
            window.MouseDown(click, MouseButton.Left); window.MouseUp(click, MouseButton.Left);
            Assert.Equal(first.Area, ((ModuleSplitChoice)window.PartsList.SelectedItem!).Part.Area);
            Assert.True(await window.SaveAsync(true)); Assert.Equal(valid.Length, window.Saved!.Count);
            var catalogue = StampCategories.Read(index); var pool = ModuleGeneration.LoadPool(catalogue, window.Saved);
            var plan = await Task.Run(() => ModuleMapGeneration.Generate(pool, new(12, 17, 64, 64, ModuleLayout.Compact)));
            Assert.Equal(12, plan.Placement.Placements.Count);
            var viewport = new MapViewport(); viewport.SetDocument(plan.Placement.Source); viewport.ApplyModuleJoin(plan.Placement);
            viewport.Undo(); Assert.Empty(viewport.Document.Cells); viewport.Redo(); Assert.Equal(12, viewport.Document.PlacedStamps.Count);
            string output = Path.Combine(root, "generated.map"); viewport.Document.Save(output);
            var loaded = MapDocument.Load(output, format); Assert.Equal(12, loaded.PlacedStamps.Count);
            for (int y = 0; y < loaded.Height; y++) for (int x = 0; x < loaded.Width; x++)
                Assert.Equal(ModuleTerrain.Blocked(viewport.Document.Get(x, y)), ModuleTerrain.Blocked(loaded.Get(x, y)));
            using var generatedPreview = new ModuleConnectionCanvas([], new(0, 0, loaded.Width - 1, loaded.Height - 1), assets);
            generatedPreview.SetMap(loaded, new(0, 0, loaded.Width - 1, loaded.Height - 1));
            var generatedWindow = new Window { Width = 1100, Height = 800, Content = generatedPreview }; generatedWindow.Show();
            try
            {
                var cells = loaded.Cells.Select(c => new StampCell(c.Key % loaded.Width, c.Key / loaded.Width, c.Value)).ToArray();
                using var rendered = await Task.Run(() => StampPreview.Create(cells, assets, 1000, 720, waitForAssets: true, mapGroundGrid: true));
                var expectedCells = viewport.Document.Cells.Select(c => new StampCell(c.Key % viewport.Document.Width, c.Key / viewport.Document.Width, c.Value)).ToArray();
                using var expectedImage = await Task.Run(() => StampPreview.Create(expectedCells, assets, 1000, 720, waitForAssets: true, mapGroundGrid: true));
                Assert.Equal(Fixture.Pixels(expectedImage!), Fixture.Pixels(rendered!));
                if (artifact != null) SaveRaster(rendered!, Path.Combine(artifact, "channel-generated-map.png"));
            }
            finally { generatedWindow.Close(); }
            Assert.Equal(revision, map.PickupRevision); Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(input)));
            if (artifact != null)
            {
                var analysis = ModuleTerrain.Analyze(map);
                File.WriteAllText(Path.Combine(artifact, "channel-acceptance.json"), JsonSerializer.Serialize(new
                {
                    input, map.Width, map.Height, hash = Convert.ToHexString(before), components = analysis.Regions,
                    total = result.Parts.Count, usable = valid.Length, saved = window.Saved.Count,
                    generated = plan.Placement.Placements.Count, generatedWidth = loaded.Width, generatedHeight = loaded.Height,
                    unresolved = result.Parts.Where(p => !p.Valid).Select(p => new { p.Index, p.Area, p.Problem }),
                    sourceUnchanged = true, renderRoundTripEqual = true, undoRedoPassed = true
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
        }
        finally { window.Close(); store.Save(savedSettings); LocalText.Initialize(store); Directory.Delete(root, true); }
    }
}
