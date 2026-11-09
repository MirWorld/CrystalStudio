namespace CrystalStudio;

internal sealed class MapDesignEditor : StackPanel
{
    private readonly MapDesignSettings design;
    private readonly ListBox points = new() { Name = "QuickMapDesignPoints", MinHeight = 65, MaxHeight = 140 };
    private readonly ListBox regions = new() { Name = "QuickMapDesignRegions", MinHeight = 65, MaxHeight = 140 };
    private readonly TextBlock feedback = new() { TextWrapping = TextWrapping.Wrap };
    internal event Action? Changed;

    internal MapDesignEditor(MapDesignSettings design, int width, int height, Func<Rect?>? selection = null)
    {
        this.design = design; Spacing = 6;
        Children.Add(Hint("Points are visited in list order. Analysis uses four-way collision flags, not game runtime navigation."));
        points.ItemTemplate = new FuncDataTemplate<MapRoutePoint>((point, _) => new TextBlock { Text = point == null ? "" : $"{point.Name} · ({point.X}, {point.Y})", TextWrapping = TextWrapping.Wrap });
        Children.Add(points);
        var pointName = new TextBox { Name = "QuickMapPointName", Watermark = LocalText.T("Point name"), MaxLength = 80 };
        var pointX = Number("QuickMapPointX", 0, width - 1, width / 2); var pointY = Number("QuickMapPointY", 0, height - 1, height / 2);
        Children.Add(pointName); Children.Add(Fields(("X", pointX), ("Y", pointY)));
        var pointActions = new WrapPanel(); Children.Add(pointActions);
        Add(pointActions, "Add route point", () =>
        {
            if (design.Points.Count >= 32) throw new InvalidOperationException(LocalText.T("Use at most 32 route points and 64 design regions."));
            design.Points.Add(new(Label(pointName, "Point", design.Points.Count), Value(pointX), Value(pointY))); Notify();
        });
        Add(pointActions, "Update selected", () =>
        {
            int index = points.SelectedIndex;
            if (index < 0) return;
            design.Points[index] = new(Label(pointName, "Point", index), Value(pointX), Value(pointY)); Notify(); points.SelectedIndex = index;
        });
        Add(pointActions, "Remove point", () => { if (points.SelectedItem is MapRoutePoint point) { design.Points.Remove(point); Notify(); } });
        Add(pointActions, "Move up", () => Move(-1)); Add(pointActions, "Move down", () => Move(1));
        points.SelectionChanged += (_, _) =>
        {
            if (points.SelectedItem is not MapRoutePoint point) return;
            pointName.Text = point.Name; pointX.Value = point.X; pointY.Value = point.Y;
        };
        var required = new CheckBox { Name = "QuickMapRequireRoute", Content = LocalText.T("Require the ordered route to be reachable"), IsChecked = design.RequireRoute };
        required.IsCheckedChanged += (_, _) => { design.RequireRoute = required.IsChecked == true; Notify(); }; Children.Add(required);
        Children.Add(Hint("Protection affects local regeneration only. Clear-object reservations check anchors and stamp footprints, not every overhanging artwork pixel."));
        Children.Add(regions);
        var regionName = new TextBox { Name = "QuickMapRegionName", Watermark = LocalText.T("Region name"), MaxLength = 80 }; Children.Add(regionName);
        var regionX = Number("QuickMapRegionX", 0, width - 1, 0); var regionY = Number("QuickMapRegionY", 0, height - 1, 0);
        var regionWidth = Number("QuickMapRegionWidth", 1, width, Math.Min(16, width)); var regionHeight = Number("QuickMapRegionHeight", 1, height, Math.Min(16, height));
        Children.Add(Fields(("X", regionX), ("Y", regionY))); Children.Add(Fields(("Width", regionWidth), ("Height", regionHeight)));
        var purpose = new ComboBox { Name = "QuickMapRegionPurpose", ItemsSource = Enum.GetValues<MapRegionPurpose>(), SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        Children.Add(purpose);
        var hard = new CheckBox { Name = "QuickMapRegionRequired", Content = LocalText.T("Required rather than preferred"), IsChecked = true }; Children.Add(hard);
        hard.IsVisible = false; purpose.SelectionChanged += (_, _) => hard.IsVisible = purpose.SelectedIndex != (int)MapRegionPurpose.Protect;
        var regionActions = new WrapPanel(); Children.Add(regionActions);
        MapDesignRegion DraftRegion(int index) => new(Label(regionName, "Region", index), Value(regionX), Value(regionY), Value(regionWidth), Value(regionHeight), (MapRegionPurpose)purpose.SelectedIndex, hard.IsChecked == true);
        Add(regionActions, "Add region", () =>
        {
            if (design.Regions.Count >= 64) throw new InvalidOperationException(LocalText.T("Use at most 32 route points and 64 design regions."));
            var region = DraftRegion(design.Regions.Count); ValidateRegion(region); design.Regions.Add(region); Notify();
        });
        Add(regionActions, "Update region", () =>
        {
            int index = regions.SelectedIndex; if (index < 0) return;
            var region = DraftRegion(index); ValidateRegion(region); design.Regions[index] = region; Notify(); regions.SelectedIndex = index;
        });
        Add(regionActions, "Remove region", () => { if (regions.SelectedItem is MapDesignRegion region) { design.Regions.Remove(region); Notify(); } });
        if (selection != null) Add(regionActions, "Use current selection", () =>
        {
            if (selection() is not Rect area) throw new InvalidOperationException(LocalText.T("Select an area on the map first."));
            regionX.Value = (int)area.X; regionY.Value = (int)area.Y; regionWidth.Value = Math.Max(1, (int)area.Width); regionHeight.Value = Math.Max(1, (int)area.Height);
            pointX.Value = (int)(area.X + area.Width / 2); pointY.Value = (int)(area.Y + area.Height / 2);
        });
        regions.SelectionChanged += (_, _) =>
        {
            if (regions.SelectedItem is not MapDesignRegion region) return;
            regionName.Text = region.Name; regionX.Value = region.X; regionY.Value = region.Y;
            regionWidth.Value = region.Width; regionHeight.Value = region.Height; purpose.SelectedIndex = (int)region.Purpose; hard.IsChecked = region.Required;
        };
        Children.Add(feedback); Refresh();

        void ValidateRegion(MapDesignRegion region) => new MapDesignSettings { Regions = [region] }.Validate(width, height);
        void Move(int direction)
        {
            int index = points.SelectedIndex, target = index + direction;
            if (index < 0 || target < 0 || target >= design.Points.Count) return;
            (design.Points[index], design.Points[target]) = (design.Points[target], design.Points[index]); Notify(); points.SelectedIndex = target;
        }
    }

    private void Refresh() { points.ItemsSource = design.Points.ToArray(); regions.ItemsSource = design.Regions.ToArray(); }
    private void Notify() { feedback.Text = ""; Refresh(); Changed?.Invoke(); }
    private static string Label(TextBox input, string fallback, int index) => string.IsNullOrWhiteSpace(input.Text) ? LocalText.T(fallback) + " " + (index + 1) : input.Text.Trim();
    internal static NumericUpDown Number(string name, int minimum, int maximum, int value) => new()
    { Name = name, Minimum = minimum, Maximum = maximum, Value = value, Increment = 1, FormatString = "0", Width = 108 };
    internal static int Value(NumericUpDown number)
    {
        if (number.Value is not { } value || value != decimal.Truncate(value)) throw new InvalidOperationException(LocalText.T("Enter a whole number."));
        return decimal.ToInt32(value);
    }
    internal static TextBlock Hint(string text) => new() { Text = LocalText.T(text), TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    internal static WrapPanel Fields(params (string Label, Control Control)[] fields)
    {
        var panel = new WrapPanel();
        foreach (var (label, control) in fields)
        {
            var item = new StackPanel { Spacing = 3, Margin = new Thickness(0, 0, 10, 4) };
            item.Children.Add(Hint(label)); item.Children.Add(control); panel.Children.Add(item);
            Avalonia.Automation.AutomationProperties.SetName(control, LocalText.T(label));
        }
        return panel;
    }
    private void Add(Panel panel, string label, Action action)
    {
        var button = new Button { Content = LocalText.T(label), Margin = new Thickness(0, 0, 5, 5) };
        button.Click += (_, _) => { try { action(); } catch (Exception exception) { feedback.Text = exception.Message; } }; panel.Children.Add(button);
    }
}
