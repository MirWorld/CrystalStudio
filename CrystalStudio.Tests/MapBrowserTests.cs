using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace CrystalStudio.Tests;

public sealed class MapBrowserTests
{
    [AvaloniaFact]
    public async Task BulkCloseConfirmsEachDirtyMapAndStopsOnCancel()
    {
        var window = new MainWindow(new AssetHistoryStore(Path.Combine(TestApplication.Root, "tab-close-assets.json")),
            new RecentMapStore(Path.Combine(TestApplication.Root, "tab-close-recent.json")));
        window.Show();
        var viewport = window.FindControl<MapViewport>("Viewport")!;
        try
        {
            await Pump();
            await Until(() => viewport.Assets != null && window.FindControl<MenuItem>("SaveButton")!.IsEnabled);
            var add = typeof(MainWindow).GetMethod("AddMapTab", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            var tabs = window.GetVisualDescendants().OfType<TabControl>().Single(control => control.Name == "MapTabs");
            for (int i = 0; i < 3; i++)
            {
                add.Invoke(window, [new MapDocument(16, 16), null]);
                if (i == 2) continue;
                viewport.History.Record(viewport.Document, 0, 0);
                viewport.Document.Set(0, 0, new Cell { MiddleImage = 1 });
                viewport.History.Commit();
            }
            var cancelled = Assert.IsType<TabItem>(tabs.Items[2]);
            var untouched = Assert.IsType<TabItem>(tabs.Items[3]);
            int confirmations = 0;
            using var dialogs = Window.WindowOpenedEvent.AddClassHandler<Window>((dialog, _) =>
            {
                if (dialog.Title != LocalText.T("Unsaved map")) return;
                string answer = confirmations++ == 0 ? "No" : "Cancel";
                Dispatcher.UIThread.Post(() => dialog.GetVisualDescendants().OfType<Button>()
                    .Single(button => Equals(button.Content, LocalText.T(answer)))
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
            });
            Assert.IsType<TabItem>(tabs.Items[0]).ContextMenu!.Items.OfType<MenuItem>()
                .Single(item => Equals(item.Header, LocalText.T("Close All")))
                .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Equal(2, confirmations);
            Assert.Equal(new[] { cancelled, untouched }, tabs.Items.Cast<TabItem>().ToArray());
            Assert.Same(cancelled, tabs.SelectedItem);
            Assert.True(viewport.History.Dirty);
            viewport.Undo();
        }
        finally { viewport.History.Saved(); window.Close(); }
    }

    [AvaloniaFact]
    public async Task MapTabsSupportRelativeCloseCommandsAndDragReordering()
    {
        var window = new MainWindow(new AssetHistoryStore(Path.Combine(TestApplication.Root, "tab-actions-assets.json")),
            new RecentMapStore(Path.Combine(TestApplication.Root, "tab-actions-recent.json")));
        window.Show();
        try
        {
            var viewport = window.FindControl<MapViewport>("Viewport")!;
            await Pump();
            await Until(() => viewport.Assets != null && window.FindControl<MenuItem>("SaveButton")!.IsEnabled);
            var add = typeof(MainWindow).GetMethod("AddMapTab", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            for (int i = 0; i < 3; i++) add.Invoke(window, [new MapDocument(16, 16), null]);
            var tabs = window.GetVisualDescendants().OfType<TabControl>()
                .Single(control => control.Items.OfType<TabItem>().Any() && control.Items.OfType<TabItem>().All(tab => tab.Tag != null));
            Assert.Equal(4, tabs.Items.Count);
            var relative = Assert.IsType<TabItem>(tabs.ContainerFromIndex(1));
            var context = Assert.IsType<ContextMenu>(relative.ContextMenu);
            Assert.Contains(context.Items.OfType<MenuItem>(), item => Equals(item.Header, LocalText.T("Close All")));
            Assert.Contains(context.Items.OfType<MenuItem>(), item => Equals(item.Header, LocalText.T("Close Tabs to Left")));
            Assert.Contains(context.Items.OfType<MenuItem>(), item => Equals(item.Header, LocalText.T("Close Tabs to Right")));
            Assert.Contains(context.Items.OfType<MenuItem>(), item => Equals(item.Header, LocalText.T("Close Other Tabs")));
            context.Items.OfType<MenuItem>().Single(item => Equals(item.Header, LocalText.T("Close Tabs to Right")))
                .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Equal(2, tabs.Items.Count);

            add.Invoke(window, [new MapDocument(16, 16), null]);
            add.Invoke(window, [new MapDocument(16, 16), null]);
            await Pump();
            var originalOrder = tabs.Items.Cast<TabItem>().ToArray();
            var first = Assert.IsType<TabItem>(tabs.ContainerFromIndex(0));
            var last = Assert.IsType<TabItem>(tabs.ContainerFromIndex(tabs.Items.Count - 1));
            tabs.SelectedItem = first;
            viewport.SetZoom(.3);
            var document = viewport.Document;
            var history = viewport.History;
            history.Record(document, 0, 0);
            document.Set(0, 0, new Cell { MiddleImage = 1 });
            history.Commit();
            var start = first.TranslatePoint(new Point(first.Bounds.Width / 2, first.Bounds.Height / 2), window)!.Value;
            var end = last.TranslatePoint(new Point(last.Bounds.Width * .9, last.Bounds.Height / 2), window)!.Value;
            Assert.True(first.Bounds.Width > 0 && last.Bounds.Width > 0, $"Tab bounds: {first.Bounds}, {last.Bounds}; parent: {tabs.Bounds}");
            window.MouseMove(start);
            window.MouseDown(start, MouseButton.Left);
            await Pump();
            window.MouseMove(end, RawInputModifiers.LeftMouseButton);
            await Pump();
            window.MouseUp(end, MouseButton.Left);
            await Pump();
            var reordered = tabs.Items.Cast<TabItem>().ToArray();
            Assert.NotEqual(originalOrder, reordered);
            Assert.Equal(originalOrder[0], reordered[^1]);
            Assert.Same(first, tabs.SelectedItem);
            Assert.Same(document, viewport.Document);
            Assert.Same(history, viewport.History);
            Assert.Equal(.3, viewport.Zoom);
            Assert.True(history.Dirty);
            viewport.Undo();

            void Close(TabItem tab, string action) => tab.ContextMenu!.Items.OfType<MenuItem>()
                .Single(item => Equals(item.Header, LocalText.T(action))).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            var anchor = Assert.IsType<TabItem>(tabs.Items[2]);
            Close(anchor, "Close Tabs to Left");
            Assert.Equal(2, tabs.Items.Count);
            Assert.Same(anchor, tabs.Items[0]);
            Close(anchor, "Close Other Tabs");
            Assert.Same(anchor, Assert.Single(tabs.Items));
            Close(anchor, "Close All");
            Assert.NotSame(anchor, Assert.Single(tabs.Items));
            Assert.False(viewport.History.Dirty);
        }
        finally { window.Close(); }
    }

    [Fact]
    public void GameRootLoadsDataAndIndexesNestedMapFilesWithoutParsingThem()
    {
        string root = NewRoot();
        Fixture.CreateLibraries(Path.Combine(root, "Data"));
        string maps = Path.Combine(root, "Map");
        Directory.CreateDirectory(Path.Combine(maps, "nested"));
        new MapDocument(16, 16).Save(Path.Combine(maps, "0.map"));
        new MapDocument(24, 32).Save(Path.Combine(maps, "nested", "0.MAP"));
        File.WriteAllText(Path.Combine(maps, "unreadable.map"), "not a map");
        File.WriteAllText(Path.Combine(maps, "ignore.txt"), "ignored");
        File.WriteAllText(Path.Combine(maps, "ignore.EliteMap"), "ignored");

        using var assets = AssetStore.Load(root);
        Assert.Equal(new[] { 0, 1, 2 }, assets.Libraries.Keys.Order());
        Assert.All(assets.Libraries.Values, library => Assert.StartsWith(Path.Combine(root, "Data"), library.Path));
        Assert.Equal(maps, DataPaths.FindMapFolder(root));
        Assert.Equal(maps, DataPaths.FindMapFolder(Path.Combine(root, "Data")));
        Assert.Equal(maps, DataPaths.FindMapFolder(Path.Combine(root, "Data", "Map")));
        foreach (string legacy in new[] { root, Path.Combine(root, "Data"), Path.Combine(root, "Data", "Map"),
            Path.Combine(root, "Data", "Map", "WemadeMir2"), maps })
            Assert.Equal(root, DataPaths.GetClientRoot(legacy));
        var entries = MapFileCatalog.Read(maps);
        Assert.Equal(3, entries.Count);
        Assert.Contains(entries, entry => entry.RelativePath == Path.Combine("nested", "0.MAP"));
        Assert.Contains(entries, entry => entry.RelativePath == "unreadable.map");
        Assert.All(entries, entry => Assert.True(Path.IsPathFullyQualified(entry.FilePath)));
    }

    [Fact]
    public void LegacySettingsDisplayAndSaveClientRootWhileKeepingLanguageAndFormat()
    {
        string root = NewRoot(), path = Path.Combine(root, "settings.json");
        string legacy = Path.Combine(root, "Data", "Map");
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new LibrarySettings(legacy, "zh-CN", AssetSourceFormat.Sanxiu)));
        var store = new SettingsStore(path);
        var settings = store.Load();
        Assert.Equal(root, settings.LibraryFolder);
        Assert.Equal("zh-CN", settings.Language);
        Assert.Equal(AssetSourceFormat.Sanxiu, settings.AssetFormat);
        store.Save(settings with { LibraryFolder = legacy });
        Assert.Equal(root, System.Text.Json.JsonSerializer.Deserialize<LibrarySettings>(File.ReadAllText(path))!.LibraryFolder);
        Assert.Equal(Path.Combine(root, "flat"), DataPaths.GetClientRoot(Path.Combine(root, "flat")));
    }

    [Fact]
    public void DirectDataMapAndFlatLibraryFoldersRemainSupported()
    {
        string root = NewRoot();
        Fixture.CreateLibraries(Path.Combine(root, "Data"));
        foreach (string selected in new[] { Path.Combine(root, "Data"), Path.Combine(root, "Data", "Map") })
        {
            using var assets = AssetStore.Load(selected);
            Assert.Equal(3, assets.Libraries.Count);
        }
        string flat = Path.Combine(root, "flat");
        Directory.CreateDirectory(flat);
        Fixture.WriteLibrary(Path.Combine(flat, "Tiles.lib"), 96, 64, 0, 0, 0);
        using var legacy = AssetStore.Load(flat);
        Assert.Single(legacy.Libraries);
        Assert.Null(DataPaths.FindMapFolder(flat));
        Assert.Null(DataPaths.FindMapFolder(root));
    }

    [AvaloniaFact]
    public async Task LeftMapListLoadsSavedRootAndOpensClickedMapsWithoutDuplicateTabs()
    {
        string root = NewRoot();
        Fixture.CreateLibraries(Path.Combine(root, "Data"));
        string maps = Path.Combine(root, "Map");
        Directory.CreateDirectory(Path.Combine(maps, "nested"));
        var first = new MapDocument(32, 32);
        first.Set(0, 0, new Cell { BackImage = 1 });
        first.Save(Path.Combine(maps, "01.map"));
        new MapDocument(40, 48).Save(Path.Combine(maps, "nested", "second.map"));
        var settings = new SettingsStore(Path.Combine(root, "profile", "settings.json"));
        settings.Save(new LibrarySettings(root, "en-US"));
        var window = new MainWindow(new AssetHistoryStore(Path.Combine(root, "profile", "assets.json")),
            new RecentMapStore(Path.Combine(root, "profile", "recent.json")), settings);
        window.Show();
        try
        {
            var sidebar = window.GetVisualDescendants().OfType<TabControl>().Single(control => control.Name == "LeftSidebarTabs");
            Assert.Equal(2, sidebar.Items.Count);
            Assert.Equal(0, sidebar.SelectedIndex);
            var toolPanel = window.FindControl<ResponsivePanel>("ToolPanel")!;
            Assert.Contains(sidebar, toolPanel.GetVisualAncestors());
            var toolsPage = Assert.IsType<TabItem>(sidebar.Items[0]);
            var tools = Assert.IsType<Grid>(toolsPage.Content);
            Assert.All(new[] { "Tools", "Tool Options", "Layers", "Cell Inspector" }, title =>
                Assert.Contains(tools.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "\u283F  " + LocalText.T(title)));
            sidebar.SelectedIndex = 1;
            await Pump();
            var list = window.GetVisualDescendants().OfType<ListBox>().Single(control => control.Name == "MapFileList");
            var save = window.FindControl<MenuItem>("SaveButton")!;
            await Until(() => list.Items.Count == 2 && save.IsEnabled);
            var mapRow = Assert.IsType<ListBoxItem>(list.ContainerFromIndex(0));
            Assert.InRange(mapRow.Bounds.Height, 18, 24);
            Assert.Equal(root, settings.Load().LibraryFolder);
            var viewport = window.FindControl<MapViewport>("Viewport")!;
            Assert.True(list.TranslatePoint(default, window)!.Value.X < viewport.TranslatePoint(default, window)!.Value.X);

            await ClickMap(window, list, "01.map");
            await Until(() => window.Title!.StartsWith("01.map", StringComparison.Ordinal) && save.IsEnabled);
            Assert.Equal(32, viewport.Document.Width);
            Assert.Equal(1, viewport.Document.Get(0, 0).BackImage);
            Assert.True(viewport.Zoom < 1, "Opening a map should fit it to the available viewport.");
            var originalSize = Assert.IsType<Button>(window.FindControl<Button>("OriginalSizeButton"));
            originalSize.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(1, viewport.Zoom);
            viewport.History.Record(viewport.Document, 2, 2);
            viewport.Document.Set(2, 2, new Cell { MiddleLibrary = 1, MiddleImage = 1 });
            viewport.History.Commit();

            await ClickMap(window, list, Path.Combine("nested", "second.map"));
            await Until(() => window.Title!.StartsWith("second.map", StringComparison.Ordinal) && save.IsEnabled);
            Assert.Equal((40, 48), (viewport.Document.Width, viewport.Document.Height));
            await ClickMap(window, list, "01.map");
            await Until(() => window.Title!.StartsWith("01.map", StringComparison.Ordinal) && save.IsEnabled);
            var documentTabs = window.GetVisualDescendants().OfType<TabControl>()
                .Single(control => control.Items.OfType<TabItem>().Any() && control.Items.OfType<TabItem>().All(tab => tab.Tag != null));
            Assert.Equal(3, documentTabs.Items.Count);
            Assert.True(viewport.History.Dirty);
            Assert.Equal(1, viewport.Document.Get(2, 2).MiddleImage);
            Assert.Equal(1, viewport.Zoom);
            Assert.Equal(0, MapDocument.Load(Path.Combine(maps, "01.map")).Get(2, 2).MiddleImage);
            viewport.Undo();

            var search = window.GetVisualDescendants().OfType<TextBox>().Single(control => control.Name == "MapFileSearch");
            search.Text = "SECOND";
            await Until(() => list.Items.Count == 1);
            search.Text = "nested";
            await Until(() => list.Items.Count == 0);
            search.Clear();
            await Until(() => list.Items.Count == 2);
            sidebar.SelectedIndex = 0;
            await Pump();
            var pencil = toolPanel.Children.OfType<RadioButton>().Single(button => Equals(button.Tag, "Pencil"));
            var pencilPosition = pencil.TranslatePoint(new Point(pencil.Bounds.Width / 2, pencil.Bounds.Height / 2), window)!.Value;
            window.MouseDown(pencilPosition, MouseButton.Left);
            window.MouseUp(pencilPosition, MouseButton.Left);
            await Pump();
            Assert.Equal(EditorTool.Pencil, viewport.Tool);
            sidebar.SelectedIndex = 1;
            await Pump();
            Assert.IsType<ListBoxItem>(list.ContainerFromIndex(0)).Focus();
            Assert.True(list.IsKeyboardFocusWithin);
            var selectedTool = viewport.Tool;
            Assert.Equal(EditorTool.Pencil, selectedTool);
            window.KeyPressQwerty(PhysicalKey.B, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.B, RawInputModifiers.None);
            Assert.Equal(selectedTool, viewport.Tool);
            sidebar.SelectedIndex = 0;
            await Pump();
            Assert.Same(toolPanel, tools.GetVisualDescendants().OfType<ResponsivePanel>().Single(control => control.Name == "ToolPanel"));
            Assert.Equal(selectedTool, viewport.Tool);
            sidebar.SelectedIndex = 1;
            await Pump();
            Assert.Same(list, window.GetVisualDescendants().OfType<ListBox>().Single(control => control.Name == "MapFileList"));
            new MapDocument(16, 16).Save(Path.Combine(maps, "added.map"));
            var refresh = window.GetVisualDescendants().OfType<Button>().Single(control => control.Name == "MapRefreshButton");
            var refreshPosition = refresh.TranslatePoint(default, window)!.Value;
            var listPosition = list.TranslatePoint(default, window)!.Value;
            Assert.True(refreshPosition.X + refresh.Bounds.Width <= listPosition.X + list.Bounds.Width + 1,
                "Map refresh must remain visible when the saved sidebar is narrow.");
            refresh.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => list.Items.Count == 3 && list.IsEnabled);
            Assert.False(viewport.History.Dirty);

            string screenshot = Path.Combine(AppContext.BaseDirectory, "MapBrowserScreenshots", "root-maps.png");
            Directory.CreateDirectory(Path.GetDirectoryName(screenshot)!);
            using var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            using var stream = File.Create(screenshot);
            frame.Save(stream);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task MapBrowserStillListsMapsWhenLibrariesAreUnavailable()
    {
        string root = NewRoot(), maps = Path.Combine(root, "Map");
        Directory.CreateDirectory(maps);
        new MapDocument(16, 16).Save(Path.Combine(maps, "available.map"));
        var store = new SettingsStore(Path.Combine(root, "profile", "settings.json"));
        store.Save(new LibrarySettings(root, "en-US"));
        var window = new MainWindow(new AssetHistoryStore(Path.Combine(root, "profile", "assets.json")),
            new RecentMapStore(Path.Combine(root, "profile", "recent.json")), store);
        window.Show();
        try
        {
            var sidebar = window.GetVisualDescendants().OfType<TabControl>().Single(control => control.Name == "LeftSidebarTabs");
            sidebar.SelectedIndex = 1;
            await Pump();
            var list = window.GetVisualDescendants().OfType<ListBox>().Single(control => control.Name == "MapFileList");
            await Until(() => list.Items.Count == 1 && list.IsEnabled);
            await ClickMap(window, list, "available.map");
            await Until(() => window.Title!.StartsWith("available.map", StringComparison.Ordinal));
        }
        finally { window.Close(); }
    }

    private static string NewRoot()
    {
        string path = Path.Combine(Path.GetTempPath(), "CrystalStudio-MapBrowser-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static async Task ClickMap(Window window, ListBox list, string relativePath)
    {
        var entry = list.Items.Cast<MapFileEntry>().Single(item => item.RelativePath == relativePath);
        list.ScrollIntoView(entry);
        await Pump();
        int index = list.Items.IndexOf(entry);
        var item = Assert.IsType<ListBoxItem>(list.ContainerFromIndex(index));
        var point = item.TranslatePoint(new Point(item.Bounds.Width / 2, item.Bounds.Height / 2), window)!.Value;
        window.MouseMove(point);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        await Pump();
    }

    private static async Task Until(Func<bool> condition)
    {
        for (int i = 0; i < 600 && !condition(); i++) await Pump();
        Assert.True(condition());
    }

    private static async Task Pump()
    {
        await Task.Delay(25);
        Dispatcher.UIThread.RunJobs();
    }
}
