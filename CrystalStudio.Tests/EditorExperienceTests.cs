using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.Reflection;

namespace CrystalStudio.Tests;

public sealed class EditorExperienceTests
{
    [AvaloniaTheory]
    [InlineData("en-US")]
    [InlineData("zh-CN")]
    public async Task EditingContextRemainsVisibleAndExplainsAutomaticToolChanges(string language)
    {
        var settings = new SettingsStore(System.IO.Path.Combine(TestApplication.Root, Guid.NewGuid().ToString("N"), "settings.json"));
        settings.Save(new(System.IO.Path.Combine(TestApplication.Root, "Data"), language));
        string previousLanguage = LocalText.Language; LocalText.Initialize(settings);
        var window = new MainWindow(new AssetHistoryStore(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(settings.FilePath)!, "assets.json")),
            new RecentMapStore(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(settings.FilePath)!, "recent.json")), settings); window.Show();
        try
        {
            await WaitFor(() => window.FindControl<MapViewport>("Viewport")!.IsHitTestVisible);
            var viewport = window.FindControl<MapViewport>("Viewport")!;
            var layers = window.FindControl<ListBox>("LayerList")!;
            layers.SelectedIndex = (int)MapLayer.Ground;
            typeof(MainWindow).GetMethod("SetTool", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [EditorTool.Eyedropper]);
            layers.SelectedIndex = (int)MapLayer.Objects;
            var context = window.GetLogicalDescendants().OfType<TextBlock>().Single(control => control.Name == "EditingContext");
            Assert.True(context.IsVisible);
            Assert.Contains(LocalText.T("Objects"), context.Text);
            Assert.Contains(LocalText.T("Pencil"), context.Text);
            Assert.Contains(LocalText.T("Tool changed to Pencil because this layer does not support the previous tool."), window.FindControl<TextBlock>("Status")!.Text);
            viewport.SetLayerLocked(MapLayer.Objects, true);
            Assert.Contains(LocalText.T("Locked"), context.Text);
            Assert.False(viewport.History.Dirty);
            window.GetVisualDescendants().OfType<TabControl>().Single(control => control.Name == "LeftSidebarTabs").SelectedIndex = 1;
            window.Width = 1100; window.Height = 720; Dispatcher.UIThread.RunJobs();
            var position = context.TranslatePoint(default, window)!.Value;
            Assert.InRange(position.Y, 0, window.ClientSize.Height - context.Bounds.Height);
            var aliasButton = window.GetVisualDescendants().OfType<Button>().Single(control => control.Name == "EditAssetAlias");
            var aliasPosition = aliasButton.TranslatePoint(default, window)!.Value;
            Assert.True(aliasPosition.Y + aliasButton.Bounds.Height < position.Y);
            await Capture(window, $"editing-context-{language}.png");
        }
        finally
        {
            window.FindControl<MapViewport>("Viewport")!.History.Saved(); window.Close();
            settings.Save(settings.Load() with { Language = previousLanguage }); LocalText.Initialize(settings);
        }
    }

    [AvaloniaFact]
    public async Task MapChecksStayOpenAndInvalidateAfterEditing()
    {
        var window = new MainWindow(); window.Show();
        try
        {
            var viewport = window.FindControl<MapViewport>("Viewport")!;
            await WaitFor(() => viewport.IsHitTestVisible);
            viewport.SetDocument(new MapDocument(4, 4));
            viewport.Document.Set(2, 3, new Cell { BackLibrary = 777, BackImage = 1 });
            viewport.Document.Set(1, 1, new Cell { MiddleLibrary = 778, MiddleImage = 1 });
            typeof(MainWindow).GetMethod("CheckMapClick", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [window, new RoutedEventArgs()]);
            var panel = window.OwnedWindows.OfType<EditorWindow>().Single();
            var list = panel.GetLogicalDescendants().OfType<ListBox>().Single();
            var locate = panel.GetLogicalDescendants().OfType<Button>().Single(control => control.Name == "MapCheckLocate");
            await WaitFor(() => locate.IsEnabled);
            Assert.Equal(2, list.Items.Count);
            locate.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(panel.IsVisible);
            Assert.Equal(new Rect(2, 3, 1, 1), viewport.Selection);
            panel.GetLogicalDescendants().OfType<Button>().Single(control => Equals(control.Content, LocalText.T("Next finding")))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(new Rect(1, 1, 1, 1), viewport.Selection);
            Assert.True(viewport.IsHitTestVisible);
            await Capture(panel, "map-check-continuous-en.png");
            viewport.Document.Set(2, 3, new Cell());
            viewport.Document.Set(1, 1, new Cell());
            viewport.Redo();
            Assert.False(locate.IsEnabled);
            var refresh = panel.GetLogicalDescendants().OfType<Button>().Single(control => control.Name == "MapCheckRefresh");
            refresh.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitFor(() => refresh.IsEnabled && list.Items.Count == 0);
            Assert.True(panel.IsVisible);
            panel.Close();
        }
        finally { window.FindControl<MapViewport>("Viewport")!.History.Saved(); window.Close(); }
    }

    [AvaloniaFact]
    public void CreationShowsPersistentSummaryAndFieldErrors()
    {
        var window = new NewMapWindow(System.IO.Path.GetTempPath(), new Dictionary<MapCreationKind, string?>()); window.Show();
        try
        {
            window.Width = 600; window.Height = 480;
            window.WidthBox.Text = "not a number";
            window.NameBox.Text = "bad/name";
            Dispatcher.UIThread.RunJobs();
            var summary = window.GetLogicalDescendants().OfType<TextBlock>().Single(control => control.Name == "QuickMapSetupSummary");
            var errors = window.GetLogicalDescendants().OfType<TextBlock>().Where(control => control.Name?.EndsWith("FieldError") == true).ToArray();
            Assert.True(summary.IsVisible);
            Assert.Contains("bad/name", summary.Text);
            Assert.True(errors.Count(control => control.IsVisible) >= 2);
            Assert.False(window.CreateButton.IsEnabled);
            var locate = window.GetLogicalDescendants().OfType<Button>().Single(control => control.Name == "QuickMapLocateError");
            locate.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(window.WidthBox.IsFocused);
        }
        finally { window.Close(); }
    }

    [Fact]
    public void NamedUndoSurvivesRedoWithoutChangingSaveState()
    {
        var document = new MapDocument(16, 16); var history = new EditHistory();
        history.Begin("Fill selection"); history.Record(document, 0, 0); document.Set(0, 0, new Cell { MiddleImage = 1 }); history.Commit();
        Assert.Equal("Fill selection", history.UndoName); Assert.True(history.Dirty);
        history.Saved(); history.Undo(document);
        Assert.Equal("Fill selection", history.RedoName); Assert.True(history.Dirty);
        history.Redo(document); Assert.Equal("Fill selection", history.UndoName); Assert.False(history.Dirty);
    }

    [Fact]
    public void LocalAliasesPersistWithoutCrossingLibraryIdentity()
    {
        string root = System.IO.Path.Combine(TestApplication.Root, Guid.NewGuid().ToString("N"));
        var store = new AssetHistoryStore(System.IO.Path.Combine(root, "assets.json"));
        var asset = AssetReference.Tile(System.IO.Path.Combine(root, "Tiles.lib"), 0, 10, MapLayer.Ground, TilePatternLayout.None);
        store.SetAlias(asset, "青石地板"); store.Remember(asset); store.ToggleFavourite(asset); store.Save();
        var loaded = new AssetHistoryStore(store.FilePath); loaded.Load();
        Assert.Equal("青石地板", loaded.Alias(asset)); Assert.True(loaded.IsFavourite(asset)); Assert.Single(loaded.Recent);
        Assert.Empty(loaded.Alias(asset with { Source = System.IO.Path.Combine(root, "other", "Tiles.lib") }));
        loaded.SetAlias(asset, ""); loaded.Save(); loaded.Load(); Assert.Empty(loaded.Aliases);
    }

    [AvaloniaFact]
    public async Task UsedArtworkAndAliasesFilterTheCurrentLayerAndRefreshAfterChanges()
    {
        var history = new AssetHistoryStore(System.IO.Path.Combine(TestApplication.Root, Guid.NewGuid().ToString("N"), "assets.json"));
        var window = new MainWindow(history); window.Show();
        try
        {
            var viewport = window.FindControl<MapViewport>("Viewport")!; await WaitFor(() => viewport.IsHitTestVisible);
            viewport.SetDocument(new MapDocument(16, 16));
            window.FindControl<ListBox>("LayerList")!.SelectedIndex = (int)MapLayer.Ground;
            typeof(MainWindow).GetMethod("SetTool", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [EditorTool.Pencil]);
            var library = viewport.Assets!.Libraries[0];
            history.SetAlias(AssetReference.Tile(library.Path, 0, 0, MapLayer.Ground, TilePatternLayout.None), "blue floor");
            var used = window.GetLogicalDescendants().OfType<CheckBox>().Distinct().Single(control => control.Name == "UsedArtworkOnly");
            var filter = typeof(MainWindow).GetMethod("FilteredIndices", BindingFlags.NonPublic | BindingFlags.Instance)!;
            List<int> Indices() => (List<int>)filter.Invoke(window, [library])!;
            used.IsChecked = true; Assert.Empty(Indices());
            viewport.Document.Set(0, 0, new Cell { BackImage = 1, MiddleLibrary = 1, MiddleImage = 1 });
            window.FindControl<TextBox>("AssetSearch")!.Text = "blue floor";
            Assert.Equal(new[] { 0 }, Indices());
            window.FindControl<TextBox>("AssetSearch")!.Text = "missing"; Assert.Empty(Indices());
            window.FindControl<TextBox>("AssetSearch")!.Text = "";
            viewport.Document.Set(0, 0, new Cell { MiddleLibrary = 1, MiddleImage = 1 }); Assert.Empty(Indices());
            Assert.False(viewport.History.Dirty);
        }
        finally { window.Close(); }
    }

    [Fact]
    public void LateOptionalResourceReportDoesNotMakeAnOldPreviewReady()
    {
        var state = new GeneratorPreviewState(); int completions = 0; state.Completed += (_, _) => completions++;
        state.SetPhase(GeneratorPreviewPhase.Generating); state.Complete();
        state.Report(new([new("River banks", false, false)]));
        Assert.Equal(GeneratorPreviewPhase.Ready, state.Phase); Assert.Equal(1, completions);
        state.SetPhase(GeneratorPreviewPhase.Waiting); Assert.Null(state.Resources);
        state.Report(new([new("River banks", false, true)]));
        Assert.Equal(GeneratorPreviewPhase.Waiting, state.Phase); Assert.Equal(1, completions);
    }

    [AvaloniaFact]
    public async Task FailedCreationProvidesRealSourcesAndSafeRecoveryStepsInChinese()
    {
        string root = System.IO.Path.Combine(TestApplication.Root, Guid.NewGuid().ToString("N"));
        var settings = new SettingsStore(System.IO.Path.Combine(root, "settings.json"));
        string previousLanguage = LocalText.Language;
        settings.Save(new(System.IO.Path.Combine(TestApplication.Root, "Data"), "zh-CN")); LocalText.Initialize(settings);
        using var assets = AssetStore.Load(System.IO.Path.Combine(TestApplication.Root, "Data"));
        var window = new ProvinceGeneratorWindow(new MapDocument(128, 128), assets);
        try
        {
            QuickMapPreview.Configure(window, new MapCreationSession(new(MapCreationKind.Province, 128, 128, "预览地图", SaveImmediately: false)), _ => false);
            window.Show(); await WaitFor(() => window.PreviewState.Phase == GeneratorPreviewPhase.Failed);
            var controls = window.GetLogicalDescendants().ToArray();
            var diagnostics = controls.OfType<Expander>().Single(control => control.Name == "QuickMapResourceDiagnostics");
            var detail = controls.OfType<TextBlock>().Single(control => control.Name == "QuickMapResourceDetails");
            Assert.Contains(assets.LoadedFolder!, detail.Text);
            Assert.Contains(assets.Libraries[0].Path, detail.Text);
            Assert.Contains("重新打开快速创建", detail.Text);
            Assert.False(controls.OfType<Button>().Single(control => control.Name == "ApplyButton").IsEnabled);
            Assert.False(diagnostics.IsExpanded); await Capture(window, "creation-failed-zh.png");
            diagnostics.IsExpanded = true; await Capture(window, "creation-diagnostics-zh.png");
        }
        finally { window.Close(); settings.Save(settings.Load() with { Language = previousLanguage }); LocalText.Initialize(settings); }
    }

    [AvaloniaFact]
    public async Task HoverWaitsAndHidesWhileDrawingWithoutChangingTheMap()
    {
        var window = new MainWindow(); window.Show();
        try
        {
            var viewport = window.FindControl<MapViewport>("Viewport")!; await WaitFor(() => viewport.IsHitTestVisible);
            viewport.SetDocument(new MapDocument(16, 16));
            var toggle = window.FindControl<MenuItem>("CellHoverToggle")!; toggle.IsChecked = true;
            var point = viewport.TranslatePoint(new Point(132, 118), window)!.Value;
            window.MouseMove(point);
            var hover = window.FindControl<Border>("CellHoverInfo")!;
            Assert.False(hover.IsVisible); await WaitFor(() => hover.IsVisible);
            Assert.False(window.FindControl<TextBlock>("CellHoverDetails")!.IsVisible);
            await Capture(window, "hover-summary-en.png");
            window.MouseDown(point, MouseButton.Left); Assert.False(hover.IsVisible);
            window.MouseMove(point + new Vector(5, 0)); Assert.False(hover.IsVisible);
            window.MouseUp(point, MouseButton.Left);
            await Capture(window, "editing-context-en.png");
        }
        finally { window.FindControl<MapViewport>("Viewport")!.History.Saved(); window.Close(); }
    }

    private static async Task Capture(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("CRYSTALSTUDIO_EDITOR_UX_ARTIFACTS") is not { Length: > 0 } folder) return;
        System.IO.Directory.CreateDirectory(folder); await Task.Delay(120); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
        using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame); frame.Save(System.IO.Path.Combine(folder, name));
    }

    internal static async Task WaitFor(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 300; attempt++)
        {
            Dispatcher.UIThread.RunJobs();
            if (condition()) return;
            await Task.Delay(20);
        }
        Assert.Fail("The editor did not reach the expected state.");
    }
}
