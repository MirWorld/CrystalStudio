using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
namespace CrystalStudio;

public sealed class StampCategoryNode : INotifyPropertyChanged
{
    public required string Label { get; init; }
    public string Detail { get; init; } = "";
    public string Description => Label
        + (Detail.Length == 0 ? "" : "\n" + string.Join(" \u00b7 ", Detail.Split(" \u00b7 ").Select(LocalText.T)))
        + (Stamp == null ? "" : "\n" + Stamp.File);
    public CategorizedStamp? Stamp { get; init; }
    public string ShortLabel
    {
        get
        {
            if (Stamp == null) return Label;
            string file = Path.GetFileNameWithoutExtension(Stamp.File);
            foreach (string separator in new[] { " - ", " \u2014 ", " \u2013 " })
            {
                string suffix = separator + file;
                if (Label.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    return Label[..^suffix.Length].Trim();
            }
            return Label;
        }
    }
    public bool LoadWhenVisible { get; init; }
    public Func<RasterImage?>? PreviewLoader { get; init; }
    public Func<CancellationToken, Task<RasterImage?>>? LivePreviewLoader { get; init; }
    public CancellationToken PreviewCancellation { get; init; }
    public event PropertyChangedEventHandler? PropertyChanged;
    private static readonly SemaphoreSlim previewReaders=new(2);
    private bool previewRequested;
    private RasterImage? bitmap;
    private CancellationTokenSource? visiblePreview;
    public void PausePreview()
    {
        visiblePreview?.Cancel(); visiblePreview = null;
        previewRequested = bitmap != null;
    }
    public void ReleasePreview()
    {
        PausePreview();
        previewRequested = false; bitmap = null;
    }
    internal void SetBitmap(RasterImage value)
    {
        previewRequested = true; bitmap = value; PropertyChanged?.Invoke(this, new(nameof(Bitmap)));
    }
    public RasterImage? Bitmap
    {
        get
        {
            if (!LoadWhenVisible) RequestPreview();
            return bitmap;
        }
    }
    internal void RequestPreview()
    {
        if(previewRequested || (PreviewLoader==null&&LivePreviewLoader==null) || PreviewCancellation.IsCancellationRequested)return;
        previewRequested=true;
        visiblePreview=CancellationTokenSource.CreateLinkedTokenSource(PreviewCancellation);
        _ = LoadPreviewAsync(Dispatcher.UIThread, visiblePreview);
    }
    private async Task LoadPreviewAsync(Dispatcher dispatcher, CancellationTokenSource request)
    {
        var token = request.Token;
        try
        {
            var preview=await Task.Run(async () =>
            {
                await previewReaders.WaitAsync(token);
                try { token.ThrowIfCancellationRequested();return LivePreviewLoader != null ? await LivePreviewLoader(token) : PreviewLoader!(); }
                finally { previewReaders.Release(); }
            },token).ConfigureAwait(false);
            if(token.IsCancellationRequested||EditorLifetime.IsExiting)return;
            await dispatcher.InvokeAsync(() =>
            {
                if(token.IsCancellationRequested||bitmap!=null)return;
                bitmap=preview;PropertyChanged?.Invoke(this,new(nameof(Bitmap)));
            },DispatcherPriority.Background);
        }
        catch(OperationCanceledException) when(token.IsCancellationRequested) { }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        { }
        finally
        {
            if(!EditorLifetime.IsExiting)
                await dispatcher.InvokeAsync(() => { if(ReferenceEquals(visiblePreview,request))visiblePreview=null; request.Dispose(); });
            else request.Dispose();
        }
    }
}

public sealed class StampCategoryGroup
{
    public required string Name { get; init; }
    public required List<CategorizedStamp> Members { get; init; }
    public string Count => Members.Count.ToString("N0");
    public string Description => LocalText.Format("{0}\n{1} stamps", LocalText.T(Name), Count);
    public ObservableCollection<StampCategoryNode> Items { get; } = [];
    internal int Page { get; set; }
    internal Vector ScrollOffset { get; set; }
    internal string? SelectedFile { get; set; }
    public override string ToString() => Name;
}

public partial class MainWindow
{
    private const int CategoryPageSize = 24;
    private ComboBox? categoryGrouping;
    private StampCategoryIndex? categoryIndex;
    private StampCategoryIndex? displayedCategoryIndex;
    private AssetStore? categoryPreviewAssets;
    private string? categoryQuery, categoryGroupingKey;
    private List<StampCategoryGroup> categoryGroups = [];
    private StampCategoryGroup? displayedCategory;
    private bool rebuildingCategories, restoringCategoryPosition, categoryWasVisible;
    private int matchingCategoryStamps;
    private readonly Dictionary<string, RasterImage> categoryPreviewCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> categoryPreviewOrder = new();
    private bool categoriesLoading, categoriesAttempted;
    private string? categoryError;
    private AssetChoice? selectedCategoryChoice;
    private StampCategoryNode? selectedCategoryNode;
    private CancellationTokenSource categoryPreviews=new();
    private bool CategoryPalette => paletteTabs?.SelectedIndex == 3;

