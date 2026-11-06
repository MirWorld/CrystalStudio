using System.IO;

namespace CrystalStudio;

internal sealed class ModuleCaptureWindow : EditorWindow
{
    private readonly ModuleCaptureDraft draft;
    private readonly string indexPath;
    private readonly AssetStore? assets;
    private readonly StackPanel choices = new() { Spacing = 3 };
    private readonly List<(ModuleConnection Point, CheckBox Check)> candidates = [];
    private readonly ModuleConnectionCanvas diagram;
    private readonly MapViewport preview = new() { IsHitTestVisible = false, Focusable = false, CollisionVisible = true };
    private readonly StackPanel form = new() { Spacing = 8, Margin = new Thickness(0, 0, 14, 0) };
    private readonly CancellationTokenSource lifetime = new();
    private bool closed, saving, closeRequested;
    internal TextBox NameBox { get; } = new() { Text = LocalText.T("New module") };
    internal Button SaveButton { get; } = new() { Content = LocalText.T("Save module to catalogue") };
    internal TextBlock Feedback { get; } = new() { TextWrapping = TextWrapping.Wrap };
    internal CategorizedStamp? Saved { get; private set; }
    internal ModuleConnections Reviewed => new(1, candidates.Where(c => c.Check.IsChecked == true).Select(c => c.Point).ToList())
        { Footprint = draft.Connections.Footprint };

    internal ModuleCaptureWindow(ModuleCaptureDraft draft, string indexPath, AssetStore? assets)
    {
        this.draft = draft; this.indexPath = indexPath; this.assets = assets;
        Title = LocalText.T("Create module from selection"); Width = 1120; Height = 800; MinWidth = 880; MinHeight = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var bounds = ModuleConnectionRules.GetBounds(draft.Cells, draft.Connections);
        diagram = new ModuleConnectionCanvas(draft.Cells, bounds);
        var root = new Grid { ColumnDefinitions = new ColumnDefinitions("360,*"), Margin = new Thickness(14) };
        form.Children.Add(new TextBlock { Text = LocalText.T("Module name") }); form.Children.Add(NameBox);
        form.Children.Add(new TextBlock { Text = LocalText.Format("Selection: {0} × {1} cells. Empty margins are preserved.",
            bounds.Right - bounds.Left + 1, bounds.Bottom - bounds.Top + 1), TextWrapping = TextWrapping.Wrap });
        form.Children.Add(new TextBlock { Text = LocalText.T("Review detected openings (1–64)") });
        form.Children.Add(new ScrollViewer { Content = choices, Height = 230, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto });
        var edit = new Button { Content = LocalText.T("Edit module connections…") };
        edit.Click += (_, _) =>
        {
            if (Reviewed.Points.Count > 64) { Feedback.Text = LocalText.T("Review detected openings (1–64)"); return; }
            var dialog = new ModuleConnectionsWindow(draft.Cells, Reviewed) { Owner = this };
            if (dialog.ShowDialog() == true && dialog.Result is { } result) SetCandidates(result.Points);
        };
        form.Children.Add(edit);
        form.Children.Add(new TextBlock
        {
            Text = LocalText.T("Openings are detected from collision data, not artwork. Empty cells are walkable. Review the proposed exits and edit their type or width if needed. All walkable cells must be connected inside the selection."),
            TextWrapping = TextWrapping.Wrap
        });
        form.Children.Add(Feedback);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        SaveButton.Click += async (_, _) => { if (await SaveAsync() && !closed) DialogResult = true; };
        actions.Children.Add(SaveButton); actions.Children.Add(new Button { Content = LocalText.T("Cancel"), IsCancel = true });
        form.Children.Add(actions); root.Children.Add(new ScrollViewer { Content = form });
        var map = new MapDocument(bounds.Right - bounds.Left + 1, bounds.Bottom - bounds.Top + 1);
        foreach (var cell in draft.Cells) map.Set(cell.X - bounds.Left, cell.Y - bounds.Top, cell.Cell.Copy());
        // A one-cell leading margin retains ground rendering parity for odd selection origins.
        int px = bounds.Left & 1, py = bounds.Top & 1;
        if (px != 0 || py != 0)
        {
            map = new MapDocument(map.Width + px, map.Height + py);
            foreach (var cell in draft.Cells) map.Set(cell.X - bounds.Left + px, cell.Y - bounds.Top + py, cell.Cell.Copy());
        }
        preview.Assets = assets; preview.SetDocument(map);
        var tabs = new TabControl();
        tabs.Items.Add(new TabItem { Header = LocalText.T("Map preview"), Content = preview });
        tabs.Items.Add(new TabItem { Header = LocalText.T("Connection layout"), Content = diagram });
        tabs.SelectedIndex = assets == null ? 1 : 0;
        Grid.SetColumn(tabs, 1); root.Children.Add(tabs); Content = root;
        preview.SizeChanged += (_, _) =>
        {
            if (preview.Bounds.Width <= 0 || preview.Bounds.Height <= 0) return;
            preview.SetZoom(Math.Min((preview.Bounds.Width - 48) / ((map.Width + 2) * 48.0),
                (preview.Bounds.Height - 48) / ((map.Height + 2) * 32.0)));
            preview.CenterOnWorld(new Point(map.Width * 24.0, map.Height * 16.0));
        };
        NameBox.PropertyChanged += (_, change) => { if (change.Property == TextBox.TextProperty) Refresh(); };
        if (assets != null) assets.Updated += AssetsUpdated;
        Closing += (_, e) =>
        {
            if (!saving) return;
            // Finish cancellation/commit before returning to the owner, so a completed save
            // is always reflected in its catalogue even when the user closes during I/O.
            e.Cancel = true; closeRequested = true; lifetime.Cancel();
        };
        Closed += (_, _) =>
        {
            closed = true; lifetime.Cancel(); preview.StopBackgroundWork();
            if (assets != null) assets.Updated -= AssetsUpdated;
        };
        SetCandidates(draft.Connections.Points);
    }

