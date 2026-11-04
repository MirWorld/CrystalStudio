using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
namespace CrystalStudio;

public sealed class StampCategoryNode : INotifyPropertyChanged
{
    public required string Label { get; init; }
    public string Detail { get; init; } = "";
    public string Description => Label + (Detail.Length == 0 ? "" : "\n" + Detail);
    public CategorizedStamp? Stamp { get; init; }
    public bool HasStamp => Stamp != null;
    public List<CategorizedStamp>? Members { get; init; }
    public ObservableCollection<StampCategoryNode> Children { get; private set; } = [];
    internal void Populate(IEnumerable<StampCategoryNode> children)
    {
        Children=new(children);Populated=true;PropertyChanged?.Invoke(this,new(nameof(Children)));
    }
    public bool Populated { get; set; }
    public Func<RasterImage?>? PreviewLoader { get; init; }
    public Func<CancellationToken, Task<RasterImage?>>? LivePreviewLoader { get; init; }
    public CancellationToken PreviewCancellation { get; init; }
    public event PropertyChangedEventHandler? PropertyChanged;
    private static readonly SemaphoreSlim previewReaders=new(2);
    private bool previewRequested;
    private RasterImage? bitmap;
    private CancellationTokenSource? visiblePreview;
    public void ReleasePreview()
    {
        visiblePreview?.Cancel(); visiblePreview = null;
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
            if(!previewRequested&&(PreviewLoader!=null||LivePreviewLoader!=null)&&!PreviewCancellation.IsCancellationRequested)
            {
                previewRequested=true;
                visiblePreview=CancellationTokenSource.CreateLinkedTokenSource(PreviewCancellation);
                _ = LoadPreviewAsync(Dispatcher.UIThread, visiblePreview);
            }
            return bitmap;
        }
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

public partial class MainWindow
{
    private TreeView? categoryTree;
    private ComboBox? categoryGrouping;
    private StampCategoryIndex? categoryIndex;
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
        categoryGrouping.SetTip("Group stamps by category");
        RegisterEditorName("CategoryGrouping", categoryGrouping);
        ((StackPanel)LibraryList.Parent).Children.Insert(2, categoryGrouping);
        categoryGrouping.SelectionChanged += (_, _) => { if (CategoryPalette) BuildPage(); };
        categoryTree = new TreeView { IsVisible = false, Background = Brushes.Transparent,
            BorderThickness = new Thickness(0), ItemTemplate = (Avalonia.Markup.Xaml.Templates.TreeDataTemplate)this.FindResource("StampCategoryTemplate"),
            ItemContainerTheme = (Style)this.FindResource("StampCategoryItem") };
        categoryTree.SetResource(ForegroundProperty, "ThemeE5E9F0");
        categoryTree.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        // Dock grids can probe content with an unbounded height before arranging
        // their star rows. Keep every measure pass within the current viewport.
        categoryTree.Bind(MaxHeightProperty, new Avalonia.Data.Binding("Bounds.Height") { Source = AssetArea });
        categoryTree.AddHandler(TreeViewItem.ExpandedEvent, new EventHandler<RoutedEventArgs>(CategoryExpanded));
        categoryTree.SelectionChanged += CategorySelected;
        Closed += (_,_) => { categoryPreviews.Cancel(); categoryPreviews.Dispose(); ClearCategorySelection(); };
        RegisterEditorName("CategoryTree", categoryTree);
        AssetArea.Children.Insert(1, categoryTree);
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

    private void BuildCategoryTree()
    {
        categoryPreviews.Cancel();categoryPreviews.Dispose();categoryPreviews=new();
        pageVersion++; choices.Clear(); ClearCategorySelection(); filteredCount = 0;
        categoryTree!.Items.Clear();
        if (!categoriesAttempted) { _ = LoadCategoriesAsync(); UpdateWorkspace(); return; }
        string query = AssetSearch.Text.Trim(), grouping = (string?)categoryGrouping!.SelectedItem ?? "Type";
        var filtered = categoryIndex?.Objects.Where(s => StampCategories.Matches(s, query)).ToList() ?? [];
        filteredCount = filtered.Count;
        foreach (var group in filtered.SelectMany(s => StampCategories.Groups(s, grouping).Select(g => (Group: g, Stamp: s)))
            .GroupBy(s => s.Group).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var members = group.Select(s => s.Stamp).OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
            var node = new StampCategoryNode { Label = $"{group.Key} ({members.Count:N0})", Members = members };
            node.Children.Add(new() { Label = "" }); // Populate only when expanded.
            categoryTree.Items.Add(node);
        }
        PageLabel.Text = categoriesLoading ? "Loading Categories\u2026" : $"{filteredCount:N0} stamps";
        UpdateWorkspace();
    }

