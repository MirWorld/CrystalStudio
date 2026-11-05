using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace CrystalStudio.Tests;

public sealed class MapCreationTests
{
    [AvaloniaFact]
    public async Task CancellingTheGeneratorPreviewLeavesTheWorkspaceUnchanged()
    {
        string root = Path.Combine(TestApplication.Root, "cancel-preview-" + Guid.NewGuid().ToString("N"));
        Fixture.CreateLibraries(Path.Combine(root, "Data"));
        var settings = new SettingsStore(Path.Combine(root, "profile", "settings.json")); settings.Save(new(root, "en-US"));
        var window = new MainWindow(new AssetHistoryStore(Path.Combine(root, "assets.json")), new RecentMapStore(Path.Combine(root, "recent.json")), settings);
        window.Show();
        try
        {
            await Until(() => window.FindControl<MapViewport>("Viewport")!.Assets != null && window.FindControl<MenuItem>("NewButton")!.IsEnabled);
            var document = window.FindControl<MapViewport>("Viewport")!.Document; bool previewOpened = false;
            using var dialogs = Window.WindowOpenedEvent.AddClassHandler<Window>((dialog, _) =>
            {
                if (dialog is NewMapWindow wizard) Dispatcher.UIThread.Post(() =>
                {
                    wizard.KindBox.SelectedItem = MapCreationKind.Terrain; wizard.WidthBox.Text = "16"; wizard.HeightBox.Text = "16";
                    wizard.NameBox.Text = "Cancelled"; wizard.FolderBox.Text = Path.Combine(root, "Map"); wizard.SaveToggle.IsChecked = false;
                    wizard.CreateButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                });
                else if (dialog is TerrainGeneratorWindow) { previewOpened = true; Dispatcher.UIThread.Post(() => dialog.Close(false)); }
            });
            typeof(MainWindow).GetMethod("QuickCreateClick", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .Invoke(window, [window, new RoutedEventArgs()]);
            await Until(() => previewOpened && window.FindControl<MenuItem>("NewButton")!.IsEnabled);
            Assert.Same(document, window.FindControl<MapViewport>("Viewport")!.Document);
            Assert.Single(window.GetVisualDescendants().OfType<TabControl>().Single(control => control.Name == "MapTabs").Items);
            Assert.False(File.Exists(Path.Combine(root, "Map", "Cancelled.map")));
        }
        finally { window.FindControl<MapViewport>("Viewport")!.History.Saved(); window.Close(); }
    }

    [AvaloniaFact]
    public async Task CancellingQuickCreationDoesNotAddATab()
    {
        var window = new MainWindow(new AssetHistoryStore(Path.Combine(TestApplication.Root, "quick-assets.json")));
        window.Show();
        try
        {
            for (int attempt = 0; attempt < 500 && !window.FindControl<MenuItem>("NewButton")!.IsEnabled; attempt++)
            { await Task.Delay(20); Dispatcher.UIThread.RunJobs(); }
            var tabs = window.GetVisualDescendants().OfType<TabControl>().Single(control => control.Name == "MapTabs");
            int count = tabs.Items.Count;
            Assert.Contains(window.GetLogicalDescendants().OfType<MenuItem>(), item => item.Name == "QuickCreateMap");
            var method = typeof(MainWindow).GetMethod("QuickCreateClick", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.NotNull(method);
            using var dialogs = Window.WindowOpenedEvent.AddClassHandler<Window>((dialog, _) =>
            {
                if (dialog.Title == LocalText.T("Quick Create Map")) Dispatcher.UIThread.Post(() => dialog.Close(false));
            });
            method.Invoke(window, [window, new RoutedEventArgs()]);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(count, tabs.Items.Count);
        }
        finally { window.FindControl<MapViewport>("Viewport")!.History.Saved(); window.Close(); }
    }

    [AvaloniaFact]
    public async Task QuickBlankMapSavesRecipeAndAppearsInTheMapBrowser()
    {
        string root = Path.Combine(TestApplication.Root, "quick-save-" + Guid.NewGuid().ToString("N"));
        Fixture.CreateLibraries(Path.Combine(root, "Data")); Directory.CreateDirectory(Path.Combine(root, "Map"));
        var settings = new SettingsStore(Path.Combine(root, "profile", "settings.json")); settings.Save(new(root, "en-US"));
        var window = new MainWindow(new AssetHistoryStore(Path.Combine(root, "profile", "assets.json")),
            new RecentMapStore(Path.Combine(root, "profile", "recent.json")), settings);
        window.Show();
        try
        {
            await Until(() => window.FindControl<MapViewport>("Viewport")!.Assets != null && window.FindControl<MenuItem>("NewButton")!.IsEnabled);
            using var dialogs = Window.WindowOpenedEvent.AddClassHandler<Window>((dialog, _) =>
            {
                if (dialog is not NewMapWindow wizard) return;
                Dispatcher.UIThread.Post(() =>
                {
                    wizard.KindBox.SelectedItem = MapCreationKind.Blank; wizard.WidthBox.Text = "16"; wizard.HeightBox.Text = "16";
                    wizard.NameBox.Text = "Forest"; wizard.FolderBox.Text = Path.Combine(root, "Map"); wizard.SaveToggle.IsChecked = true;
                    wizard.PresetNameBox.Text = "Small blank";
                    Assert.True(wizard.CreateButton.IsEnabled);
                    wizard.CreateButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                });
            });
            typeof(MainWindow).GetMethod("QuickCreateClick", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .Invoke(window, [window, new RoutedEventArgs()]);
            string target = Path.Combine(root, "Map", "Forest.map");
            await Until(() => File.Exists(target) && window.GetLogicalDescendants().OfType<ListBox>().Single(list => list.Name == "MapFileList").SelectedItem is MapFileEntry);
            var viewport = window.FindControl<MapViewport>("Viewport")!;
            Assert.False(viewport.History.Dirty); Assert.Equal(16, MapDocument.Load(target).Width);
            Assert.Equal(MapCreationKind.Blank, MapCreationStore.LoadRecipe(target, viewport.Document)!.Kind);
            var preferences = new MapCreationStore(Path.Combine(root, "profile", "map-creation.json")); preferences.Load();
            Assert.Contains("Small blank", preferences.Data.Presets.Keys);
            viewport.History.Record(viewport.Document, 0, 0); viewport.Document.Set(0, 0, new Cell { MiddleImage = 3 }); viewport.History.Commit();
            await window.SaveRecoverySnapshotsAsync();
            Assert.Single(Directory.GetFiles(Path.Combine(root, "profile", "recovery"), "*.map", SearchOption.AllDirectories));
            viewport.Undo(); await window.SaveRecoverySnapshotsAsync();
            Assert.Empty(Directory.GetFiles(Path.Combine(root, "profile", "recovery"), "*.map", SearchOption.AllDirectories));
            viewport.Redo(); await window.SaveRecoverySnapshotsAsync();
            var save = typeof(MainWindow).GetMethod("SaveMapTo", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            Assert.True((bool)save.Invoke(window, [target, true])!);
            Assert.Empty(Directory.GetFiles(Path.Combine(root, "profile", "recovery"), "*.map", SearchOption.AllDirectories));
            viewport.History.Record(viewport.Document, 0, 0); viewport.Document.Set(0, 0, new Cell { MiddleImage = 5 }); viewport.History.Commit();
            using var errors = Window.WindowOpenedEvent.AddClassHandler<Window>((dialog, _) =>
            {
                if (dialog.Title != LocalText.T("Save failed")) return;
                Dispatcher.UIThread.Post(() => dialog.GetVisualDescendants().OfType<Button>()
                    .Single(button => Equals(button.Content, LocalText.T("OK"))).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
            });
            Assert.False((bool)save.Invoke(window, [Path.Combine(root, "Map"), true])!);
            Assert.True(viewport.History.Dirty); Assert.Equal((ushort)5, viewport.Document.Get(0, 0).MiddleImage);
            Assert.Equal((ushort)3, MapDocument.Load(target).Get(0, 0).MiddleImage);
        }
        finally { window.FindControl<MapViewport>("Viewport")!.History.Saved(); window.Close(); }
    }

    private static async Task Until(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 500; attempt++)
        {
            Dispatcher.UIThread.RunJobs(); if (condition()) return; await Task.Delay(20);
        }
        Assert.True(condition(), "Timed out waiting for map creation.");
    }

    [Theory]
    [InlineData(16, MapCreationKind.Province, false)]
    [InlineData(64, MapCreationKind.Dungeon, true)]
    [InlineData(128, MapCreationKind.Province, true)]
    [InlineData(1000, MapCreationKind.Province, true)]
    [InlineData(1024, MapCreationKind.Province, false)]
    [InlineData(1024, MapCreationKind.Terrain, true)]
    [InlineData(4096, MapCreationKind.Blank, false)]
    public void CreationSizesMatchGeneratorLimits(int size, MapCreationKind kind, bool allowed)
    {
        Assert.Equal(allowed, MapDimensions.CreationError(size, size, kind) == null);
    }

    [Theory]
    [InlineData(4096, 4096)]
    [InlineData(0, 256)]
    [InlineData(256, -1)]
    [InlineData(32768, 16)]
    public void DocumentRejectsDimensionsThatCannotRoundTrip(int width, int height)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MapDocument(width, height));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(16, 16)]
    [InlineData(1000, 1000)]
    [InlineData(4096, 3906)]
    public void DocumentRetainsSupportedDimensions(int width, int height)
    {
        var document = new MapDocument(width, height);
        Assert.Equal(width, document.Width);
        Assert.Equal(height, document.Height);
    }
}
