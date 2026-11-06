using System.Globalization;
using System.IO;

namespace CrystalStudio;

internal sealed class ModuleGenerationWindow : EditorWindow
{
    private readonly MapDocument source;
    private readonly StampCategoryIndex catalogue;
    private readonly AssetStore? assets;
    private readonly List<(CategorizedStamp Entry, CheckBox Check)> choices = [];
    private readonly MapViewport preview = new() { IsHitTestVisible = false, Focusable = false, Tool = EditorTool.Select, CollisionVisible = true };
    private readonly ContentControl diagram = new();
    private readonly TabControl previews = new();
    private CancellationTokenSource? pending;
    private int revision;
    private bool closed;
    internal TextBox CountBox { get; } = new() { Text = "12" };
    internal TextBox SeedBox { get; } = new() { Text = Random.Shared.Next(int.MaxValue).ToString(CultureInfo.InvariantCulture) };
    internal TextBox XBox { get; } = new();
    internal TextBox YBox { get; } = new();
    internal TextBox WidthBox { get; } = new();
    internal TextBox HeightBox { get; } = new();
    internal ComboBox LayoutBox { get; } = new() { ItemsSource = Enum.GetValues<ModuleLayout>(), SelectedItem = ModuleLayout.Compact };
    internal Button ApplyButton { get; } = new() { Content = LocalText.T("Apply to map"), IsEnabled = false };
    internal TextBlock Feedback { get; } = new() { TextWrapping = TextWrapping.Wrap };
    internal ModuleGenerationPlan? Plan { get; private set; }

