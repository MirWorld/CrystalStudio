using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using CrystalStudio.Rendering;

namespace CrystalStudio.Tests;

public sealed class QuickMapWorkflowInteractionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreviewProjectionRoundTripsRealMapCoordinates(bool padded)
    {
        var region = new Rect(100, 80, 32, 24); var bitmap = new Size(1600, 1000); var cell = new Point(107.5, 91.5);
        var normalized = MapPreviewProjection.Project(cell, region, bitmap, padded);
        Assert.True(MapPreviewRegion.TrySelect(normalized, region, bitmap, padded, out var restored));
        Assert.Equal(cell.X, restored.X, 8); Assert.Equal(cell.Y, restored.Y, 8);
        if (padded) Assert.False(MapPreviewRegion.TrySelect(default, region, bitmap, true, out _));
    }

    [AvaloniaFact]
    public void MarkerWorkspaceSynchronizesSelectionsAndSupportsAtomicUndoAndRedo()
    {
        var design = new MapDesignSettings(); var editor = new MapDesignEditor(design, 32, 32);
        var workspace = new MapDesignWorkspace(editor, design, 32, 32);
        workspace.AddPoint(3, 4); Assert.Equal(0, workspace.PointIndex);
        workspace.MoveSelected(2, 3); Assert.Equal((5, 7), (design.Points[0].X, design.Points[0].Y));
        workspace.Undo(); Assert.Equal((3, 4), (design.Points[0].X, design.Points[0].Y));
        workspace.Redo(); Assert.Equal((5, 7), (design.Points[0].X, design.Points[0].Y));
        workspace.AddRegion(new(8, 8, 4, 5), MapRegionPurpose.Protect); workspace.ResizeSelected(14, 15);
        Assert.Equal((7, 8), (design.Regions[0].Width, design.Regions[0].Height));
        workspace.MoveSelected(100, 100); Assert.Equal((25, 24), (design.Regions[0].X, design.Regions[0].Y));
        workspace.Undo(); Assert.Equal((8, 8), (design.Regions[0].X, design.Regions[0].Y));
        workspace.Select(0, -1);
        var pointList = editor.GetLogicalDescendants().OfType<ListBox>().Single(list => list.Name == "QuickMapDesignPoints");
        Assert.Equal(0, pointList.SelectedIndex); workspace.DeleteSelected(); Assert.Empty(design.Points);
        workspace.Undo(); Assert.Single(design.Points);
    }

    [AvaloniaFact]
    public void CanvasGesturesCommitOnceCancelCleanlyAndDoNotEditWhileBusy()
    {
        var design = new MapDesignSettings(); var editor = new MapDesignEditor(design, 16, 16);
        var workspace = new MapDesignWorkspace(editor, design, 16, 16) { Tool = MapDesignTool.Point };
        bool available = true;
        var canvas = new MapPreviewDesign(design, workspace, 16, 16, () => (new Rect(0, 0, 16, 16), false), () => available);
        var bitmap = new Size(1600, 1000);
        Assert.True(canvas.Press(new(.2, .3), bitmap)); canvas.Move(new(.4, .5), bitmap); canvas.Cancel(); Assert.Empty(design.Points);
        canvas.Press(new(.2, .3), bitmap); canvas.Release(new(.2, .3), bitmap); Assert.Equal((3, 4), (design.Points[0].X, design.Points[0].Y));
        workspace.Tool = MapDesignTool.Protect; canvas.Press(new(.2, .3), bitmap); canvas.Release(new(.4, .5), bitmap); Assert.Single(design.Regions);
        Assert.True(canvas.Key(Key.Z, KeyModifiers.Control)); Assert.Empty(design.Regions); Assert.Single(design.Points);
        Assert.True(canvas.Key(Key.Y, KeyModifiers.Control)); Assert.Single(design.Regions);
        available = false; canvas.Press(new(.5, .5), bitmap); canvas.Release(new(.8, .8), bitmap); Assert.Single(design.Regions);
        workspace.Tool = MapDesignTool.Pan; Assert.False(canvas.Press(new(.2, .3), bitmap));
    }

    [AvaloniaFact]
    public void RoleEditorUpdatesAndRestoresAllOptInIntentSettings()
    {
        var design = new MapDesignSettings { Points = [new("Entry", 3, 3), new("Exit", 12, 12)] };
        var editor = new MapDesignEditor(design, 32, 32); editor.Select(0, -1);
        Control Find(string name) => editor.GetLogicalDescendants().OfType<Control>().Single(control => control.Name == name);
        ((ComboBox)Find("QuickMapPointRole")).SelectedItem = MapPointRole.Entry;
        ((NumericUpDown)Find("QuickMapPointClearance")).Value = 2;
        ((Button)Find("QuickMapUpdatePoint")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        ((NumericUpDown)Find("QuickMapMinimumSteps")).Value = 10;
        ((NumericUpDown)Find("QuickMapMaximumSteps")).Value = 100;
        ((CheckBox)Find("QuickMapIntentRequired")).IsChecked = true;
        Assert.Equal(MapPointRole.Entry, design.Points[0].Role); Assert.Equal(2, design.Points[0].ClearRadius); Assert.True(design.IntentRequired);
        var copy = design.Copy(); editor.Restore(new() { Points = [new("Reset", 1, 1)] }); editor.Restore(copy);
        Assert.True(copy.SameAs(design)); Assert.True(editor.InputValid);
    }

    [AvaloniaTheory]
    [InlineData("en-US")]
    [InlineData("zh-CN")]
    public async Task NativePreviewSupportsPointerMarkersStructuralLockDraftsAndSafeVariants(string language)
    {
        using var assets = AssetStore.Load(Path.Combine(TestApplication.Root, "Data"));
        var terrain = new TerrainCatalogue { Sets = [new TerrainSet { Id = "test", Name = "Fixture", Biome = "Province", Base = true, Library = 0, Shapes = new() { [15] = [new TerrainVariant { Index = 0 }] } }] };
        var source = new MapDocument(64, 64); var session = new MapCreationSession(new(MapCreationKind.Terrain, 64, 64, "Workflow", null, false), QuickMapWorkflowTests.Parameters(5));
        string root = Path.Combine(TestApplication.Root, "workflow-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        string previousLanguage = LocalText.Language;
        var languageStore = new SettingsStore(Path.Combine(root, "language.json")); languageStore.Save(new(Path.Combine(TestApplication.Root, "Data"), language)); LocalText.Initialize(languageStore);
        session.DraftStore = new QuickMapDraftStore(Path.Combine(root, "task.json")); session.RecipeFactory = () => QuickMapWorkflowTests.Recipe(session);
        var window = new TerrainGeneratorWindow(source, assets, terrain, session.Parameters) { Width = 1100, Height = 850 };
        QuickMapPreview.Configure(window, session, _ => false);
        try
        {
            window.Show(); await Until(() => window.PreviewState.Phase == GeneratorPreviewPhase.Ready && window.PreferencesRestored);
            Control Find(string name) => window.GetLogicalDescendants().OfType<Control>().Single(control => control.Name == name);
            var viewport = (MapPreviewViewport)Find("QuickMapViewport"); var image = (Image)Find("PreviewImage");
            var tools = (ComboBox)Find("QuickMapDesignTool"); tools.SelectedItem = MapDesignTool.Point;
            window.UpdateLayout();
            var frame = window.DesignFrame; var normalized = MapPreviewProjection.Project(new(10.5, 12.5), frame.Region, image.Source!.Size, frame.Padded);
            var bounds = new PreviewNavigation().ImageRect(viewport.Bounds.Size, image.Source.Size);
            var screen = viewport.TranslatePoint(new Point(bounds.X + normalized.X * bounds.Width, bounds.Y + normalized.Y * bounds.Height), window)!.Value;
            window.MouseDown(screen, MouseButton.Left); window.MouseUp(screen, MouseButton.Left);
            Assert.Single(session.Design.Points);
            normalized = MapPreviewProjection.Project(new(20.5, 22.5), frame.Region, image.Source.Size, frame.Padded);
            screen = viewport.TranslatePoint(new Point(bounds.X + normalized.X * bounds.Width, bounds.Y + normalized.Y * bounds.Height), window)!.Value;
            window.MouseDown(screen, MouseButton.Left); window.MouseUp(screen, MouseButton.Left);
            await Until(() => session.Design.Points.Count == 2 && window.PreviewState.Phase == GeneratorPreviewPhase.Ready);
            Assert.Equal((10, 12), (session.Design.Points[0].X, session.Design.Points[0].Y));
            var favorite = (CheckBox)Find("QuickMapFavorite0"); favorite.IsChecked = true;
            var variants = window.GetLogicalDescendants().OfType<QuickMapVariants>().Single();
            Assert.True(await variants.LockAsync()); Assert.True(session.StructureLocked); Assert.False(Find("SeedBox").IsEffectivelyEnabled);
            Assert.False(Find("RandomizeButton").IsEffectivelyEnabled);
            Assert.All(((DockPanel)Find("SeedBox").Parent!).Children.OfType<Button>(), button => Assert.False(button.IsEffectivelyEnabled));
            string fingerprint = MapDecorationVariation.Fingerprint(session.LockedBaseline!);
            await variants.GenerateAsync(); Assert.NotNull(session.DecorationVariant);
            Assert.Equal(fingerprint, MapDecorationVariation.Fingerprint(session.Output!)); Assert.Empty(source.Cells);
            Assert.True(await QuickMapPreview.ConfirmAsync(window));
            ((Button)Find("QuickMapSaveDraft")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var saved = session.DraftStore.Load(); Assert.Contains(saved.Candidates, candidate => candidate.Favorite); Assert.Equal(1, saved.Recipe.DecorationSeed);
            ((Expander)Find("QuickMapVariants")).IsExpanded = true;
            window.MouseMove(new Point(1080, 820)); ToolTip.SetIsOpen(viewport, false);
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            string folder = Path.Combine(AppContext.BaseDirectory, "QuickMapScreenshots"); Directory.CreateDirectory(folder);
            using var screenshot = window.CaptureRenderedFrame(); Assert.NotNull(screenshot);
            using var output = File.Create(Path.Combine(folder, "workflow-controlled-variants-" + language + ".png")); screenshot.Save(output);
        }
        finally { window.Close(); session.Candidates.Dispose(); languageStore.Save(new(Path.Combine(TestApplication.Root, "Data"), previousLanguage)); LocalText.Initialize(languageStore); }
    }

    [AvaloniaFact]
    public void WizardResumesTheTaskWithoutEnablingOverwriteOrDroppingMarkers()
    {
        var source = new MapCreationSession(new(MapCreationKind.Terrain, 64, 64, "SavedTask", Path.GetTempPath(), true), QuickMapWorkflowTests.Parameters(91));
        source.Design.Points.Add(new("Entry", 3, 3) { Role = MapPointRole.Entry });
        var draft = QuickMapDraftStore.Capture(source, QuickMapWorkflowTests.Recipe(source));
        var session = new MapCreationSession(new(MapCreationKind.Blank, 128, 128));
        var available = Enum.GetValues<MapCreationKind>().ToDictionary(kind => kind, _ => (string?)null);
        var wizard = new NewMapWindow(Path.GetTempPath(), available, session.Settings);
        wizard.OfferDraft(() => { QuickMapDraftStore.Restore(draft, session, true); return true; }, session);
        try
        {
            wizard.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "QuickMapResumeDraft").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(MapCreationKind.Terrain, wizard.KindBox.SelectedItem); Assert.Equal("64", wizard.WidthBox.Text); Assert.Equal("SavedTask", wizard.NameBox.Text);
            Assert.False(wizard.SaveToggle.IsChecked); Assert.Equal("91", wizard.SelectedParameters!["SeedBox"]); Assert.Single(session.Design.Points);
            Assert.True(wizard.CreateButton.IsEnabled); Assert.Equal(MapPointRole.Entry, session.Design.Points[0].Role);
        }
        finally { wizard.Close(); source.Candidates.Dispose(); session.Candidates.Dispose(); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RequestedVariantsAreRebuiltOrRequireAnExplicitFallback(bool missingType)
    {
        using var assets = AssetStore.Load(Path.Combine(TestApplication.Root, "Data"));
        var terrain = new TerrainCatalogue { Sets = [new TerrainSet { Id = "test", Name = "Fixture", Biome = "Province", Base = true, Library = 0, Shapes = new() { [15] = [new TerrainVariant { Index = 0 }] } }] };
        var session = new MapCreationSession(new(MapCreationKind.Terrain, 64, 64, "Restored", null, false), QuickMapWorkflowTests.Parameters(5))
            { RequestedDecorationSeed = 41, DecorationNames = missingType ? ["Missing approved type"] : [] };
        var window = new TerrainGeneratorWindow(new MapDocument(64, 64), assets, terrain, session.Parameters);
        QuickMapPreview.Configure(window, session, _ => false);
        try
        {
            window.Show();
            var apply = window.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "ApplyButton");
            if (missingType)
            {
                var feedback = window.GetLogicalDescendants().OfType<TextBlock>().Single(text => text.Name == "QuickMapVariationFeedback");
                await Until(() => feedback.Text?.Contains("requested native decoration type") == true && !session.VariationBusy);
                Assert.False(apply.IsEnabled); Assert.Null(session.DecorationVariant);
                window.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "QuickMapDiscardRequestedVariant").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.True(apply.IsEnabled); Assert.Null(session.RequestedDecorationSeed);
            }
            else
            {
                await Until(() => session.DecorationVariant?.Seed == 41 && !session.VariationBusy && apply.IsEnabled);
                Assert.True(session.StructureLocked); Assert.Null(session.RequestedDecorationSeed);
                var decorationSeed = window.GetLogicalDescendants().OfType<NumericUpDown>().Single(number => number.Name == "QuickMapDecorationSeed");
                decorationSeed.Value = 42; Assert.Null(session.DecorationVariant); Assert.Same(session.LockedBaseline, session.Output);
            }
        }
        finally { window.Close(); session.Candidates.Dispose(); }
    }

    [AvaloniaFact]
    public async Task ConstraintIssuesLocateTheMapAndBecomeStaleAfterEditing()
    {
        using var assets = AssetStore.Load(Path.Combine(TestApplication.Root, "Data"));
        var terrain = new TerrainCatalogue { Sets = [new TerrainSet { Id = "test", Name = "Fixture", Biome = "Province", Base = true, Library = 0, Shapes = new() { [15] = [new TerrainVariant { Index = 0 }] } }] };
        var parameters = QuickMapWorkflowTests.Parameters(5); parameters[GenerationAlgorithms.Parameter] = "1";
        var session = new MapCreationSession(new(MapCreationKind.Terrain, 64, 64, "Issues", null, false), parameters)
            { Design = new() { FootprintSize = 2, Points = [new("Too close to edge", 63, 63)] } };
        var window = new TerrainGeneratorWindow(new MapDocument(64, 64), assets, terrain, parameters);
        QuickMapPreview.Configure(window, session, _ => false);
        try
        {
            window.Show(); await Until(() => window.PreviewState.Phase == GeneratorPreviewPhase.Ready && window.PreferencesRestored);
            var production = window.GetLogicalDescendants().OfType<QuickMapProduction>().Single();
            Assert.True(await production.CheckAsync(false));
            var issues = window.GetLogicalDescendants().OfType<ListBox>().Single(list => list.Name == "QuickMapConstraintIssues");
            Assert.Single(issues.Items); issues.SelectedIndex = 0;
            window.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "QuickMapLocateIssue").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(4, window.GetLogicalDescendants().OfType<MapPreviewViewport>().Single(view => view.Name == "QuickMapViewport").Zoom);
            var numbers = production.DesignEditor.GetLogicalDescendants().OfType<NumericUpDown>().ToArray();
            numbers.Single(number => number.Name == "QuickMapPointX").Value = 60; numbers.Single(number => number.Name == "QuickMapPointY").Value = 60;
            production.DesignEditor.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "QuickMapUpdatePoint").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Empty(issues.Items); Assert.True(await production.CheckAsync(false)); Assert.Empty(issues.Items);
        }
        finally { window.Close(); session.Candidates.Dispose(); }
    }

    [AvaloniaFact]
    public async Task CreationHandoffComparesWithoutApplyingAndInvalidatesOldResults()
    {
        using var assets = AssetStore.Load(Path.Combine(TestApplication.Root, "Data"));
        var map = QuickMapWorkflowTests.DecoratedMap(); var design = new MapDesignSettings(); var review = new MapCreationReview(map, design, null);
        map.Set(2, 2, new Cell { BackImage = 2 }); long revision = map.PickupRevision;
        var owner = new EditorWindow { Width = 1000, Height = 750 }; Point? located = null;
        var view = new MapCreationReviewView(owner, map, design, review, assets, (column, row) => located = new(column, row)); owner.Content = view;
        try
        {
            owner.Show();
            var compare = view.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "ProductionCompareCreation"); compare.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var difference = view.GetLogicalDescendants().OfType<TextBlock>().Single(text => text.Name == "ProductionCreationDifference");
            await Until(() => difference.Text?.StartsWith("Since creation:") == true);
            Assert.Equal(revision, map.PickupRevision);
            var changes = view.GetLogicalDescendants().OfType<ListBox>().Single(list => list.Name == "ProductionCreationChanges"); Assert.Single(changes.Items); changes.SelectedIndex = 0;
            view.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "ProductionLocateCreationChange").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.Equal(new Point(2, 2), located);
            map.Set(3, 3, new Cell { BackImage = 3 });
            await Until(() => difference.Text?.Contains("stale") == true); Assert.Empty(changes.Items);
        }
        finally { owner.Close(); }
    }

    [AvaloniaFact]
    public void ComparisonRestoresThePreviouslyChosenPair()
    {
        var session = new MapCreationSession(new(MapCreationKind.Terrain, 64, 64));
        for (int seed = 1; seed <= 3; seed++) session.Candidates.Remember(session.Settings, QuickMapWorkflowTests.Parameters(seed));
        session.CompareAIndex = 2; session.CompareBIndex = 0;
        var dialog = new QuickMapComparisonWindow(session.Candidates.Items.ToArray(), session.Candidates.Items[1], session: session);
        try
        {
            var choices = dialog.GetLogicalDescendants().OfType<ComboBox>().ToArray();
            Assert.Same(session.Candidates.Items[2], choices.Single(choice => choice.Name == "QuickMapCompareA").SelectedItem);
            Assert.Same(session.Candidates.Items[0], choices.Single(choice => choice.Name == "QuickMapCompareB").SelectedItem);
        }
        finally { dialog.Close(); session.Candidates.Dispose(); }
    }

    private static async Task Until(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 500; attempt++)
        {
            Dispatcher.UIThread.RunJobs(); if (condition()) return; await Task.Delay(20);
        }
        Assert.True(condition(), "Timed out waiting for the quick-map workflow.");
    }
}
