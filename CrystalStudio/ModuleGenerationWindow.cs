using System.Globalization;
using System.IO;
using Avalonia.Data;

namespace CrystalStudio;

internal enum ModuleGenerationViewState { Empty, Generating, Ready, Stale, Failed }

internal sealed class ModuleGenerationWindow : EditorWindow
{
    private readonly StampCategoryIndex catalogue;
    private readonly AssetStore? assets;
    private readonly List<ModulePoolItem> choices = [];
    private readonly ContentControl previewHost = new();
    private readonly TextBlock selectedCount = new(), previewStatus = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock instanceInfo = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ProgressBar progress = new() { IsIndeterminate = true, Height = 4, IsVisible = false };
    private readonly Button generate = new() { Content = LocalText.T("Generate connected layout"), Name = "GenerateModuleLayoutButton" };
    private readonly Button stop = new() { Content = LocalText.T("Stop generation"), IsEnabled = false };
    private readonly CheckBox connections = new() { Content = LocalText.T("Connections"), IsChecked = true };
    private readonly CheckBox regions = new() { Content = LocalText.T("Module boundaries"), IsChecked = true };
    private CancellationTokenSource? pending;
    private int revision;
    private bool closed;
    private sealed record Candidate(string[] Modules, int Count, int Seed, int Width, int Height, ModuleLayout Layout, string Summary)
    { public override string ToString() => Summary; }
    private readonly List<Candidate> candidates = [];
    private readonly ComboBox candidateList = new() { Name = "ModuleGenerationCandidates", HorizontalAlignment = HorizontalAlignment.Stretch };
    internal ModuleConnectionCanvas? Preview { get; private set; }
    internal ModuleGenerationPlan? DisplayedPlan { get; private set; }
    internal ModuleGenerationViewState ViewState { get; private set; }
    internal ModuleProblem? Problem { get; private set; }
    internal TextBox SearchBox { get; } = new();
    internal ListBox PoolList { get; } = new() { Height = 240 };
    internal TextBox CountBox { get; } = new() { Text = "12" };
    internal TextBox SeedBox { get; } = new() { Text = Random.Shared.Next(int.MaxValue).ToString(CultureInfo.InvariantCulture) };
    internal TextBox WidthBox { get; } = new() { Text = "256" };
    internal TextBox HeightBox { get; } = new() { Text = "256" };
    internal ComboBox LayoutBox { get; } = new() { ItemsSource = Enum.GetValues<ModuleLayout>(), SelectedItem = ModuleLayout.Compact };
    internal Button ApplyButton { get; } = new() { Content = LocalText.T("Create new map"), IsEnabled = false };
    internal TextBlock Feedback { get; } = new() { TextWrapping = TextWrapping.Wrap };
    internal ModuleGenerationPlan? Plan { get; private set; }
    internal IReadOnlyList<string> SelectedModuleIds => choices.Where(c => c.Selected).Select(c => c.Entry.Id).ToArray();