    internal ModuleGenerationWindow(MapDocument source, StampCategoryIndex catalogue, AssetStore? assets, ModuleArea? selectedArea = null)
    {
        this.source = source; this.catalogue = catalogue; this.assets = assets;
        Title = LocalText.T("Generate modules automatically"); Width = 1180; Height = 850; MinWidth = 900; MinHeight = 680;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var area = selectedArea ?? new ModuleArea(Math.Max(0, (source.Width - 128) / 2), Math.Max(0, (source.Height - 128) / 2),
            Math.Min(128, source.Width), Math.Min(128, source.Height));
        XBox.Text = area.X.ToString(CultureInfo.InvariantCulture); YBox.Text = area.Y.ToString(CultureInfo.InvariantCulture);
        WidthBox.Text = area.Width.ToString(CultureInfo.InvariantCulture); HeightBox.Text = area.Height.ToString(CultureInfo.InvariantCulture);
        var root = new Grid { ColumnDefinitions = new ColumnDefinitions("355,*"), Margin = new Thickness(14) };
        var form = new StackPanel { Spacing = 7, Margin = new Thickness(0, 0, 14, 0) };
        form.Children.Add(new TextBlock { Text = LocalText.T("Module pool (instances may repeat)") });
        var pool = new StackPanel { Spacing = 2 };
        foreach (var entry in catalogue.Objects.Where(s => s.Connections is { Points.Count: > 0 }).OrderBy(s => s.Id, StringComparer.Ordinal))
        {
            var check = new CheckBox { Content = entry.Name + " · " + entry.File, IsChecked = true };
            check.IsCheckedChanged += (_, _) => InvalidatePreview();
            choices.Add((entry, check)); pool.Children.Add(check);
        }
        form.Children.Add(new ScrollViewer { Content = pool, Height = 155, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto });
        var poolActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var all = new Button { Content = LocalText.T("Select all modules") };
        var none = new Button { Content = LocalText.T("Clear module selection") };
        all.Click += (_, _) => { foreach (var choice in choices) choice.Check.IsChecked = true; };
        none.Click += (_, _) => { foreach (var choice in choices) choice.Check.IsChecked = false; };
        poolActions.Children.Add(all); poolActions.Children.Add(none); form.Children.Add(poolActions);
        void Pair(string firstName, Control first, string secondName, Control second)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*") };
            var a = new StackPanel { Spacing = 3, Margin = new Thickness(0, 0, 8, 0) };
            a.Children.Add(new TextBlock { Text = LocalText.T(firstName) }); a.Children.Add(first); row.Children.Add(a);
            var b = new StackPanel { Spacing = 3 };
            b.Children.Add(new TextBlock { Text = LocalText.T(secondName) }); b.Children.Add(second); Grid.SetColumn(b, 1); row.Children.Add(b);
            form.Children.Add(row);
        }
        Pair("Module count", CountBox, "Generation seed", SeedBox);
        Pair("Area left X", XBox, "Area top Y", YBox);
        Pair("Area width", WidthBox, "Area height", HeightBox);
        form.Children.Add(new TextBlock { Text = LocalText.T("Layout style") }); form.Children.Add(LayoutBox);
        form.Children.Add(new TextBlock
        {
            Text = LocalText.T("The generator chooses modules and connections automatically. Chain extends the latest module; Branching spreads outward from the start; Compact prefers nearby connections. Only empty footprints are used."),
            TextWrapping = TextWrapping.Wrap
        });
        var generation = new WrapPanel();
        var generate = new Button { Content = LocalText.T("Generate connected layout"), Name = "GenerateModuleLayoutButton", Margin = new Thickness(0, 0, 8, 5) };
        generate.Click += async (_, _) => await GeneratePreviewAsync(); generation.Children.Add(generate);
        var shuffle = new Button { Content = LocalText.T("New seed"), Margin = new Thickness(0, 0, 8, 5) };
        shuffle.Click += (_, _) => SeedBox.Text = Random.Shared.Next(int.MaxValue).ToString(CultureInfo.InvariantCulture); generation.Children.Add(shuffle);
        var stop = new Button { Content = LocalText.T("Stop generation"), Margin = new Thickness(0, 0, 0, 5) };
        stop.Click += (_, _) => pending?.Cancel(); generation.Children.Add(stop); form.Children.Add(generation);
        form.Children.Add(Feedback);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        ApplyButton.Click += (_, _) => { if (TryAccept()) DialogResult = true; };
        actions.Children.Add(ApplyButton); actions.Children.Add(new Button { Content = LocalText.T("Cancel"), IsCancel = true }); form.Children.Add(actions);
        root.Children.Add(new ScrollViewer { Content = form });
        preview.Assets = assets;
        previews.Items.Add(new TabItem { Header = LocalText.T("Map preview"), Content = preview });
        previews.Items.Add(new TabItem { Header = LocalText.T("Connection layout"), Content = diagram });
        previews.SelectedIndex = assets == null ? 1 : 0;
        Grid.SetColumn(previews, 1); root.Children.Add(previews); Content = root;
        foreach (var box in new[] { CountBox, SeedBox, XBox, YBox, WidthBox, HeightBox })
            box.PropertyChanged += (_, change) => { if (change.Property == TextBox.TextProperty) InvalidatePreview(); };
        LayoutBox.SelectionChanged += (_, _) => InvalidatePreview();
        preview.SizeChanged += (_, _) => FocusPreview();
        if (assets != null) assets.Updated += AssetsUpdated;
        Closed += (_, _) =>
        {
            closed = true; pending?.Cancel(); preview.StopBackgroundWork();
            if (assets != null) assets.Updated -= AssetsUpdated;
        };
        InvalidatePreview();
        if (choices.Count == 0) Feedback.Text = LocalText.T("No modules yet. Select a map region and use Tools → Create module from selection.");
    }

    internal void SelectModules(IEnumerable<string> ids)
    {
        var selected = ids.ToHashSet(StringComparer.Ordinal);
        foreach (var choice in choices) choice.Check.IsChecked = selected.Contains(choice.Entry.Id);
    }
    private void AssetsUpdated() => Dispatcher.UIThread.Post(() => { if (!closed) preview.RefreshAssets(); });
    private void InvalidatePreview()
    {
        revision++; pending?.Cancel(); Plan = null; ApplyButton.IsEnabled = false; previews.IsVisible = false;
        Feedback.Text = LocalText.T("Generate a preview before applying.");
    }
    internal async Task GeneratePreviewAsync()
    {
        InvalidatePreview();
        var selected = choices.Where(c => c.Check.IsChecked == true).Select(c => c.Entry).ToArray();
        if (selected.Length is < 1 or > 256) { Feedback.Text = LocalText.T("Select between 1 and 256 modules for automatic generation."); return; }
        if (!int.TryParse(CountBox.Text, out int count) || !int.TryParse(SeedBox.Text, out int seed) ||
            !int.TryParse(XBox.Text, out int x) || !int.TryParse(YBox.Text, out int y) ||
            !int.TryParse(WidthBox.Text, out int width) || !int.TryParse(HeightBox.Text, out int height) ||
            LayoutBox.SelectedItem is not ModuleLayout layout)
        { Feedback.Text = LocalText.T("Enter whole numbers for module count, seed and generation area."); return; }
        int request = revision;
        using var job = new CancellationTokenSource(); pending = job;
        Feedback.Text = LocalText.T("Selecting and connecting modules…");
        try
        {
            var options = new ModuleGenerationOptions(count, seed, new(x, y, width, height), layout);
            var result = await Task.Run(() =>
            {
                var modules = ModuleGeneration.LoadPool(catalogue, selected, job.Token);
                return ModuleGeneration.Generate(source, modules, options, job.Token);
            }, job.Token);
            if (closed || request != revision || job.IsCancellationRequested) return;
            Plan = result; preview.SetDocument(result.Placement.Generated);
            diagram.Content = new ModuleConnectionCanvas(result.Placement.Cells, result.Placement.Bounds) { Points = result.Placement.Openings };
            previews.IsVisible = true; ApplyButton.IsEnabled = true;
            Feedback.Text = LocalText.Format("Generated {0} modules and {1} connections. All {2} walkable cells are connected. Seed: {3}. Ctrl+Z undoes the whole layout.",
                result.Placement.Placements.Count, result.Links.Count, result.WalkableCells, seed);
            Dispatcher.UIThread.Post(FocusPreview);
        }
        catch (OperationCanceledException)
        { if (!closed && request == revision) Feedback.Text = LocalText.T("Generation cancelled. The map was not changed."); }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException or OverflowException or UnauthorizedAccessException)
        { if (!closed && request == revision) Feedback.Text = exception.Message; }
        finally { if (ReferenceEquals(pending, job)) pending = null; }
    }
    internal bool TryAccept()
    {
        if (Plan == null) return false;
        if (source.PickupRevision != Plan.Placement.SourceRevision)
        {
            InvalidatePreview(); Feedback.Text = LocalText.T("The map changed after the module preview. Generate a fresh preview."); return false;
        }
        return true;
    }
    private void FocusPreview()
    {
        if (closed || Plan == null || preview.Bounds.Width <= 0 || preview.Bounds.Height <= 0) return;
        var area = Plan.Placement.Bounds;
        preview.SetZoom(Math.Min((preview.Bounds.Width - 64) / ((area.Right - area.Left + 3) * 48.0),
            (preview.Bounds.Height - 64) / ((area.Bottom - area.Top + 3) * 32.0)));
        preview.CenterOnWorld(new Point((area.Left + area.Right + 1) * 24.0, (area.Top + area.Bottom + 1) * 16.0));
    }
}
