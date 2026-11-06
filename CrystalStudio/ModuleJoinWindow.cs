using System.IO;
using System.Globalization;

namespace CrystalStudio;

internal sealed class ModuleJoinWindow : EditorWindow
{
    private readonly MapDocument source;
    private readonly StampCategoryIndex catalogue;
    private readonly AssetStore? assets;
    private readonly MapViewport preview = new() { IsHitTestVisible = false, Tool = EditorTool.Select };
    private readonly ContentControl layoutPreview = new();
    private readonly TabControl previews = new();
    private CancellationTokenSource? pending;
    private bool closed;
    private int revision;
    internal ComboBox FirstModule { get; } = new();
    internal ComboBox SecondModule { get; } = new();
    internal ComboBox FirstConnection { get; } = new();
    internal ComboBox SecondConnection { get; } = new();
    internal TextBox XBox { get; } = new();
    internal TextBox YBox { get; } = new();
    internal TextBlock Feedback { get; } = new() { TextWrapping = TextWrapping.Wrap };
    internal Button ApplyButton { get; } = new() { Content = LocalText.T("Apply to map"), IsEnabled = false };
    internal ModuleJoinPlan? Plan { get; private set; }

    internal ModuleJoinWindow(MapDocument source, StampCategoryIndex catalogue, AssetStore? assets)
    {
        this.source = source; this.catalogue = catalogue; this.assets = assets;
        Title = LocalText.T("Join modules"); Width = 1100; Height = 760; MinWidth = 850; MinHeight = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new Grid { ColumnDefinitions = new ColumnDefinitions("340,*"), Margin = new Thickness(14) };
        var form = new StackPanel { Spacing = 8, Margin = new Thickness(0, 0, 14, 0) };
        void Field(string title, Control control)
        { form.Children.Add(new TextBlock { Text = LocalText.T(title) }); form.Children.Add(control); }
        var choices = catalogue.Objects.Where(s => s.Connections is { Points.Count: > 0 }).OrderBy(s => s.Name).ToArray();
        foreach (var combo in new[] { FirstModule, SecondModule })
        {
            combo.ItemsSource = choices;
            combo.ItemTemplate = new FuncDataTemplate<CategorizedStamp>((item, _) => new TextBlock
                { Text = item == null ? "" : $"{item.Name} · {item.File}", TextTrimming = TextTrimming.CharacterEllipsis });
        }
        foreach (var combo in new[] { FirstConnection, SecondConnection })
            combo.ItemTemplate = new FuncDataTemplate<ModuleConnection>((item, _) => new TextBlock
                { Text = item == null ? "" : $"{item.Id} · {LocalText.T(item.Edge.ToString())} · {item.Kind} ×{item.Width}" });
        Field("First module", FirstModule); Field("First connection", FirstConnection);
        Field("Second module", SecondModule); Field("Matching connection", SecondConnection);
        Field("First module origin X", XBox); Field("First module origin Y", YBox);
        XBox.Text = (source.Width / 2 & ~1).ToString(CultureInfo.InvariantCulture);
        YBox.Text = (source.Height / 2 & ~1).ToString(CultureInfo.InvariantCulture);
        form.Children.Add(new TextBlock { Text = LocalText.T("Choose two saved modules. Opposite connections must have the same type and width. Both footprints must be empty. Artwork keeps its original orientation."), TextWrapping = TextWrapping.Wrap });
        var generate = new Button { Content = LocalText.T("Preview join"), Name = "PreviewModuleJoinButton" };
        generate.Click += async (_, _) => await GeneratePreviewAsync(); form.Children.Add(generate);
        form.Children.Add(Feedback);
        var actions = new WrapPanel();
        ApplyButton.Click += (_, _) => { if (TryAccept()) DialogResult = true; };
        actions.Children.Add(ApplyButton);
        actions.Children.Add(new Button { Content = LocalText.T("Cancel"), IsCancel = true, Margin = new Thickness(8, 0, 0, 0) });
        form.Children.Add(actions);
        panel.Children.Add(new ScrollViewer { Content = form });
        preview.Assets = assets; preview.CollisionVisible = true;
        previews.Items.Add(new TabItem { Header = LocalText.T("Map preview"), Content = preview });
        previews.Items.Add(new TabItem { Header = LocalText.T("Connection layout"), Content = layoutPreview });
        previews.SelectedIndex = assets == null ? 1 : 0;
        Grid.SetColumn(previews, 1); panel.Children.Add(previews); Content = panel;
        FirstModule.SelectionChanged += (_, _) => { FillConnections(FirstConnection, FirstModule); RefreshMatches(); };
        SecondModule.SelectionChanged += (_, _) => RefreshMatches();
        FirstConnection.SelectionChanged += (_, _) => RefreshMatches();
        SecondConnection.SelectionChanged += (_, _) => InvalidatePreview();
        foreach (var box in new[] { XBox, YBox })
            box.PropertyChanged += (_, change) => { if (change.Property == TextBox.TextProperty) InvalidatePreview(); };
        preview.SizeChanged += (_, _) => FocusPreview();
        if (assets != null) assets.Updated += AssetsUpdated;
        Closed += (_, _) =>
        {
            closed = true; pending?.Cancel(); preview.StopBackgroundWork();
            if (assets != null) assets.Updated -= AssetsUpdated;
        };
        FirstModule.SelectedIndex = choices.Length > 0 ? 0 : -1;
        SecondModule.SelectedIndex = choices.Length > 1 ? 1 : choices.Length > 0 ? 0 : -1;
        InvalidatePreview();
        if (choices.Length == 0) Feedback.Text = LocalText.T("Save module connections before joining modules.");
    }

