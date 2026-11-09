namespace CrystalStudio;

internal sealed class ResourceDiagnosticsView : StackPanel
{
    private readonly GeneratorPreviewState state;
    private readonly ComboBox filter = new() { Name = "QuickMapResourceFilter", SelectedIndex = 0 };
    private readonly ListBox issues = new() { Name = "QuickMapResourceIssues", MaxHeight = 130 };
    private readonly TextBlock detail = new() { Name = "QuickMapResourceDetails", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock copied = new() { TextWrapping = TextWrapping.Wrap };
    private MapResourceReport? displayedReport;
    private int displayedFilter = -1;

    internal ResourceDiagnosticsView(GeneratorPreviewState state, string prefix = "QuickMap")
    {
        this.state = state; Spacing = 6;
        filter.Name = prefix + "ResourceFilter"; issues.Name = prefix + "ResourceIssues"; detail.Name = prefix + "ResourceDetails";
        filter.ItemsSource = new[] { LocalText.T("All artwork checks"), LocalText.T("Required artwork problems"), LocalText.T("Optional artwork problems") };
        Avalonia.Automation.AutomationProperties.SetName(filter, LocalText.T("Artwork problem filter"));
        Avalonia.Automation.AutomationProperties.SetName(issues, LocalText.T("Artwork checks; select an item for its source"));
        issues.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<MapResourceCheck>((check, _) => new TextBlock
        {
            Text = check == null ? "" : LocalText.T(check.Label) + " · " + LocalText.T(check.Available ? "Ready" : check.Required ? "Required artwork problems" : "Optional artwork problems"),
            TextWrapping = TextWrapping.Wrap
        });
        var copy = new Button { Name = prefix + "CopyDiagnostics", Content = LocalText.T("Copy diagnostic report") };
        copy.Click += async (_, _) =>
        {
            try
            {
                if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) { copied.Text = LocalText.T("Clipboard unavailable."); return; }
                await clipboard.SetTextAsync(BuildReport(state)); copied.Text = LocalText.T("Diagnostic report copied. It includes local library paths.");
            }
            catch (Exception error) { copied.Text = error.Message; }
        };
        Children.Add(new TextBlock { Text = LocalText.T("Select a problem to see its source. Recheck uses currently loaded artwork; no libraries are reloaded or settings changed."), TextWrapping = TextWrapping.Wrap });
        Children.Add(filter); Children.Add(issues); Children.Add(new ScrollViewer { Content = detail, MaxHeight = 180 });
        Children.Add(copy); Children.Add(copied);
        filter.SelectionChanged += (_, _) => Refresh(); issues.SelectionChanged += (_, _) => ShowDetail();
        state.Changed += StateChanged;
        Refresh();
    }

    private void StateChanged(object? sender, EventArgs args) => Refresh();
    private void Refresh()
    {
        if (ReferenceEquals(displayedReport, state.Resources) && displayedFilter == filter.SelectedIndex) { ShowDetail(); return; }
        displayedReport = state.Resources; displayedFilter = filter.SelectedIndex;
        string? selected = (issues.SelectedItem as MapResourceCheck)?.Label;
        var checks = state.Resources?.Checks.Where(check => filter.SelectedIndex switch
        { 1 => check.Required && !check.Available, 2 => !check.Required && !check.Available, _ => true }).ToArray() ?? [];
        issues.ItemsSource = checks;
        issues.SelectedItem = checks.FirstOrDefault(check => check.Label == selected) ?? checks.FirstOrDefault(check => !check.Available) ?? checks.FirstOrDefault();
        ShowDetail();
    }
    private void ShowDetail() => detail.Text = (issues.SelectedItem is MapResourceCheck check
        ? LocalText.T(check.Label) + "\n" + (check.Detail ?? LocalText.T("No per-item source details; see the diagnostic report.")) + "\n\n" : "")
        + LocalText.T("Choose another style, or return to map settings and cancel the preview. Then check the library format and folder in Settings and reopen Quick Create. Do not reload libraries while this preview is open.")
        + "\n\n" + state.SourceDescription;

    internal static string BuildReport(GeneratorPreviewState state) => LocalText.T("Artwork diagnostic report") + "\n" + DateTimeOffset.Now.ToString("O")
        + "\n" + LocalText.T(state.Phase.ToString()) + " · " + LocalText.T(state.Stage)
        + "\n\n" + (state.Resources?.Summary ?? LocalText.T("No artwork report yet."))
        + "\n\n" + string.Join("\n\n", state.Resources?.Checks.Select(check => LocalText.T(check.Label) + " · " + LocalText.T(check.Available ? "Ready" : check.Required ? "Required artwork problems" : "Optional artwork problems") + "\n" + check.Detail) ?? [])
        + (state.Failure == null ? "" : "\n" + state.Failure) + "\n\n" + state.SourceDescription;
}
