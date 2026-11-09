using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using CrystalStudio.Rendering;
using System.Diagnostics;
using System.Reflection;

namespace CrystalStudio.Tests;

public sealed class EditorWorkflowTests
{
    [Fact]
    public void ImpactCountsAdditionsDeletionsAndChangesWithoutMutatingDocuments()
    {
        var original = new MapDocument(32, 32); var generated = new MapDocument(32, 32);
        original.Set(1, 1, new Cell { BackImage = 1 }); generated.Set(1, 1, new Cell { BackImage = 1 });
        original.Set(4, 5, new Cell { MiddleImage = 1 });
        generated.Set(6, 7, new Cell { Foreground = new(2, 1) });
        original.Set(8, 9, new Cell { BackImage = 1 }); generated.Set(8, 9, new Cell { BackImage = 0x60000001 });
        long version = original.PickupRevision;
        var impact = GenerationImpact.Compare(original, generated);
        Assert.Equal(3, impact.Affected); Assert.Equal(3, impact.Coverage.Count(value => value != 0));
        Assert.Equal(version, original.PickupRevision); Assert.Equal((ushort)1, original.Get(4, 5).MiddleImage);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => GenerationImpact.Compare(original, generated, cancellation.Token));
    }

    [AvaloniaFact]
    public void TerrainImpactIsReadOnlyAndDescribesTheExistingWriteBoundary()
    {
        var document = new MapDocument(32, 32);
        document.Set(3, 4, new Cell { Foreground = new(2, 1), BackImage = 0x60000000 });
        var plan = new TerrainPlan(document, new(2, 2, 8, 8), [new(2, 2, 0, 0, "test", 15, "test")], 7);
        long version = document.PickupRevision;
        var impact = GenerationImpact.Terrain(plan); using var bitmap = impact.CreateMap();
        Assert.Equal(1, impact.Affected); Assert.Equal(GenerationImpact.Columns, bitmap.PixelWidth);
        Assert.Equal(GenerationImpact.Rows, bitmap.PixelHeight); Assert.Equal(version, document.PickupRevision);
        Assert.Equal(0x60000000, document.Get(3, 4).BackImage); Assert.NotNull(document.Get(3, 4).Foreground);
    }

    [Fact]
    public void CheckComparisonDoesNotClaimTruncatedResultsWereResolved()
    {
        var old = new MapIssue(1, 1, "old"); var remaining = new MapIssue(2, 2, "remaining"); var added = new MapIssue(3, 3, "new");
        var comparison = MapCheckComparison.Compare(new([old, remaining], 2), new([remaining, added], 2))!;
        Assert.Equal(new[] { added }, comparison.Added); Assert.Equal(new[] { remaining }, comparison.Remaining); Assert.Equal(new[] { old }, comparison.Resolved);
        Assert.Null(MapCheckComparison.Compare(new([old], 1200), new([], 0)));
        Assert.Null(MapCheckComparison.Compare(new([], 0), new([added], 1200)));
        Assert.Null(MapCheckComparison.Compare(null, new([], 0)));
    }

    [AvaloniaTheory]
    [InlineData("en-US")]
    [InlineData("zh-CN")]
    public async Task ComparisonSynchronizesKeyboardNavigationAndShowsOnlyParameterDifferences(string language)
    {
        var store = new SettingsStore(); var saved = store.Load(); store.Save(saved with { Language = language }); LocalText.Initialize(store);
        using var image = Bitmap(800, 500); using var candidates = new QuickMapCandidates();
        var settings = new MapCreationSettings(MapCreationKind.Terrain, 128, 128);
        var first = candidates.Remember(settings, new Dictionary<string, string> { ["SeedBox"] = "1", ["CoverageSlider"] = "0.2" }, image)!;
        var second = candidates.Remember(settings, new Dictionary<string, string> { ["SeedBox"] = "2", ["CoverageSlider"] = "0.2" }, image)!;
        var window = new QuickMapComparisonWindow(candidates.Items.ToArray(), second); window.Show();
        try
        {
            var left = Find<MapPreviewViewport>(window, "QuickMapCompareLeft"); var right = Find<MapPreviewViewport>(window, "QuickMapCompareRight");
            left.ChangeZoom(2); Assert.Equal(2, right.Zoom);
            right.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.OemPlus }); Assert.Equal(left.Zoom, right.Zoom);
            left.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Home }); Assert.Equal(1, right.Zoom);
            var text = Find<TextBlock>(window, "QuickMapParameterDifferences").Text;
            Assert.Contains("1 → 2", text); Assert.DoesNotContain("0.2", text);
            window.Width = 660; window.Height = 440; Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.True(left.Bounds.Width > 100 && left.Bounds.Height > 100);
            await Capture(window, "compare-" + language + "-minimum.png");
            Assert.Same(first, candidates.Items[0]); Assert.Equal(2, candidates.Items.Count);
        }
        finally { window.Close(); store.Save(saved); LocalText.Initialize(store); }
    }

    [AvaloniaFact]
    public void DetailCandidatesAreUpgradedToBoundedOverviewSnapshotsWithoutDuplicatingParameters()
    {
        using var image = Bitmap(1600, 1000); using var candidates = new QuickMapCandidates();
        var settings = new MapCreationSettings(MapCreationKind.Terrain, 128, 128);
        var parameters = new Dictionary<string, string> { ["SeedBox"] = "7" };
        var detail = candidates.Remember(settings, parameters, image, false)!; Assert.Null(detail.Comparison);
        Assert.Same(detail, candidates.Remember(settings, parameters, image)); Assert.Single(candidates.Items);
        Assert.Equal(640, detail.Comparison!.PixelWidth); Assert.Equal(400, detail.Comparison.PixelHeight);
        Assert.InRange(detail.Thumbnail!.PixelWidth, 1, 144); Assert.InRange(detail.Thumbnail.PixelHeight, 1, 80);
    }

    [AvaloniaFact]
    public async Task ExplicitCancellationAcknowledgesCompletionAndRecheckKeepsTheOriginalMapUntouched()
    {
        using var assets = AssetStore.Load(System.IO.Path.Combine(TestApplication.Root, "Data"));
        var document = new MapDocument(128, 128); document.Set(3, 3, new Cell { MiddleImage = 1 }); long version = document.PickupRevision;
        var window = new TerrainGeneratorWindow(document, assets, Terrain()); window.Show();
        try
        {
            var apply = Find<Button>(window, "ApplyButton");
            await EditorExperienceTests.WaitFor(() => window.PreferencesRestored && apply.IsEnabled);
            Assert.NotNull(window.PreviewState.Impact);
            Find<Expander>(window, "GeneratorImpact").IsExpanded = true;
            await Capture(window, "application-impact.png");
            bool stopOnce = true, requested = false;
            window.PreviewState.Changed += (_, _) =>
            {
                if (!stopOnce || window.PreviewState.Phase != GeneratorPreviewPhase.Generating) return;
                stopOnce = false; window.CancelPreview(); requested = window.PreviewState.Phase == GeneratorPreviewPhase.Cancelling;
            };
            Find<TextBox>(window, "SeedBox").Text = "45";
            await EditorExperienceTests.WaitFor(() => window.PreviewState.Phase == GeneratorPreviewPhase.Cancelled && Find<Button>(window, "GeneratorRecheck").IsEnabled);
            Assert.True(requested); Assert.Null(window.Plan); Assert.False(apply.IsEnabled); Assert.NotNull(Find<Image>(window, "PreviewImage").Source);
            Assert.Equal(version, document.PickupRevision);
            Find<Button>(window, "GeneratorRecheck").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await EditorExperienceTests.WaitFor(() => window.Plan?.Seed == 45 && apply.IsEnabled);
            Assert.Equal(version, document.PickupRevision); Assert.Single(document.Cells);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData("en-US")]
    [InlineData("zh-CN")]
    public async Task ResourceFiltersExposeSourcesAndCopyAReadOnlyReport(string language)
    {
        var store = new SettingsStore(); var saved = store.Load(); store.Save(saved with { Language = language }); LocalText.Initialize(store);
        var state = new GeneratorPreviewState(); state.SetStage("Checking required and optional artwork…");
        state.Report(new([new("Base ground", true, false, "C:\\fixture\\Tiles.lib"), new("River banks", false, false, "C:\\fixture\\Objects.lib"), new("Ground", true, true)]));
        var window = new EditorWindow { Content = new ScrollViewer { Content = new ResourceDiagnosticsView(state) }, Width = 520, Height = 500 }; window.Show();
        try
        {
            var filter = Find<ComboBox>(window, "QuickMapResourceFilter"); var list = Find<ListBox>(window, "QuickMapResourceIssues");
            filter.SelectedIndex = 1; Assert.Single(list.Items); Assert.Contains("Tiles.lib", Find<TextBlock>(window, "QuickMapResourceDetails").Text);
            filter.SelectedIndex = 2; Assert.Single(list.Items); Assert.Contains("Objects.lib", Find<TextBlock>(window, "QuickMapResourceDetails").Text);
            string report = ResourceDiagnosticsView.BuildReport(state);
            Assert.Contains("Tiles.lib", report); Assert.Contains("Objects.lib", report); Assert.Equal(2, state.Resources!.Errors.Count + state.Resources.Warnings.Count);
            Find<Button>(window, "QuickMapCopyDiagnostics").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Capture(window, "resource-report-" + language + ".png");
            Assert.Equal(GeneratorPreviewPhase.Checking, state.Phase);
        }
        finally { window.Close(); store.Save(saved); LocalText.Initialize(store); }
    }

    [AvaloniaFact]
    public async Task RecheckKeepsSelectionAndTypeWhileShowingAddedRemainingAndResolvedFindings()
    {
        using var assets = new AssetStore();
        var document = new MapDocument(8, 8); document.Set(1, 1, new Cell { BackLibrary = 9, BackImage = 1 }); document.Set(2, 2, new Cell { BackLibrary = 9, BackImage = 2 });
        MapIssue? located = null;
        var window = new MapCheckWindow(() => (document, assets), issue => located = issue); window.Show();
        try
        {
            var refresh = Find<Button>(window, "MapCheckRefresh"); var findings = Find<ListBox>(window, "MapCheckFindings");
            await EditorExperienceTests.WaitFor(() => refresh.IsEnabled && findings.ItemCount == 2);
            var groups = Find<ComboBox>(window, "MapCheckGroups"); groups.SelectedItem = LocalText.T("Image references"); findings.SelectedIndex = 1;
            document.Set(1, 1, new Cell()); document.Set(3, 3, new Cell { BackLibrary = 9, BackImage = 3 });
            window.RefreshValidity(); refresh.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await EditorExperienceTests.WaitFor(() => refresh.IsEnabled && Find<ComboBox>(window, "MapCheckChanges").IsEnabled);
            Assert.Equal(2, ((MapIssue)findings.SelectedItem!).X); Assert.Equal(LocalText.T("Image references"), groups.SelectedItem);
            var changes = Find<ComboBox>(window, "MapCheckChanges");
            changes.SelectedIndex = 1; Assert.Single(findings.Items); Assert.Equal(3, ((MapIssue)findings.Items[0]!).X);
            changes.SelectedIndex = 2; Assert.Single(findings.Items); Assert.Equal(2, ((MapIssue)findings.Items[0]!).X);
            changes.SelectedIndex = 3; Assert.Single(findings.Items); Assert.Equal(1, ((MapIssue)findings.Items[0]!).X);
            changes.SelectedIndex = 0; findings.SelectedIndex = 0;
            var expected = findings.Items[1] as MapIssue;
            window.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.F3 }); Assert.Equal(expected, located);
            await Capture(window, "map-check-delta.png");
            document = new MapDocument(8, 8); refresh.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await EditorExperienceTests.WaitFor(() => refresh.IsEnabled && findings.ItemCount == 0); Assert.False(changes.IsEnabled);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ModuleCandidatesCompareBoundedFootprintsWithoutRetainingMapsAndStopExplicitly()
    {
        string root = System.IO.Path.Combine(TestApplication.Root, "module-workflow-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var cells = Enumerable.Range(0, 4).SelectMany(row => Enumerable.Range(0, 4).Select(column => new StampCell(column, row, new Cell { MiddleLibrary = 1, MiddleImage = 1 }))).ToArray();
        var ports = new ModuleConnections(1, [new("north", 1, 0, ModuleEdge.North, "path"), new("east", 3, 1, ModuleEdge.East, "path"),
            new("south", 1, 3, ModuleEdge.South, "path"), new("west", 0, 1, ModuleEdge.West, "path")]);
        CellCodec.SaveStamp(System.IO.Path.Combine(root, "room.X"), cells);
        var entry = new CategorizedStamp("room", "room.X", "Room", null, null, [], [], [], [], null, "unknown", null, false, "unreviewed") { Connections = ports };
        var window = new ModuleGenerationWindow(new(1, root, root, [entry]), null); window.Show();
        try
        {
            await window.GeneratePreviewAsync(); window.SeedBox.Text = "37"; await window.GeneratePreviewAsync();
            Assert.True(Find<Button>(window, "CompareModuleCandidates").IsEnabled);
            Assert.Empty(window.Plan!.Placement.Source.Cells);
            using var bitmap = ModuleLayoutSnapshot.Create(window.Plan); Assert.Equal(320, bitmap.PixelWidth); Assert.Equal(200, bitmap.PixelHeight);
            window.CountBox.Text = "128";
            var work = window.GeneratePreviewAsync();
            var stop = window.GetLogicalDescendants().OfType<Button>().Single(button => Equals(button.Content, LocalText.T("Stop generation")));
            stop.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.Equal(ModuleGenerationViewState.Cancelling, window.ViewState);
            await work; Assert.NotEqual(ModuleGenerationViewState.Cancelling, window.ViewState); Assert.False(window.ApplyButton.IsEnabled);
            Assert.Null(window.Plan); Assert.NotNull(window.DisplayedPlan);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void SparseLargeMapImpactAndCandidateStorageStayBounded()
    {
        var source = new MapDocument(1024, 1024); var generated = new MapDocument(1024, 1024);
        for (int row = 0; row < 1024; row += 2) for (int column = 0; column < 1024; column += 2) generated.Set(column, row, new Cell { BackImage = 1 });
        var timer = Stopwatch.StartNew(); long before = GC.GetTotalAllocatedBytes();
        var impact = GenerationImpact.Compare(source, generated); timer.Stop();
        double impactMilliseconds = timer.Elapsed.TotalMilliseconds; long impactAllocation = GC.GetTotalAllocatedBytes() - before;
        Assert.Equal(262144, impact.Affected); Assert.Equal(16000, impact.Coverage.Length); Assert.Empty(source.Cells);
        using var image = Bitmap(1600, 1000); using var candidates = new QuickMapCandidates();
        before = GC.GetTotalAllocatedBytes(); timer.Restart();
        for (int seed = 0; seed < 12; seed++) candidates.Remember(new(MapCreationKind.Terrain, 1024, 1024), new Dictionary<string, string> { ["SeedBox"] = seed.ToString() }, image);
        Assert.Equal(5, candidates.Items.Count);
        timer.Stop();
        long snapshotBytes = candidates.Items.Sum(candidate => (long)candidate.Comparison!.PixelWidth * candidate.Comparison.PixelHeight * 4 + (long)candidate.Thumbnail!.PixelWidth * candidate.Thumbnail.PixelHeight * 4);
        Assert.InRange(snapshotBytes, 1, 5 * (640L * 400 + 144L * 80) * 4);
        if (Environment.GetEnvironmentVariable("CRYSTALSTUDIO_WORKFLOW_ARTIFACTS") is { Length: > 0 } folder)
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(System.IO.Path.Combine(folder, "performance.csv"), "scenario,observations,elapsed_ms,managed_allocated_bytes,snapshot_pixel_bytes\n"
                + FormattableString.Invariant($"sparse-impact,262144,{impactMilliseconds:F3},{impactAllocation},0\n")
                + FormattableString.Invariant($"candidate-churn,12,{timer.Elapsed.TotalMilliseconds:F3},{GC.GetTotalAllocatedBytes() - before},{snapshotBytes}\n"));
        }
    }

    private static TerrainCatalogue Terrain() => new() { Sets = [new TerrainSet { Id = "test", Name = "Fixture ground", Base = true, Biome = "Province", Library = 0,
        Shapes = new() { [15] = [new TerrainVariant { Index = 0, Weight = 1 }] } }] };
    private static RasterImage Bitmap(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (int row = 0; row < height; row++) for (int column = 0; column < width; column++)
        {
            int index = (row * width + column) * 4;
            pixels[index] = (byte)(40 + column * 150 / width); pixels[index + 1] = (byte)(50 + row * 120 / height);
            pixels[index + 2] = (byte)((column / 40 + row / 40) % 2 == 0 ? 30 : 100); pixels[index + 3] = 255;
        }
        return RasterImage.Create(width, height, 96, 96, RasterFormats.Bgra32, null, pixels, width * 4);
    }
    private static T Find<T>(EditorWindow window, string name) where T : Control => window.GetLogicalDescendants().OfType<T>().Single(control => control.Name == name);
    private static async Task Capture(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("CRYSTALSTUDIO_WORKFLOW_ARTIFACTS") is not { Length: > 0 } folder) return;
        Directory.CreateDirectory(folder); await Task.Delay(100); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
        using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame); frame.Save(System.IO.Path.Combine(folder, name));
    }
}
