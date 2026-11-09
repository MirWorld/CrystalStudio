using System.ComponentModel;
using System.IO;
using Avalonia.Data;

namespace CrystalStudio;

internal sealed class ModuleSplitChoice(ModuleSplitPart part, AssetStore? assets = null) : INotifyPropertyChanged, IDisposable
{
    private static readonly SemaphoreSlim readers = new(2);
    private CancellationTokenSource? loading;
    private bool disposed;
    public RasterImage? Thumbnail { get; private set; }
    private bool selected = part.Valid;
    internal ModuleSplitPart Part { get; } = part;
    public bool Selected
    {
        get => selected;
        set { value &= Part.Valid; if (selected == value) return; selected = value; PropertyChanged?.Invoke(this, new(nameof(Selected))); Changed?.Invoke(); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    internal event Action? Changed;
    public string Description => $"#{Part.Index + 1} · {Part.Area.Width} × {Part.Area.Height} · ({Part.Area.X}, {Part.Area.Y})\n" +
        (Part.Problem?.Message ?? LocalText.Format("{0} connections", Part.Draft!.Connections.Points.Count) + " · " + Shape());
    internal string Shape()
    {
        var edges = Part.Draft?.Connections.Points.Select(p => p.Edge).Distinct().ToArray() ?? [];
        return LocalText.T(edges.Length switch
        {
            1 => "Dead end", 2 => ((int)edges[0] + 2) % 4 == (int)edges[1] ? "Straight passage" : "Bend",
            >= 3 => "Junction", _ => "Unclassified"
        });
    }
    internal async Task LoadAsync()
    {
        if (disposed || loading != null || Thumbnail != null || assets == null || Part.Draft == null) return;
        using var job = new CancellationTokenSource(); loading = job;
        try
        {
            var bitmap = await Task.Run(async () =>
            {
                await readers.WaitAsync(job.Token);
                try { return StampPreview.Create(Part.Draft.Cells, assets, 76, 56, job.Token, waitForAssets: true, mapGroundGrid: true); }
                finally { readers.Release(); }
            }, job.Token);
            if (disposed || job.IsCancellationRequested) { bitmap?.Dispose(); return; }
            Thumbnail = bitmap; PropertyChanged?.Invoke(this, new(nameof(Thumbnail)));
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
        { /* Artwork is optional; the collision overlay and save diagnostics remain available. */ }
        finally { loading = null; }
    }
    internal void Pause() => loading?.Cancel();
    public void Dispose() { disposed = true; Pause(); Thumbnail?.Dispose(); Thumbnail = null; }
}

internal sealed partial class ModuleMapSplitWindow : EditorWindow
{
    private readonly MapDocument source;
    private readonly string indexPath;
    private readonly AssetStore? splitAssets;
    private readonly StackPanel settings;
    private readonly ProgressBar progress = new() { IsIndeterminate = true, Height = 4, IsVisible = false };
    private readonly TextBlock details = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock splitSummary = new() { Name = "ModuleSplitSummary", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
    private readonly Button stop = new() { Content = LocalText.T("Stop") };
    private CancellationTokenSource? pending;
    private bool closed, saving, closeRequested;
    private string? errorMessage;
    private int revision;
    internal ModuleSplitResult? Result { get; private set; }
    internal IReadOnlyList<CategorizedStamp>? Saved { get; private set; }
    internal bool ContinueToGeneration { get; private set; }
    internal List<ModuleSplitChoice> Choices { get; } = [];
    internal ModuleConnectionCanvas Preview { get; }
    internal ListBox PartsList { get; } = new();
    internal TextBox WidthBox { get; } = new() { Text = "64" };
    internal TextBox HeightBox { get; } = new() { Text = "64" };
    internal TextBox NameBox { get; } = new();
    internal Button SplitButton { get; } = new() { Content = LocalText.T("Preview split") };
    internal Button SaveButton { get; } = new() { Content = LocalText.T("Save selected modules") };
    internal Button GenerateButton { get; } = new() { Content = LocalText.T("Save and generate a new map") };
    internal TextBlock Feedback { get; } = new() { TextWrapping = TextWrapping.Wrap };

    internal ModuleMapSplitWindow(MapDocument source, string mapPath, string indexPath, AssetStore? assets)
    {
        this.source = source; this.indexPath = indexPath; splitAssets = assets;
        Title = LocalText.T("Split map into modules"); Width = 1280; Height = 880; MinWidth = 1020; MinHeight = 700;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        int Suggested(int dimension) => Math.Clamp(Math.Max(Math.Min(64, dimension / 4 * 2), (int)Math.Ceiling(dimension / 32.0) * 2), 4, 512);
        WidthBox.Text = Suggested(source.Width).ToString(); HeightBox.Text = Suggested(source.Height).ToString();
        string name = Path.GetFileNameWithoutExtension(mapPath); NameBox.Text = name[..Math.Min(96, name.Length)];
        var root = new Grid { RowDefinitions = new("*,Auto,Auto"), ColumnDefinitions = new("340,*"), Margin = new Thickness(14) };
        var left = new Grid { RowDefinitions = new("Auto,*,Auto"), Margin = new Thickness(0, 0, 14, 0) };
        settings = new StackPanel { Spacing = 6 };
        settings.Children.Add(new TextBlock { Text = Path.GetFileName(mapPath) + $" · {source.Width} × {source.Height}", TextWrapping = TextWrapping.Wrap });
        settings.Children.Add(new TextBlock { Text = LocalText.T("Module name prefix") }); settings.Children.Add(NameBox);
        var dimensions = new Grid { ColumnDefinitions = new("*,*") };
        var w = new StackPanel(); w.Children.Add(new TextBlock { Text = LocalText.T("Block width") }); w.Children.Add(WidthBox);
        var h = new StackPanel { Margin = new Thickness(8, 0, 0, 0) }; h.Children.Add(new TextBlock { Text = LocalText.T("Block height") }); h.Children.Add(HeightBox);
        dimensions.Children.Add(w); Grid.SetColumn(h, 1); dimensions.Children.Add(h); settings.Children.Add(dimensions);
        settings.Children.Add(new Expander { Header = LocalText.T("Split workflow and connection rules"), HorizontalAlignment = HorizontalAlignment.Stretch,
            Content = new TextBlock { Text = LocalText.T("Preview even-sized blocks. Connections are paired across shared passable edges; empty or disconnected blocks are excluded. Choose fixed blocks or corridor-aware cuts, then inspect and adjust the candidates."), TextWrapping = TextWrapping.Wrap } });
        settings.Children.Add(SplitButton); left.Children.Add(settings);
        PartsList.ItemTemplate = new FuncDataTemplate<ModuleSplitChoice>((item, _) =>
        {
            if (item == null) return new TextBlock();
            var row = new Grid { ColumnDefinitions = new("Auto,Auto,*"), Margin = new Thickness(0, 4) };
            var check = new CheckBox { IsEnabled = item.Part.Valid, VerticalAlignment = VerticalAlignment.Center };
            check.Bind(CheckBox.IsCheckedProperty, new Binding(nameof(item.Selected)) { Source = item, Mode = BindingMode.TwoWay }); row.Children.Add(check);
            var text = new TextBlock { Text = (item.Part.Valid ? "✓ " : "! ") + item.Description, TextWrapping = TextWrapping.Wrap };
            var thumbnail = new Image { Width = assets == null ? 0 : 76, Height = 56, Stretch = Stretch.Uniform };
            thumbnail.Bind(Image.SourceProperty, new Binding(nameof(item.Thumbnail)) { Source = item }); Grid.SetColumn(thumbnail, 1); row.Children.Add(thumbnail);
            Grid.SetColumn(text, 2); row.Children.Add(text);
            row.Loaded += async (_, _) => await item.LoadAsync(); row.Unloaded += (_, _) => item.Pause(); return row;
        });
        Grid.SetRow(PartsList, 1); left.Children.Add(PartsList);
        var selection = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 8, 0, 0) };
        var all = new Button { Content = LocalText.T("Select valid blocks") }; all.Click += (_, _) => { if (pending == null) foreach (var item in Choices) item.Selected = true; };
        var none = new Button { Content = LocalText.T("Clear module selection") }; none.Click += (_, _) => { if (pending == null) foreach (var item in Choices) item.Selected = false; };
        selection.Children.Add(all); selection.Children.Add(none); Grid.SetRow(selection, 2); left.Children.Add(selection); root.Children.Add(left);

        var bounds = new ModuleConnectionRules.Bounds(0, 0, source.Width - 1, source.Height - 1);
        Preview = new(assets == null ? source.Cells.Select(c => new StampCell(c.Key % source.Width, c.Key / source.Width, c.Value)).ToArray() : [], bounds, assets);
        Preview.SetMap(source, bounds);
        var right = new Grid { RowDefinitions = new("Auto,Auto,*,Auto") };
        right.Children.Add(splitSummary);
        var toolbar = new WrapPanel { Orientation = Orientation.Horizontal };
        var fit = new Button { Content = LocalText.T("Fit preview") }; fit.Click += (_, _) => Preview.Fit(); toolbar.Children.Add(fit);
        stop.Click += (_, _) => pending?.Cancel(); toolbar.Children.Add(stop); InitializeEditing(toolbar); Grid.SetRow(toolbar, 1); right.Children.Add(toolbar);
        Grid.SetRow(Preview, 2); right.Children.Add(Preview); Grid.SetRow(details, 3); right.Children.Add(details); Grid.SetColumn(right, 1); root.Children.Add(right);
        Grid.SetRow(progress, 1); Grid.SetColumnSpan(progress, 2); root.Children.Add(progress);
        var footer = new Grid { ColumnDefinitions = new("*,Auto"), Margin = new Thickness(0, 10, 0, 0) }; footer.Children.Add(Feedback);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(SaveButton); actions.Children.Add(GenerateButton); actions.Children.Add(new Button { Content = LocalText.T("Cancel"), IsCancel = true });
        Grid.SetColumn(actions, 1); footer.Children.Add(actions); Grid.SetRow(footer, 2); Grid.SetColumnSpan(footer, 2); root.Children.Add(footer); Content = root;
        SplitButton.Click += async (_, _) => await SplitAsync();
        SaveButton.Click += async (_, _) => { if (await SaveAsync(false) && !closed) DialogResult = true; };
        GenerateButton.Click += async (_, _) => { if (await SaveAsync(true) && !closed) DialogResult = true; };
        PartsList.SelectionChanged += (_, _) => { if (Preview.CutPreview == null && mergeFirst == null) errorMessage = null; Refresh(); };
        foreach (var box in new[] { WidthBox, HeightBox }) box.PropertyChanged += (_, change) =>
        {
            if (change.Property != TextBox.TextProperty) return;
            InvalidateSplit();
            Feedback.Text = LocalText.T("Block dimensions changed. Preview the split again before saving.");
        };
        NameBox.PropertyChanged += (_, change) => { if (change.Property == TextBox.TextProperty) Refresh(); };
        Closing += (_, e) => { if (saving) { e.Cancel = true; closeRequested = true; pending?.Cancel(); } };
        Closed += (_, _) => { closed = true; pending?.Cancel(); Preview.Dispose(); foreach (var choice in Choices) choice.Dispose(); };
        Refresh();
    }

    private void Refresh()
    {
        bool busy = pending != null;
        settings.IsEnabled = !busy && Saved == null; PartsList.IsEnabled = !saving && Saved == null; progress.IsVisible = busy; stop.IsEnabled = busy;
        var selected = Choices.Where(c => c.Selected && c.Part.Valid).ToArray();
        bool validName = !string.IsNullOrWhiteSpace(NameBox.Text) && NameBox.Text.Trim().Length <= 96 && !NameBox.Text.Any(char.IsControl);
        SaveButton.IsEnabled = Result != null && !busy && Saved == null && selected.Length > 0 && validName;
        var keys = selected.SelectMany(c => c.Part.Draft!.Connections.Points).Select(p => (p.Edge, Kind: p.Kind.ToUpperInvariant(), p.Width)).ToHashSet();
        bool matches = keys.Any(k => keys.Contains(((ModuleEdge)(((int)k.Edge + 2) % 4), k.Kind, k.Width)));
        splitSummary.Text = LocalText.Format("{0} blocks · {1} usable · {2} selected.", Choices.Count, Choices.Count(choice => choice.Part.Valid), selected.Length)
            + " · " + LocalText.Format("Selected ports: {0}", selected.Sum(choice => choice.Part.Draft!.Connections.Points.Count)) + "\n"
            + LocalText.T(matches ? "Matching opposite connections found" : "Select modules with matching opposite connections to generate a map.");
        if (selected.Length > 0) splitSummary.Text += "\n" + string.Join(" · ", selected.GroupBy(choice => choice.Shape()).Select(group => $"{group.Key}: {group.Count()}"));
        GenerateButton.IsEnabled = SaveButton.IsEnabled && matches;
        var active = PartsList.SelectedItem as ModuleSplitChoice;
        Preview.Regions = Choices.Select(c => new ModulePreviewRegion($"#{c.Part.Index + 1} {(c.Part.Valid ? c.Selected ? "✓" : "○" : "!")}",
            new(c.Part.Area.X, c.Part.Area.Y, c.Part.Area.X + c.Part.Area.Width - 1, c.Part.Area.Y + c.Part.Area.Height - 1), c == active)).ToArray();
        Preview.Points = active?.Part.Draft is { } draft ? draft.Connections.Points.Select(p => p with
        { X = p.X - draft.Connections.Footprint!.X + active.Part.Area.X, Y = p.Y - draft.Connections.Footprint!.Y + active.Part.Area.Y }).ToArray() : [];
        RefreshEditing(); Preview.Refresh(); details.Text = errorMessage ?? active?.Description ?? "";
        Feedback.Text = !validName ? LocalText.T("Enter a module name prefix of 1–96 characters.") : Result == null ? LocalText.T("Preview the split before saving modules.") :
            LocalText.Format("{0} blocks · {1} usable · {2} selected.", Choices.Count, Choices.Count(c => c.Part.Valid), selected.Length) +
            (selected.Length > 0 && !matches ? "\n" + LocalText.T("Select modules with matching opposite connections to generate a map.") : "");
    }

    internal async Task SplitAsync()
    {
        if (pending != null || closed || Saved != null) return;
        if (!int.TryParse(WidthBox.Text, out int width) || !int.TryParse(HeightBox.Text, out int height))
        { Feedback.Text = LocalText.T("Use even block dimensions from 4 to 512 cells."); return; }
        int request = ++revision; previousResult = Result ?? previousResult;
        var selected = Choices.Count == 0 ? null : Choices.Where(c => c.Selected).Select(c => ModulePartitions.Key(c.Part.Area)).ToHashSet();
        int mode = ModeBox.SelectedIndex; var protectedAreas = objectProtection.Concat(protection).ToArray();
        Result = null; errorMessage = null; undo.Clear(); redo.Clear(); CancelDraft();
        using var job = new CancellationTokenSource(); pending = job; Refresh();
        try
        {
            var output = await Task.Run(() =>
            {
                var analysis = ModuleTerrain.Analyze(source, job.Token);
                var split = mode == 1 ? ModuleChannelSplit.Split(source, width, height, protectedAreas, job.Token) : ModuleMapSplit.Split(source, width, height, job.Token);
                if (mode == 0 && protectedAreas.Length > 0)
                {
                    foreach (var region in protectedAreas)
                        if (split.Parts.Count(p => ModulePartitions.Intersects(p.Area, region)) > 1)
                            throw new InvalidDataException(LocalText.T("The grid crosses a protected region. Use corridor mode or change the block dimensions."));
                }
                return (analysis, split);
            }, job.Token);
            if (closed || job.IsCancellationRequested || request != revision) return;
            Preview.Terrain = output.analysis;
            RegionsBox.ItemsSource = output.analysis.Regions.OrderByDescending(r => r.Count).ToArray();
            if (ModeBox.SelectedIndex == 1) RegionsBox.SelectedIndex = 0;
            // Unchanged rectangles retain their checkbox state; genuinely new candidates start selected.
            if (selected != null)
                foreach (var part in output.split.Parts)
                    if (previousResult?.Parts.Any(p => p.Area == part.Area) != true) selected.Add(ModulePartitions.Key(part.Area));
            Present(output.split, selected);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or OverflowException)
        { if (!closed) errorMessage = error.Message; }
        finally { pending = null; if (!closed) Refresh(); }
    }

    internal async Task<bool> SaveAsync(bool generate)
    {
        if (closed || pending != null || Saved != null) return false;
        Refresh(); if (!(generate ? GenerateButton.IsEnabled : SaveButton.IsEnabled)) return false;
        string prefix = NameBox.Text!.Trim();
        var requests = Choices.Where(c => c.Selected && c.Part.Valid).Select(c => new ModuleCaptureRequest(c.Part.Draft!,
            $"{prefix}-{c.Part.Index + 1:D3}", c.Part.Draft!.Connections, "from-map")).ToArray();
        using var job = new CancellationTokenSource(); pending = job; saving = true; errorMessage = null; Refresh();
        try
        {
            Saved = await Task.Run(() => ModuleCaptureStore.SaveBatch(indexPath, requests, job.Token), job.Token);
            ContinueToGeneration = generate && !closeRequested; return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or OverflowException or UnauthorizedAccessException)
        { errorMessage = error.Message; return false; }
        finally { pending = null; saving = false; if (!closed) Refresh(); if (closeRequested) Close(); }
    }
}
