using System.IO;
namespace CrystalStudio;

public partial class MainWindow
{
    private AssetReference? previewTile;
    private AssetStore? previewAssetStore;
    private RasterImage? previewTileBitmap;
    private List<AssetReference> savedBrowserAssets = [];

    private RasterImage? ActiveTilePreview()
    {
        if (ActiveAssetReference() is not AssetReference tile) return null;
        if (previewTile?.SameAsset(tile) != true || !ReferenceEquals(previewAssetStore, assets))
        { previewTile = tile; previewAssetStore = assets; previewTileBitmap = null; }
        previewTileBitmap ??= choices.FirstOrDefault(c => c.Asset?.SameAsset(tile) == true)?.Bitmap ?? TilePreview(tile);
        return previewTileBitmap;
    }
    private TabControl CreatePaletteTabs(Grid palette)
    {
        paletteTabs = new TabControl { Theme = (Style)this.FindResource("PaletteTabControl") };
        RegisterEditorName("PaletteTabs", paletteTabs);
        InitializeCategoryPalette();
        foreach (string title in new[] { "Library", "Favourites", "Recently Used", "Categories" })
            paletteTabs.Items.Add(new TabItem { Header = LocalText.T(title), Theme = (Style)this.FindResource("PaletteTabItem") });
        ((TabItem)paletteTabs.Items[0]).Content = palette;
        paletteTabs.SelectedIndex = 0;
        paletteTabs.SelectionChanged += (_, e) =>
        {
            if (!ReferenceEquals(e.Source, paletteTabs)) return;
            foreach (TabItem old in e.RemovedItems) old.Content = null;
            ((TabItem)paletteTabs.SelectedItem).Content = palette;
            searchDelay.Stop(); AssetSearch.Clear(); searchDelay.Stop(); page = 0; BuildPage();
        };
        return paletteTabs;
    }

    private AssetReference? ActiveAssetReference()
    {
        if (StampMode) return selectedStampPath == null ? null : AssetReference.Stamp(selectedStampPath);
        if (Viewport.SpriteIndex < 0 || Viewport.IsCollisionLayer ||
            Viewport.Tool is not (EditorTool.Brush or EditorTool.Pencil or EditorTool.Bucket) ||
            assets?.Libraries.TryGetValue(Viewport.Library, out var library) != true) return null;
        return AssetReference.Tile(library!.Path, Viewport.Library, Viewport.SpriteIndex, Viewport.Layer,
            PatternPalette ? AutoTileBrush.Layout(Viewport.Library, Viewport.Layer) : TilePatternLayout.None);
    }

    private void SaveAssetHistory()
    {
        try { assetHistory.Save(); }
        catch (Exception ex) { Status.Text = LocalText.Format("Could not save favourites and recent assets: {0}",ex.Message); }
    }

    private void RememberActiveAsset()
    {
        if (ActiveAssetReference() is AssetReference asset && assetHistory.Remember(asset)) SaveAssetHistory();
    }

