using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
namespace CrystalStudio;

public sealed record TreeBrushSource(string Name, string Path, string Type, string File, StampCategoryNode Preview,
    IReadOnlyList<StampCell>? Cells = null, string? PreviewPath = null);

public partial class SpecialBrushWindow : EditorWindow
{
    private void BrushTypeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ready || applying || !Enum.TryParse<SpecialBrushKind>((BrushTypeList.SelectedItem as ComboBoxItem)?.Tag as string, out var kind) || kind == Kind) return;
        SaveState(); RestoreState(kind);
    }
    private readonly AssetStore? assets;
    private readonly CancellationTokenSource cancellation = new();
    private List<TreeBrushSource> sources = [];
    private readonly Dictionary<SpecialBrushKind, List<TreeBrushSource>> sourcesByKind = [];
    private readonly Dictionary<SpecialBrushKind, SpecialBrushState> states = [];
    private readonly HashSet<string> selectedPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly ObservableCollection<TreeBrushSource> curated = [];
    private bool filtering, applying, ready, catalogueLoaded;
    private TreeBrushSource? previewSource;
    private IReadOnlyList<StampCell>? previewCells;
    private int previewVersion, previewQueued;
    private bool closed;
    public TreeBrushOptions Options { get; private set; }
    public SpecialBrushKind Kind { get; private set; }
    public IReadOnlyDictionary<SpecialBrushKind, SpecialBrushState> BrushStates => new Dictionary<SpecialBrushKind, SpecialBrushState>(states);
    private string AllTypes => "All " + Kind.Plural();
    public int BrushSize => (int)SizeSlider.Value;
    public IReadOnlyList<BrushTree> Trees { get; private set; } = [];
    public int UnavailableTrees { get; private set; }
    public IReadOnlyList<TreeBrushSource> CuratedTrees => curated.ToArray();
    public string TreeListName => ListNameBox.Text.Trim();

    public SpecialBrushWindow(AssetStore? assets, TreeBrushOptions options, int size,
        IReadOnlyList<BrushTree> currentTrees, IReadOnlyList<TreeBrushSource>? currentList = null, string listName = "My trees",
        SpecialBrushKind kind = SpecialBrushKind.Tree, IReadOnlyDictionary<SpecialBrushKind, SpecialBrushState>? brushStates = null)
    {
        this.assets = assets; Options = options.Normalize() with { Round = false, Collisions = true };
        InitializeComponent(); 
        CuratedList.ItemsSource = curated;
        foreach (var brushKind in Enum.GetValues<SpecialBrushKind>())
        {
            var state = brushStates?.GetValueOrDefault(brushKind) ?? new(new(TreeType: "All " + brushKind.Plural()), 9, [], [], "My " + brushKind.Plural());
            if (brushKind == kind) state = state with { Options = options, Size = size, Variants = currentTrees, List = currentList ?? [], ListName = listName };
            var list = state.List.Select(t => Source(t.Name, t.Path, t.Type, t.File, t.PreviewPath, t.Cells)).ToArray();
            var available = list.ToList();
            foreach (var tree in state.Variants)
                if (!available.Any(s => string.Equals(s.Path, tree.Path, StringComparison.OrdinalIgnoreCase)))
                    available.Add(Source(tree.Name, tree.Path, "Selected " + brushKind.Plural(), System.IO.Path.GetFileName(tree.Path), null, tree.Cells));
            states[brushKind] = state with { List = list };
            sourcesByKind[brushKind] = available;
        }
        BrushTypeList.SelectedIndex = (int)kind;
        RestoreState(kind);
        Loaded += async (_, _) => await LoadSourcesAsync();
        Closing += (_, e) => { if (applying) e.Cancel = true; };
        if (assets != null) assets.Updated += AssetsUpdated;
        Closed += (_, _) =>
        {
            closed = true; previewVersion++;
            if (assets != null) assets.Updated -= AssetsUpdated;
            if (previewSource != null) previewSource.Preview.PropertyChanged -= PreviewChanged;
            cancellation.Cancel(); cancellation.Dispose();
        };
    }

    private TreeBrushOptions ReadOptions() => new((int)DensitySlider.Value, (int)SpacingSlider.Value, false, true, Options.Seed,
        PaintSourceList.SelectedIndex == 0, catalogueLoaded ? TypeList.SelectedItem as string ?? AllTypes : Options.TreeType, PaintSourceList.SelectedIndex == 1);

    private void SaveState() => states[Kind] = states[Kind] with
    {
        Options = ReadOptions(), Size = BrushSize, List = CuratedTrees, ListName = TreeListName,
        Search = SearchBox.Text, SelectedPaths = selectedPaths.ToArray()
    };

    private void RestoreState(SpecialBrushKind kind)
    {
        ready = false; filtering = true; Kind = kind;
        var state = states[kind]; sources = sourcesByKind[kind];
        Options = state.Options.Normalize() with { Round = false, Collisions = true };
        selectedPaths.Clear();
        foreach (string path in state.SelectedPaths ?? state.Variants.Select(t => t.Path).ToArray()) selectedPaths.Add(path);
        curated.Clear(); foreach (var source in state.List) curated.Add(source);
        TreeList.ItemsSource = null; TypeList.ItemsSource = null; TypeList.ItemTemplate = LocalText.ChoiceTemplate;
        SizeSlider.Value = Math.Clamp(state.Size | 1, 3, 31); DensitySlider.Value = Options.Density; SpacingSlider.Value = Options.Spacing;
        PaintSourceList.SelectedIndex = Options.UseCuratedList ? 1 : Options.RandomFromType ? 0 : 2;
        ListNameBox.Text = state.ListName; SearchBox.Text = state.Search;
        string plural = System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(kind.Plural());
        TypeLabel.Text = string.Concat(LocalText.T(kind.ToString()), LocalText.T(" Type")); ToolTip.SetTip(TypeList, LocalText.Format("{0} Type", LocalText.T(kind.ToString())));
        BrowseLabel.Text = string.Concat(LocalText.T("Browse "), LocalText.T(plural)); CuratedLabel.Text = string.Concat(string.Concat(LocalText.T("My "), LocalText.T(kind.ToString())), LocalText.T(" List"));
        (SearchBox).Tag = LocalText.Format("Search {0}…", LocalText.T(plural));
        (((ComboBoxItem)PaintSourceList.Items[0])).Content = string.Concat(string.Concat(LocalText.T("Random "), LocalText.T(plural)), LocalText.T(" from This Type"));
        (((ComboBoxItem)PaintSourceList.Items[1])).Content = LocalText.Format("My {0} List", LocalText.T(kind.ToString()));
        (((ComboBoxItem)PaintSourceList.Items[2])).Content = string.Concat(LocalText.T("Selected "), LocalText.T(plural));
        Avalonia.Automation.AutomationProperties.SetName(TreeList, kind + " Variants");
        Avalonia.Automation.AutomationProperties.SetName(CuratedList, "My Curated " + plural);
        Avalonia.Automation.AutomationProperties.SetName(TreePreviewImage, "Selected " + kind + " Preview");
        Avalonia.Automation.AutomationProperties.SetName(SearchBox, "Search " + plural);
        Avalonia.Automation.AutomationProperties.SetName(ListNameBox, kind + " List Name");
        Avalonia.Automation.AutomationProperties.SetName(SizeSlider, kind + " Brush Size");
        Avalonia.Automation.AutomationProperties.SetName(DensitySlider, kind + " Density");
        Avalonia.Automation.AutomationProperties.SetName(SpacingSlider, kind + " Spacing");
        ToolTip.SetTip(TreeList, string.Concat(string.Concat(LocalText.T("Double-Click to Add a "), LocalText.T(kind.ToString())), LocalText.T(". Ctrl-Click to Select Several.")));
        ToolTip.SetTip(AddTypeButton, string.Concat(string.Concat(LocalText.T("Add All "), LocalText.T(plural)), LocalText.T(" of This Type")));
        ToolTip.SetTip(RemoveTreesButton, string.Concat(LocalText.T("Remove Selected "), LocalText.T(plural))); ToolTip.SetTip(ClearTreesButton, string.Concat(string.Concat(LocalText.T("Clear My "), LocalText.T(kind.ToString())), LocalText.T(" List")));
        ListStatusLabel.IsVisible = ErrorLabel.IsVisible = false;
        ApplyButton.Content = string.Concat(string.Concat(LocalText.T("Use "), LocalText.T(kind.ToString())), LocalText.T(" Brush"));
        filtering = false; ready = true; ShowTreePreview(null); RefreshTypes(); UpdateOptionLabels();
    }

    private TreeBrushSource Source(string name, string path, string type, string file, string? previewPath, IReadOnlyList<StampCell>? cells = null) => new(name, path, type, file,
        new StampCategoryNode { Label = name, PreviewCancellation = cancellation.Token,
            PreviewLoader = previewPath == null ? null : () => ReadPreview(previewPath) }, cells, previewPath);

    private static RasterImage? ReadPreview(string path)
    {
        try
        {
            using var stream = System.IO.File.OpenRead(path);
            return RasterImage.Load(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidDataException) { return null; }
    }

    private async Task LoadSourcesAsync()
    {
        try
        {
            var index = await Task.Run(() =>
            {
                string path = StampCategories.FindIndex() ?? throw new FileNotFoundException("No object catalogue found. Load a saved brush list to get started.");
                return StampCategories.Read(path);
            });
            if (cancellation.IsCancellationRequested) return;
            foreach (var brushKind in Enum.GetValues<SpecialBrushKind>())
            foreach (var stamp in index.Objects.Where(s => brushKind.Matches(s)))
            {
                string path = StampCategories.Resolve(index.ObjectRoot, stamp.File);
                var available = sourcesByKind[brushKind];
                available.RemoveAll(s => string.Equals(s.Path, path, StringComparison.OrdinalIgnoreCase));
                available.Add(Source(stamp.Name, path, stamp.Subtype ?? "Other " + brushKind.Plural(), stamp.File,
                    stamp.Preview == null ? null : StampCategories.Resolve(index.PreviewRoot, stamp.Preview)));
            }
        }
        catch (Exception ex) { if (!cancellation.IsCancellationRequested) ShowError(ex.Message); }
        if (cancellation.IsCancellationRequested) return;
        catalogueLoaded = true;
        filtering = true; TypeList.SelectedItem = null; filtering = false;
        RefreshTypes();
        if (assets == null) ShowError("Load Crystal libraries in Settings before using special brushes.");
    }

    private void RefreshTypes()
    {
        filtering = true;
        string type = TypeList.SelectedItem as string ?? Options.TreeType;
        TypeList.ItemsSource = new[] { AllTypes }.Concat(sources.Select(s => s.Type).Distinct().Order(StringComparer.OrdinalIgnoreCase)).ToArray();
        TypeList.SelectedItem = TypeList.Items.Contains(type) ? type : AllTypes;
        filtering = false; FilterTrees();
    }

    private void FilterTrees()
    {
        if (!ready || filtering) return;
        filtering = true;
        string type = TypeList.SelectedItem as string ?? AllTypes, query = SearchBox.Text.Trim();
        var filtered = sources.Where(s => (type == AllTypes || s.Type == type) &&
            $"{s.Name} {s.Type} {s.File}".Contains(query, StringComparison.OrdinalIgnoreCase)).OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
        TreeList.SelectedItems.Clear();
        TreeList.ItemsSource = filtered;
        foreach (var source in filtered.Where(s => selectedPaths.Contains(s.Path))) TreeList.SelectedItems.Add(source);
        if (selectedPaths.Count == 0 && filtered.Count > 0) { TreeList.SelectedItem = filtered[0]; selectedPaths.Add(filtered[0].Path); }
        EmptyLabel.Text = (sources.Count == 0 ? LocalText.Format("No {0} available. Load a {1} list.",LocalText.T(Kind.Plural()),LocalText.T(Kind.Singular())) : LocalText.Format("No {0} match this filter.",LocalText.T(Kind.Plural())));
        EmptyLabel.IsVisible = filtered.Count == 0 ? true : false;
        filtering = false; UpdateSelection(); ShowTreePreview(TreeList.SelectedItem as TreeBrushSource);
    }

    private void FilterChanged(object sender, RoutedEventArgs e) => FilterTrees();
    private void TypeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ready || filtering) return;
        Options = Options with { TreeType = TypeList.SelectedItem as string ?? AllTypes };
        selectedPaths.Clear(); FilterTrees();
    }
    private void TreesChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ready || filtering) return;
        foreach (TreeBrushSource source in e.RemovedItems) selectedPaths.Remove(source.Path);
        foreach (TreeBrushSource source in e.AddedItems) selectedPaths.Add(source.Path);
        UpdateSelection(); ShowTreePreview(e.AddedItems.Cast<TreeBrushSource>().LastOrDefault() ?? TreeList.SelectedItem as TreeBrushSource);
    }

    private void UpdateSelection()
    {
        int count = SourcesForBrush().Count();
        SelectionLabel.Text = (PaintSourceList.SelectedIndex == 1 ? LocalText.Format("{0} {1} in {2}",count,LocalText.T(Kind.Plural()),TreeListName) : LocalText.Format("{0} {1} to paint",count,LocalText.T(Kind.Plural())));
        ApplyButton.IsEnabled = !applying && assets != null && count > 0;
        AddTreesButton.IsEnabled = TreeList.SelectedItems.Count > 0;
        AddTypeButton.IsEnabled = SourcesForType().Any();
        RemoveTreesButton.IsEnabled = CuratedList.SelectedItems.Count > 0;
        ClearTreesButton.IsEnabled = curated.Count > 0;
        SaveTreeListButton.IsEnabled = curated.Count > 0 && TreeListName.Length > 0;
    }

    private IEnumerable<TreeBrushSource> SourcesForType() => sources.Where(s => TypeList.SelectedItem is not string type || type == AllTypes || s.Type == type);
    private IEnumerable<TreeBrushSource> SourcesForBrush() => PaintSourceList.SelectedIndex switch
    {
        1 => curated,
        2 => sources.Where(s => selectedPaths.Contains(s.Path)),
        _ => SourcesForType()
    };

    private void PaintSourceChanged(object sender, SelectionChangedEventArgs e) { if (ready) UpdateSelection(); }
    private void ListNameChanged(object sender, TextChangedEventArgs e) { if (ready) UpdateSelection(); }
    private void CuratedSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ready) return;
        UpdateSelection(); ShowTreePreview(e.AddedItems.Cast<TreeBrushSource>().LastOrDefault() ?? CuratedList.SelectedItem as TreeBrushSource ?? TreeList.SelectedItem as TreeBrushSource);
    }

    private void AddToList(IEnumerable<TreeBrushSource> trees)
    {
        foreach (var tree in trees)
            if (!curated.Any(s => string.Equals(s.Path, tree.Path, StringComparison.OrdinalIgnoreCase))) curated.Add(tree);
        PaintSourceList.SelectedIndex = 1; UpdateSelection();
    }
    private void AddTreesClick(object sender, RoutedEventArgs e) => AddToList(TreeList.SelectedItems.Cast<TreeBrushSource>());
    private void TreeDoubleClick(object sender, TappedEventArgs e)
    {
        if ((e.Source as Visual)?.FindAncestorOfType<ListBoxItem>() is ListBoxItem)
            AddTreesClick(sender, e);
    }
    private void AddTypeClick(object sender, RoutedEventArgs e) => AddToList(SourcesForType());
    private void RemoveTreesClick(object sender, RoutedEventArgs e)
    {
        foreach (var tree in CuratedList.SelectedItems.Cast<TreeBrushSource>().ToArray()) curated.Remove(tree);
        UpdateSelection();
    }
    private void ClearTreesClick(object sender, RoutedEventArgs e) { curated.Clear(); UpdateSelection(); }

    private async void ShowTreePreview(TreeBrushSource? source)
    {
        if (closed || source != null && ReferenceEquals(source, previewSource)) return;
        if (previewSource != null) previewSource.Preview.PropertyChanged -= PreviewChanged;
        previewSource = source; previewCells = null;
        int version = ++previewVersion;
        SelectedTreeName.Text = (source?.Name ?? string.Concat(LocalText.T("Select a "), LocalText.T(Kind.ToString())));
        SelectedTreeType.Text = LocalText.T(source?.Type ?? "");
        TreePreviewImage.Source = null;
        PreviewPlaceholder.Text = (source == null ? string.Concat(LocalText.T("Select a "), LocalText.T(Kind.ToString())) : LocalText.T("Loading Preview\u2026"));
        PreviewPlaceholder.IsVisible = true;
        if (source == null) return;
        source.Preview.PropertyChanged += PreviewChanged;
        UpdateTreePreview();
        try
        {
            var cells = source.Cells ?? await Task.Run(() => LoadTree(source).Cells);
            if (closed || version != previewVersion) return;
            previewCells = cells;
            UpdateTreePreview();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or OverflowException)
        {
            if (closed || version != previewVersion) return;
            UpdateTreePreview();
            PreviewPlaceholder.Text = LocalText.T("Preview Unavailable");
        }
    }

    private void PreviewChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(StampCategoryNode.Bitmap)) QueuePreviewUpdate();
    }

    private void AssetsUpdated() => QueuePreviewUpdate();

    private void QueuePreviewUpdate()
    {
        if (closed || Interlocked.Exchange(ref previewQueued, 1) != 0) return;
        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref previewQueued, 0);
            if (!closed) UpdateTreePreview();
        });
    }

    private void UpdateTreePreview()
    {
        if (previewSource == null || closed) return;
        RasterImage? bitmap = previewSource.Preview.Bitmap;
        var fronts = previewCells?.SelectMany(c => c.Cell.ForegroundImages).ToArray();
        bool canRender = assets != null && fronts is { Length: > 0 } && fronts.All(f =>
            assets.Libraries.TryGetValue(f.Library, out var library) && (f.Image & 0x7FFF) > 0 && (f.Image & 0x7FFF) <= library.Count);
        if (canRender && previewCells != null)
        {
            var rendered = StampPreview.Create(previewCells, assets!, 512, 512);
            if (rendered != null)
            {
                bitmap = rendered;
                previewSource.Preview.SetBitmap(rendered);
                previewCells = null;
            }
        }
        TreePreviewImage.Source = bitmap;
        PreviewPlaceholder.IsVisible = bitmap == null ? true : false;
        if (bitmap == null && previewCells != null && !canRender) PreviewPlaceholder.Text = LocalText.T("Preview Unavailable");
    }

    private static BrushTree LoadTree(TreeBrushSource source, AssetStore? store = null) =>
        BrushTree.FromCells(source.Name, source.Path, source.Cells ?? CellCodec.LoadStamp(source.Path, store?.Format ?? AssetSourceFormat.Default), store);

    private void SetWorking(bool working, string? label = null)
    {
        applying = working; TreeSettings.IsEnabled = BrushTypeList.IsEnabled = !working;
        ApplyButton.Content = (label ?? string.Concat(string.Concat(LocalText.T("Use "), LocalText.T(Kind.ToString())), LocalText.T(" Brush"))); UpdateSelection();
    }

    private async void SaveTreeListClick(object sender, RoutedEventArgs e)
    {
        if (applying || curated.Count == 0 || TreeListName.Length == 0) return;
        string extension = Kind.ListExtension();
        var picker = new SaveFileDialog { Title = $"Save {Kind} List", Filter = $"{Kind} List (*{extension})|*{extension}", DefaultExt = extension, FileName = Kind.Plural() + extension };
        if (picker.ShowDialog(this) != true) return;
        var trees = curated.ToArray(); string name = TreeListName;
        SetWorking(true, "Saving list\u2026"); ErrorLabel.IsVisible = false;
        try
        {
            int count = await Task.Run(() =>
            {
                var entries = trees.Select(s => TreeBrushList.Entry(s.Name, s.Type, LoadTree(s).Cells)).DistinctBy(s => s.Id).ToArray();
                SpecialBrushList.Save(picker.FileName, Kind, name, entries); return entries.Length;
            });
            ListStatusLabel.Text = LocalText.Format("Saved {0} · {1} {2}",name,count,LocalText.T(Kind.Plural()));
            ListStatusLabel.IsVisible = true;
        }
        catch (Exception ex) { ShowError(ex.Message); }
        finally { SetWorking(false); }
    }

    private void ReplaceTreeList(TreeListDocument document)
    {
        var entries = document.Trees.Select(t => Source(t.Name, Kind.Singular() + "-list:" + t.Id, t.Type, $"Shared {Kind.Singular()} layout", null, t.Cells)).ToArray();
        // Construct everything before replacing the current list so failed loads preserve it.
        curated.Clear();
        foreach (var source in entries)
        {
            curated.Add(source);
            if (!sources.Any(s => string.Equals(s.Path, source.Path, StringComparison.OrdinalIgnoreCase))) sources.Add(source);
        }
        ListNameBox.Text = document.Name; PaintSourceList.SelectedIndex = 1; RefreshTypes();
        ListStatusLabel.Text = LocalText.Format("Loaded {0} · {1} {2}",document.Name,entries.Length,LocalText.T(Kind.Plural()));
        ListStatusLabel.IsVisible = true;
    }

    private async void LoadTreeListClick(object sender, RoutedEventArgs e)
    {
        if (applying) return;
        string extension = Kind.ListExtension();
        var picker = new OpenFileDialog { Title = $"Load {Kind} List", Filter = $"{Kind} List (*{extension})|*{extension}" };
        if (picker.ShowDialog(this) != true) return;
        SetWorking(true, "Loading list\u2026"); ErrorLabel.IsVisible = false;
        try { ReplaceTreeList(await Task.Run(() => SpecialBrushList.Read(picker.FileName, Kind))); }
        catch (Exception ex) { ShowError(ex.Message); }
        finally { SetWorking(false); }
    }

    private void OptionsChanged(object sender, RangeBaseValueChangedEventArgs e) { if (ready) UpdateOptionLabels(); }
    private void UpdateOptionLabels()
    {
        SizeLabel.Text = LocalText.Format("{0} × {1} cells",BrushSize,BrushSize); DensityLabel.Text = ($"{DensitySlider.Value:0}%");
        SpacingLabel.Text = LocalText.Format("{0:0} cells",SpacingSlider.Value);
    }

    private void ShowError(string message) { ErrorLabel.Text = LocalText.T(message); ErrorLabel.IsVisible = true; }
    private async void ApplyClick(object sender, RoutedEventArgs e)
    {
        var selected = SourcesForBrush().ToArray();
        if (applying || assets == null || selected.Length == 0) return;
        bool random = PaintSourceList.SelectedIndex != 2;
        SetWorking(true, $"Loading {Kind.Plural()}\u2026"); ErrorLabel.IsVisible = false;
        try
        {
            var result = await Task.Run(() =>
            {
                var trees = new List<BrushTree>();
                var errors = new List<string>();
                foreach (var source in selected)
                {
                    try { trees.Add(LoadTree(source, assets)); }
                    catch (Exception ex) when (random && ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or OverflowException)
                    { errors.Add(ex.Message); }
                }
                if (trees.Count == 0) throw new InvalidDataException($"No usable {Kind.Plural()} in this type. " + errors.FirstOrDefault());
                return (Trees: trees, Unavailable: errors.Count);
            });
            Trees = result.Trees; UnavailableTrees = result.Unavailable;
            for (int i = 0; i < curated.Count; i++)
                if (Trees.FirstOrDefault(t => string.Equals(t.Path, curated[i].Path, StringComparison.OrdinalIgnoreCase)) is { } tree)
                    curated[i] = curated[i] with { Cells = tree.Cells };
            Options = ReadOptions(); SaveState(); states[Kind] = states[Kind] with { Variants = Trees };
            applying = false; DialogResult = true;
        }
        catch (Exception ex)
        {
            ShowError(ex.Message); SetWorking(false);
        }
    }
}