    internal ModuleGenerationWindow(StampCategoryIndex catalogue, AssetStore? assets)
    {
        this.catalogue = catalogue; this.assets = assets;
        Title = LocalText.T("Generate modules automatically"); Width = 1240; Height = 880; MinWidth = 980; MinHeight = 700;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new Grid { ColumnDefinitions = new ColumnDefinitions("370,*"), Margin = new Thickness(14) };
        var form = new StackPanel { Spacing = 8, Margin = new Thickness(0, 0, 14, 0) };
        form.Children.Add(new TextBlock { Text = LocalText.T("Module pool (instances may repeat)") });
        SearchBox.Watermark = LocalText.T("Search name, tag or connection type"); form.Children.Add(SearchBox);
        foreach (var entry in catalogue.Objects.Where(s => s.Connections is { Points.Count: > 0 }).OrderBy(s => s.Id, StringComparer.Ordinal))
        {
            var item = new ModulePoolItem(catalogue, entry, assets); item.SelectionChanged += InvalidatePreview; choices.Add(item);
        }
        PoolList.ItemTemplate = new FuncDataTemplate<ModulePoolItem>((item, _) =>
        {
            if (item == null) return new TextBlock();
            var card = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,100,*"), Margin = new Thickness(0, 4) };
            ToolTip.SetTip(card, item.Entry.File);
            var check = new CheckBox { VerticalAlignment = VerticalAlignment.Center };
            check.Bind(CheckBox.IsCheckedProperty, new Binding(nameof(item.Selected)) { Source = item, Mode = BindingMode.TwoWay }); card.Children.Add(check);
            var image = new Image { Width = 96, Height = 70, Stretch = Stretch.Uniform };
            image.Bind(Image.SourceProperty, new Binding(nameof(item.Thumbnail)) { Source = item }); Grid.SetColumn(image, 1); card.Children.Add(image);
            var text = new StackPanel { Spacing = 4 };
            text.Children.Add(new TextBlock { Text = item.Name, TextWrapping = TextWrapping.Wrap });
            var summary = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 11 };
            summary.Bind(TextBlock.TextProperty, new Binding(nameof(item.Summary)) { Source = item }); text.Children.Add(summary);
            Grid.SetColumn(text, 2); card.Children.Add(text);
            card.Loaded += async (_, _) => await item.LoadAsync(); card.Unloaded += (_, _) => item.Pause(); return card;
        });
        form.Children.Add(PoolList); form.Children.Add(selectedCount);
        var poolActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var all = new Button { Content = LocalText.T("Select search results") }; all.Click += (_, _) => SelectSearchResults(true);
        var none = new Button { Content = LocalText.T("Clear search results") }; none.Click += (_, _) => SelectSearchResults(false);
        poolActions.Children.Add(all); poolActions.Children.Add(none); form.Children.Add(poolActions);
        void Pair(StackPanel parent, string firstName, Control first, string secondName, Control second)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*") };
            var a = new StackPanel { Spacing = 3, Margin = new Thickness(0, 0, 8, 0) };
            a.Children.Add(new TextBlock { Text = LocalText.T(firstName) }); a.Children.Add(first); row.Children.Add(a);
            var b = new StackPanel { Spacing = 3 }; b.Children.Add(new TextBlock { Text = LocalText.T(secondName) }); b.Children.Add(second);
            Grid.SetColumn(b, 1); row.Children.Add(b); parent.Children.Add(row);
        }
        Pair(form, "Module count", CountBox, "Generation seed", SeedBox);
        Pair(form, "Reference width", WidthBox, "Reference height", HeightBox);
        form.Children.Add(new TextBlock { Text = LocalText.T("Map size is estimated from module footprints and count. These dimensions are a reference; the generator adjusts them as needed, up to 4096 per side and 1,000,000 cells. The original map is preserved."), TextWrapping = TextWrapping.Wrap });
        form.Children.Add(new TextBlock { Text = LocalText.T("Layout style") }); form.Children.Add(LayoutBox);
        form.Children.Add(new TextBlock { Text = LocalText.T("The generator chooses modules and connections automatically. Chain extends the latest module; Branching spreads outward from the start; Compact prefers nearby connections. Only empty footprints are used."), TextWrapping = TextWrapping.Wrap });
        var commands = new WrapPanel();
        generate.Click += async (_, _) => await GeneratePreviewAsync(); commands.Children.Add(generate);
        var shuffle = new Button { Content = LocalText.T("New seed"), Margin = new Thickness(6, 0, 6, 0) };
        shuffle.Click += (_, _) => SeedBox.Text = Random.Shared.Next(int.MaxValue).ToString(CultureInfo.InvariantCulture); commands.Children.Add(shuffle);
        stop.Click += (_, _) => pending?.Cancel(); commands.Children.Add(stop); form.Children.Add(commands); form.Children.Add(progress); form.Children.Add(Feedback);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 8, 0, 0) };
        ApplyButton.Click += (_, _) => { if (TryAccept()) DialogResult = true; };
        actions.Children.Add(ApplyButton); actions.Children.Add(new Button { Content = LocalText.T("Cancel"), IsCancel = true });
        var left = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        left.Children.Add(new ScrollViewer { Content = form }); Grid.SetRow(actions, 1); left.Children.Add(actions); root.Children.Add(left);
        var right = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto,Auto") };
        right.Children.Add(previewStatus);
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(0, 8) };
        var fit = new Button { Content = LocalText.T("Fit preview") }; fit.Click += (_, _) => Preview?.Fit(); toolbar.Children.Add(fit);
        toolbar.Children.Add(regions); toolbar.Children.Add(connections);
        void Overlays() { if (Preview == null) return; Preview.ShowRegions = regions.IsChecked == true; Preview.ShowConnections = connections.IsChecked == true; Preview.Refresh(); }
        regions.IsCheckedChanged += (_, _) => Overlays(); connections.IsCheckedChanged += (_, _) => Overlays();
        Grid.SetRow(toolbar, 1); right.Children.Add(toolbar); Grid.SetRow(previewHost, 2); right.Children.Add(previewHost);
        Grid.SetRow(instanceInfo, 3); right.Children.Add(instanceInfo);
        var compare = new StackPanel { Spacing = 5, Margin = new Thickness(0, 10, 0, 0) };
        compare.Children.Add(new TextBlock { Text = LocalText.T("Compare recent layouts (this session only)"), FontWeight = FontWeight.SemiBold });
        compare.Children.Add(candidateList);
        var restore = new Button { Name = "RestoreModuleCandidate", Content = LocalText.T("Restore parameters and regenerate"), IsEnabled = false };
        candidateList.SelectionChanged += (_, _) => restore.IsEnabled = candidateList.SelectedItem is Candidate;
        restore.Click += async (_, _) =>
        {
            if (candidateList.SelectedItem is not Candidate candidate) return;
            SelectModules(candidate.Modules); CountBox.Text = candidate.Count.ToString(CultureInfo.InvariantCulture);
            SeedBox.Text = candidate.Seed.ToString(CultureInfo.InvariantCulture);
            WidthBox.Text = candidate.Width.ToString(CultureInfo.InvariantCulture); HeightBox.Text = candidate.Height.ToString(CultureInfo.InvariantCulture);
            LayoutBox.SelectedItem = candidate.Layout; await GeneratePreviewAsync();
        };
        compare.Children.Add(restore); Grid.SetRow(compare, 4); right.Children.Add(compare);
        Grid.SetColumn(right, 1); root.Children.Add(right); Content = root;
        foreach (var box in new[] { CountBox, SeedBox, WidthBox, HeightBox })
            box.PropertyChanged += (_, change) => { if (change.Property == TextBox.TextProperty) InvalidatePreview(); };
        LayoutBox.SelectionChanged += (_, _) => InvalidatePreview();
        SearchBox.PropertyChanged += (_, change) => { if (change.Property == TextBox.TextProperty) FilterPool(); };
        Closed += (_, _) => { closed = true; pending?.Cancel(); Preview?.Dispose(); foreach (var item in choices) item.Dispose(); };
        FilterPool(); InvalidatePreview();
        if (choices.Count == 0) Feedback.Text = LocalText.T("No modules yet. Select a map region and use Tools → Create module from selection.");
    }
    private void FilterPool() => PoolList.ItemsSource = choices.Where(c => c.Matches(SearchBox.Text)).ToArray();
    internal void SelectSearchResults(bool selected) { foreach (var item in choices.Where(c => c.Matches(SearchBox.Text))) item.Selected = selected; }
    internal void SelectModules(IEnumerable<string> ids)
    { var selected = ids.ToHashSet(StringComparer.Ordinal); foreach (var item in choices) item.Selected = selected.Contains(item.Entry.Id); }
    private void SetState(ModuleGenerationViewState state, string message)
    {
        ViewState = state; Feedback.Text = message;
        previewStatus.Text = state switch
        {
            ModuleGenerationViewState.Ready => LocalText.T("Preview ready · click a module to inspect its connections."),
            ModuleGenerationViewState.Generating => LocalText.T("Generating… Previous preview is for reference only."),
            ModuleGenerationViewState.Stale => LocalText.T("Parameters changed. Generate again before applying."),
            ModuleGenerationViewState.Failed => LocalText.T("Generation failed. Previous preview is for reference only."),
            _ => LocalText.T("Generate a preview before applying.")
        };
        ApplyButton.IsEnabled = state == ModuleGenerationViewState.Ready && Plan != null;
        progress.IsVisible = state == ModuleGenerationViewState.Generating;
        stop.IsEnabled = state == ModuleGenerationViewState.Generating; generate.IsEnabled = state != ModuleGenerationViewState.Generating;
    }
    private void InvalidatePreview()
    {
        revision++; pending?.Cancel(); Plan = null; Problem = null;
        selectedCount.Text = LocalText.Format("Selected: {0} / {1}", choices.Count(c => c.Selected), choices.Count);
        SetState(DisplayedPlan == null ? ModuleGenerationViewState.Empty : ModuleGenerationViewState.Stale, LocalText.T("Generate a preview before applying."));
    }
    internal async Task GeneratePreviewAsync()
    {
        InvalidatePreview();
        var selected = choices.Where(c => c.Selected).Select(c => c.Entry).ToArray();
        void Fail(string message) => SetState(ModuleGenerationViewState.Failed, message);
        if (selected.Length is < 1 or > 256) { Fail(LocalText.T("Select between 1 and 256 modules for automatic generation.")); return; }
        if (!int.TryParse(CountBox.Text, out int count) || !int.TryParse(SeedBox.Text, out int seed) ||
            !int.TryParse(WidthBox.Text, out int width) || !int.TryParse(HeightBox.Text, out int height) || LayoutBox.SelectedItem is not ModuleLayout layout)
        { Fail(LocalText.T("Enter whole numbers for module count, seed and reference dimensions.")); return; }
        if (count is < 2 or > 128)
        { Fail(LocalText.T("Enter 2–128 placed module instances. One selected module type can be reused.")); return; }
        if (width <= 0 || height <= 0)
        { Fail(LocalText.T("Enter positive whole numbers for the reference width and height.")); return; }
        int request = revision; using var job = new CancellationTokenSource(); pending = job;
        SetState(ModuleGenerationViewState.Generating, LocalText.T("Selecting and connecting modules…"));
        try
        {
            var result = await Task.Run(() => ModuleMapGeneration.Generate(ModuleGeneration.LoadPool(catalogue, selected, job.Token),
                new(count, seed, width, height, layout), job.Token, area => Dispatcher.UIThread.Post(() =>
                {
                    if (!closed && request == revision && ViewState == ModuleGenerationViewState.Generating)
                        Feedback.Text = LocalText.Format("Trying a {0} × {1} new map based on module footprints…", area.Width, area.Height);
                })), job.Token);
            if (closed || request != revision || job.IsCancellationRequested) return;
            Plan = DisplayedPlan = result; Preview?.Dispose(); instanceInfo.Text = ""; PoolList.SelectedItem = null;
            Preview = new(result.Placement.Cells, result.Placement.Bounds, assets) { Points = result.Placement.Openings,
                ShowConnections = connections.IsChecked == true, ShowRegions = regions.IsChecked == true };
            Preview.SetMap(result.Placement.Generated, result.Placement.Bounds);
            UpdateRegions(-1); Preview.Pressed += InspectInstance; previewHost.Content = Preview;
            SetState(ModuleGenerationViewState.Ready, LocalText.Format("Reference: {0} × {1}. Actual new map: {2} × {3}.", width, height, result.Placement.Source.Width, result.Placement.Source.Height) + "\n" +
                LocalText.Format("Generated {0} modules and {1} connections. All {2} walkable cells are connected. Seed: {3}. Ctrl+Z undoes the whole layout.", result.Placement.Placements.Count, result.Links.Count, result.WalkableCells, seed));
            previewStatus.Text = LocalText.Format("Reference: {0} × {1}. Actual new map: {2} × {3}.", width, height, result.Placement.Source.Width, result.Placement.Source.Height) + "\n" +
                LocalText.T("Preview ready · click a module to inspect its connections.");
            int unique = result.Placement.Placements.Select(placement => placement.Module).Distinct().Count();
            string statistics = LocalText.Format("Requested {0} · Placed {1} · Types {2} · Reused {3} · Links {4} · Joined ports {5}",
                count, result.Placement.Placements.Count, unique, result.Placement.Placements.Count - unique, result.Links.Count, result.Placement.Openings.Count);
            previewStatus.Text += "\n" + statistics;
            Feedback.Text += "\n" + statistics + "\n" + LocalText.T("Creates a new map. The original map is not modified.");
            string candidateSummary = LocalText.Format("Seed {0}", seed) + $" · {result.Placement.Source.Width} × {result.Placement.Source.Height} · " + statistics;
            var candidate = new Candidate(selected.Select(entry => entry.Id).Order().ToArray(), count, seed, width, height, layout, candidateSummary);
            candidates.RemoveAll(previous => previous.Seed == seed && previous.Count == count && previous.Width == width && previous.Height == height &&
                previous.Layout == layout && previous.Modules.SequenceEqual(candidate.Modules));
            candidates.Insert(0, candidate); if (candidates.Count > 5) candidates.RemoveAt(5);
            candidateList.ItemsSource = candidates.ToArray(); candidateList.SelectedIndex = 0;
            Dispatcher.UIThread.Post(() => { if (!closed) Preview?.Fit(); });
        }
        catch (OperationCanceledException)
        { if (!closed && request == revision) SetState(DisplayedPlan == null ? ModuleGenerationViewState.Empty : ModuleGenerationViewState.Stale, LocalText.T("Generation cancelled. The map was not changed.")); }
        catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or OverflowException or UnauthorizedAccessException)
        {
            if (closed || request != revision) return;
            Problem = ModuleDiagnostics.Read(error); Fail(error.Message);
            if (Problem is { ModuleId: { } id } problem)
            {
                var item = choices.FirstOrDefault(c => c.Entry.Id == id);
                SearchBox.Text = ""; PoolList.SelectedItem = item; if (item != null) PoolList.ScrollIntoView(item);
                instanceInfo.Text = LocalText.Format("Problem module: {0} · Connection: {1} · Cell: ({2}, {3})", item?.Name ?? id, problem.ConnectionId ?? "—", problem.X?.ToString() ?? "—", problem.Y?.ToString() ?? "—");
            }
        }
        finally { if (ReferenceEquals(pending, job)) pending = null; }
    }
    private void UpdateRegions(int selected)
    {
        if (Preview == null || DisplayedPlan == null) return;
        Preview.Regions = DisplayedPlan.Placement.Placements.Select((p, i) =>
        {
            var b = ModuleConnectionRules.GetBounds(p.Module.Cells, p.Module.Connections);
            return new ModulePreviewRegion($"#{i + 1} {p.Module.Name}", new(b.Left + p.X, b.Top + p.Y, b.Right + p.X, b.Bottom + p.Y), i == selected);
        }).ToArray(); Preview.Refresh();
    }
    private void InspectInstance(int x, int y)
    {
        if (Preview == null || DisplayedPlan == null) return;
        int index = Preview.Regions.ToList().FindIndex(r => x >= r.Bounds.Left && x <= r.Bounds.Right && y >= r.Bounds.Top && y <= r.Bounds.Bottom);
        UpdateRegions(index); if (index < 0) { instanceInfo.Text = ""; return; }
        var module = DisplayedPlan.Placement.Placements[index].Module;
        instanceInfo.Text = $"#{index + 1} {module.Name} · " + string.Join(" · ", module.Connections.Points.Select(p => $"{p.Id}: {LocalText.T(p.Edge.ToString())} {p.Kind} ×{p.Width}"));
    }
    internal bool TryAccept()
    {
        if (Plan == null || ViewState != ModuleGenerationViewState.Ready) return false;
        if (Plan.Placement.Source.PickupRevision != Plan.Placement.SourceRevision)
        { InvalidatePreview(); Feedback.Text = LocalText.T("The map changed after the module preview. Generate a fresh preview."); return false; }
        return true;
    }
}
