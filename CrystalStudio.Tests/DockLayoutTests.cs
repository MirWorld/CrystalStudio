using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace CrystalStudio.Tests;

public sealed class DockLayoutTests
{
    [AvaloniaFact]
    public async Task LeftTabsKeepResizeAndPanelDockingAcrossPageChanges()
    {
        string layout = Path.Combine(Path.GetTempPath(), "CrystalStudio-TabbedDock-" + Guid.NewGuid().ToString("N"), "workspace.json");
        var window = new EditorWindow { Width = 1100, Height = 700 };
        var workspace = new DockWorkspace(window, new Border(), (_, _) => { }, layout);
        workspace.EnableLeftTabs();
        var tools = new TextBlock { Text = "Tools content" };
        var maps = new TextBlock { Text = "test.map" };
        workspace.Add("Tools", tools, Dock.Left, 1);
        workspace.Add("Maps", maps, Dock.Left, 2);
        workspace.Add("Layers", new TextBlock { Text = "Layers content" }, Dock.Left, 1);
        window.Content = workspace;
        window.Show();
        try
        {
            await Pump();
            var tabs = window.GetVisualDescendants().OfType<TabControl>().Single();
            Assert.Same(window, tools.GetVisualRoot());
            tabs.SelectedIndex = 1;
            await Pump();
            Assert.Same(window, maps.GetVisualRoot());
            var grip = window.GetVisualDescendants().OfType<Thumb>().Single(t => t.Name == "DockResizeLeft");
            double before = maps.Bounds.Width;
            await Drag(window, Centre(grip, window), new Vector(90, 0));
            Assert.True(maps.Bounds.Width > before + 50);
            tabs.SelectedIndex = 0;
            await Pump();
            Assert.Same(window, tools.GetVisualRoot());
            var split = window.GetVisualDescendants().OfType<GridSplitter>().Single();
            var frame = tools.GetVisualAncestors().OfType<Border>().First();
            double height = frame.Bounds.Height;
            await Drag(window, Centre(split, window), new Vector(0, 40));
            Assert.True(frame.Bounds.Height > height + 20);
            tabs.SelectedIndex = 1;
            await Pump();
            var header = Header(window);
            var dockRight = header.ContextMenu!.Items.OfType<MenuItem>().Single(item => Equals(item.Header, LocalText.T("Dock ") + LocalText.T("Right")));
            dockRight.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
            await Pump();
            Assert.Same(window, maps.GetVisualRoot());
            var dockLeft = header.ContextMenu!.Items.OfType<MenuItem>().Single(item => Equals(item.Header, LocalText.T("Dock ") + LocalText.T("Left")));
            dockLeft.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
            await Pump();
            Assert.Equal(1, tabs.SelectedIndex);
            Assert.Contains(tabs, maps.GetVisualAncestors());
            workspace.SetLayoutLocked(true);
            Assert.False(window.GetVisualDescendants().OfType<Thumb>().Single(t => t.Name == "DockResizeLeft").IsEnabled);
            tabs.SelectedIndex = 0;
            await Pump();
            Assert.Same(window, tools.GetVisualRoot());
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task MapPaneCanResizeMoveAndRestoreItsSavedPosition()
    {
        string layout = Path.Combine(Path.GetTempPath(), "CrystalStudio-Dock-" + Guid.NewGuid().ToString("N"), "workspace.json");
        (EditorWindow Window, DockWorkspace Workspace, TextBlock Maps, Border Canvas) Create()
        {
            var window = new EditorWindow { Width = 1100, Height = 700 };
            var canvas = new Border();
            var workspace = new DockWorkspace(window, canvas, (_, _) => { }, layout);
            var maps = new TextBlock { Text = "test.map" };
            workspace.Add("Maps", maps, Dock.Left, 2);
            workspace.Add("Tools", new TextBlock { Text = "Tools" }, Dock.Left, 1);
            window.Content = workspace;
            window.Show();
            workspace.Restore();
            return (window, workspace, maps, canvas);
        }
        var first = Create();
        double savedWidth;
        try
        {
            await Pump();
            var grip = first.Window.GetVisualDescendants().OfType<Thumb>().Single(t => t.Name == "DockResizeLeft");
            double before = first.Maps.Bounds.Width;
            await Drag(first.Window, Centre(grip, first.Window), new Vector(100, 0));
            Assert.True(first.Maps.Bounds.Width > before + 50);
            double expanded = first.Maps.Bounds.Width;
            await Drag(first.Window, Centre(grip, first.Window), new Vector(-40, 0));
            Assert.True(first.Maps.Bounds.Width < expanded - 20);

            var split = first.Window.GetVisualDescendants().OfType<GridSplitter>().Single();
            var frame = first.Maps.GetVisualAncestors().OfType<Border>().First();
            double height = frame.Bounds.Height;
            await Drag(first.Window, Centre(split, first.Window), new Vector(0, 40));
            Assert.True(frame.Bounds.Height > height + 20);

            var header = Header(first.Window);
            var start = Centre(header, first.Window);
            var end = first.Workspace.TranslatePoint(new Point(first.Workspace.Bounds.Width - 12, 100), first.Window)!.Value;
            await Drag(first.Window, start, end - start);
            Assert.True(first.Maps.TranslatePoint(default, first.Window)!.Value.X > first.Canvas.TranslatePoint(default, first.Window)!.Value.X);

            var rightGrip = first.Window.GetVisualDescendants().OfType<Thumb>().Single(t => t.Name == "DockResizeRight");
            await Drag(first.Window, Centre(rightGrip, first.Window), new Vector(-60, 0));
            savedWidth = first.Maps.Bounds.Width;
            var entries = JsonSerializer.Deserialize<List<DockWorkspace.LayoutEntry>>(File.ReadAllText(layout))!;
            Assert.Equal(Dock.Right, entries.Single(e => e.Title == "Maps").Side);
            Assert.True(entries.Single(e => e.Title == "Maps").DockSize > 390);
        }
        finally { first.Window.Close(); }

        var restored = Create();
        try
        {
            await Pump();
            Assert.True(restored.Maps.TranslatePoint(default, restored.Window)!.Value.X > restored.Canvas.TranslatePoint(default, restored.Window)!.Value.X);
            Assert.InRange(restored.Maps.Bounds.Width, savedWidth - 1, savedWidth + 1);
            var start = Centre(Header(restored.Window), restored.Window);
            var outside = new Point(restored.Window.Bounds.Width + 80, 120);
            await Drag(restored.Window, start, outside - start);
            var floating = Assert.IsType<EditorWindow>(restored.Maps.GetVisualRoot());
            Assert.NotSame(restored.Window, floating);
            Assert.True(floating.CanResize);
            floating.Close();
            await Pump();
            Assert.Same(restored.Window, restored.Maps.GetVisualRoot());
        }
        finally { restored.Window.Close(); }
    }

    private static Border Header(Window window) => (Border)window.GetVisualDescendants().OfType<TextBlock>()
        .Single(text => text.Text == "\u283F  " + LocalText.T("Maps")).Parent!;
    private static Point Centre(Control control, Window window) => control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
    private static async Task Drag(Window window, Point start, Vector offset)
    {
        window.MouseMove(start);
        window.MouseDown(start, MouseButton.Left);
        window.MouseMove(start + offset, RawInputModifiers.LeftMouseButton);
        await Pump();
        window.MouseUp(start + offset, MouseButton.Left);
        await Pump();
    }
    private static async Task Pump()
    {
        await Task.Delay(40);
        Dispatcher.UIThread.RunJobs();
    }
}
