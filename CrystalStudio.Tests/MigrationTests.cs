using System.IO.Compression;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CrystalStudio.Rendering;
using Path = System.IO.Path;

[assembly: AvaloniaTestApplication(typeof(CrystalStudio.Tests.TestApplication))]

namespace CrystalStudio.Tests;

public static class TestApplication
{
    public static readonly string Root = Path.Combine(Path.GetTempPath(), "CrystalStudio-Avalonia-" + Guid.NewGuid().ToString("N"));
    public static AppBuilder BuildAvaloniaApp()
    {
        Directory.CreateDirectory(Root);
        Environment.SetEnvironmentVariable("CRYSTALSTUDIO_PROFILE", Path.Combine(Root, "profile"));
        Fixture.CreateLibraries(Path.Combine(Root, "Data"));
        new SettingsStore().Save(new LibrarySettings(Path.Combine(Root, "Data")));
        return AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
    }
}

internal static class Fixture
{
    public static void CreateLibraries(string root)
    {
        string directory = Path.Combine(root, "Map", "WemadeMir2"); Directory.CreateDirectory(directory);
        WriteLibrary(Path.Combine(directory, "Tiles.lib"), 96, 64, 20, 80, 160);
        WriteLibrary(Path.Combine(directory, "Smtiles.lib"), 48, 32, 80, 160, 20);
        WriteLibrary(Path.Combine(directory, "Objects.lib"), 48, 96, 160, 20, 80);
    }
    public static void WriteLibrary(string path, int width, int height, byte b, byte g, byte r)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = b; pixels[i + 1] = g; pixels[i + 2] = r; pixels[i + 3] = 255; }
        using var compressed = new MemoryStream();
        using (var gzip = new GZipStream(compressed, CompressionLevel.SmallestSize, true)) gzip.Write(pixels);
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write(3); writer.Write(1); writer.Write(0); writer.Write(16);
        writer.Write((short)width); writer.Write((short)height); writer.Write((short)0); writer.Write((short)0);
        writer.Write((short)0); writer.Write((short)0); writer.Write((byte)0);
        writer.Write((int)compressed.Length); writer.Write(compressed.ToArray());
    }
    public static byte[] Pixels(RasterImage image)
    {
        byte[] bytes = new byte[image.PixelWidth * image.PixelHeight * 4]; image.CopyPixels(bytes, image.PixelWidth * 4, 0); return bytes;
    }
}

