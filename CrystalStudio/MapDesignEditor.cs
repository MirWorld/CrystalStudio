namespace CrystalStudio;

internal sealed class MapDesignEditor : StackPanel
{
    private readonly MapDesignSettings design;
    private readonly int width, height;
    private readonly ListBox points = new() { Name = "QuickMapDesignPoints", MinHeight = 65, MaxHeight = 140 };
    private readonly ListBox regions = new() { Name = "QuickMapDesignRegions", MinHeight = 65, MaxHeight = 140 };
    private readonly TextBlock feedback = new() { Name = "QuickMapDesignFeedback", TextWrapping = TextWrapping.Wrap };
    private readonly CheckBox required = new() { Name = "QuickMapRequireRoute", Content = LocalText.T("Require the ordered route to be reachable") };
    internal NumericUpDown Footprint { get; }
    private bool restoring;
    internal event Action? Changed;

    internal MapDesignEditor(MapDesignSettings design, int width, int height, Func<Rect?>? selection = null)
    {
        this.design = design; this.width = width; this.height = height; Spacing = 6;
        Avalonia.Automation.AutomationProperties.SetLiveSetting(feedback, Avalonia.Automation.AutomationLiveSetting.Polite);
        Footprint = Number("QuickMapDesignFootprint", 1, 64, design.FootprintSize);
        Footprint.ValueChanged += (_, _) =>
        {
            if (restoring) return;
            try { design.FootprintSize = Value(Footprint); Notify(); }
            catch (InvalidOperationException exception) { feedback.Text = exception.Message; Changed?.Invoke(); }
        };
        Children.Add(Fields(("Square footprint (cells)", Footprint)));
        Children.Add(Hint("Route points and walkable regions are top-left footprint anchors. Generation, analysis and seed filtering use the same square size. The first route point is the reachability origin."));
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
            design.Points.Add(new(Label(pointName, "Point", design.Points.Count), Value(pointX), Value(pointY))); Notify(); points.SelectedIndex = design.Points.Count - 1;
        });
        var updatePoint = Add(pointActions, "Update selected", () =>
        {
            int index = points.SelectedIndex;
            if (index < 0) return;
            design.Points[index] = new(Label(pointName, "Point", index), Value(pointX), Value(pointY)); Notify(); points.SelectedIndex = index;
        });
        var removePoint = Add(pointActions, "Remove point", () => { if (points.SelectedItem is MapRoutePoint point) { design.Points.Remove(point); Notify(); } });
        var moveUp = Add(pointActions, "Move up", () => Move(-1)); var moveDown = Add(pointActions, "Move down", () => Move(1));
        updatePoint.Name = "QuickMapUpdatePoint"; removePoint.Name = "QuickMapRemovePoint"; moveUp.Name = "QuickMapMovePointUp"; moveDown.Name = "QuickMapMovePointDown";
        points.SelectionChanged += (_, _) =>
        {
            updatePoint.IsEnabled = removePoint.IsEnabled = points.SelectedIndex >= 0;
            moveUp.IsEnabled = points.SelectedIndex > 0; moveDown.IsEnabled = points.SelectedIndex >= 0 && points.SelectedIndex < design.Points.Count - 1;
            if (points.SelectedItem is not MapRoutePoint point) return;
            pointName.Text = point.Name; pointX.Value = point.X; pointY.Value = point.Y;
        };
        required.IsChecked = design.RequireRoute;
        required.IsCheckedChanged += (_, _) => { if (!restoring) { design.RequireRoute = required.IsChecked == true; Notify(); } }; Children.Add(required);
        Children.Add(Hint("Protection affects local regeneration only. Clear-object reservations check anchors and stamp footprints, not every overhanging artwork pixel."));
        Children.Add(regions);
        var regionName = new TextBox { Name = "QuickMapRegionName", Watermark = LocalText.T("Region name"), MaxLength = 80 }; Children.Add(regionName);
        var regionX = Number("QuickMapRegionX", 0, width - 1, 0); var regionY = Number("QuickMapRegionY", 0, height - 1, 0);
        var regionWidth = Number("QuickMapRegionWidth", 1, width, Math.Min(16, width)); var regionHeight = Number("QuickMapRegionHeight", 1, height, Math.Min(16, height));
        Children.Add(Fields(("X", regionX), ("Y", regionY))); Children.Add(Fields(("Width", regionWidth), ("Height", regionHeight)));
        var purpose = new ComboBox { Name = "QuickMapRegionPurpose", ItemsSource = Enum.GetValues<MapRegionPurpose>(), ItemTemplate = LocalText.ChoiceTemplate, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        Avalonia.Automation.AutomationProperties.SetName(purpose, LocalText.T("Region purpose"));
        Children.Add(purpose);
        var hard = new CheckBox { Name = "QuickMapRegionRequired", Content = LocalText.T("Required rather than preferred"), IsChecked = true }; Children.Add(hard);
        hard.IsVisible = false; purpose.SelectionChanged += (_, _) => hard.IsVisible = purpose.SelectedIndex != (int)MapRegionPurpose.Protect;
        var regionActions = new WrapPanel(); Children.Add(regionActions);
        MapDesignRegion DraftRegion(int index) => new(Label(regionName, "Region", index), Value(regionX), Value(regionY), Value(regionWidth), Value(regionHeight), (MapRegionPurpose)purpose.SelectedIndex, hard.IsChecked == true);
        Add(regionActions, "Add region", () =>
        {
            if (design.Regions.Count >= 64) throw new InvalidOperationException(LocalText.T("Use at most 32 route points and 64 design regions."));
            var region = DraftRegion(design.Regions.Count); ValidateRegion(region); design.Regions.Add(region); Notify(); regions.SelectedIndex = design.Regions.Count - 1;
        });
        var updateRegion = Add(regionActions, "Update region", () =>
        {
            int index = regions.SelectedIndex; if (index < 0) return;
            var region = DraftRegion(index); ValidateRegion(region); design.Regions[index] = region; Notify(); regions.SelectedIndex = index;
        });
        var removeRegion = Add(regionActions, "Remove region", () => { if (regions.SelectedItem is MapDesignRegion region) { design.Regions.Remove(region); Notify(); } });
        updateRegion.Name = "QuickMapUpdateRegion"; removeRegion.Name = "QuickMapRemoveRegion";
        if (selection != null) Add(regionActions, "Use current selection", () =>
        {
            if (selection() is not Rect area) throw new InvalidOperationException(LocalText.T("Select an area on the map first."));
            regionX.Value = (int)area.X; regionY.Value = (int)area.Y; regionWidth.Value = Math.Max(1, (int)area.Width); regionHeight.Value = Math.Max(1, (int)area.Height);
            pointX.Value = (int)(area.X + area.Width / 2); pointY.Value = (int)(area.Y + area.Height / 2);
        });
        regions.SelectionChanged += (_, _) =>
        {
            updateRegion.IsEnabled = removeRegion.IsEnabled = regions.SelectedIndex >= 0;
            if (regions.SelectedItem is not MapDesignRegion region) return;
            regionName.Text = region.Name; regionX.Value = region.X; regionY.Value = region.Y;
            regionWidth.Value = region.Width; regionHeight.Value = region.Height; purpose.SelectedIndex = (int)region.Purpose; hard.IsChecked = region.Required;
        };
        updatePoint.IsEnabled = removePoint.IsEnabled = moveUp.IsEnabled = moveDown.IsEnabled = updateRegion.IsEnabled = removeRegion.IsEnabled = false;
        Children.Add(feedback); Refresh(); ValidateDesign();

        void ValidateRegion(MapDesignRegion region) => new MapDesignSettings { Regions = [region] }.Validate(width, height);
        void Move(int direction)
        {
            int index = points.SelectedIndex, target = index + direction;
            if (index < 0 || target < 0 || target >= design.Points.Count) return;
            (design.Points[index], design.Points[target]) = (design.Points[target], design.Points[index]); Notify(); points.SelectedIndex = target;
        }
    }

    internal void Restore(MapDesignSettings snapshot)
    {
        snapshot.Validate(width, height);
        if (design.SameAs(snapshot) && Footprint.Value == snapshot.FootprintSize) return;
        restoring = true;
        try
        {
            design.Points = [.. snapshot.Points]; design.Regions = [.. snapshot.Regions]; design.RequireRoute = snapshot.RequireRoute; design.FootprintSize = snapshot.FootprintSize;
            required.IsChecked = snapshot.RequireRoute; Footprint.Value = snapshot.FootprintSize;
        }
        finally { restoring = false; }
        Notify();
    }

    internal void EnsureValid() { Value(Footprint); design.Validate(width, height); }
    internal bool InputValid { get { try { EnsureValid(); return true; } catch (Exception exception) when (exception is InvalidOperationException or System.IO.InvalidDataException) { return false; } } }

    private void Refresh()
    {
        int pointIndex = points.SelectedIndex, regionIndex = regions.SelectedIndex;
        points.ItemsSource = design.Points.ToArray(); regions.ItemsSource = design.Regions.ToArray();
        points.SelectedIndex = pointIndex < 0 ? -1 : Math.Min(pointIndex, design.Points.Count - 1);
        regions.SelectedIndex = regionIndex < 0 ? -1 : Math.Min(regionIndex, design.Regions.Count - 1);
    }
    private void ValidateDesign()
    {
        try { design.Validate(width, height); feedback.Text = ""; }
        catch (System.IO.InvalidDataException exception) { feedback.Text = exception.Message; }
    }
    private void Notify() { Refresh(); ValidateDesign(); Changed?.Invoke(); }
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
    private Button Add(Panel panel, string label, Action action)
    {
        var button = new Button { Content = LocalText.T(label), Margin = new Thickness(0, 0, 5, 5) };
        button.Click += (_, _) => { try { action(); } catch (Exception exception) { feedback.Text = exception.Message; } }; panel.Children.Add(button);
        return button;
    }
}
