using System.IO;
using System.Globalization;

namespace CrystalStudio;

internal sealed class ModuleConnectionsWindow : EditorWindow
{
    private readonly IReadOnlyList<StampCell> cells;
    private readonly List<ModuleConnection> points;
    private readonly ModuleFootprint? footprint;
    private readonly ModuleConnectionCanvas preview;
    private (string?, string?, string?, string?, string?, ModuleEdge?) draftBaseline;
    private (string?, string?, string?, string?, string?, ModuleEdge?) Draft =>
        (IdBox.Text, KindBox.Text, XBox.Text, YBox.Text, WidthBox.Text, EdgeBox.SelectedItem as ModuleEdge?);
    internal ListBox ConnectionsList { get; } = new() { Height = 155 };
    internal TextBox IdBox { get; } = new() { Text = "entry" };
    internal TextBox KindBox { get; } = new() { Text = "path" };
    internal TextBox XBox { get; } = new();
    internal TextBox YBox { get; } = new();
    internal TextBox WidthBox { get; } = new() { Text = "1" };
    internal ComboBox EdgeBox { get; } = new() { ItemsSource = Enum.GetValues<ModuleEdge>(), SelectedItem = ModuleEdge.North };
    internal TextBlock Feedback { get; } = new() { TextWrapping = TextWrapping.Wrap, MinHeight = 48 };
    internal ModuleConnections? Result { get; private set; }

    internal ModuleConnectionsWindow(IReadOnlyList<StampCell> source, ModuleConnections? saved)
    {
        footprint = saved?.Footprint;
        cells = source.Select(c => c with { Cell = c.Cell.Copy() }).ToArray();
        var bounds = cells.Any(c => !c.Cell.Empty) ? ModuleConnectionRules.GetBounds(cells, saved) : new ModuleConnectionRules.Bounds(0, 0, 0, 0);
        if (saved != null && (saved.Version != 1 || saved.Points == null || saved.Points.Count > 64 || saved.Points.Any(p => p == null)))
            throw new InvalidDataException(LocalText.T("Unsupported connections or more than 64 openings."));
        points = saved?.Points.ToList() ?? [];
        Title = LocalText.T("Module connections"); Width = 940; Height = 740; MinWidth = 820; MinHeight = 650;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var layout = new Grid { ColumnDefinitions = new ColumnDefinitions("*,320"), Margin = new Thickness(12) };
        var left = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), Margin = new Thickness(0, 0, 16, 0) };
        left.Children.Add(new TextBlock { Text = LocalText.T("Click a boundary cell to set the opening position."), TextWrapping = TextWrapping.Wrap });
        preview = new ModuleConnectionCanvas(cells, bounds);
        preview.Picked += (x, y) => { XBox.Text = x.ToString(CultureInfo.InvariantCulture); YBox.Text = y.ToString(CultureInfo.InvariantCulture); };
        Grid.SetRow(preview, 1); left.Children.Add(preview);
        var help = new TextBlock { Text = LocalText.T("Blue: occupied cells. Red: collision. Green: connections. Coordinates are relative to the object origin. North/south width extends right; east/west width extends down."), TextWrapping = TextWrapping.Wrap };
        Grid.SetRow(help, 2); left.Children.Add(help); layout.Children.Add(left);
        var form = new StackPanel { Spacing = 6 };
        void Field(string name, Control input)
        { form.Children.Add(new TextBlock { Text = LocalText.T(name) }); form.Children.Add(input); }
        Field("Connections", ConnectionsList);
        Field("Connection ID", IdBox); Field("Connection type", KindBox);
        Field("Boundary direction", EdgeBox);
        var coordinates = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*") };
        var controls = new[] { XBox, YBox, WidthBox };
        var names = new[] { "X", "Y", LocalText.T("Opening width") };
        for (int i = 0; i < controls.Length; i++)
        {
            var field = new StackPanel { Margin = new Thickness(0, 0, 6, 0) };
            field.Children.Add(new TextBlock { Text = names[i] }); field.Children.Add(controls[i]);
            Grid.SetColumn(field, i); coordinates.Children.Add(field);
        }
        form.Children.Add(coordinates);
        var commands = new WrapPanel();
        void Command(string label, Action action)
        { var button = new Button { Content = LocalText.T(label), Margin = new Thickness(0, 4, 6, 4) }; button.Click += (_, _) => action(); commands.Children.Add(button); }
        Command("New connection", () => { ConnectionsList.SelectedIndex = -1; IdBox.Text = "entry" + (points.Count + 1); draftBaseline = Draft; });
        Command("Add / update", () => StoreDraft());
        Command("Remove connection", () =>
        {
            if (ConnectionsList.SelectedIndex is int index && index >= 0 && index < points.Count)
            { points.RemoveAt(index); Refresh(); draftBaseline = Draft; }
        });
        form.Children.Add(commands); form.Children.Add(Feedback);
        form.Children.Add(new TextBlock { Text = LocalText.T("Apply includes changes in the form. Save the object, then use Tools → Join modules."), TextWrapping = TextWrapping.Wrap });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 8, 0, 0) };
        var apply = new Button { Content = LocalText.T("Apply"), IsDefault = true };
        apply.Click += (_, _) => { if (TryApply()) DialogResult = true; };
        buttons.Children.Add(apply); buttons.Children.Add(new Button { Content = LocalText.T("Cancel"), IsCancel = true });
        form.Children.Add(buttons);
        var scroll = new ScrollViewer { Content = form }; Grid.SetColumn(scroll, 1); layout.Children.Add(scroll); Content = layout;
        ConnectionsList.SelectionChanged += (_, _) =>
        {
            int i = ConnectionsList.SelectedIndex;
            if (i < 0 || i >= points.Count) return;
            var point = points[i]; IdBox.Text = point.Id; KindBox.Text = point.Kind; EdgeBox.SelectedItem = point.Edge;
            XBox.Text = point.X.ToString(CultureInfo.InvariantCulture); YBox.Text = point.Y.ToString(CultureInfo.InvariantCulture);
            WidthBox.Text = point.Width.ToString(CultureInfo.InvariantCulture);
            draftBaseline = Draft;
        };
        XBox.Text = bounds.Left.ToString(CultureInfo.InvariantCulture); YBox.Text = bounds.Top.ToString(CultureInfo.InvariantCulture);
        Refresh();
        draftBaseline = Draft;
    }

    internal bool StoreDraft()
    {
        if (!int.TryParse(XBox.Text, out int x) || !int.TryParse(YBox.Text, out int y) || !int.TryParse(WidthBox.Text, out int width) || EdgeBox.SelectedItem is not ModuleEdge edge)
        { Feedback.Text = LocalText.T("Enter integer coordinates and an opening width."); return false; }
        var point = new ModuleConnection(IdBox.Text?.Trim() ?? "", x, y, edge, KindBox.Text?.Trim() ?? "", width);
        var candidate = points.ToList(); int selected = ConnectionsList.SelectedIndex;
        if (selected >= 0 && selected < candidate.Count) candidate[selected] = point; else candidate.Add(point);
        try { ModuleConnectionRules.Validate(new(1, candidate) { Footprint = footprint }, cells); }
        catch (InvalidDataException exception) { Feedback.Text = exception.Message; return false; }
        points.Clear(); points.AddRange(candidate); Refresh();
        ConnectionsList.SelectedIndex = selected >= 0 ? selected : points.Count - 1;
        draftBaseline = Draft;
        return true;
    }

    internal bool TryApply()
    {
        if (Draft != draftBaseline && !StoreDraft()) return false;
        var result = new ModuleConnections(1, points.ToList()) { Footprint = footprint };
        try { ModuleConnectionRules.Validate(result, cells); }
        catch (InvalidDataException exception) { Feedback.Text = exception.Message; return false; }
        Result = result; return true;
    }

    private void Refresh()
    {
        ConnectionsList.ItemsSource = points.Select(p => $"{p.Id} · {LocalText.T(p.Edge.ToString())} ({p.X},{p.Y}) ×{p.Width} · {p.Kind}").ToArray();
        preview.Points = points.ToArray(); preview.InvalidateVisual();
        try { ModuleConnectionRules.Validate(new(1, points) { Footprint = footprint }, cells); Feedback.Text = LocalText.T("Connections are valid."); }
        catch (InvalidDataException exception) { Feedback.Text = exception.Message; }
    }
}