    private void InitializeCategoryPalette()
    {
        categoryGrouping = new ComboBox { ItemsSource = new[] { "Type", "Region", "Environment", "Facing" },
            SelectedIndex = 0, IsVisible = false };
        ToolTip.SetTip(categoryGrouping, LocalText.T("Group stamps by category"));
        RegisterEditorName("CategoryGrouping", categoryGrouping);
        ((StackPanel)LibraryList.Parent).Children.Insert(2, categoryGrouping);
        categoryGrouping.SelectionChanged += (_, _) => { if (CategoryPalette) BuildPage(); };
        Closed += (_,_) => { categoryPreviews.Cancel(); categoryPreviews.Dispose(); ClearCategorySelection(); };
    }

    private async Task LoadCategoriesAsync()
    {
        if (categoriesLoading || closed) return;
        categoriesAttempted = true; categoriesLoading = true; categoryError = null; UpdateWorkspace();
        try
        {
            var result = await Task.Run(() =>
            {
                string path = StampCategories.FindIndex() ?? throw new FileNotFoundException("The stamp category catalogue was not found.");
                return StampCategories.Read(path);
            });
            await Dispatcher.UIThread.InvokeAsync(() => { if (!closed) categoryIndex = result; });
        }
        catch (Exception ex) { await Dispatcher.UIThread.InvokeAsync(() => { if (!closed) { categoryIndex = null; categoryError = ex.Message; } }); }
        finally { await Dispatcher.UIThread.InvokeAsync(() => { categoriesLoading = false; if (!closed && CategoryPalette) BuildPage(); }); }
    }

    private void BuildCategoryBrowser()
    {
        if (!categoriesAttempted) { _ = LoadCategoriesAsync(); UpdateWorkspace(); return; }
        string query = AssetSearch.Text.Trim(), grouping = (string?)categoryGrouping!.SelectedItem ?? "Type";
        bool assetsChanged = !ReferenceEquals(categoryPreviewAssets, assets);
        bool indexChanged = !ReferenceEquals(displayedCategoryIndex, categoryIndex);
        if (assetsChanged || indexChanged)
        {
            categoryPreviewCache.Clear(); categoryPreviewOrder.Clear();
            categoryPreviewAssets = assets; displayedCategoryIndex = categoryIndex;
        }
        if (assetsChanged || indexChanged || categoryQuery != query || categoryGroupingKey != grouping)
        {
            var selected = CategoryTabs.SelectedItem as StampCategoryGroup;
            string? selectedName = selected?.Name;
            bool sameFilter = categoryQuery == query && categoryGroupingKey == grouping;
            var previous = sameFilter ? categoryGroups.ToDictionary(g => g.Name) : [];
            ReleaseCategoryPage();
            var filtered = categoryIndex?.Objects.Where(s => MatchesCategorySearch(s, query)).ToList() ?? [];
            matchingCategoryStamps = filtered.Count;
            categoryGroups = filtered.SelectMany(s => StampCategories.Groups(s, grouping).Select(g => (Group: g, Stamp: s)))
                .GroupBy(s => s.Group).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g =>
                {
                    var result = new StampCategoryGroup { Name = g.Key, Members = g.Select(s => s.Stamp).OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList() };
                    if (previous.TryGetValue(g.Key, out var old))
                    { result.Page = old.Page; result.ScrollOffset = old.ScrollOffset; result.SelectedFile = old.SelectedFile; }
                    return result;
                }).ToList();
            categoryQuery = query; categoryGroupingKey = grouping;
            rebuildingCategories = true;
            try
            {
                CategoryTabs.ItemsSource = categoryGroups;
                CategoryTabs.SelectedItem = categoryGroups.FirstOrDefault(g => g.Name == selectedName) ?? categoryGroups.FirstOrDefault();
            }
            finally { rebuildingCategories = false; }
        }
        BuildCategoryPage();
    }

