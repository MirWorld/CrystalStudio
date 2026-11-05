using Avalonia.LogicalTree;
using System.Runtime.CompilerServices;

namespace CrystalStudio;

internal sealed class QuickMapPreview
{
    private static readonly ConditionalWeakTable<EditorWindow, QuickMapPreview> Configured = new();
    private Func<bool>? confirm;
    public bool RestartRequested { get; private set; }

    public static bool Confirm(EditorWindow window) => !Configured.TryGetValue(window, out var preview) || preview.confirm?.Invoke() != false;

    public static QuickMapPreview Configure(EditorWindow window, MapCreationSession session, Func<EditorWindow, bool> editSetup, Func<bool>? confirm = null)
    {
        var navigation = new QuickMapPreview { confirm = confirm };
        Configured.Add(window, navigation);
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
            if (preferencesReady) session.RememberParameters(GeneratorPreferences.Capture(window));
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
            int keep = session.Settings.Kind == MapCreationKind.Province ? 3 : 4;
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
        settings?.Children.Insert(1, report);
        var image = window.GetLogicalDescendants().OfType<Image>().Single(control => control.Name == "PreviewImage");
        if (image.Parent is not Grid preview) return;
        image.IsVisible = false;
        var viewport = new MapPreviewViewport(image) { Name = "QuickMapViewport", Margin = image.Margin };
        Grid.SetRow(viewport, Grid.GetRow(image)); preview.Children.Insert(preview.Children.IndexOf(image) + 1, viewport);
        var detail = window.GetLogicalDescendants().OfType<CheckBox>().Single(control => control.Name == "DetailToggle");
        detail.Content = LocalText.T("Local detail");
        var zoom = new TextBlock { VerticalAlignment = VerticalAlignment.Center, MinWidth = 48 };
        viewport.ViewChanged += () => zoom.Text = $"{viewport.Zoom:P0}";
        zoom.Text = $"{viewport.Zoom:P0}";
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        void Action(string name, string label, Action action)
        {
            var button = new Button { Name = name, Content = LocalText.T(label), Padding = new Thickness(8, 3) };
            button.Click += (_, _) => action(); toolbar.Children.Add(button);
        }
        Action("QuickMapZoomOut", "Zoom out", () => viewport.ChangeZoom(1 / 1.2)); toolbar.Children.Add(zoom);
        Action("QuickMapZoomIn", "Zoom in", () => viewport.ChangeZoom(1.2));
        Action("QuickMapResetView", "Fit preview", viewport.ResetView);
        Action("QuickMapInspectCentre", "Inspect centre", viewport.InspectCentre);
        Action("QuickMapOverview", "Overview", () => { detail.IsChecked = false; viewport.ResetView(); });
        var inspection = new StackPanel { Spacing = 5, Margin = new Thickness(0, 8, 0, 0) };
        inspection.Children.Add(toolbar);
        inspection.Children.Add(new TextBlock { Text = LocalText.T("Wheel: zoom · Drag: pan · Double-click: redraw local detail. Zoom enlarges the preview, not the original artwork."),
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
        void RefreshCandidates()
        {
            candidates.Children.Clear();
            foreach (var candidate in session.Candidates.Items)
            {
                string seed = candidate.Parameters.GetValueOrDefault("SeedBox", "?");
                var content = new StackPanel { Spacing = 3 };
                content.Children.Add(new Image { Source = candidate.Thumbnail, Width = 94, Height = 48, Stretch = Stretch.Uniform });
                content.Children.Add(new TextBlock { Text = LocalText.Format("Seed {0}", seed), FontSize = 11 });
                var restore = new Button { Name = "QuickMapCandidate" + candidates.Children.Count, Content = content, Padding = new Thickness(4), IsEnabled = candidate.CanRestore(session.Settings), BorderThickness = new Thickness(ReferenceEquals(selected, candidate) ? 2 : 1) };
                restore.SetTip(LocalText.T("Restore these parameters and regenerate with the currently loaded artwork."));
                restore.Click += (_, _) => { GeneratorPreferences.Restore(window, candidate.Parameters); selected = candidate; RefreshCandidates(); };
                var favorite = new CheckBox { Name = "QuickMapFavorite" + candidates.Children.Count, Content = LocalText.T("Favorite"), IsChecked = candidate.IsFavorite, FontSize = 11 };
                favorite.IsCheckedChanged += (_, _) => session.Candidates.SetFavorite(candidate, favorite.IsChecked == true);
                var card = new StackPanel { Spacing = 2 }; card.Children.Add(restore); card.Children.Add(favorite); candidates.Children.Add(card);
            }
        }
        state.Changed += (_, _) =>
        {
            report.Text = state.Resources?.Summary ?? LocalText.T("Checking required and optional artwork…");
        };
        state.Completed += (_, _) =>
        {
            if (window is TerrainGeneratorWindow { PreferencesRestored: false }) return;
            if (!apply.IsEnabled || image.Source is not RasterImage bitmap) return;
            selected = session.Candidates.Remember(session.Settings, GeneratorPreferences.Capture(window), bitmap);
            candidateHint.Text = selected == null ? LocalText.T("All five candidates are favorites. Unfavorite one to retain another result.")
                : LocalText.T("Candidates store parameters and thumbnails only; switching regenerates the map. Favorites are not saved after closing.");
            RefreshCandidates();
        };
        report.Text = LocalText.T("Checking required and optional artwork…"); RefreshCandidates();
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
