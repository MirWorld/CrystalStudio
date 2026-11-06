namespace CrystalStudio;

public partial class MainWindow
{
    private void CheckMapClick(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        var source = Viewport.Document;
        long revision = source.PickupRevision;
        var snapshot = new MapDocument(source.Width, source.Height);
        foreach (var (key, cell) in source.Cells) snapshot.Set(key % source.Width, key / source.Width, cell.Copy());
        var libraries = assets?.Libraries.ToDictionary(pair => pair.Key, pair => pair.Value);
        using var cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        var dialog = new EditorWindow { Title = LocalText.T("Check Map"), Width = 760, Height = 480, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new DockPanel { Margin = new(18) };
        var summary = new TextBlock { Text = LocalText.T("Checking map…"), TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 12) };
        DockPanel.SetDock(summary, Dock.Top); panel.Children.Add(summary);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new(0, 12, 0, 0) };
        DockPanel.SetDock(buttons, Dock.Bottom); panel.Children.Add(buttons);
        var locate = new Button { Content = LocalText.T("Locate"), IsEnabled = false };
        var cancel = new Button { Content = LocalText.T("Cancel"), IsCancel = true };
        buttons.Children.Add(locate); buttons.Children.Add(cancel);
        var list = new ListBox { ItemTemplate = new FuncDataTemplate<MapIssue>((issue, _) => new TextBlock
            { Text = issue?.ToString(), TextWrapping = TextWrapping.Wrap, Margin = new(3) }) };
        panel.Children.Add(list); dialog.Content = panel;
        bool finished = false;
        dialog.Closed += (_, _) => cancellation.Cancel();
        list.SelectionChanged += (_, _) => locate.IsEnabled = finished && list.SelectedItem is MapIssue;
        locate.Click += (_, _) =>
        {
            if (list.SelectedItem is not MapIssue issue) return;
            Viewport.LocateCell(issue.X, issue.Y);
            UpdateCellInspector(issue.X, issue.Y);
            dialog.DialogResult = true;
        };
        dialog.Loaded += async (_, _) =>
        {
            try
            {
                var result = await Task.Run(() => MapCheck.Run(snapshot, libraries, token));
                if (!dialog.IsVisible || closed) return;
                if (Viewport.Document != source || source.PickupRevision != revision)
                { summary.Text = LocalText.T("The map changed. Run the check again."); return; }
                finished = true;
                list.ItemsSource = result.Issues;
                if (result.Issues.Count > 0) list.SelectedIndex = 0;
                summary.Text = LocalText.Format("{0:N0} findings; showing {1:N0}. Disconnected areas may be intentional.", result.Total, result.Issues.Count)
                    + (libraries == null ? "\n" + LocalText.T("Load libraries to check image references.") : "");
                cancel.Content = LocalText.T("Close");
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { StudioLog.Error("Map.Check", ex); if (dialog.IsVisible) summary.Text = ex.Message; }
        };
        busy = true; UpdateLabels();
        try { dialog.ShowDialog(); }
        finally { busy = false; UpdateLabels(); }
    }
}
