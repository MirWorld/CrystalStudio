using System.Globalization;

namespace CrystalStudio;

internal sealed class ModuleConnectionEditor : Grid, IDisposable
{
    internal ModuleConnectionEditorState State { get; }
    internal ModuleConnectionCanvas Canvas { get; }
    internal ListBox ConnectionsList { get; } = new() { MinHeight = 160 };
    internal TextBox IdBox { get; } = new();
    internal TextBox KindBox { get; } = new();
    internal TextBox XBox { get; } = new();
    internal TextBox YBox { get; } = new();
    internal TextBox WidthBox { get; } = new();
    internal ComboBox EdgeBox { get; } = new() { ItemsSource = Enum.GetValues<ModuleEdge>() };
    internal TextBlock Feedback { get; } = new() { TextWrapping = TextWrapping.Wrap, MinHeight = 60 };
    internal Button NewButton { get; }
    internal event Action? Changed;
    private readonly Button undoButton, redoButton;
    private bool updating, typing, gesture, hoverOnly;
    private ModuleConnection? original;
    private (int X, int Y) anchor;
    private int dragMode; // 0 new, 1 move, 2 start handle, 3 end handle
    private ModuleConnectionEntry[] displayed = [];

    internal ModuleConnectionEditor(IReadOnlyList<StampCell> cells, ModuleConnections? saved, AssetStore? assets)
    {
        State = new(cells, saved); Canvas = new(State.Cells, State.Bounds, assets);
        ColumnDefinitions = new ColumnDefinitions("230,*,245");
        var left = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), Margin = new Thickness(0, 0, 10, 0) };
        left.Children.Add(new TextBlock { Text = LocalText.T("Connections · check to enable, click to edit"), TextWrapping = TextWrapping.Wrap });
        ConnectionsList.ItemTemplate = new FuncDataTemplate<ModuleConnectionEntry>((entry, _) =>
        {
            if (entry == null) return new TextBlock();
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
            var check = new CheckBox { IsChecked = entry.Enabled, VerticalAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(check, LocalText.T("Enable connection"));
            check.IsCheckedChanged += (_, _) =>
            {
                if (hoverOnly) { hoverOnly = false; State.CancelDraft(); }
                if (!State.Toggle(entry.Point.Id, check.IsChecked == true)) check.IsChecked = entry.Enabled;
            };
            row.Children.Add(check);
            var label = new TextBlock { Text = $"{entry.Point.Id}\n{LocalText.T(entry.Point.Edge.ToString())} · {entry.Point.Kind} ×{entry.Point.Width}", Margin = new Thickness(5, 2, 0, 2) };
            Grid.SetColumn(label, 1); row.Children.Add(label); return row;
        });
        Grid.SetRow(ConnectionsList, 1); left.Children.Add(ConnectionsList);
        var commands = new WrapPanel();
        Button Command(string label, Action action)
        {
            var button = new Button { Content = LocalText.T(label), Margin = new Thickness(0, 4, 5, 0) };
            button.Click += (_, _) => action(); commands.Children.Add(button); return button;
        }
        NewButton = Command("New connection", BeginNew);
        Command("Remove connection", () => State.Remove());
        undoButton = Command("Undo", State.Undo); redoButton = Command("Redo", State.Redo);
        Grid.SetRow(commands, 2); left.Children.Add(commands); Children.Add(left);
        var center = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        var fit = new Button { Content = LocalText.T("Fit preview") }; fit.Click += (_, _) => Canvas.Fit(); toolbar.Children.Add(fit);
        void Toggle(string text, bool value, Action<bool> change)
        {
            var check = new CheckBox { Content = LocalText.T(text), IsChecked = value };
            check.IsCheckedChanged += (_, _) => { change(check.IsChecked == true); Canvas.Viewport.InvalidateVisual(); Canvas.Refresh(); };
            toolbar.Children.Add(check);
        }
        Toggle("Grid", true, value => Canvas.Viewport.GridVisible = value);
        Toggle("Collision", true, value => Canvas.Viewport.CollisionVisible = value); Canvas.Viewport.CollisionVisible = true;
        center.Children.Add(toolbar); Grid.SetRow(Canvas, 1); center.Children.Add(Canvas);
        var hint = new TextBlock { Text = LocalText.T("Wheel: zoom · Middle/Space: pan · Esc: cancel draft · Delete: remove · Ctrl+Z/Y: undo/redo"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
        Grid.SetRow(hint, 2); center.Children.Add(hint); Grid.SetColumn(center, 1); Children.Add(center);
        var properties = new StackPanel { Spacing = 7, Margin = new Thickness(12, 0, 0, 0) };
        void Field(string label, Control control) { properties.Children.Add(new TextBlock { Text = LocalText.T(label) }); properties.Children.Add(control); }
        Field("Connection ID", IdBox); Field("Connection type", KindBox); Field("Boundary direction", EdgeBox); Field("Opening width", WidthBox);
        var advanced = new StackPanel { Spacing = 5 }; advanced.Children.Add(new TextBlock { Text = "X" }); advanced.Children.Add(XBox);
        advanced.Children.Add(new TextBlock { Text = "Y" }); advanced.Children.Add(YBox);
        properties.Children.Add(new Expander { Header = LocalText.T("Advanced coordinates"), Content = advanced });
        properties.Children.Add(Feedback);
        properties.Children.Add(new TextBlock { Text = LocalText.T("Green: enabled · Cyan + handles: selected · Dashed orange: draft · Red: invalid · Dashed grey: disabled"), TextWrapping = TextWrapping.Wrap });
        var scroll = new ScrollViewer { Content = properties }; Grid.SetColumn(scroll, 2); Children.Add(scroll);
        ConnectionsList.SelectionChanged += (_, _) =>
        {
            if (updating) return;
            string? target = (ConnectionsList.SelectedItem as ModuleConnectionEntry)?.Point.Id;
            if (hoverOnly) { hoverOnly = false; State.CancelDraft(); }
            if (!State.Select(target)) Refresh();
        };
        foreach (var box in new[] { IdBox, KindBox, XBox, YBox, WidthBox })
        {
            box.PropertyChanged += (_, args) => { if (args.Property == TextBox.TextProperty) FormChanged(); };
            box.LostFocus += (_, _) => { if (!updating) State.Commit(); };
            box.KeyDown += (_, e) => { if (e.Key == Key.Escape) { State.CancelDraft(); e.Handled = true; } };
        }
        EdgeBox.SelectionChanged += (_, _) => FormChanged();
        EdgeBox.LostFocus += (_, _) => { if (!updating) State.Commit(); };
        Canvas.Pressed += Press; Canvas.Moved += Move; Canvas.Released += Release;
        Canvas.CaptureCancelled += () => { gesture = false; original = null; hoverOnly = false; State.CancelDraft(); };
        Canvas.Shortcut += (key, modifiers) =>
        {
            if (key == Key.Escape) { gesture = hoverOnly = false; State.CancelDraft(); }
            else if (key == Key.Delete) State.Remove();
            else if (key == Key.Y || modifiers.HasFlag(KeyModifiers.Shift)) State.Redo(); else State.Undo();
        };
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Handled || e.Source is Visual source && (source is TextBox || source.GetVisualAncestors().OfType<TextBox>().Any())) return;
            if (e.Key == Key.Escape) { gesture = hoverOnly = false; State.CancelDraft(); }
            else if (e.Key == Key.Delete) State.Remove();
            else if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key is Key.Z or Key.Y)
            { if (e.Key == Key.Y || e.KeyModifiers.HasFlag(KeyModifiers.Shift)) State.Redo(); else State.Undo(); }
            else return;
            e.Handled = true;
        });
        State.Changed += Refresh; Refresh();
    }

    internal void BeginNew()
    {
        if (hoverOnly) State.CancelDraft();
        if (!State.Commit()) return;
        // Move focus before creating the ghost; a form's LostFocus must never commit an unplaced ghost.
        Canvas.FocusCanvas();
        if (!State.BeginNew()) return;
        hoverOnly = true; PreviewNew(State.Bounds.Left, State.Bounds.Top);
    }
    private ModuleEdge EdgeAt(int x, int y) => y == State.Bounds.Top ? ModuleEdge.North : y == State.Bounds.Bottom ? ModuleEdge.South :
        x == State.Bounds.Right ? ModuleEdge.East : ModuleEdge.West;
    private bool OnBoundary(int x, int y) => x >= State.Bounds.Left && x <= State.Bounds.Right && y >= State.Bounds.Top && y <= State.Bounds.Bottom &&
        (x == State.Bounds.Left || x == State.Bounds.Right || y == State.Bounds.Top || y == State.Bounds.Bottom);
    private void PreviewNew(int x, int y)
    {
        var edge = EdgeAt(x, y);
        State.Preview(new(State.NextId(), x, y, edge, "path"), null,
            OnBoundary(x, y) ? null : LocalText.T("Place the connection on the module boundary."));
    }
    private void Press(int x, int y)
    {
        if (State.Adding)
        {
            hoverOnly = false; anchor = (x, y); gesture = true; dragMode = 0;
            PreviewNew(x, y); original = State.Draft; return;
        }
        if (!State.Commit()) return;
        var hit = State.Entries.OrderByDescending(e => e.Point.Id == State.SelectedId).FirstOrDefault(e => ModuleConnectionRules.Opening(e.Point).Contains(((long)x, (long)y)));
        if (hit == null) { State.Select(null); return; }
        bool alreadySelected = State.SelectedId == hit.Point.Id;
        State.Select(hit.Point.Id); original = hit.Point; anchor = (x, y); gesture = true; dragMode = 1;
        bool horizontal = original.Edge is ModuleEdge.North or ModuleEdge.South;
        int position = horizontal ? x : y, start = horizontal ? original.X : original.Y;
        if (alreadySelected && original.Width > 1)
            dragMode = position == start ? 2 : position == start + original.Width - 1 ? 3 : 1;
        else if (alreadySelected)
        {
            double fraction = horizontal ? Canvas.PointerCellPosition.X - x : Canvas.PointerCellPosition.Y - y;
            dragMode = fraction < .25 ? 2 : fraction > .75 ? 3 : 1;
        }
    }
    private void Move(int x, int y)
    {
        if (!gesture) { if (State.Adding && hoverOnly) PreviewNew(x, y); return; }
        if (original == null) return;
        bool horizontal = original.Edge is ModuleEdge.North or ModuleEdge.South;
        int position = horizontal ? x : y, start = horizontal ? original.X : original.Y, end = start + original.Width - 1;
        int first, last;
        if (dragMode == 0) { first = Math.Min(horizontal ? anchor.X : anchor.Y, position); last = Math.Max(horizontal ? anchor.X : anchor.Y, position); }
        else if (dragMode == 2) { first = Math.Min(position, end); last = Math.Max(position, end); }
        else if (dragMode == 3) { first = Math.Min(start, position); last = Math.Max(start, position); }
        else { first = start + position - (horizontal ? anchor.X : anchor.Y); last = first + original.Width - 1; }
        var point = original with { X = horizontal ? first : original.X, Y = horizontal ? original.Y : first, Width = last - first + 1 };
        State.Preview(point, dragMode == 0 ? null : original.Id,
            dragMode == 0 && !OnBoundary(anchor.X, anchor.Y) ? LocalText.T("Place the connection on the module boundary.") : null);
    }
    private void Release(int x, int y)
    {
        if (!gesture) return;
        Move(x, y); gesture = false; original = null; State.Commit();
    }
    private void FormChanged()
    {
        if (updating) return;
        hoverOnly = false; typing = true;
        try
        {
            if (!int.TryParse(XBox.Text, out int x) || !int.TryParse(YBox.Text, out int y) || !int.TryParse(WidthBox.Text, out int width) || EdgeBox.SelectedItem is not ModuleEdge edge)
            { State.Preview(State.Draft ?? State.Selected, State.SelectedId, LocalText.T("Enter integer coordinates and an opening width.")); return; }
            State.Preview(new(IdBox.Text?.Trim() ?? "", x, y, edge, KindBox.Text?.Trim() ?? "", width), State.SelectedId);
        }
        finally { typing = false; }
    }
    internal bool Commit()
    {
        if (hoverOnly || State.Adding && State.Draft == null)
        { Feedback.Text = LocalText.T("Click or drag along a boundary to place a connection. Esc cancels."); return false; }
        return State.Commit();
    }
    internal ModuleConnections? Accept()
    {
        if (!Commit()) return null;
        var result = State.Connections;
        var problem = State.Check(result);
        if (problem != null) { Feedback.Text = problem.Message; if (problem.X is int x && problem.Y is int y) Canvas.Locate(x, y); return null; }
        return result;
    }
    private void Refresh()
    {
        updating = true;
        try
        {
            if (!displayed.SequenceEqual(State.Entries)) { displayed = State.Entries.ToArray(); ConnectionsList.ItemsSource = displayed; }
            ConnectionsList.SelectedItem = displayed.FirstOrDefault(e => e.Point.Id == State.SelectedId);
            var point = State.Draft ?? State.Selected;
            if (!typing && State.Problem?.Code != "input")
            {
                IdBox.Text = point?.Id ?? State.NextId(); KindBox.Text = point?.Kind ?? "path";
                XBox.Text = (point?.X ?? State.Bounds.Left).ToString(CultureInfo.InvariantCulture);
                YBox.Text = (point?.Y ?? State.Bounds.Top).ToString(CultureInfo.InvariantCulture);
                WidthBox.Text = (point?.Width ?? 1).ToString(CultureInfo.InvariantCulture); EdgeBox.SelectedItem = point?.Edge ?? ModuleEdge.North;
            }
            Canvas.Points = State.Entries.Select(e => e.Point).ToArray(); Canvas.Disabled = State.Entries.Where(e => !e.Enabled).Select(e => e.Point.Id).ToHashSet();
            var validation = State.Check(State.Connections);
            Canvas.SelectedId = State.SelectedId; Canvas.Draft = State.Draft; Canvas.DraftTarget = State.DraftTarget; Canvas.InvalidDraft = State.Problem != null;
            Canvas.InvalidId = validation?.ConnectionId; Canvas.Refresh();
            Feedback.Text = State.Problem?.Message ?? validation?.Message ?? (State.Adding ? LocalText.T("Click or drag along a boundary to place a connection. Esc cancels.") :
                State.Selected is { } selected ? LocalText.Format("Editing {0}. Drag its body to move or its handles to resize.", selected.Id) : LocalText.T("Select a connection on the map or in the list."));
            undoButton.IsEnabled = State.CanUndo || State.Draft != null; redoButton.IsEnabled = State.CanRedo;
        }
        finally { updating = false; }
        Changed?.Invoke();
    }
    public void Dispose() { State.Changed -= Refresh; Canvas.Dispose(); }
}