public sealed class MigrationTests
{
    [AvaloniaFact]
    public async Task CompactSliderKeepsKeyboardAndPointerValueChanges()
    {
        var slider = new Slider { Minimum = 3, Maximum = 31, Value = 3, TickFrequency = 2, IsSnapToTickEnabled = true, Width = 100, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top };
        var window = new Window { Width = 150, Height = 80, Content = slider }; window.Show();
        try
        {
            await Task.Delay(30); Dispatcher.UIThread.RunJobs();
            Assert.Equal(24, slider.Bounds.Height);
            slider.Focus(); window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None); window.KeyReleaseQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
            Assert.Equal(5, slider.Value);
            window.MouseDown(new Point(85, 12), MouseButton.Left); window.MouseUp(new Point(85, 12), MouseButton.Left);
            Assert.True(slider.Value > 5, string.Join("; ", slider.GetVisualDescendants().OfType<Control>().Select(c => $"{c.GetType().Name}#{c.Name}={c.Bounds}")) + $" Value={slider.Value}, LargeChange={slider.LargeChange}");
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task CategoryGroupsStayCompactAndExpandNativeChildItems()
    {
        var root = new StampCategoryNode { Label = "Group", Populated = true };
        root.Children.Add(new StampCategoryNode { Label = "Child", Populated = true });
        var tree = new TreeView
        {
            ItemsSource = new[] { root },
            ItemTemplate = (Avalonia.Markup.Xaml.Templates.TreeDataTemplate)Application.Current!.FindResource("StampCategoryTemplate")!,
            ItemContainerTheme = (Avalonia.Styling.ControlTheme)Application.Current.FindResource("StampCategoryItem")!
        };
        var window = new Window { Width = 300, Height = 240, Content = tree }; window.Show();
        try
        {
            await Task.Delay(40); Dispatcher.UIThread.RunJobs();
            var container = Assert.IsType<TreeViewItem>(tree.ContainerFromIndex(0));
            Assert.InRange(container.Bounds.Height, 18, 28);
            container.IsExpanded = true;
            await Task.Delay(40); Dispatcher.UIThread.RunJobs();
            Assert.IsType<TreeViewItem>(container.ContainerFromIndex(0));
            Assert.Contains(tree.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "Child");
            Assert.True(container.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>().First().IsChecked);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task CancelButtonClosesModalAndReturnsFalse()
    {
        var owner = new EditorWindow(); owner.Show();
        var button = new Button { Content = "Cancel", IsCancel = true };
        var dialog = new EditorWindow { Content = button };
        var completion = ((Window)dialog).ShowDialog<bool?>(owner);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.False(dialog.IsVisible); Assert.False(await completion);
        owner.Close();
    }

    [AvaloniaFact]
    public async Task CompactTemplatesRetainOriginalToolLayerAndPaletteDimensions()
    {
        var window = new MainWindow(); window.Show();
        try
        {
            for (int i = 0; i < 30; i++) { await Task.Delay(20); Dispatcher.UIThread.RunJobs(); }
            var tools = window.FindControl<ResponsivePanel>("ToolPanel")!.Children.OfType<RadioButton>().ToArray();
            Assert.Equal(10, tools.Length);
            Assert.All(tools, tool => { Assert.Equal(32, tool.Bounds.Width); Assert.Equal(32, tool.Bounds.Height); });
            var eye = window.FindControl<CheckBox>("GroundVisible")!;
            Assert.Equal(22, eye.Bounds.Width); Assert.Equal(22, eye.Bounds.Height);
            Assert.Contains(eye.GetVisualDescendants(), visual => visual is Avalonia.Controls.Shapes.Path { Name: "Eye" });
            var list = window.FindControl<ListBox>("LayerList")!;
            Assert.All(list.Items.OfType<ListBoxItem>(), row => Assert.InRange(row.Bounds.Height, 25, 28));
            var paletteTabs = window.GetVisualDescendants().OfType<TabControl>().Single(t => t.Items.Count == 4);
            Assert.All(paletteTabs.Items.OfType<TabItem>(), tab => { Assert.Equal(11, tab.FontSize); Assert.InRange(tab.Bounds.Height, 25, 32); });
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task HoverKeepsMapAndMinimapRetainedWhilePanRefreshesThem()
    {
        ThemeService.Initialize();
        using var assets = AssetStore.Load(Path.Combine(TestApplication.Root, "Data"));
        var viewport = new MapViewport { Assets = assets };
        var document = new MapDocument(64, 64);
        for (int y = 0; y < 64; y += 2) for (int x = 0; x < 64; x += 2)
            document.Set(x, y, new Cell { BackLibrary = 0, BackImage = 1 });
        viewport.SetDocument(document);
        var window = new Window { Width = 800, Height = 600, Content = viewport };
        window.Show();
        try
        {
            for (int i = 0; i < 30; i++) { await Task.Delay(20); Dispatcher.UIThread.RunJobs(); }
            viewport.RefreshAssets(); await Task.Delay(40); Dispatcher.UIThread.RunJobs();
            int builds = viewport.MapSceneBuilds, submissions = viewport.MapRenderSubmissions, cameras = viewport.MapCameraUpdates, recordings = viewport.MapWorldRecordings, minimaps = 0;
            byte[] CapturePixels()
            {
                using var frame = window.CaptureRenderedFrame()!; using var stream = new MemoryStream();
                frame.Save(stream); stream.Position = 0; using var image = RasterImage.Load(stream);
                return Fixture.Pixels(image);
            }
            var beforePan = CapturePixels();
            Assert.Equal(new byte[] { 20, 80, 160, 255 }, beforePan.AsSpan((100 * 800 + 75) * 4, 4).ToArray());
            viewport.MinimapInvalidated += () => minimaps++;
            for (int i = 0; i < 80; i++)
            {
                window.MouseMove(new Point(100 + i * 3, 150 + i));
                Dispatcher.UIThread.RunJobs();
            }
            await Task.Delay(40); Dispatcher.UIThread.RunJobs();
            Assert.Equal(builds, viewport.MapSceneBuilds);
            Assert.Equal(submissions, viewport.MapRenderSubmissions);
            Assert.Equal(0, minimaps);
            viewport.PanBy(new Vector(30, 20));
            await Task.Delay(40); Dispatcher.UIThread.RunJobs();
            Assert.True(viewport.MapSceneBuilds > builds);
            Assert.True(viewport.MapCameraUpdates > cameras);
            Assert.Equal(recordings, viewport.MapWorldRecordings);
            Assert.True(minimaps > 0);
            var afterPan = CapturePixels();
            Assert.False(beforePan.AsSpan((100 * 800 + 75) * 4, 4).SequenceEqual(afterPan.AsSpan((100 * 800 + 75) * 4, 4)));
        }
        finally { viewport.StopBackgroundWork(); window.Close(); }
    }

    [Fact]
    public void AtlasBatchesPreserveTransparencyPainterOrderAndIndependentSpriteScaling()
    {
        using var atlas = RasterImage.Create(2, 2, 96, 96, RasterFormats.Bgra32, null,
            [0, 0, 255, 255, 0, 255, 0, 128, 255, 0, 0, 200, 255, 255, 255, 0], 8);
        var sprites = Enumerable.Range(0, 8200).Select(i =>
            (Source: new Rect(i % 2, (i / 2) % 2, 1, 1), Destination: new Rect(i % 101, (i / 101) % 83, 3, 5))).ToArray();
        var expected = new SceneVisual(); var actual = new SceneVisual();
        foreach (var s in sprites) expected.Drawing.Children.Add(new SceneImage(atlas, s.Destination, s.Source));
        actual.Drawing.Children.Add(new SceneAtlasBatch(atlas, sprites));
        var a = new RasterTarget(110, 90, 96, 96, RasterFormats.Pbgra32); a.Render(expected);
        var b = new RasterTarget(110, 90, 96, 96, RasterFormats.Pbgra32); b.Render(actual);
        Assert.Equal(Fixture.Pixels(a), Fixture.Pixels(b));
    }

    [Fact]
    public void OverviewRasterCachePreservesPixelsAtPhysicalResolution()
    {
        var scene = new SceneGroup();
        scene.Children.Add(new SceneRectangle(new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Color.FromRgb(20, 40, 80)), null, new Rect(-128, -128, 1024, 1024)));
        scene.Children.Add(new SceneRectangle(new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Color.FromArgb(128, 220, 40, 10)), null, new Rect(0, 0, 256, 128)));
        using var recorder = new SkiaSharp.SKPictureRecorder();
        scene.Raster(recorder.BeginRecording(new SkiaSharp.SKRect(-128, -128, 896, 896)));
        var recording = new RecordedPicture(recorder.EndRecording(), new Rect(-128, -128, 1024, 1024));
        try
        {
            foreach (float scale in new[] { .125f, .25f, .5f })
            {
                using var expected = new SkiaSharp.SKBitmap(180, 160, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Premul);
                using var actual = new SkiaSharp.SKBitmap(expected.Info);
                using (var dc = new SkiaSharp.SKCanvas(expected)) { dc.Clear(); dc.Translate(20, 10); dc.Scale(scale); dc.DrawPicture(recording.Picture); }
                using (var dc = new SkiaSharp.SKCanvas(actual)) { dc.Clear(); dc.Translate(20, 10); dc.Scale(scale); recording.Draw(dc, null); }
                Assert.Equal(expected.Bytes, actual.Bytes);
            }
        }
        finally { recording.Release(); }
    }

    [Fact]
    public void MapAndStampRoundTripsPreserveArtworkFlagsAndMetadata()
    {
        string directory = Path.Combine(Path.GetTempPath(), "crystal-roundtrip-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            var document = new MapDocument(17, 23);
            var cell = new Cell { BackLibrary = 2, BackImage = 0x60000021, MiddleLibrary = 4, MiddleImage = 123,
                Foreground = new Front(5, 321, 0x83, 2), Door = 1, DoorOffset = 5, MiddleFrames = 3,
                MiddleTick = 2, TileAnimation = 7, TileOffset = 8, TileFrames = 9, Light = 3 };
            document.Set(4, 6, cell);
            string path = Path.Combine(directory, "test.map"); document.Save(path);
            var loaded = MapDocument.Load(path);
            Assert.Equal((17, 23), (loaded.Width, loaded.Height)); Assert.True(cell.SameContents(loaded.Get(4, 6)), $"Expected {cell}; actual {loaded.Get(4, 6)}");
            var stamp = new[] { new StampCell(-2, 4, cell) }; path = Path.Combine(directory, "test.X"); CellCodec.SaveStamp(path, stamp);
            var actual = Assert.Single(CellCodec.LoadStamp(path)); Assert.Equal((-2, 4), (actual.X, actual.Y)); Assert.True(cell.SameContents(actual.Cell));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void UndoRedoAndSavedStateRetainCells()
    {
        var document = new MapDocument(16, 16); var history = new EditHistory();
        history.Record(document, 2, 3); document.Set(2, 3, new Cell { MiddleImage = 15 }); history.Commit();
        Assert.True(history.Dirty); history.Saved(); Assert.False(history.Dirty);
        history.Undo(document, (_, _) => { }); Assert.Equal(0, document.Get(2, 3).MiddleImage); Assert.True(history.Dirty);
        history.Redo(document, (_, _) => { }); Assert.Equal(15, document.Get(2, 3).MiddleImage); Assert.False(history.Dirty);
    }

    [AvaloniaFact]
    public async Task WorkerRasterizationPreservesAlphaAdditiveBlendingAndCropping()
    {
        var actual = await Task.Run(() =>
        {
            var pixel = RasterImage.Create(1, 1, 96, 96, RasterFormats.Bgra32, null, [100, 50, 20, 128], 4);
            var behind = new SceneGroup();
            behind.Children.Add(new SceneRectangle(new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Color.FromRgb(10, 20, 30)), null,
                new Rect(0, 0, 1, 1)));
            var additive = MapSpriteDrawing.Additive(new Sprite(pixel, 0, 0), new Rect(0, 0, 1, 1), behind);
            return Fixture.Pixels(additive);
        });
        Assert.Equal(new byte[] { 80, 45, 20, 255 }, actual);
    }

    [AvaloniaFact]
    public async Task OverviewAndMinimapExportKeepLayerOrderAndOpaquePng()
    {
        using var assets = AssetStore.Load(Path.Combine(TestApplication.Root, "Data"));
        var document = new MapDocument(4, 6);
        document.Set(0, 0, new Cell { BackImage = 1 });
        document.Set(0, 2, new Cell { Foreground = new Front(2, 1) });
        var result = await Task.Run(() => OverviewBatch.BuildAsync(document.Cells.ToArray(), 4, 6, 7, 2, assets, default));
        Assert.Equal(2, result.Sprites); Assert.Equal(2, result.Scene.Children.Count);
        var image = await Task.Run(() => MinimapExporter.CreateAsync(document, assets));
        Assert.Equal((48, 48), (image.PixelWidth, image.PixelHeight));
        var bytes = Fixture.Pixels(image); Assert.All(Enumerable.Range(0, bytes.Length / 4), i => Assert.Equal(255, bytes[i * 4 + 3]));
        Assert.Equal(new byte[] { 160, 20, 80, 255 }, bytes.Take(4));
        using var output = new MemoryStream(); image.Save(output); output.Position = 0;
        using var decoded = RasterImage.Load(output); Assert.Equal((48, 48), (decoded.PixelWidth, decoded.PixelHeight));
    }

    [AvaloniaFact]
    public void ViewportPointerPaintingSelectionZoomAndPanRemainEditable()
    {
        var viewport = new MapViewport { Tool = EditorTool.Pencil, Layer = MapLayer.Middle, Library = 1, SpriteIndex = 3 };
        viewport.SetDocument(new MapDocument(32, 32));
        var window = new EditorWindow { Width = 800, Height = 600, Content = viewport };
        window.Show(); Dispatcher.UIThread.RunJobs();
        try
        {
            window.MouseDown(new Point(70, 80), MouseButton.Left); window.MouseMove(new Point(170, 80)); window.MouseUp(new Point(170, 80), MouseButton.Left);
            Assert.Equal(4, viewport.Document.Get(0, 0).MiddleImage); Assert.Equal(4, viewport.Document.Get(2, 0).MiddleImage);
            viewport.Undo(); Assert.Equal(0, viewport.Document.Get(0, 0).MiddleImage); viewport.Redo(); Assert.Equal(4, viewport.Document.Get(2, 0).MiddleImage);
            viewport.Tool = EditorTool.Select;
            window.MouseDown(new Point(70, 80), MouseButton.Left); window.MouseMove(new Point(170, 120)); window.MouseUp(new Point(170, 120), MouseButton.Left);
            Assert.NotNull(viewport.Selection);
            viewport.SetZoom(.5); Assert.Equal(.5, viewport.Zoom);
            var before = viewport.VisibleWorldBounds;
            window.MouseDown(new Point(200, 200), MouseButton.Middle); window.MouseMove(new Point(240, 230)); window.MouseUp(new Point(240, 230), MouseButton.Middle);
            Assert.NotEqual(before, viewport.VisibleWorldBounds);
            Dispatcher.UIThread.RunJobs(); using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame);
        }
        finally { viewport.StopBackgroundWork(); window.Close(); }
    }

    [AvaloniaFact]
    public void AllEditorWindowsLoadNativeAvaloniaControls()
    {
        using var assets = AssetStore.Load(Path.Combine(TestApplication.Root, "Data"));
        var document = new MapDocument(256, 256);
        var terrain = TerrainCatalogue.Load(); var walls = WallCatalogue.Read(WallCatalogue.ResolvePath());
        var windows = new EditorWindow[]
        {
            new MainWindow(new AssetHistoryStore(Path.Combine(TestApplication.Root, "history.json")), new RecentMapStore(Path.Combine(TestApplication.Root, "recent.json"))),
            new SettingsWindow(null, _ => Task.CompletedTask),
            new WallBrushWindow(walls.Families, assets),
            new SpecialBrushWindow(assets, new TreeBrushOptions(), 3, []),
            new TownStampWindow(assets, TownCatalogue.Load()),
            new TerrainGeneratorWindow(document, assets, terrain),
            new ProvinceGeneratorWindow(document, assets, walls),
            new TempleGeneratorWindow(document, assets, walls, terrain),
        };
        foreach (var window in windows)
        {
            Assert.IsAssignableFrom<Window>(window); Assert.NotNull(window.Content);
            Assert.NotEmpty(((Control)window.Content!).GetVisualDescendants().OfType<Control>());
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task MainWindowShowsRendersAndSwitchesPaletteWithoutChangingDocument()
    {
        var window = new MainWindow(new AssetHistoryStore(Path.Combine(TestApplication.Root, "assets-ui.json")), new RecentMapStore(Path.Combine(TestApplication.Root, "recent-ui.json")));
        window.Show();
        try
        {
            for (int i = 0; i < 30; i++) { await Task.Delay(30); Dispatcher.UIThread.RunJobs(); }
            var viewport = window.FindControl<MapViewport>("Viewport")!;
            Assert.False(viewport.History.Dirty);
            var tabs = window.GetVisualDescendants().OfType<TabControl>().First(t => t.Items.Count == 4);
            tabs.SelectedIndex = 1; Dispatcher.UIThread.RunJobs(); tabs.SelectedIndex = 2; Dispatcher.UIThread.RunJobs(); tabs.SelectedIndex = 0;
            Assert.False(viewport.History.Dirty);
            using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame);
            Assert.NotEmpty(window.GetVisualDescendants().OfType<TabItem>());
        }
        finally { window.Close(); }
    }
}
