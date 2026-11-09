using Avalonia.LogicalTree;
using System.Runtime.CompilerServices;

namespace CrystalStudio;

internal sealed class QuickMapPreview
{
    private static readonly ConditionalWeakTable<EditorWindow, QuickMapPreview> Configured = new();
    private Func<bool>? confirm;
    private QuickMapProduction? production;
    public bool RestartRequested { get; private set; }

    public static bool Confirm(EditorWindow window) => !Configured.TryGetValue(window, out var preview) || preview.confirm?.Invoke() != false;

    public static async Task<bool> ConfirmAsync(EditorWindow window)
    {
        if (Configured.TryGetValue(window, out var preview) && preview.production != null && !await preview.production.ConfirmAsync()) return false;
        return Confirm(window);
    }

    public static QuickMapPreview Configure(EditorWindow window, MapCreationSession session, Func<EditorWindow, bool> editSetup, Func<bool>? confirm = null)
    {
        var navigation = new QuickMapPreview { confirm = confirm };
        Configured.Add(window, navigation);
        switch (window)
        {
            case TerrainGeneratorWindow terrain: terrain.Workflow.UseQuickCreate(); break;
            case ProvinceGeneratorWindow province: province.Workflow.UseQuickCreate(); break;
            case TempleGeneratorWindow dungeon: dungeon.Workflow.UseQuickCreate(); break;
        }
        var apply = window.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "ApplyButton");
        var summary = new TextBlock { Name = "QuickMapSummary", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
        void UpdateSummary()
        {
            summary.Text = LocalText.Format("Step 2 of 2 · {0} · {1} × {2} cells", session.Settings.Name, session.Settings.Width, session.Settings.Height)
                + "\n" + MapCreationTemplates.KindLabel(session.Settings.Kind) + "\n"
                + (session.Settings.SaveImmediately ? LocalText.Format("Save after creation: {0}", Path.Combine(session.Settings.Directory ?? "", MapCreationPaths.FileName(session.Settings.Name)))
                    : LocalText.T("Create an unsaved map; choose its location with Save As."))
                + (string.IsNullOrWhiteSpace(session.PresetName) ? "" : "\n" + LocalText.Format("Save preset: {0}", session.PresetName));
            apply.Content = LocalText.T(session.Settings.SaveImmediately ? "Create and save" : "Create Map");
            apply.SetTip(session.Settings.SaveImmediately ? Path.Combine(session.Settings.Directory ?? "", MapCreationPaths.FileName(session.Settings.Name)) : session.Settings.Name);
        }
        UpdateSummary();
        var targetInfo = window.GetLogicalDescendants().OfType<TextBlock>().FirstOrDefault(control => control.Name == "PreviewMapInfo");
        if (targetInfo != null) targetInfo.Text = LocalText.T("Creates a new map. The original map is not modified.");
        window.Title = LocalText.T("Quick Create Map") + " · " + MapCreationTemplates.KindLabel(session.Settings.Kind);
        var back = new Button { Name = "QuickMapBack", Content = LocalText.T("Previous: Map settings"), Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(12, 7) };
        back.Click += (_, _) =>
        {
            bool preferencesReady = window switch
            {
                TerrainGeneratorWindow terrain => terrain.PreferencesRestored,
                ProvinceGeneratorWindow province => province.PreferencesRestored,
                TempleGeneratorWindow dungeon => dungeon.PreferencesRestored,
                _ => true
            };
            var captured = GeneratorPreferences.Capture(window);
            if (preferencesReady) session.RememberParameters(captured);
            else
            {
                var parameters = new Dictionary<string, string>(session.Parameters);
                foreach (var control in window.GetLogicalDescendants().OfType<Control>().Where(control =>
                    control is Slider or TextBox { Name: "SeedBox" } or CheckBox { Name: "DetailToggle" } or
                        ComboBox { Name: "SceneList" or "ThemeList" or "DungeonTypeList" or "WidthList" or "RouteList" }))
                    if (control.Name is { } name && captured.TryGetValue(name, out string? value)) parameters[name] = value;
                session.RememberParameters(parameters);
            }
            int revision = session.GenerationRevision;
            if (!editSetup(window)) return;
            navigation.RestartRequested = session.GenerationRevision != revision;
            if (navigation.RestartRequested) window.Close(false); else UpdateSummary();
        };
        if (apply.Parent is DockPanel actions) { DockPanel.SetDock(back, Dock.Left); actions.Children.Insert(0, back); }
        var settings = window.GetLogicalDescendants().OfType<ScrollViewer>().Select(view => view.Content).OfType<StackPanel>()
            .FirstOrDefault(panel => panel.Children.OfType<ComboBox>().Any());
        if (settings != null)
        {
            // Keep the complete primary selector groups, including newer scene/theme controls.
            string lastPrimary = session.Settings.Kind switch
            {
                MapCreationKind.Province => "StyleList",
                MapCreationKind.Dungeon => "TempleList",
                _ => "BaseList"
            };
            int lastPrimaryIndex = settings.Children.ToList().FindIndex(control => control.Name == lastPrimary);
            int keep = lastPrimaryIndex >= 0 ? lastPrimaryIndex + 1 : 4;
            var advanced = new StackPanel();
            foreach (var control in settings.Children.Skip(keep).ToArray())
            { settings.Children.Remove(control); advanced.Children.Add(control); }
            settings.Children.Insert(0, summary);
            settings.Children.Add(new Expander { Header = LocalText.T("Advanced generation settings"), Content = advanced, HorizontalAlignment = HorizontalAlignment.Stretch });
            AddDensityChoice(window, settings, session.Settings.Kind);
        }
        ConfigureInspection(window, session, apply, settings);
        return navigation;
    }