internal sealed class ModuleConnectionCanvas(IReadOnlyList<StampCell> cells, ModuleConnectionRules.Bounds area) : Control
{
    internal IReadOnlyList<ModuleConnection> Points { get; set; } = [];
    internal event Action<int, int>? Picked;
    private (double Scale, double X, double Y) Layout()
    {
        double width = area.Right - area.Left + 1, height = area.Bottom - area.Top + 1;
        double scale = Math.Max(.001, Math.Min(Math.Max(1, Bounds.Width - 24) / width, Math.Max(1, Bounds.Height - 24) / (height * 2 / 3)));
        return (scale, (Bounds.Width - width * scale) / 2, (Bounds.Height - height * scale * 2 / 3) / 2);
    }
    public override void Render(DrawingContext context)
    {
        context.DrawRectangle(Brushes.Black, null, new Rect(Bounds.Size));
        var (scale, ox, oy) = Layout();
        Rect Tile(long x, long y) => new(ox + (x - area.Left) * scale, oy + (y - area.Top) * scale * 2 / 3, scale, scale * 2 / 3);
        foreach (var cell in cells)
        {
            if (cell.Cell.Empty) continue;
            bool blocked = (cell.Cell.BackImage & 0x60000000) != 0 || cell.Cell.Foreground is { } front && (front.Image & 0x8000) != 0;
            context.DrawRectangle(blocked ? Brushes.IndianRed : Brushes.SteelBlue, null, Tile(cell.X, cell.Y));
        }
        context.DrawRectangle(null, new Pen(Brushes.White, 1), new Rect(ox, oy, (area.Right - area.Left + 1) * scale, (area.Bottom - area.Top + 1) * scale * 2 / 3));
        foreach (var point in Points.Where(p => p.Width is > 0 and <= 64 && Enum.IsDefined(p.Edge)))
            foreach (var (x, y) in ModuleConnectionRules.Opening(point))
                if (x >= area.Left && x <= area.Right && y >= area.Top && y <= area.Bottom)
                    context.DrawRectangle(Brushes.LimeGreen, new Pen(Brushes.White, 1), Tile(x, y));
    }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var (scale, ox, oy) = Layout(); var mouse = e.GetPosition(this);
        int x = area.Left + (int)Math.Floor((mouse.X - ox) / scale), y = area.Top + (int)Math.Floor((mouse.Y - oy) / (scale * 2 / 3));
        if (x >= area.Left && x <= area.Right && y >= area.Top && y <= area.Bottom) Picked?.Invoke(x, y);
    }
}