    private void AssetsUpdated() => Dispatcher.UIThread.Post(() => { if (!closed) preview.RefreshAssets(); });
    internal void SelectConnections(IEnumerable<string> ids)
    {
        var selected = ids.ToHashSet(StringComparer.Ordinal);
        foreach (var candidate in candidates) candidate.Check.IsChecked = selected.Contains(candidate.Point.Id);
    }
    internal void SetCandidates(IReadOnlyList<ModuleConnection> points)
    {
        candidates.Clear(); choices.Children.Clear();
        foreach (var point in points)
        {
            var check = new CheckBox { Content = $"{LocalText.T(point.Edge.ToString())} ({point.X},{point.Y}) ×{point.Width} · {point.Kind}", IsChecked = true };
            check.IsCheckedChanged += (_, _) => Refresh();
            candidates.Add((point, check)); choices.Children.Add(check);
        }
        Refresh();
    }
    private void Refresh()
    {
        var connections = Reviewed;
        diagram.Points = connections.Points; diagram.InvalidateVisual(); SaveButton.IsEnabled = false;
        try
        {
            ModuleCapture.Prepare(draft, NameBox.Text ?? "", connections);
            Feedback.Text = LocalText.T("The module is connected and ready to save. It will appear in automatic generation after saving.");
            SaveButton.IsEnabled = !saving && !closed;
        }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException or OverflowException)
        { Feedback.Text = exception.Message; }
    }
    internal async Task<bool> SaveAsync()
    {
        if (saving || closed || Saved != null) return false;
        var connections = Reviewed; string name = NameBox.Text ?? "";
        saving = true; form.IsEnabled = false;
        try
        {
            Saved = await Task.Run(() => ModuleCaptureStore.Save(indexPath, draft, name, connections, lifetime.Token), lifetime.Token);
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException or OverflowException or UnauthorizedAccessException)
        { Feedback.Text = exception.Message; return false; }
        finally { saving = false; form.IsEnabled = true; if (closeRequested) Close(); }
    }
}