    private static GeneratorPreviewState? State(EditorWindow window) => window switch
    {
        TerrainGeneratorWindow terrain => terrain.PreviewState,
        ProvinceGeneratorWindow province => province.PreviewState,
        TempleGeneratorWindow dungeon => dungeon.PreviewState,
        _ => null
    };

    private static void ConfigureInspection(EditorWindow window, MapCreationSession session, Button apply, StackPanel? settings)
    {
        var state = State(window);
        if (state == null) return;
        var report = new TextBlock { Name = "QuickMapResourceFeedback", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
        var recipeFeedback = new TextBlock { Name = "QuickMapRecipeFeedback", TextWrapping = TextWrapping.Wrap, IsVisible = false };
        bool restorationReported = false;
        settings?.Children.Add(recipeFeedback);
        settings?.Children.Insert(1, report);
        var resourceDetails = new ResourceDiagnosticsView(state);
        var diagnostics = new Expander { Name = "QuickMapResourceDiagnostics", Header = LocalText.T("Artwork details and recovery"),
            Content = resourceDetails, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 0, 0, 10) };
        settings?.Children.Insert(2, diagnostics);
        var image = window.GetLogicalDescendants().OfType<Image>().Single(control => control.Name == "PreviewImage");
        Control previewContent = image.GetLogicalAncestors().OfType<GeneratorPreview>().FirstOrDefault() is { } navigator ? navigator : image;
        if (previewContent.Parent is not Grid preview) return;
        previewContent.IsVisible = false;
        image.IsVisible = false;
        var viewport = new MapPreviewViewport(image) { Name = "QuickMapViewport", Margin = previewContent.Margin };
        Grid.SetRow(viewport, Grid.GetRow(previewContent)); preview.Children.Insert(preview.Children.IndexOf(previewContent) + 1, viewport);
        var detail = window.GetLogicalDescendants().OfType<CheckBox>().Single(control => control.Name == "DetailToggle");
        detail.Content = LocalText.T("Local detail");
        var zoom = new TextBlock { VerticalAlignment = VerticalAlignment.Center, MinWidth = 48 };
        viewport.ViewChanged += () => zoom.Text = $"{viewport.Zoom:P0}";
        zoom.Text = $"{viewport.Zoom:P0}";
        var toolbar = new WrapPanel { Orientation = Orientation.Horizontal };
        void Action(string name, string label, Action action)
        {
            var button = new Button { Name = name, Content = LocalText.T(label), Padding = new Thickness(8, 3), Margin = new Thickness(0, 0, 6, 4) };
            button.Click += (_, _) => action(); toolbar.Children.Add(button);
        }
        Action("QuickMapZoomOut", "Zoom out", () => viewport.ChangeZoom(1 / 1.2)); toolbar.Children.Add(zoom);
        Action("QuickMapZoomIn", "Zoom in", () => viewport.ChangeZoom(1.2));
        Action("QuickMapResetView", "Fit preview", viewport.ResetView);
        Action("QuickMapInspectCentre", "Inspect centre", viewport.InspectCentre);
        Action("QuickMapOverview", "Overview", () => { detail.IsChecked = false; viewport.ResetView(); });
        var inspection = new StackPanel { Spacing = 5, Margin = new Thickness(0, 8, 0, 0) };
        inspection.Children.Add(toolbar);
        inspection.Children.Add(new TextBlock { Text = LocalText.T("Wheel: zoom · Drag: pan · Double-click: redraw local detail. Keyboard: + / - to zoom, arrows to pan, Home to fit, Enter for detail."),
            TextWrapping = TextWrapping.Wrap, FontSize = 11 });
        preview.RowDefinitions.Add(new RowDefinition(GridLength.Auto)); Grid.SetRow(inspection, preview.RowDefinitions.Count - 1); preview.Children.Add(inspection);
        viewport.InspectRequested += normalized =>
        {
            if (!apply.IsEnabled) return;
            switch (window)
            {
                case TerrainGeneratorWindow terrain: terrain.Inspect(normalized); break;
                case ProvinceGeneratorWindow province: province.Inspect(normalized); break;
                case TempleGeneratorWindow dungeon: dungeon.Inspect(normalized); break;
            }
        };
        var candidates = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var candidateHint = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 11 };
        var candidatePanel = new StackPanel { Spacing = 5, Margin = new Thickness(0, 10, 0, 0) };
        candidatePanel.Children.Add(new TextBlock { Text = LocalText.T("Recent candidates · favorites stay in this session"), FontWeight = FontWeight.SemiBold });
        candidatePanel.Children.Add(new ScrollViewer { Content = candidates, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled });
        candidatePanel.Children.Add(candidateHint);
        preview.RowDefinitions.Add(new RowDefinition(GridLength.Auto)); Grid.SetRow(candidatePanel, preview.RowDefinitions.Count - 1); preview.Children.Add(candidatePanel);
        QuickMapCandidate? selected = null;
        var compare = new Button { Name = "QuickMapCompare", Content = LocalText.T("Compare candidates A / B"), IsEnabled = false };
        candidatePanel.Children.Insert(1, compare);
        compare.Click += (_, _) => new QuickMapComparisonWindow(session.Candidates.Items.Where(candidate => candidate.CanRestore(session.Settings) && candidate.Comparison != null).ToArray(), selected).ShowDialog(window);
        void RefreshCandidates()
        {
            candidates.Children.Clear();
            compare.IsEnabled = !session.BatchRunning && state.Phase == GeneratorPreviewPhase.Ready && session.Candidates.Items.Count(candidate => candidate.CanRestore(session.Settings) && candidate.Comparison != null) >= 2;
            foreach (var candidate in session.Candidates.Items)
            {
                string seed = candidate.Parameters.GetValueOrDefault("SeedBox", "?");
                var content = new StackPanel { Spacing = 3 };
                content.Children.Add(new Image { Source = candidate.Thumbnail, Width = 94, Height = 48, Stretch = Stretch.Uniform });
                content.Children.Add(new TextBlock { Text = LocalText.Format("Seed {0}", seed), FontSize = 11 });
                if (candidate.Comparison == null) content.Children.Add(new TextBlock { Text = LocalText.T("Detail only"), FontSize = 11 });
                var restore = new Button { Name = "QuickMapCandidate" + candidates.Children.Count, Content = content, Padding = new Thickness(4), IsEnabled = !session.BatchRunning && candidate.CanRestore(session.Settings), BorderThickness = new Thickness(ReferenceEquals(selected, candidate) ? 2 : 1) };
                restore.SetTip(LocalText.T("Restore these parameters and regenerate with the currently loaded artwork."));
                if (candidate.Metrics != null) restore.SetTip(candidate.Metrics.ToString());
                restore.Click += (_, _) => { GeneratorPreferences.Restore(window, candidate.Parameters); selected = candidate; RefreshCandidates(); };
                var favorite = new CheckBox { Name = "QuickMapFavorite" + candidates.Children.Count, Content = LocalText.T("Favorite"), IsChecked = candidate.IsFavorite, FontSize = 11 };
                favorite.IsCheckedChanged += (_, _) => session.Candidates.SetFavorite(candidate, favorite.IsChecked == true);
                var card = new StackPanel { Spacing = 2 }; card.Children.Add(restore); card.Children.Add(favorite); candidates.Children.Add(card);
            }
        }
        state.Changed += (_, _) =>
        {
            compare.IsEnabled = !session.BatchRunning && state.Phase == GeneratorPreviewPhase.Ready && session.Candidates.Items.Count(candidate => candidate.CanRestore(session.Settings) && candidate.Comparison != null) >= 2;
            string phase = state.Phase switch
            {
                GeneratorPreviewPhase.Waiting => "Parameters changed. Waiting to rebuild; the old preview cannot be applied.",
                GeneratorPreviewPhase.Generating => "Generating preview…",
                GeneratorPreviewPhase.Ready => "Ready to create a new map",
                GeneratorPreviewPhase.Failed => "Preview failed. Review artwork details or change the style.",
                GeneratorPreviewPhase.Cancelling => "Stopping… Please wait for cancellation to finish.",
                GeneratorPreviewPhase.Cancelled => "Generation cancelled. The map was not changed.",
                _ => "Checking required and optional artwork…"
            };
            report.Text = LocalText.T(phase) + (state.Resources is { } resources ? "\n" +
                LocalText.Format("{0} required missing · {1} optional unavailable", resources.Errors.Count, resources.Warnings.Count) : "");
            if (state.Phase == GeneratorPreviewPhase.Failed && window.GetLogicalDescendants().OfType<TextBlock>().FirstOrDefault(control => control.Name == "FeedbackText") is { } errorFeedback)
                errorFeedback.Text = LocalText.T("Expand artwork details for the source and recovery steps. You can also go back without changing any maps.");
        };
        state.Completed += (_, _) =>
        {
            if (session.BatchRunning) return;
            if (window is TerrainGeneratorWindow { PreferencesRestored: false }) return;
            if (!restorationReported && session.ExpectedRecipeParameters is { } expected)
            {
                var actual = GeneratorPreferences.Capture(window).Where(pair => expected.ContainsKey(pair.Key) && pair.Key != "DetailToggle").ToDictionary(pair => pair.Key, pair => pair.Value);
                var requested = expected.Where(pair => pair.Key != "DetailToggle").ToDictionary(pair => pair.Key, pair => pair.Value);
                var differences = QuickMapComparisonWindow.Differences(requested, actual, GeneratorPreferences.Describe(window, requested), GeneratorPreferences.Describe(window, actual));
                recipeFeedback.IsVisible = differences.Count > 0;
                recipeFeedback.Text = LocalText.T("Recipe parameter adjustments:") + "\n" + string.Join("\n", differences.Take(8));
                restorationReported = true;
            }
            if (!apply.IsEnabled || image.Source is not RasterImage bitmap) return;
            var overview = window switch { TerrainGeneratorWindow terrain => terrain.OverviewPreview, ProvinceGeneratorWindow province => province.OverviewPreview, TempleGeneratorWindow dungeon => dungeon.OverviewPreview, _ => null };
            selected = session.Candidates.Remember(session.Settings, GeneratorPreferences.Capture(window), overview ?? bitmap, overview != null || detail.IsChecked != true);
            if (selected != null) selected.DisplayValues = GeneratorPreferences.Describe(window, selected.Parameters);
            candidateHint.Text = selected == null ? LocalText.T("All five candidates are favorites. Unfavorite one to retain another result.")
                : selected.Comparison == null ? LocalText.T("Local detail is not stored as an overview candidate. Switch to Overview to retain this result for A / B comparison.")
                : LocalText.T("Candidates store parameters and bounded overview snapshots only; switching regenerates the map. Favorites are not saved after closing.");
            RefreshCandidates();
        };
        state.Notify(); RefreshCandidates();
        var production = new QuickMapProduction(window, session, state, apply);
        if (Configured.TryGetValue(window, out var navigation)) navigation.production = production;
        production.CandidatesChanged += RefreshCandidates;
        settings?.Children.Add(new Expander { Name = "QuickMapProduction", Header = LocalText.T("Production tools and seed search"), Content = production, HorizontalAlignment = HorizontalAlignment.Stretch });
        var feedback = window.GetLogicalDescendants().OfType<TextBlock>().FirstOrDefault(control => control.Name == "FeedbackText");
        if (feedback?.Parent is StackPanel feedbackPanel)
        {
            int index = feedbackPanel.Children.IndexOf(feedback); feedbackPanel.Children.Remove(feedback);
            feedbackPanel.Children.Insert(index, new ScrollViewer { Content = feedback, MaxHeight = 96, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        }
    }

    private static void AddDensityChoice(EditorWindow window, StackPanel settings, MapCreationKind kind)
    {
        string sliderName = kind switch { MapCreationKind.Terrain => "CoverageSlider", MapCreationKind.Province => "VegetationSlider", _ => "RoomsSlider" };
        var slider = window.GetLogicalDescendants().OfType<Slider>().FirstOrDefault(control => control.Name == sliderName);
        if (slider == null) return;
        double[] values = kind switch { MapCreationKind.Terrain => [.2, .45, .7], MapCreationKind.Province => [20, 55, 80], _ => [6, 12, 20] };
        string label = kind switch { MapCreationKind.Terrain => "Patch density", MapCreationKind.Province => "Vegetation density", _ => "Room density" };
        var choice = new ComboBox { Name = "QuickMapDensity", ItemsSource = new[] { "Custom", "Sparse", "Balanced", "Dense" }, Margin = new Thickness(0, 6, 0, 12) };
        Avalonia.Automation.AutomationProperties.SetName(choice, LocalText.T(label));
        bool synchronizing = false;
        void Synchronize()
        {
            synchronizing = true; choice.SelectedIndex = Array.FindIndex(values, value => Math.Abs(value - slider.Value) < .001) + 1; synchronizing = false;
        }
        Synchronize();
        choice.SelectionChanged += (_, _) => { if (!synchronizing && choice.SelectedIndex > 0) slider.Value = values[choice.SelectedIndex - 1]; };
        slider.PropertyChanged += (_, args) => { if (args.Property == RangeBase.ValueProperty) Synchronize(); };
        settings.Children.Insert(settings.Children.Count - 1, new TextBlock { Text = LocalText.T(label) });
        settings.Children.Insert(settings.Children.Count - 1, choice);
    }
}