    private void AssetsUpdated() => Dispatcher.UIThread.Post(() => { if (!closed) preview.RefreshAssets(); });

    private static void FillConnections(ComboBox target, ComboBox module)
    {
        target.ItemsSource = module.SelectedItem is CategorizedStamp { Connections: { Version: 1, Points: { Count: <= 64 } points } }
            ? points.Where(p => p != null).ToArray() : [];
        target.SelectedIndex = target.Items.Count > 0 ? 0 : -1;
    }

    private void RefreshMatches()
    {
        var prior = SecondConnection.SelectedItem as ModuleConnection;
        FillConnections(SecondConnection, SecondModule);
        var candidates = SecondConnection.Items.Cast<ModuleConnection>()
            .Where(p => FirstConnection.SelectedItem is ModuleConnection first && ModuleJoining.Matches(first, p)).ToArray();
        SecondConnection.ItemsSource = candidates;
        SecondConnection.SelectedItem = candidates.Contains(prior) ? prior : candidates.FirstOrDefault();
        InvalidatePreview();
        if (candidates.Length == 0) Feedback.Text = LocalText.T("No matching connection. Choose another module or edit its connections.");
    }

    private void InvalidatePreview()
    {
        revision++; pending?.Cancel(); Plan = null; ApplyButton.IsEnabled = false; previews.IsVisible = false;
        Feedback.Text = LocalText.T("Generate a preview before applying.");
    }

    internal async Task GeneratePreviewAsync()
    {
        InvalidatePreview();
        if (FirstModule.SelectedItem is not CategorizedStamp first || SecondModule.SelectedItem is not CategorizedStamp second ||
            FirstConnection.SelectedItem is not ModuleConnection a || SecondConnection.SelectedItem is not ModuleConnection b)
        { Feedback.Text = LocalText.T("Choose opposite connections with the same type and width."); return; }
        if (!int.TryParse(XBox.Text, out int x) || !int.TryParse(YBox.Text, out int y))
        { Feedback.Text = LocalText.T("Enter integer module coordinates."); return; }
        int request = revision;
        using var job = new CancellationTokenSource(); pending = job;
        Feedback.Text = LocalText.T("Generating module preview…");
        try
        {
            var result = await Task.Run(() => ModuleJoining.Preview(source, ModuleJoining.Load(catalogue, first, job.Token), a.Id,
                ModuleJoining.Load(catalogue, second, job.Token), b.Id, x, y, job.Token), job.Token);
            if (closed || request != revision || job.IsCancellationRequested) return;
            Plan = result;
            preview.SetDocument(result.Generated);
            layoutPreview.Content = new ModuleConnectionCanvas(result.Cells, result.Bounds) { Points = result.Openings };
            previews.IsVisible = true; ApplyButton.IsEnabled = true;
            var placement = result.Placements[1];
            Feedback.Text = LocalText.Format("Passage checked. Second origin: ({0}, {1}). Apply places both modules; Ctrl+Z undoes the whole join.", placement.X, placement.Y);
            Dispatcher.UIThread.Post(FocusPreview);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException or OverflowException or UnauthorizedAccessException)
        {
            if (!closed && request == revision) Feedback.Text = exception.Message;
        }
        finally { if (ReferenceEquals(pending, job)) pending = null; }
    }

    internal bool TryAccept()
    {
        if (Plan == null) return false;
        if (source.PickupRevision != Plan.SourceRevision)
        {
            InvalidatePreview();
            Feedback.Text = LocalText.T("The map changed after the module preview. Generate a fresh preview."); return false;
        }
        return true;
    }

    private void FocusPreview()
    {
        if (closed || Plan == null || preview.Bounds.Width <= 0 || preview.Bounds.Height <= 0) return;
        var area = Plan.Bounds;
        preview.SetZoom(Math.Min((preview.Bounds.Width - 64) / ((area.Right - area.Left + 3) * 48.0),
            (preview.Bounds.Height - 64) / ((area.Bottom - area.Top + 3) * 32.0)));
        preview.CenterOnWorld(new Point((area.Left + area.Right + 1) * 24.0, (area.Top + area.Bottom + 1) * 16.0));
    }
}
