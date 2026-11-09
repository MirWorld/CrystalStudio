namespace CrystalStudio;

internal sealed class MapCheckWindow : EditorWindow
{
    private readonly Func<(MapDocument Document, AssetStore? Assets)> current;
    private readonly Func<bool> canInspect;
    private readonly TextBlock summary = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ComboBox groups = new() { Name = "MapCheckGroups" };
    private readonly ListBox findings = new() { Name = "MapCheckFindings" };
    private readonly Button locate = new() { Name = "MapCheckLocate", Content = LocalText.T("Locate"), IsEnabled = false };
    private readonly Button previous = new() { Content = LocalText.T("Previous finding"), IsEnabled = false };
    private readonly Button next = new() { Content = LocalText.T("Next finding"), IsEnabled = false };
    private readonly Button refresh = new() { Name = "MapCheckRefresh", Content = LocalText.T("Check again") };
    private CancellationTokenSource? pending;
    private MapDocument? source;
    private AssetStore? sourceAssets;
    private long revision;
    private bool closed, ready;
    private MapCheckResult? result;

    internal MapCheckWindow(Func<(MapDocument Document, AssetStore? Assets)> current, Action<MapIssue> locateIssue, Func<bool>? canInspect = null)
    {
        this.current = current;
        this.canInspect = canInspect ?? (() => true);
        Title = LocalText.T("Check Map"); Width = 760; Height = 500; MinWidth = 540; MinHeight = 340;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new Grid { Margin = new Thickness(18), RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto") };
        root.Children.Add(summary);
        groups.Margin = new Thickness(0, 10); Grid.SetRow(groups, 1); root.Children.Add(groups);
        findings.ItemTemplate = new FuncDataTemplate<MapIssue>((issue, _) => new TextBlock { Text = issue?.ToString(), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4) });
        Grid.SetRow(findings, 2); root.Children.Add(findings);
        var actions = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        foreach (var button in new[] { previous, next, locate, refresh, new Button { Content = LocalText.T("Close"), IsCancel = true } })
        { button.Margin = new Thickness(0, 0, 8, 4); actions.Children.Add(button); }
        Grid.SetRow(actions, 3); root.Children.Add(actions); Content = root;
        locate.Click += (_, _) => { RefreshValidity(); if (locate.IsEnabled && findings.SelectedItem is MapIssue issue) locateIssue(issue); };
        previous.Click += (_, _) => SelectFinding(-1); next.Click += (_, _) => SelectFinding(1);
        findings.SelectionChanged += (_, _) => UpdateActions();
        groups.SelectionChanged += (_, _) => Filter();
        refresh.Click += async (_, _) => await CheckAsync();
        Loaded += async (_, _) => await CheckAsync();
        Closed += (_, _) => { closed = true; pending?.Cancel(); };
    }

    internal void RefreshValidity()
    {
        UpdateActions(); refresh.IsEnabled = pending == null && canInspect();
        if (source == null) return;
        var now = current();
        if (now.Document == source && now.Assets == sourceAssets && source.PickupRevision == revision) return;
        pending?.Cancel(); ready = false;
        summary.Text = LocalText.T("The map changed. Run the check again."); UpdateActions();
    }

    private void UpdateActions()
    {
        locate.IsEnabled = ready && canInspect() && findings.SelectedItem is MapIssue;
        previous.IsEnabled = locate.IsEnabled && findings.SelectedIndex > 0;
        next.IsEnabled = locate.IsEnabled && findings.SelectedIndex + 1 < findings.ItemCount;
    }

    private void SelectFinding(int direction)
    {
        RefreshValidity(); if (!ready || !canInspect()) return;
        findings.SelectedIndex = Math.Clamp(findings.SelectedIndex + direction, 0, Math.Max(0, findings.ItemCount - 1));
        if (findings.SelectedItem is { } selected) findings.ScrollIntoView(selected);
        locate.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private void Filter()
    {
        findings.ItemsSource = result?.Issues.Where(issue => groups.SelectedIndex <= 0 || LocalText.T(issue.Kind) == groups.SelectedItem?.ToString()).ToArray();
        if (findings.ItemCount > 0) findings.SelectedIndex = 0;
        UpdateActions();
    }

    private async Task CheckAsync()
    {
        if (closed || !canInspect()) return;
        pending?.Cancel(); using var job = new CancellationTokenSource(); pending = job;
        (source, sourceAssets) = current(); revision = source.PickupRevision;
        var document = source; long version = revision; var loadedAssets = sourceAssets;
        ready = false; refresh.IsEnabled = false; UpdateActions(); summary.Text = LocalText.T("Checking map…");
        try
        {
            var snapshot = new MapDocument(document.Width, document.Height);
            foreach (var (key, cell) in document.Cells) snapshot.Set(key % document.Width, key / document.Width, cell.Copy());
            var libraries = loadedAssets?.Libraries.ToDictionary(pair => pair.Key, pair => pair.Value);
            var checkedResult = await Task.Run(() => MapCheck.Run(snapshot, libraries, job.Token), job.Token);
            if (closed || job.IsCancellationRequested || current().Document != document || current().Assets != loadedAssets || document.PickupRevision != version) return;
            result = checkedResult; ready = true;
            groups.ItemsSource = new[] { LocalText.T("All findings") }.Concat(result.Issues.Select(issue => LocalText.T(issue.Kind)).Distinct()).ToArray();
            groups.SelectedIndex = 0; Filter();
            summary.Text = LocalText.Format("{0:N0} findings; showing {1:N0}. Disconnected areas may be intentional.", result.Total, result.Issues.Count)
                + (libraries == null ? "\n" + LocalText.T("Load libraries to check image references.") : "");
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { StudioLog.Error("Map.Check", error); if (!closed && !job.IsCancellationRequested) { summary.Text = error.Message; RefreshValidity(); } }
        finally { if (ReferenceEquals(pending, job)) { pending = null; refresh.IsEnabled = !closed && canInspect(); UpdateActions(); } }
    }
}