    private static bool MatchesCategorySearch(CategorizedStamp stamp, string query) => StampCategories.Matches(stamp, query) ||
        LocalText.Language == "zh-CN" && string.Join(' ', new[] { stamp.Type, stamp.Subtype }.Concat(stamp.Regions).Concat(stamp.RegionGroups).Concat(stamp.Environments).Concat(stamp.Tags)
            .Where(value => value != null).Select(value => LocalText.T(value!))).Contains(query.Trim(), StringComparison.OrdinalIgnoreCase);

    private void CategoryTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, CategoryTabs) || rebuildingCategories || !CategoryPalette) return;
        BuildCategoryPage();
    }

    private void ReleaseCategoryPage()
    {
        ClearCategorySelection();
        categoryPreviews.Cancel(); categoryPreviews.Dispose(); categoryPreviews = new();
        if (displayedCategory != null)
        {
            foreach (var node in displayedCategory.Items)
            { node.PropertyChanged -= CategoryPreviewChanged; node.ReleasePreview(); }
            displayedCategory.Items.Clear();
        }
    }

    private void BuildCategoryPage()
    {
        rebuildingCategories = true;
        try
        {
            ReleaseCategoryPage();
            pageVersion++; choices.Clear();
            displayedCategory = CategoryTabs.SelectedItem as StampCategoryGroup;
            filteredCount = displayedCategory?.Members.Count ?? 0;
            if (displayedCategory != null && categoryIndex != null)
            {
                displayedCategory.Page = Math.Clamp(displayedCategory.Page, 0, Math.Max(0, (filteredCount - 1) / CategoryPageSize));
                page = displayedCategory.Page;
                string objectRoot = categoryIndex.ObjectRoot;
                var previewAssets = assets;
                foreach (var stamp in displayedCategory.Members.Skip(page * CategoryPageSize).Take(CategoryPageSize))
                {
                    string path = StampCategories.Resolve(objectRoot, stamp.File);
                    var node = new StampCategoryNode
                    {
                        Label = stamp.Name, Stamp = stamp, LoadWhenVisible = true,
                        Detail = string.Join(" \u00b7 ", new[] { stamp.Type, stamp.Subtype }.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct())
                            + (stamp.ReviewStatus == "stale" ? " \u00b7 Needs review" : ""),
                        LivePreviewLoader = token => RenderCategoryPreviewAsync(path, previewAssets, token),
                        PreviewCancellation = categoryPreviews.Token
                    };
                    if (categoryPreviewCache.TryGetValue(path, out var cached)) node.SetBitmap(cached);
                    node.PropertyChanged += CategoryPreviewChanged;
                    displayedCategory.Items.Add(node);
                }
            }
            else page = 0;
        }
        finally { rebuildingCategories = false; }
        RestoreCategoryPosition();
        UpdateWorkspace();
    }

    private void ChangeCategoryPage(int delta)
    {
        if (displayedCategory == null) return;
        int target = Math.Clamp(displayedCategory.Page + delta, 0, Math.Max(0, (displayedCategory.Members.Count - 1) / CategoryPageSize));
        if (target == displayedCategory.Page) return;
        displayedCategory.Page = target; displayedCategory.ScrollOffset = default;
        BuildCategoryPage();
    }

    private void CategoryItemsLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ListBox list) return;
        list.AddHandler(InputElement.KeyDownEvent, CategoryItemsKeyDown, RoutingStrategies.Tunnel);
        if (list.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault() is { } scroll)
        {
            void Changed(object? _, ScrollChangedEventArgs args)
            {
                if (!rebuildingCategories && !restoringCategoryPosition && list.DataContext is StampCategoryGroup group && ReferenceEquals(displayedCategory, group))
                    group.ScrollOffset = scroll.Offset;
            }
            void Unloaded(object? _, RoutedEventArgs args)
            { scroll.ScrollChanged -= Changed; list.RemoveHandler(InputElement.KeyDownEvent, CategoryItemsKeyDown); list.Unloaded -= Unloaded; }
            scroll.ScrollChanged += Changed; list.Unloaded += Unloaded;
        }
        RestoreCategoryPosition();
    }

    private void CategoryItemsKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not ListBox list || list.Items.Count == 0 || e.Key is not (Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End)) return;
        var panel = list.GetVisualDescendants().OfType<ResponsivePanel>().FirstOrDefault();
        int columns = panel == null ? 1 : Math.Max(1, Math.Min(list.Items.Count, (int)Math.Floor(panel.Bounds.Width / panel.ItemMinWidth)));
        int current = list.SelectedIndex;
        int target = current < 0 ? 0 : e.Key switch
        {
            Key.Left => current - 1, Key.Right => current + 1,
            Key.Up => current - columns, Key.Down => current + columns,
            Key.Home => 0, Key.End => list.Items.Count - 1, _ => current
        };
        list.SelectedIndex = Math.Clamp(target, 0, list.Items.Count - 1);
        list.ScrollIntoView(list.SelectedItem!);
        (list.ContainerFromIndex(list.SelectedIndex) as Control)?.Focus();
        e.Handled = true;
    }

    private void RestoreCategoryPosition()
    {
        int version = pageVersion;
        var group = displayedCategory;
        var offset = group?.ScrollOffset ?? default;
        Dispatcher.UIThread.Post(() =>
        {
            if (closed || version != pageVersion || group == null || !CategoryPalette || !ReferenceEquals(group, CategoryTabs.SelectedItem)) return;
            var list = CategoryTabs.GetVisualDescendants().OfType<ListBox>().FirstOrDefault(l => ReferenceEquals(l.DataContext, group));
            if (list == null) return;
            restoringCategoryPosition = true;
            try
            {
                list.SelectedItem = group.Items.FirstOrDefault(n => n.Stamp?.File == group.SelectedFile);
                if (list.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault() is { } scroll) scroll.Offset = offset;
                group.ScrollOffset = offset;
            }
            finally { restoringCategoryPosition = false; }
        }, DispatcherPriority.Loaded);
    }

    private void CategorySelected(object sender, SelectionChangedEventArgs e)
    {
        ClearCategorySelection();
        if (rebuildingCategories || sender is not ListBox { SelectedItem: StampCategoryNode { Stamp: { } stamp } node } || categoryIndex == null) return;
        if (displayedCategory != null) displayedCategory.SelectedFile = stamp.File;
        var reference = AssetReference.Stamp(StampCategories.Resolve(categoryIndex.ObjectRoot, stamp.File));
        var choice = new AssetChoice { Asset = reference, StampPath = reference.StampPath, Label = stamp.Name,
            Description = node.Description,
            Bitmap = node.Bitmap, IsFavourite = assetHistory.IsFavourite(reference) };
        selectedCategoryChoice = choice;
        selectedCategoryNode=node;
        Status.Text = LocalText.T("Loading stamp\u2026");
        _ = ReadStampChoiceAsync(choice, pageVersion);
    }

    private void ClearCategorySelection()
    {
        selectedCategoryNode=null;selectedCategoryChoice=null;
    }
    private void CategoryPreviewChanged(object? sender,PropertyChangedEventArgs e)
    {
        if(e.PropertyName!=nameof(StampCategoryNode.Bitmap)||sender is not StampCategoryNode { Stamp: { } stamp } node||node.Bitmap==null)return;
        if (categoryIndex != null)
        {
            string path = StampCategories.Resolve(categoryIndex.ObjectRoot, stamp.File);
            if (categoryPreviewCache.TryAdd(path, node.Bitmap)) categoryPreviewOrder.Enqueue(path);
            while (categoryPreviewOrder.Count > 128) categoryPreviewCache.Remove(categoryPreviewOrder.Dequeue());
        }
        if (!ReferenceEquals(node,selectedCategoryNode)||selectedCategoryChoice==null)return;
        selectedCategoryChoice.Bitmap=node.Bitmap;
        if(IsSelectedStampChoice(selectedCategoryChoice)&&selectedStampPath==selectedCategoryChoice.StampPath)
        {selectedStampPreview=node.Bitmap;SelectedPreview.Source=ActivePreview();}
    }
    internal static Task<RasterImage?> RenderCategoryPreviewAsync(string stampPath, AssetStore? previewAssets, CancellationToken token)
    {
        if(previewAssets==null)return Task.FromResult<RasterImage?>(null);
        // Skia previews share the bounded preview queue and the thread pool.
        return Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            var cells=CellCodec.LoadStamp(stampPath,token);
            return StampPreview.Create(cells,previewAssets,120,84,token,waitForAssets:true);
        }, token);
    }


    private bool IsSelectedStampChoice(AssetChoice choice) => ReferenceEquals(AssetList.SelectedItem, choice) ||
        CategoryPalette && ReferenceEquals(selectedCategoryChoice, choice);

    private void UpdateCategoryWorkspace()
    {
        CategoryTabs.IsVisible = CategoryPalette && matchingCategoryStamps > 0;
        categoryGrouping!.IsVisible = CategoryPalette ? true : false;
        AssetList.IsVisible = CategoryPalette ? false : true;
        ((Control)PreviousButton.Parent).IsVisible = true;
        if (categoryWasVisible && !CategoryPalette) ReleaseCategoryPage();
        categoryWasVisible = CategoryPalette;
        if (!CategoryPalette) return;
        AssetTitle.Text = LocalText.T("Categories"); AssetCount.Text = ($"{filteredCount:N0} / {matchingCategoryStamps:N0}");
        ToolTip.SetTip(AssetCount, LocalText.T("Current category / all matching stamps"));
        PageLabel.Text = (categoriesLoading ? LocalText.T("Loading Categories\u2026") : (filteredCount == 0 ? LocalText.T("No Stamps") : LocalText.Format("Page {0} of {1}",page + 1,(filteredCount + CategoryPageSize - 1) / CategoryPageSize)));
        ToolTip.SetTip(PageLabel, displayedCategory?.Description);
        LibraryList.IsVisible = StampFolderHint.IsVisible = false;
        (AssetSearch).Tag = LocalText.T("Search Stamps and Categories\u2026"); ToolTip.SetTip(AssetSearch, LocalText.T("Search Names, Types, Regions and Tags"));
        AssetEmpty.IsVisible = filteredCount == 0 ? true : false;
        EmptyTitle.Text = (categoriesLoading ? LocalText.T("Loading Categories\u2026") : (categoryError != null ? LocalText.T("Categories Unavailable") : LocalText.T("No Matching Stamps")));
        EmptyDescription.Text = (categoryError ?? LocalText.T("Try another stamp name or category."));
        EmptyAction.Content = (categoryError != null ? LocalText.T("Reload Categories") : LocalText.T("Clear Search"));
        EmptyAction.IsVisible = categoriesLoading ? false : true;
    }
}