    private void PinAssetClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: AssetChoice { Asset: AssetReference asset } choice }) return;
        bool pinned = assetHistory.ToggleFavourite(asset);
        choice.IsFavourite = pinned;
        Status.Text = (pinned ? LocalText.Format("{0} pinned to Favourites",choice.Label) : LocalText.Format("{0} unpinned",choice.Label));
        SaveAssetHistory();
        if (FavouritesPalette) BuildPage(); else UpdateWorkspace();
        e.Handled = true;
    }

    private void PinActiveAssetClick(object sender, RoutedEventArgs e)
    {
        if (ActiveAssetReference() is not AssetReference asset) return;
        bool pinned = assetHistory.ToggleFavourite(asset);
        foreach (var choice in choices.Where(c => c.Asset?.SameAsset(asset) == true)) choice.IsFavourite = pinned;
        Status.Text = (pinned ? LocalText.T("Active asset pinned to Favourites") : LocalText.T("Active asset unpinned"));
        SaveAssetHistory();
        if (FavouritesPalette) BuildPage(); else UpdateWorkspace();
    }

    private bool TileAvailable(AssetReference asset) =>
        assets?.Libraries.TryGetValue(asset.Library, out var library) == true &&
        string.Equals(library!.Path, asset.Source, StringComparison.OrdinalIgnoreCase) && asset.Index < library.Count;

    private RasterImage? TilePreview(AssetReference asset) => !TileAvailable(asset) ? null :
        asset.Pattern == TilePatternLayout.None ? assets!.Get(asset.Library, asset.Index)?.Bitmap :
        TilePatternPreview.Create(asset.Pattern, asset.Index, n => assets!.Get(asset.Library, n), n => assets!.IsResolved(asset.Library, n));

    private AssetChoice TileChoice(AssetReference asset, bool saved = false)
    {
        string label = asset.Pattern == TilePatternLayout.None ? asset.Index.ToString() :
            $"Set {asset.Index / AutoTileBrush.BlockSize(asset.Pattern)}{(asset.Pattern == TilePatternLayout.Mir3 && asset.Index % 30 == 5 ? " B" : "")}";
        string kind = asset.Pattern == TilePatternLayout.None ? "Sprite" : "Terrain set";
        return new AssetChoice
        {
            Asset = asset, Index = asset.Index, PreviewLayout = asset.Pattern, IsFavourite = assetHistory.IsFavourite(asset),
            Label = label, Detail = saved ? $"{asset.Layer} \u00b7 Lib {asset.Library}" : "",
            Description = $"{kind} {label} \u00b7 {asset.Layer} \u00b7 Library {asset.Library}\n{asset.Source}" +
                (TileAvailable(asset) ? "" : "\nUnavailable in the current library folder"),
            Bitmap = TilePreview(asset)
        };
    }

    private List<AssetReference> FilteredSavedAssets()
    {
        string query = AssetSearch.Text.Trim();
        return (FavouritesPalette ? assetHistory.Favourites : assetHistory.Recent).Where(asset =>
            query.Length == 0 || (asset.StampPath != null ? asset.StampPath :
                $"{asset.Layer} {asset.Library} {asset.Index} {asset.Source} Set {(asset.Pattern == TilePatternLayout.None ? asset.Index : asset.Index / AutoTileBrush.BlockSize(asset.Pattern))}")
            .Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private void BuildSavedPage(List<AssetReference>? snapshot = null)
    {
        int version = ++pageVersion; choices.Clear();
        var filtered = snapshot ?? FilteredSavedAssets(); savedBrowserAssets = filtered; filteredCount = filtered.Count;
        page = Math.Clamp(page, 0, Math.Max(0, (filteredCount - 1) / PageSize));
        foreach (var asset in filtered.Skip(page * PageSize).Take(PageSize))
        {
            if (asset.StampPath == null) { choices.Add(TileChoice(asset, true)); continue; }
            var choice = new AssetChoice { Asset = asset, StampPath = asset.StampPath,
                Label = Path.GetFileNameWithoutExtension(asset.StampPath), Description = asset.StampPath,
                Detail = "Stamp",
                IsFavourite = assetHistory.IsFavourite(asset) };
            if (string.Equals(selectedStampPath, asset.StampPath, StringComparison.OrdinalIgnoreCase) && File.Exists(asset.StampPath))
            { choice.StampCells = Viewport.Stamp; choice.Bitmap = selectedStampPreview; }
            choices.Add(choice);
            if (choice.StampCells == null) _ = ReadStampChoiceAsync(choice, version);
        }
        PageLabel.Text = (filteredCount == 0 ? LocalText.T("No Saved Assets") : LocalText.Format("Page {0} of {1}",page + 1,(filteredCount + PageSize - 1) / PageSize));
        UpdateWorkspace();
    }

    private void RestoreTile(AssetChoice choice)
    {
        if (choice.Asset is not AssetReference asset || !TileAvailable(asset))
        { Status.Text = LocalText.T("Asset unavailable \u00b7 Load its original library folder in Settings"); return; }
        restoringAsset = true;
        try
        {
            LayerList.SelectedIndex = (int)asset.Layer; Viewport.Layer = asset.Layer;
            LibraryList.SelectedItem = LibraryList.Items.OfType<LibraryChoice>().FirstOrDefault(l => l.Id == asset.Library);
            Viewport.Library = asset.Library; Viewport.SpriteIndex = asset.Index;
            Viewport.Tool = asset.Pattern != TilePatternLayout.None ? EditorTool.Brush :
                Viewport.Tool == EditorTool.Bucket && asset.Layer is MapLayer.Ground or MapLayer.Middle ? EditorTool.Bucket : EditorTool.Pencil;
        }
        finally { restoringAsset = false; }
        BrushSize.IsEnabled = Viewport.Tool != EditorTool.Pencil;
        RememberActiveAsset(); UpdateLabels(); Viewport.InvalidateVisual();
    }

    private void RefreshPaletteThumbnails()
    {
        if (assets == null) return;
        if (selectedStampPreview == null && Viewport.Stamp != null) selectedStampPreview = StampPreview.Create(Viewport.Stamp, assets);
        foreach (var choice in choices)
        {
            if (choice.Bitmap == null)
                choice.Bitmap = choice.StampCells != null ? StampPreview.Create(choice.StampCells, assets) :
                    choice.Asset is AssetReference { StampPath: null } tile ? TilePreview(tile) : null;
            if (string.Equals(choice.StampPath, selectedStampPath, StringComparison.OrdinalIgnoreCase) && choice.Bitmap != null && choice.StampPath != null)
                selectedStampPreview = choice.Bitmap;
        }
        SelectedPreview.Source = ActivePreview();
    }
}
