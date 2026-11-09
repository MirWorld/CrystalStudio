namespace CrystalStudio;

internal sealed class QuickMapComparisonWindow : EditorWindow
{
    private static string ParameterLabel(string key) => LocalText.T(key switch
    {
        "SeedBox" => "Seed", "BiomeList" => "Biome", "BaseList" => "Base terrain", "PatchList" => "Patch terrain", "PathList" => "Path terrain",
        "CoverageSlider" => "Coverage", "ScaleSlider" => "Patch scale", "WidthSlider" => "Path width", "RouteList" => "Routes",
        "StyleList" => "Style", "SceneList" => "Scene", "ThemeList" => "Theme", "TempleList" => "Dungeon style", "DungeonTypeList" => "Dungeon type",
        "RoomsSlider" => "Rooms", "SizeSlider" => "Room size", "DoorCountSlider" => "Entrances", "WidthList" => "Corridor width",
        "VegetationSlider" => "Vegetation", "ForestSlider" => "Forest", "CliffSlider" => "Cliffs", "BeachSlider" => "Beach", "RiverSlider" => "River",
        "RiverToggle" => "River", "BeachToggle" => "Beach", "BridgeToggle" => "River crossing", "TownCountSlider" => "Towns",
        "RuinCountSlider" => "Ruins", "MineCountSlider" => "Mines", _ => key
    });
    internal static IReadOnlyList<string> Differences(IReadOnlyDictionary<string, string> left, IReadOnlyDictionary<string, string> right,
        IReadOnlyDictionary<string, string>? leftDisplay = null, IReadOnlyDictionary<string, string>? rightDisplay = null) =>
        left.Keys.Concat(right.Keys).Distinct().Order().Where(key => left.GetValueOrDefault(key) != right.GetValueOrDefault(key))
            .Select(key => ParameterLabel(key) + ": " + LocalText.T((leftDisplay ?? left).GetValueOrDefault(key, "—")) + " → " + LocalText.T((rightDisplay ?? right).GetValueOrDefault(key, "—"))).ToArray();

    internal QuickMapComparisonWindow(QuickMapCandidate[] candidates, QuickMapCandidate? selected, bool moduleLayout = false)
    {
        Title = LocalText.T("Compare candidates A / B"); Width = 1060; Height = 680; MinWidth = 660; MinHeight = 440;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new Grid { Margin = new Thickness(16), RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto") };
        var heading = new TextBlock { Text = LocalText.T(moduleLayout
            ? "A / B · Synchronized relative zoom and pan. Colors show module footprints, not artwork or walkability. Compare dimensions before judging spacing. Comparing does not apply or regenerate a map."
            : "A / B · Synchronized zoom and pan. Bounded overview snapshots, not original-resolution artwork. Comparing does not apply or regenerate a map."), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        root.Children.Add(heading);
        var views = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*") };
        var navigation = new PreviewNavigation();
        var leftImage = new Image(); var rightImage = new Image();
        var left = new MapPreviewViewport(leftImage, navigation) { Name = "QuickMapCompareLeft" };
        var right = new MapPreviewViewport(rightImage, navigation) { Name = "QuickMapCompareRight" };
        Avalonia.Automation.AutomationProperties.SetName(left, LocalText.T("Candidate A"));
        Avalonia.Automation.AutomationProperties.SetName(right, LocalText.T("Candidate B"));
        left.SetTip(LocalText.T("Keyboard: + / - to zoom, arrows to pan, Home to fit.")); right.SetTip(LocalText.T("Keyboard: + / - to zoom, arrows to pan, Home to fit."));
        left.ViewChanged += right.InvalidateVisual; right.ViewChanged += left.InvalidateVisual;
        var first = new ComboBox { Name = "QuickMapCompareA", ItemsSource = candidates, HorizontalAlignment = HorizontalAlignment.Stretch };
        var second = new ComboBox { Name = "QuickMapCompareB", ItemsSource = candidates, HorizontalAlignment = HorizontalAlignment.Stretch };
        var template = new Avalonia.Controls.Templates.FuncDataTemplate<QuickMapCandidate>((candidate, _) => new TextBlock
        { Text = LocalText.Format("Seed {0}", candidate?.Parameters.GetValueOrDefault("SeedBox", "?")) + $" · {candidate?.Generation.Width} × {candidate?.Generation.Height}" });
        first.ItemTemplate = template; second.ItemTemplate = template;
        Avalonia.Automation.AutomationProperties.SetName(first, LocalText.T("Candidate A"));
        Avalonia.Automation.AutomationProperties.SetName(second, LocalText.T("Candidate B"));
        foreach (var (choice, viewport, column) in new[] { (first, left, 0), (second, right, 1) })
        {
            var pane = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), Margin = new Thickness(4) };
            pane.Children.Add(choice); Grid.SetRow(viewport, 1); pane.Children.Add(viewport); Grid.SetColumn(pane, column); views.Children.Add(pane);
        }
        Grid.SetRow(views, 1); root.Children.Add(views);
        var difference = new TextBlock { Name = "QuickMapParameterDifferences", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DarkOrange };
        var differences = new ScrollViewer { Content = difference, MaxHeight = 120, Margin = new Thickness(0, 8) };
        Grid.SetRow(differences, 2); root.Children.Add(differences);
        void Refresh()
        {
            leftImage.Source = (first.SelectedItem as QuickMapCandidate)?.Comparison;
            rightImage.Source = (second.SelectedItem as QuickMapCandidate)?.Comparison;
            var firstCandidate = first.SelectedItem as QuickMapCandidate; var secondCandidate = second.SelectedItem as QuickMapCandidate;
            var rows = Differences(firstCandidate?.Parameters ?? new Dictionary<string, string>(), secondCandidate?.Parameters ?? new Dictionary<string, string>(),
                firstCandidate?.DisplayValues, secondCandidate?.DisplayValues);
            difference.Text = rows.Count == 0 ? LocalText.T("No parameter differences.") : string.Join("\n", rows);
        }
        first.SelectionChanged += (_, _) => Refresh(); second.SelectionChanged += (_, _) => Refresh();
        second.SelectedItem = selected != null && candidates.Contains(selected) ? selected : candidates.LastOrDefault();
        first.SelectedItem = candidates.FirstOrDefault(candidate => !ReferenceEquals(candidate, second.SelectedItem)) ?? candidates.FirstOrDefault();
        var actions = new WrapPanel();
        var zoom = new TextBlock { Name = "QuickMapCompareZoom", Text = "100%", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        left.ViewChanged += () => zoom.Text = $"{left.Zoom:P0}"; right.ViewChanged += () => zoom.Text = $"{right.Zoom:P0}";
        actions.Children.Add(zoom);
        foreach (var (label, action) in new (string, Action)[] { ("Zoom out", () => left.ChangeZoom(1 / 1.2)), ("Zoom in", () => left.ChangeZoom(1.2)), ("Fit preview", left.ResetView) })
        {
            var button = new Button { Content = LocalText.T(label), Margin = new Thickness(0, 0, 6, 0) };
            button.Click += (_, _) => action(); actions.Children.Add(button);
        }
        actions.Children.Add(new Button { Content = LocalText.T("Close"), IsCancel = true });
        Grid.SetRow(actions, 3); root.Children.Add(actions); Content = root;
        Closed += (_, _) => { leftImage.Source = null; rightImage.Source = null; };
    }
}
