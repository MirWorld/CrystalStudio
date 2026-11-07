using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;

namespace CrystalStudio.Tests;

public sealed class MapPointerTests
{
    [AvaloniaFact]
    public async Task RightClickAndEyedropperSelectTilesWithNullEmptyOrFilteredSearch()
    {
        string root = Path.Combine(TestApplication.Root, "pointer-" + Guid.NewGuid().ToString("N"));
        var window = new MainWindow(new AssetHistoryStore(Path.Combine(root, "assets.json")),
            new RecentMapStore(Path.Combine(root, "recent.json")));
        window.Show();
        var viewport = window.FindControl<MapViewport>("Viewport")!;
        try
        {
            await Until(() => viewport.Assets != null && viewport.IsHitTestVisible);
            var document = new MapDocument(16, 16);
            document.Set(2, 2, new Cell { BackLibrary = 0, BackImage = 1, MiddleLibrary = 1, MiddleImage = 1 });
            viewport.SetDocument(document);
            var search = window.FindControl<TextBox>("AssetSearch")!;
            var layers = window.FindControl<ListBox>("LayerList")!;
            var list = window.FindControl<ListBox>("AssetList")!;
            var point = viewport.TranslatePoint(new Point(180, 150), window)!.Value;
            foreach (var layer in new[] { MapLayer.Ground, MapLayer.Middle })
            foreach (string? query in new string?[] { null, "", "999" })
            {
                layers.SelectedIndex = (int)layer;
                viewport.Tool = EditorTool.Pencil;
                search.Text = query;
                window.MouseMove(point);
                window.MouseDown(point, MouseButton.Right);
                window.MouseUp(point, MouseButton.Right);
                viewport.ContextMenu!.Close();
                Assert.True(string.IsNullOrEmpty(search.Text));
                Assert.Equal(layer == MapLayer.Ground ? 0 : 1, viewport.Library);
                Assert.Equal(0, viewport.SpriteIndex);
                var selected = Assert.IsType<AssetChoice>(list.SelectedItem);
                Assert.Equal(viewport.Library, selected.Asset!.Library);
                Assert.Equal(0, selected.Index);
            }
            viewport.Tool = EditorTool.Eyedropper;
            search.Text = null;
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            Assert.NotNull(list.SelectedItem);

            layers.SelectedIndex = (int)MapLayer.Objects;
            window.MouseDown(point, MouseButton.Right);
            window.MouseUp(point, MouseButton.Right);
            await Until(() => viewport.ContextMenu!.IsOpen);
            viewport.ContextMenu!.Close();
            Assert.Equal(1, document.Get(2, 2).BackImage);
            Assert.Equal(1, document.Get(2, 2).MiddleImage);
            Assert.False(viewport.History.Dirty);
        }
        finally { viewport.StopBackgroundWork(); window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(1)]
    [InlineData(.5)]
    [InlineData(.2)]
    public async Task HoverOutlineIsHollowDarkBlueAndFourScreenPixelsAtEveryZoom(double zoom)
    {
        ThemeService.Initialize();
        var viewport = new MapViewport { GridVisible = false, Tool = EditorTool.Select };
        viewport.SetDocument(new MapDocument(128, 128));
        var window = new Window { Width = 800, Height = 600, Content = viewport };
        window.Show();
        try
        {
            await Until(() => viewport.MapSceneBuilds > 0);
            viewport.SetZoom(zoom);
            viewport.CenterOnWorld(new Point(64 * 48, 64 * 32));
            await Pump();
            var baseline = Capture(window);
            var bounds = viewport.VisibleWorldBounds;
            int x = 69, y = 69;
            var topLeft = new Point((x * 48 - bounds.X) * zoom, (y * 32 - bounds.Y) * zoom);
            var point = topLeft + new Vector(24 * zoom, 16 * zoom);
            int builds = viewport.MapSceneBuilds, submissions = viewport.MapRenderSubmissions;
            window.MouseMove(point);
            await Pump();
            var frame = Capture(window);
            int left = (int)Math.Round(topLeft.X), middle = (int)Math.Floor(point.Y);
            for (int dx = -2; dx < 2; dx++)
            {
                var before = Pixel(baseline, left + dx, middle);
                var actual = Pixel(frame, left + dx, middle);
                // BGRA: CSS DarkBlue (#00008B), 70% opacity, over the unchanged map.
                Assert.InRange(actual[0], (int)Math.Round(139 * .7 + before[0] * .3) - 1,
                    (int)Math.Round(139 * .7 + before[0] * .3) + 1);
                Assert.InRange(actual[1], (int)Math.Round(before[1] * .3) - 1, (int)Math.Round(before[1] * .3) + 1);
                Assert.InRange(actual[2], (int)Math.Round(before[2] * .3) - 1, (int)Math.Round(before[2] * .3) + 1);
            }
            Assert.Equal(Pixel(baseline, left - 3, middle), Pixel(frame, left - 3, middle));
            Assert.Equal(Pixel(baseline, left + 2, middle), Pixel(frame, left + 2, middle));
            Assert.Equal(Pixel(baseline, (int)point.X, middle), Pixel(frame, (int)point.X, middle));
            Assert.Equal(builds, viewport.MapSceneBuilds);
            Assert.Equal(submissions, viewport.MapRenderSubmissions);
            Assert.Null(viewport.Selection);
            Assert.False(viewport.History.Dirty);

            // Leaving the viewport removes the outline without changing the map.
            window.MouseMove(new Point(-10, -10));
            await Pump();
            Assert.Equal(baseline, Capture(window));
        }
        finally { viewport.StopBackgroundWork(); window.Close(); }
    }

    private static byte[] Capture(Window window)
    {
        using var frame = window.CaptureRenderedFrame()!;
        using var stream = new MemoryStream();
        frame.Save(stream); stream.Position = 0;
        using var image = CrystalStudio.Rendering.RasterImage.Load(stream);
        return Fixture.Pixels(image);
    }

    private static byte[] Pixel(byte[] bytes, int x, int y) => bytes.AsSpan((y * 800 + x) * 4, 4).ToArray();
    private static async Task Pump() { await Task.Delay(40); Dispatcher.UIThread.RunJobs(); }
    private static async Task Until(Func<bool> ready)
    {
        for (int i = 0; i < 200 && !ready(); i++) await Pump();
        Assert.True(ready());
    }
}