    private void CategoryExpanded(object sender,RoutedEventArgs e)
    {
        if (categoryIndex==null||e.Source is not TreeViewItem { DataContext: StampCategoryNode { Members: { } members, Populated: false } node }) return;
        string objectRoot=categoryIndex.ObjectRoot;
        var previewAssets=assets;
        node.Populate(members.Select(stamp => new StampCategoryNode { Label = stamp.Name, Stamp = stamp,
                LivePreviewLoader = token => RenderCategoryPreviewAsync(StampCategories.Resolve(objectRoot,stamp.File),previewAssets,token), PreviewCancellation=categoryPreviews.Token,
                Detail = $"{stamp.Subtype ?? stamp.Type ?? "Unknown type"}{(stamp.ReviewStatus == "stale" ? " \u00b7 Needs review" : "")}" }));
    }

    private void CategorySelected(object sender, SelectionChangedEventArgs e)
    {
        ClearCategorySelection();
        if (categoryTree?.SelectedItem is not StampCategoryNode { Stamp: { } stamp } node || categoryIndex == null) return;
        var reference = AssetReference.Stamp(StampCategories.Resolve(categoryIndex.ObjectRoot, stamp.File));
        var choice = new AssetChoice { Asset = reference, StampPath = reference.StampPath, Label = stamp.Name,
            Description = $"{stamp.File}\n{stamp.Type ?? "Unknown type"} \u00b7 {stamp.Subtype}\n{string.Join(", ", stamp.RegionGroups)}",
            Bitmap = node.Bitmap, IsFavourite = assetHistory.IsFavourite(reference) };
        selectedCategoryChoice = choice;
        selectedCategoryNode=node;node.PropertyChanged+=CategoryPreviewChanged;
        Status.Text = "Loading stamp\u2026";
        _ = ReadStampChoiceAsync(choice, pageVersion);
    }

    private void ClearCategorySelection()
    {
        if(selectedCategoryNode!=null)selectedCategoryNode.PropertyChanged-=CategoryPreviewChanged;
        selectedCategoryNode=null;selectedCategoryChoice=null;
    }
    private void CategoryPreviewChanged(object? sender,PropertyChangedEventArgs e)
    {
        if(e.PropertyName!=nameof(StampCategoryNode.Bitmap)||sender is not StampCategoryNode node||
            !ReferenceEquals(node,selectedCategoryNode)||selectedCategoryChoice==null||node.Bitmap==null)return;
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
        categoryTree!.IsVisible = CategoryPalette ? true : false;
        categoryGrouping!.IsVisible = CategoryPalette ? true : false;
        AssetList.IsVisible = CategoryPalette ? false : true;
        ((Control)PreviousButton.Parent).IsVisible = CategoryPalette ? false : true;
        if (!CategoryPalette) return;
        AssetTitle.Text = "Stamp Categories"; AssetCount.Text = $"{filteredCount:N0} stamps";
        LibraryList.IsVisible = StampFolderHint.IsVisible = false;
        AssetSearch.Tag = "Search Stamps and Categories\u2026"; AssetSearch.SetTip("Search Names, Types, Regions and Tags");
        AssetEmpty.IsVisible = filteredCount == 0 ? true : false;
        EmptyTitle.Text = categoriesLoading ? "Loading Categories\u2026" : categoryError != null ? "Categories Unavailable" : "No Matching Stamps";
        EmptyDescription.Text = categoryError ?? "Try another stamp name or category.";
        EmptyAction.Content = categoryError != null ? "Reload Categories" : "Clear Search";
        EmptyAction.IsVisible = categoriesLoading ? false : true;
    }
}
