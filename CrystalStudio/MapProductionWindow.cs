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
    internal MapCreationRecipe? SelectedRecipe { get; private set; }

    internal MapProductionWindow(MapDocument source, AssetStore? assets, MapDesignSettings design, MapCreationRecipe? recipe,
        Func<MapCreationRecipe, IReadOnlyList<string>> compatibility, Action designChanged, Action<int, int> locate,
        Func<Rect?> selection, Func<EditorWindow, MapCreationKind, PartialGenerationLayer, CancellationToken, Task> regenerate,
        Func<EditorWindow, CancellationToken, Task> export)
    {
        Title = LocalText.T("Map production tools"); Width = 1100; Height = 780; MinWidth = 780; MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new Grid { Margin = new Thickness(16), RowDefinitions = new RowDefinitions("*,Auto,Auto") };
        root.Children.Add(tabs); Grid.SetRow(feedback, 1); root.Children.Add(feedback);
        var footer = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        var cancel = new Button { Content = LocalText.T("Stop operation"), IsEnabled = false };
        cancel.Click += (_, _) => pending?.Cancel(); footer.Children.Add(cancel);
        footer.Children.Add(new Button { Content = LocalText.T("Close"), IsCancel = true, Margin = new Thickness(8, 0, 0, 0) });
        Grid.SetRow(footer, 2); root.Children.Add(footer); Content = root;

        var route = new Grid { ColumnDefinitions = new ColumnDefinitions("340,*"), ColumnSpacing = 16 };
        var editor = new MapDesignEditor(design, source.Width, source.Height, selection);
        var editorPanel = new StackPanel { Spacing = 8 }; editorPanel.Children.Add(editor);
        var analyze = new Button { Name = "ProductionAnalyzeRoute", Content = LocalText.T("Analyze routes and reservations") }; editorPanel.Children.Add(analyze);
        route.Children.Add(new ScrollViewer { Content = editorPanel, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        var right = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto") };
        right.Children.Add(MapDesignEditor.Hint("Schematic only: red points, blue sampled route, orange design boundaries. Narrow cells have at most two walkable neighbours; this is not a clearance or gameplay-time guarantee."));
        Grid.SetRow(routeImage, 1); right.Children.Add(routeImage);
        var legs = new ListBox { Name = "ProductionRouteLegs", MaxHeight = 160 };
        legs.ItemTemplate = new FuncDataTemplate<MapRouteLeg>((leg, _) => new TextBlock { Text = leg?.ToString(), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4) });
        Grid.SetRow(legs, 2); right.Children.Add(legs);
        var locatePoint = new Button { Content = LocalText.T("Locate route target"), IsEnabled = false };
        legs.SelectionChanged += (_, _) => locatePoint.IsEnabled = legs.SelectedItem is MapRouteLeg;
        locatePoint.Click += (_, _) => { if (legs.SelectedItem is MapRouteLeg leg) locate(leg.To.X, leg.To.Y); };
        Grid.SetRow(locatePoint, 3); right.Children.Add(locatePoint); Grid.SetColumn(right, 1); route.Children.Add(right);
        tabs.Items.Add(new TabItem { Header = LocalText.T("Layout and routes"), Content = route });
        editor.Changed += () =>
        {
            designChanged(); legs.ItemsSource = null; routeImage.Source = null; routeBitmap?.Dispose(); routeBitmap = null;
            feedback.Text = LocalText.T("Design markers changed. Analyze again; save the map to persist editor metadata.");
        };
        analyze.Click += async (_, _) => await Operation(async cancellation =>
        {
            var markers = design.Copy(); markers.Validate(source.Width, source.Height); long revision = source.PickupRevision;
            var snapshot = MapRecoveryStore.Capture(source);
            var result = await Task.Run(() => (Route: MapRouteAnalysis.Analyze(snapshot, markers.Points, cancellation), Check: MapDesignConstraints.Check(snapshot, markers, cancellation)), cancellation);
            cancellation.ThrowIfCancellationRequested(); if (closed || source.PickupRevision != revision) return;
            legs.ItemsSource = result.Route.Legs;
            routeImage.Source = null; routeBitmap?.Dispose(); routeImage.Source = routeBitmap = MapRouteAnalysis.Draw(snapshot, result.Route, markers);
            analyzedRevision = revision;
            feedback.Text = result.Check.Findings.Count == 0 ? LocalText.T("All layout constraints are satisfied.") :
                string.Join("\n", result.Check.Findings.Select(finding => LocalText.T(finding.Required ? "Required" : "Preferred") + " · " + finding.Name + ": " + finding.Message));
        });

        var recipes = new StackPanel { Spacing = 10 };
        var recipeText = new TextBlock { Name = "ProductionRecipeDetails", TextWrapping = TextWrapping.Wrap };
        var restoreRecipe = new Button { Name = "ProductionRestoreRecipe", Content = LocalText.T("Restore recipe into a new map"), IsEnabled = recipe != null };
        var loadRecipe = new Button { Name = "ProductionLoadRecipe", Content = LocalText.T("Open recipe file…") };
        recipes.Children.Add(MapDesignEditor.Hint("A recipe restores generation parameters, not manual edits. The result opens as a new unsaved map after confirming the creation settings."));
        recipes.Children.Add(loadRecipe); recipes.Children.Add(recipeText); recipes.Children.Add(restoreRecipe);
        void ShowRecipe()
        {
            if (recipe == null) { recipeText.Text = LocalText.T("This map has no generation recipe. Open a .creation.json file to restore another source."); return; }
            var differences = compatibility(recipe);
            recipeText.Text = MapCreationTemplates.KindLabel(recipe.Kind) + $" · {recipe.Width} × {recipe.Height}\n" + recipe.CreatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") +
                "\n" + recipe.GeneratorVersion + "\n" + LocalText.Format("Seed {0}", recipe.Parameters.GetValueOrDefault("SeedBox", "—")) + "\n\n" +
                (differences.Count == 0 ? LocalText.T("Recorded versions match current identities; exact reconstruction is still not guaranteed.") : string.Join("\n", differences)) +
                "\n\n" + LocalText.T("Missing choices or unsupported parameters may fall back to available controls. Review the restored preview before creating.");
            restoreRecipe.IsEnabled = true;
        }
        loadRecipe.Click += (_, _) =>
        {
            try
            {
                var picker = new OpenFileDialog { Filter = "Creation recipe (*.creation.json)|*.creation.json|JSON (*.json)|*.json" };
                if (picker.ShowDialog(this) != true) return;
                recipe = MapRecipeRestore.Read(picker.FileName); ShowRecipe();
            }
            catch (Exception exception) { feedback.Text = exception.Message; }
        };
        restoreRecipe.Click += (_, _) => { if (recipe != null) { SelectedRecipe = recipe; DialogResult = true; } };
        try { ShowRecipe(); } catch (Exception exception) { recipeText.Text = exception.Message; restoreRecipe.IsEnabled = false; }
        tabs.Items.Add(new TabItem { Header = LocalText.T("Generation recipe"), Content = new ScrollViewer { Content = recipes } });

        var delivery = new StackPanel { Spacing = 10 };
        delivery.Children.Add(MapDesignEditor.Hint("Local regeneration uses a new source layout, then reviews a selected-area composite. Existing stamps, protected regions, collisions and a two-cell boundary collar are preserved. Ground uses complete 2×2 anchors; decoration adds only complete tracked decoration stamps to empty anchors, never arbitrary clipped sprites."));
        var kind = new ComboBox { Name = "ProductionGeneratorKind", ItemsSource = new[] { MapCreationKind.Terrain, MapCreationKind.Province, MapCreationKind.Dungeon }, SelectedItem = recipe?.Kind is { } existing && existing != MapCreationKind.Blank ? existing : MapCreationKind.Terrain };
        delivery.Children.Add(kind);
        delivery.Children.Add(MapDesignEditor.Hint(selection() is { } area ? LocalText.Format("Selection: {0}, {1} · {2} × {3} cells", area.X, area.Y, area.Width, area.Height) : "Select an area on the map before opening these tools to regenerate locally."));
        var ground = new Button { Name = "ProductionRegenerateGround", Content = LocalText.T("Regenerate selected ground…"), IsEnabled = selection() != null && assets != null };
        var decoration = new Button { Name = "ProductionRegenerateDecoration", Content = LocalText.T("Add generated decoration to selection…"), IsEnabled = selection() != null && assets != null };
        ground.Click += async (_, _) => await Operation(cancellation => regenerate(this, (MapCreationKind)kind.SelectedItem!, PartialGenerationLayer.Ground, cancellation));
        decoration.Click += async (_, _) => await Operation(cancellation => regenerate(this, (MapCreationKind)kind.SelectedItem!, PartialGenerationLayer.Decoration, cancellation));
        delivery.Children.Add(ground); delivery.Children.Add(decoration);
        delivery.Children.Add(MapDesignEditor.Hint("Export includes a snapshot map, design markers, generation recipe when available, a fresh check report and resource dependencies. A minimap is included when libraries are loaded. Actual artwork is not bundled; resource paths are reduced to filenames. Export does not mark the working map as saved."));
        var exportButton = new Button { Name = "ProductionExportPackage", Content = LocalText.T("Export map delivery package…") };
        exportButton.Click += async (_, _) => await Operation(cancellation => export(this, cancellation)); delivery.Children.Add(exportButton);
        tabs.Items.Add(new TabItem { Header = LocalText.T("Regeneration and delivery"), Content = new ScrollViewer { Content = delivery } });
        Closed += (_, _) => { closed = true; pending?.Cancel(); routeImage.Source = null; routeBitmap?.Dispose(); };

        async Task Operation(Func<CancellationToken, Task> operation)
        {
            if (pending != null) return;
            using var job = new CancellationTokenSource(); pending = job; tabs.IsEnabled = false; cancel.IsEnabled = true; feedback.Text = LocalText.T("Working…");
            try { await operation(job.Token); }
            catch (OperationCanceledException) { if (!closed) feedback.Text = LocalText.T("Operation cancelled. No pending changes were applied."); }
            catch (Exception exception) { if (!closed) feedback.Text = exception.Message; }
            finally
            {
                pending = null;
                if (!closed)
                {
                    tabs.IsEnabled = true; cancel.IsEnabled = false;
                    if (analyzedRevision >= 0 && analyzedRevision != source.PickupRevision)
                    {
                        legs.ItemsSource = null; routeImage.Source = null; routeBitmap?.Dispose(); routeBitmap = null; analyzedRevision = -1;
                        feedback.Text = LocalText.T("The map changed. Previous route analysis is stale; analyze again.");
                    }
                }
            }
        }
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
