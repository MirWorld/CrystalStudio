using System.IO;

namespace CrystalStudio;

internal sealed class ModuleCaptureWindow : EditorWindow
{
    private readonly ModuleCaptureDraft draft;
    private readonly string indexPath;
    private readonly Grid form;
    private readonly CancellationTokenSource lifetime = new();
    private bool closed, saving, closeRequested;
    private bool topologyChecked;
    private string? topologyError;
    internal ModuleConnectionEditor Editor { get; }
    internal TextBox NameBox { get; } = new() { Text = LocalText.T("New module"), Width = 240 };
    internal Button SaveButton { get; } = new() { Content = LocalText.T("Save module to catalogue") };
    internal TextBlock Feedback { get; } = new() { TextWrapping = TextWrapping.Wrap };
    internal CategorizedStamp? Saved { get; private set; }
    internal ModuleConnections Reviewed => Editor.State.Connections;
    internal ModuleCaptureWindow(ModuleCaptureDraft draft, string indexPath, AssetStore? assets)
    {
        this.draft = draft; this.indexPath = indexPath;
        Title = LocalText.T("Create module from selection"); Width = 1280; Height = 850; MinWidth = 1040; MinHeight = 680;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Editor = new(draft.Cells, draft.Connections, assets);
        form = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto"), Margin = new Thickness(14) };
        var heading = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(0, 0, 0, 12) };
        heading.Children.Add(new TextBlock { Text = LocalText.T("Module name"), VerticalAlignment = VerticalAlignment.Center }); heading.Children.Add(NameBox);
        var b = Editor.State.Bounds;
        heading.Children.Add(new TextBlock { Text = LocalText.Format("Selection: {0} × {1} cells. Empty margins are preserved.", b.Right - b.Left + 1, b.Bottom - b.Top + 1), VerticalAlignment = VerticalAlignment.Center });
        form.Children.Add(heading); Grid.SetRow(Editor, 1); form.Children.Add(Editor);
        var help = new TextBlock { Text = LocalText.T("Detected openings are suggestions based on collision data. Empty cells are walkable; disabling an opening does not build a wall."), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 10) };
        Grid.SetRow(help, 2); form.Children.Add(help);
        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        footer.Children.Add(Feedback);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        SaveButton.Click += async (_, _) => { if (await SaveAsync() && !closed) DialogResult = true; };
        actions.Children.Add(SaveButton); actions.Children.Add(new Button { Content = LocalText.T("Cancel"), IsCancel = true });
        Grid.SetColumn(actions, 1); footer.Children.Add(actions); Grid.SetRow(footer, 3); form.Children.Add(footer); Content = form;
        NameBox.PropertyChanged += (_, change) => { if (change.Property == TextBox.TextProperty) Refresh(); };
        Editor.Changed += Refresh;
        Closing += (_, e) => { if (saving) { e.Cancel = true; closeRequested = true; lifetime.Cancel(); } };
        Closed += (_, _) => { closed = true; lifetime.Cancel(); Editor.Dispose(); };
        Refresh();
    }
    internal void SelectConnections(IEnumerable<string> ids)
    {
        var selected = ids.ToHashSet(StringComparer.Ordinal);
        foreach (var item in Editor.State.Entries.ToArray()) Editor.State.Toggle(item.Point.Id, selected.Contains(item.Point.Id));
    }
    private void Refresh()
    {
        SaveButton.IsEnabled = false;
        if (Editor.State.Problem != null || Editor.State.Adding)
        { Feedback.Text = LocalText.T("Finish or cancel the current connection edit before saving."); return; }
        try
        {
            ModuleCapture.EnsureCurrent(draft); ModuleCapture.ValidateName(NameBox.Text ?? "");
            var connections = Editor.State.PreviewConnections;
            if (Editor.State.Check(connections) is { } problem) throw new InvalidDataException(problem.Message);
            if (connections.Points.Count == 0) throw new InvalidDataException(LocalText.T("Enable at least one connection before saving a module."));
            // Artwork is a fixed snapshot. Only opening validation needs to run while dragging.
            if (!topologyChecked)
            {
                try { ModuleGeneration.ValidateModule(new(NameBox.Text!, draft.Cells, connections)); }
                catch (InvalidDataException error) { topologyError = error.InnerException?.Message ?? error.Message; }
                topologyChecked = true;
            }
            if (topologyError != null) throw new InvalidDataException(topologyError);
            Feedback.Text = LocalText.T("The module is connected and ready to save. It will appear in automatic generation after saving.");
            SaveButton.IsEnabled = !saving && !closed;
        }
        catch (Exception error) when (error is InvalidDataException or InvalidOperationException or OverflowException)
        { Feedback.Text = error.Message; }
    }
    internal async Task<bool> SaveAsync()
    {
        if (saving || closed || Saved != null) return false;
        var connections = Editor.Accept(); if (connections == null) return false;
        string name = NameBox.Text ?? ""; saving = true; form.IsEnabled = false;
        try
        {
            Saved = await Task.Run(() => ModuleCaptureStore.Save(indexPath, draft, name, connections, lifetime.Token), lifetime.Token); return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or OverflowException or UnauthorizedAccessException)
        { Feedback.Text = error.Message; return false; }
        finally { saving = false; form.IsEnabled = true; if (closeRequested) Close(); }
    }
}
