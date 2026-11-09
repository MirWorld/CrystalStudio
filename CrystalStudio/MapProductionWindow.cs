namespace CrystalStudio;

internal sealed class MapProductionWindow : EditorWindow
{
    private readonly TextBlock feedback = new() { Name = "ProductionFeedback", TextWrapping = TextWrapping.Wrap };
    private readonly TabControl tabs = new() { Name = "ProductionTabs" };
    private readonly Image routeImage = new() { Name = "ProductionRouteImage", Stretch = Stretch.Uniform };
    private RasterImage? routeBitmap;
    private CancellationTokenSource? pending;
    private bool closed;
    private long analyzedRevision = -1;
    private MapDesignSettings? analyzedDesign;
    private readonly MapDocument source;
    private readonly Func<MapCreationRecipe, IReadOnlyList<string>> compatibility;
    private readonly TextBlock recipeText = new() { Name = "ProductionRecipeDetails", TextWrapping = TextWrapping.Wrap };
    private readonly Button restoreRecipe = new() { Name = "ProductionRestoreRecipe", Content = LocalText.T("Restore recipe into a new map"), IsEnabled = false };
    private MapCreationRecipe? recipe;
    internal MapCreationRecipe? SelectedRecipe { get; private set; }

    internal MapProductionWindow(MapDocument source, AssetStore? assets, MapDesignSettings design, MapCreationRecipe? recipe,
        Func<MapCreationRecipe, IReadOnlyList<string>> compatibility, Action designChanged, Action<int, int> locate,
        Func<Rect?> selection, Func<EditorWindow, MapCreationKind, PartialGenerationLayer, CancellationToken, Task<string?>> regenerate,
        Func<EditorWindow, CancellationToken, Task<string?>> export)
    {
        this.source = source; this.compatibility = compatibility;
        Title = LocalText.T("Map production tools"); Width = 1100; Height = 780; MinWidth = 780; MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new Grid { Margin = new Thickness(16), RowDefinitions = new RowDefinitions("*,Auto,Auto") };
        root.Children.Add(tabs);
        var feedbackView = new ScrollViewer { Name = "ProductionFeedbackScroll", Content = feedback, MaxHeight = 96, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(feedbackView, 1); root.Children.Add(feedbackView);
        var footer = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        var cancel = new Button { Name = "ProductionStop", Content = LocalText.T("Stop operation"), IsEnabled = false };
        cancel.Click += (_, _) => { pending?.Cancel(); cancel.IsEnabled = false; feedback.Text = LocalText.T("Stopping… Please wait for cancellation to finish."); }; footer.Children.Add(cancel);
        Avalonia.Automation.AutomationProperties.SetLiveSetting(feedback, Avalonia.Automation.AutomationLiveSetting.Polite);
        footer.Children.Add(new Button { Content = LocalText.T("Close"), IsCancel = true, Margin = new Thickness(8, 0, 0, 0) });
        Grid.SetRow(footer, 2); root.Children.Add(footer); Content = root;

        var route = new Grid { ColumnDefinitions = new ColumnDefinitions("340,*"), ColumnSpacing = 16 };
        var editor = new MapDesignEditor(design, source.Width, source.Height, selection);
        var editorPanel = new StackPanel { Spacing = 8 };
        var footprint = MapDesignEditor.Number("ProductionRouteFootprint", 1, 64, 1);
        editorPanel.Children.Add(MapDesignEditor.Fields(("Square footprint (cells)", footprint)));
        var analyze = new Button { Name = "ProductionAnalyzeRoute", Content = LocalText.T("Analyze routes and reservations") }; editorPanel.Children.Add(analyze);
        editorPanel.Children.Add(editor);
        route.Children.Add(new ScrollViewer { Content = editorPanel, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        var right = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto") };
        right.Children.Add(MapDesignEditor.Hint("Route points are top-left anchors of an axis-aligned square footprint. Clearance uses both native collision bits and map bounds. Layout constraints keep their original one-cell semantics. Narrow cells still count neighbour degree, not width. This is not a server movement or gameplay-time guarantee."));
        Grid.SetRow(routeImage, 1); right.Children.Add(routeImage);
        var legs = new ListBox { Name = "ProductionRouteLegs", MaxHeight = 160 };
        legs.ItemTemplate = new FuncDataTemplate<MapRouteLeg>((leg, _) => new TextBlock { Text = leg?.ToString(), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4) });
        Grid.SetRow(legs, 2); right.Children.Add(legs);
        var locatePoint = new Button { Name = "ProductionLocateRoute", Content = LocalText.T("Locate route target"), IsEnabled = false };
        legs.SelectionChanged += (_, _) => locatePoint.IsEnabled = legs.SelectedItem is MapRouteLeg;
        locatePoint.Click += (_, _) => { if (legs.SelectedItem is MapRouteLeg leg) locate(leg.To.X, leg.To.Y); };
        Grid.SetRow(locatePoint, 3); right.Children.Add(locatePoint); Grid.SetColumn(right, 1); route.Children.Add(right);
        tabs.Items.Add(new TabItem { Header = LocalText.T("Layout and routes"), Content = route });
        editor.Changed += () =>
        {
            designChanged(); InvalidateAnalysis("Design markers changed. Analyze again; save the map to persist editor metadata.");
        };
        footprint.ValueChanged += (_, _) => InvalidateAnalysis("Footprint changed. Analyze again.");
        analyze.Click += async (_, _) => await Operation(async cancellation =>
        {
            InvalidateAnalysis("Checking layout constraints…");
            var markers = design.Copy(); markers.Validate(source.Width, source.Height); long revision = source.PickupRevision;
            var snapshot = MapRecoveryStore.Capture(source);
            int footprintSize = MapDesignEditor.Value(footprint);
            var result = await Task.Run(() => (Route: MapRouteAnalysis.Analyze(snapshot, markers.Points, cancellation, footprintSize), Check: MapDesignConstraints.Check(snapshot, markers, cancellation)), cancellation);
            cancellation.ThrowIfCancellationRequested();
            if (closed) return null;
            if (footprint.Value != footprintSize) return LocalText.T("Footprint changed. Analyze again.");
            if (source.PickupRevision != revision || !markers.SameAs(design))
                return LocalText.T("The map or design markers changed during analysis. Analyze again.");
            legs.ItemsSource = result.Route.Legs;
            routeImage.Source = null; routeBitmap?.Dispose(); routeImage.Source = routeBitmap = MapRouteAnalysis.Draw(snapshot, result.Route, markers);
            analyzedRevision = revision;
            analyzedDesign = markers;
            return (result.Route.Legs.Count == 0 ? LocalText.T("Add at least two ordered points to analyze a route.") :
                LocalText.Format("Route analysis complete: {0}/{1} legs reachable.", result.Route.Legs.Count(leg => leg.Reachable), result.Route.Legs.Count)) + "\n" +
                (footprintSize > 1 ? LocalText.T("Layout constraints (one-cell rule): ") : "") +
                (result.Check.Findings.Count == 0 ? LocalText.T("All layout constraints are satisfied.") :
                string.Join("\n", result.Check.Findings));
        });

        void InvalidateAnalysis(string message)
        {
            analyzedRevision = -1; analyzedDesign = null; legs.ItemsSource = null; locatePoint.IsEnabled = false;
            routeImage.Source = null; routeBitmap?.Dispose(); routeBitmap = null; feedback.Text = LocalText.T(message);
        }
        void CheckAnalysisRevision()
        {
            if (analyzedRevision >= 0 && analyzedRevision != source.PickupRevision)
                InvalidateAnalysis("The map changed. Previous route analysis is stale; analyze again.");
            else if (analyzedDesign != null && !analyzedDesign.SameAs(design))
                InvalidateAnalysis("Design markers changed. Analyze again; save the map to persist editor metadata.");
        }
        var revisionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        revisionTimer.Tick += (_, _) => { if (pending == null) CheckAnalysisRevision(); };
        Opened += (_, _) => revisionTimer.Start();

        var recipes = new StackPanel { Spacing = 10 };
        var loadRecipe = new Button { Name = "ProductionLoadRecipe", Content = LocalText.T("Open recipe file…") };
        recipes.Children.Add(MapDesignEditor.Hint("A recipe restores generation parameters, not manual edits. The result opens as a new unsaved map after confirming the creation settings."));
        recipes.Children.Add(loadRecipe); recipes.Children.Add(recipeText); recipes.Children.Add(restoreRecipe);
        loadRecipe.Click += (_, _) =>
        {
            try
            {
                var picker = new OpenFileDialog { Filter = "Creation recipe (*.creation.json)|*.creation.json|JSON (*.json)|*.json" };
                if (picker.ShowDialog(this) != true) return;
                LoadRecipe(picker.FileName);
            }
            catch (Exception exception) { feedback.Text = LocalText.T("Could not load the recipe. The previous recipe is unchanged.") + "\n" + exception.Message; }
        };
        restoreRecipe.Click += (_, _) => { if (this.recipe != null) { SelectedRecipe = this.recipe; DialogResult = true; } };
        try { SetRecipe(recipe, LocalText.T("Current map")); } catch (Exception exception) { recipeText.Text = exception.Message; restoreRecipe.IsEnabled = false; }
        tabs.Items.Add(new TabItem { Header = LocalText.T("Generation recipe"), Content = new ScrollViewer { Content = recipes } });

        var delivery = new StackPanel { Spacing = 10 };
        delivery.Children.Add(MapDesignEditor.Hint("Local regeneration uses a new source layout, then reviews a selected-area composite. Existing stamps, protected regions, collisions and a two-cell boundary collar are preserved. Ground uses complete 2×2 anchors; decoration adds only complete tracked decoration stamps to empty anchors, never arbitrary clipped sprites."));
        var kind = new ComboBox { Name = "ProductionGeneratorKind", ItemsSource = new[] { MapCreationKind.Terrain, MapCreationKind.Province, MapCreationKind.Dungeon },
            ItemTemplate = new FuncDataTemplate<MapCreationKind>((value, _) => new TextBlock { Text = MapCreationTemplates.KindLabel(value) }),
            SelectedItem = recipe?.Kind is { } existing && existing != MapCreationKind.Blank ? existing : MapCreationKind.Terrain };
        Avalonia.Automation.AutomationProperties.SetName(kind, LocalText.T("Generation type"));
        delivery.Children.Add(kind);
        var localAvailability = new TextBlock { Name = "ProductionLocalAvailability", TextWrapping = TextWrapping.Wrap, FontSize = 12 }; delivery.Children.Add(localAvailability);
        var ground = new Button { Name = "ProductionRegenerateGround", Content = LocalText.T("Regenerate selected ground…"), IsEnabled = selection() != null && assets != null };
        var decoration = new Button { Name = "ProductionRegenerateDecoration", Content = LocalText.T("Add generated decoration to selection…"), IsEnabled = selection() != null && assets != null };
        void RefreshLocalAvailability()
        {
            var area = selection(); bool terrain = kind.SelectedItem is MapCreationKind.Terrain;
            ground.IsEnabled = area != null && assets != null; decoration.IsEnabled = ground.IsEnabled && !terrain;
            localAvailability.Text = area is { } selected ? LocalText.Format("Selection: {0}, {1} · {2} × {3} cells", selected.X, selected.Y, selected.Width, selected.Height) :
                LocalText.T("Select an area on the map before opening these tools to regenerate locally.");
            if (assets == null) localAvailability.Text += "\n" + LocalText.T("Load artwork libraries before local regeneration. Export remains available without artwork.");
            else if (terrain) localAvailability.Text += "\n" + LocalText.T("Choose a province or dungeon layout for complete decoration stamps. Terrain banks and coasts are protected, not clipped.");
        }
        kind.SelectionChanged += (_, _) => RefreshLocalAvailability(); RefreshLocalAvailability();
        ground.Click += async (_, _) => await Operation(cancellation => regenerate(this, (MapCreationKind)kind.SelectedItem!, PartialGenerationLayer.Ground, cancellation));
        decoration.Click += async (_, _) => await Operation(cancellation => regenerate(this, (MapCreationKind)kind.SelectedItem!, PartialGenerationLayer.Decoration, cancellation));
        delivery.Children.Add(ground); delivery.Children.Add(decoration);
        delivery.Children.Add(MapDesignEditor.Hint("Export includes a snapshot map, design markers, generation recipe when available, a fresh check report and resource dependencies. A minimap is included when libraries are loaded. Actual artwork is not bundled; resource paths are reduced to filenames. Export does not mark the working map as saved."));
        var exportButton = new Button { Name = "ProductionExportPackage", Content = LocalText.T("Export map delivery package…") };
        exportButton.Click += async (_, _) => await Operation(cancellation => export(this, cancellation)); delivery.Children.Add(exportButton);
        tabs.Items.Add(new TabItem { Header = LocalText.T("Regeneration and delivery"), Content = new ScrollViewer { Content = delivery } });
        Closed += (_, _) => { closed = true; pending?.Cancel(); revisionTimer.Stop(); routeImage.Source = null; routeBitmap?.Dispose(); routeBitmap = null; };

        async Task Operation(Func<CancellationToken, Task<string?>> operation)
        {
            if (pending != null) return;
            using var job = new CancellationTokenSource(); pending = job; tabs.IsEnabled = false; cancel.IsEnabled = true; feedback.Text = LocalText.T("Working…");
            try
            {
                string? outcome = await operation(job.Token);
                if (!closed) feedback.Text = outcome ?? LocalText.T("Operation cancelled. No pending changes were applied.");
            }
            catch (OperationCanceledException) { if (!closed) feedback.Text = LocalText.T("Operation cancelled. No pending changes were applied."); }
            catch (Exception exception) { if (!closed) feedback.Text = exception.Message; }
            finally
            {
                pending = null;
                if (!closed)
                {
                    tabs.IsEnabled = true; cancel.IsEnabled = false;
                    CheckAnalysisRevision();
                }
            }
        }
    }

    internal void LoadRecipe(string path) => SetRecipe(MapRecipeRestore.Read(path), Path.GetFileName(path));

    private void SetRecipe(MapCreationRecipe? value, string origin)
    {
        string description;
        if (value == null) description = LocalText.T("This map has no generation recipe. Open a .creation.json file to restore another source.");
        else
        {
            MapRecipeRestore.Validate(value);
            var differences = compatibility(value);
            description = LocalText.Format("Recipe source: {0}", origin) + "\n" + MapCreationTemplates.KindLabel(value.Kind) + $" · {value.Width} × {value.Height}\n" +
                value.CreatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") + "\n" + value.GeneratorVersion + "\n" + LocalText.Format("Seed {0}", value.Parameters.GetValueOrDefault("SeedBox", "—")) + "\n\n" +
                (differences.Count == 0 ? LocalText.T("Recorded versions match current identities; exact reconstruction is still not guaranteed.") : string.Join("\n", differences)) +
                (value.Width == source.Width && value.Height == source.Height ? "" : "\n\n" + LocalText.T("The recipe size differs from this map. Restoration uses the recipe dimensions in a new map.")) +
                "\n\n" + LocalText.T("Missing choices or unsupported parameters may fall back to available controls. Review the restored preview before creating.");
        }
        recipe = value; recipeText.Text = description; restoreRecipe.IsEnabled = value != null;
    }
}

internal sealed class PartialGenerationReviewWindow : EditorWindow
{
    internal PartialGenerationReviewWindow(PartialMapPlan plan, RasterImage before, RasterImage after)
    {
        Title = LocalText.T("Review local regeneration"); Width = 1050; Height = 680; MinWidth = 720; MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new Grid { Margin = new Thickness(16), RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        root.Children.Add(new TextBlock { Text = LocalText.Format("{0:N0} changed anchors · {1:N0} protected cells · {2:N0} skipped stamps. Review seams and artwork before applying one undo step.", plan.Changed.Count, plan.ProtectedCells, plan.SkippedStamps)
            + "\n" + LocalText.Format("Selection: {0}, {1} · {2} × {3} cells", plan.Area.X, plan.Area.Y, plan.Area.Width, plan.Area.Height), TextWrapping = TextWrapping.Wrap });
        var views = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), Margin = new Thickness(0, 10) };
        var navigation = new PreviewNavigation();
        var left = new MapPreviewViewport(new Image { Source = before }, navigation) { Name = "PartialGenerationBefore" };
        var right = new MapPreviewViewport(new Image { Source = after }, navigation) { Name = "PartialGenerationAfter" };
        Avalonia.Automation.AutomationProperties.SetName(left, LocalText.T("Before local regeneration"));
        Avalonia.Automation.AutomationProperties.SetName(right, LocalText.T("After local regeneration"));
        left.ViewChanged += right.InvalidateVisual; right.ViewChanged += left.InvalidateVisual;
        var beforePanel = Pane("Before local regeneration", left); var afterPanel = Pane("After local regeneration", right);
        views.Children.Add(beforePanel); Grid.SetColumn(afterPanel, 1); views.Children.Add(afterPanel); Grid.SetRow(views, 1); root.Children.Add(views);
        var actions = new WrapPanel();
        foreach (var (label, action) in new (string, Action)[] { ("Zoom out", () => left.ChangeZoom(1 / 1.2)), ("Zoom in", () => left.ChangeZoom(1.2)), ("Fit preview", left.ResetView) })
        {
            var button = new Button { Content = LocalText.T(label), Margin = new Thickness(0, 0, 6, 4) }; button.Click += (_, _) => action(); actions.Children.Add(button);
        }
        var apply = new Button { Name = "PartialGenerationApply", Content = LocalText.T("Apply local changes"), IsEnabled = plan.Changed.Count > 0 };
        apply.Click += (_, _) => DialogResult = true; actions.Children.Add(apply);
        actions.Children.Add(new Button { Content = LocalText.T("Cancel"), IsCancel = true, Margin = new Thickness(8, 0, 0, 0) });
        Grid.SetRow(actions, 2); root.Children.Add(actions); Content = root;

        static Grid Pane(string title, Control view)
        {
            var panel = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), Margin = new Thickness(4) };
            panel.Children.Add(MapDesignEditor.Hint(title)); Grid.SetRow(view, 1); panel.Children.Add(view); return panel;
        }
    }
}
