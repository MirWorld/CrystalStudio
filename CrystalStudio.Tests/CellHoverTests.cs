using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace CrystalStudio.Tests;

public sealed class CellHoverTests
{
    [AvaloniaFact]
    public async Task ViewOptionShowsCellInfoBesidePointerAndHidesOnExitOrDisable()
    {
        var window = new MainWindow(); window.Show();
        try
        {
            var viewport = window.FindControl<MapViewport>("Viewport")!;
            viewport.SetDocument(new(16, 16));
            await WaitUntilReady(window, viewport);
            var toggle = window.FindControl<MenuItem>("CellHoverToggle");
            Assert.NotNull(toggle);
            var hover = window.FindControl<Border>("CellHoverInfo")!;
            Assert.False(toggle.IsChecked); Assert.False(hover.IsVisible);
            toggle.IsChecked = true; toggle.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            var point = viewport.TranslatePoint(new Point(132, 118), window)!.Value;
            window.MouseMove(point); Assert.False(hover.IsVisible); await Pause(); window.UpdateLayout();
            Assert.True(hover.IsVisible); Assert.False(hover.IsHitTestVisible);
            Assert.Equal(LocalText.Format("Cell {0:N0}, {1:N0}", 1, 1), window.FindControl<TextBlock>("CellHoverCoordinates")!.Text);
            var before = hover.TranslatePoint(default, window)!.Value;
            Assert.True(before.X > point.X);
            window.MouseMove(point + new Vector(6, 4)); window.UpdateLayout();
            var after = hover.TranslatePoint(default, window)!.Value;
            Assert.Equal(before.X + 6, after.X); Assert.Equal(before.Y + 4, after.Y);
            window.MouseMove(viewport.TranslatePoint(new Point(10, 10), window)!.Value);
            Assert.False(hover.IsVisible);
            window.MouseMove(point); await Pause(); Assert.True(hover.IsVisible);
            window.MouseMove(new Point(5, 5)); Assert.False(hover.IsVisible);
            window.MouseMove(point); await Pause(); Assert.True(hover.IsVisible);
            toggle.IsChecked = false; toggle.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.False(hover.IsVisible); Assert.False(viewport.History.Dirty);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task WalkabilityUsesBothCollisionFlagsAndRefreshesWithoutPointerMovement()
    {
        var window = new MainWindow(); window.Show();
        try
        {
            var viewport = window.FindControl<MapViewport>("Viewport")!;
            await WaitUntilReady(window, viewport);
            var toggle = window.FindControl<MenuItem>("CellHoverToggle");
            Assert.NotNull(toggle);
            toggle.IsChecked = true; toggle.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            foreach (int mask in new[] { 0x20000000, 0x40000000, 0x60000000, 0 })
            {
                var document = new MapDocument(16, 16);
                document.Set(1, 1, new Cell { BackImage = mask | 7, BackLibrary = 3 });
                viewport.SetDocument(document);
                window.MouseMove(viewport.TranslatePoint(new Point(132, 118), window)!.Value);
                await Pause();
                var status = window.FindControl<TextBlock>("CellHoverWalkability")!;
                Assert.Equal(LocalText.T(mask == 0 ? "Walkable" : "Not walkable"), status.Text);
                Assert.Equal(mask != 0, ((ISolidColorBrush)status.Foreground!).Color == Colors.Red);
                Assert.Contains("7", window.FindControl<TextBlock>("CellHoverDetails")!.Text);
                viewport.History.Begin(); viewport.History.Record(document, 1, 1);
                document.Set(1, 1, new Cell { BackImage = mask == 0 ? 0x20000000 : 0 });
                viewport.History.Commit(); viewport.Redo();
                Assert.Equal(LocalText.T(mask != 0 ? "Walkable" : "Not walkable"), status.Text);
                viewport.Undo();
                Assert.Equal(LocalText.T(mask == 0 ? "Walkable" : "Not walkable"), status.Text);
                viewport.History.Saved();
            }
        }
        finally { window.Close(); }
    }

    private static async Task Pause() { await Task.Delay(520); Dispatcher.UIThread.RunJobs(); }

    private static async Task WaitUntilReady(MainWindow window, MapViewport viewport)
    {
        for (int i = 0; i < 150; i++)
        {
            await Task.Delay(20); Dispatcher.UIThread.RunJobs();
            if (viewport.IsHitTestVisible) break;
        }
        Assert.True(viewport.IsHitTestVisible);
        window.UpdateLayout();
    }
}
