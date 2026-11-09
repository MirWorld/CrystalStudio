namespace CrystalStudio;

public partial class MainWindow
{
    private readonly CheckBox usedArtworkOnly = new() { Name = "UsedArtworkOnly", Content = LocalText.T("Used in this map"), Margin = new Thickness(0, 6, 0, 0) };
    private readonly Button editAssetAlias = new() { Name = "EditAssetAlias", Content = LocalText.T("Edit local alias…"), HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(0, 3) };
    private MapDocument? usedArtworkDocument;
    private long usedArtworkRevision = -1;
    private readonly HashSet<(MapLayer Layer, int Library, int Index)> usedArtwork = [];

    private void InitializeAssetSemantics()
    {
        var fields = (StackPanel)LibraryList.Parent!;
        editAssetAlias.Theme = SelectedPinButton.Theme;
        editAssetAlias.Margin = new Thickness(8, 4, 0, 0);
        editAssetAlias.FontSize = 10;
        fields.Children.Insert(fields.Children.IndexOf(LibraryList) + 1, new WrapPanel { Children = { usedArtworkOnly, editAssetAlias } });
        usedArtworkOnly.IsCheckedChanged += (_, _) => { page = 0; BuildPage(); };
        editAssetAlias.Click += (_, _) => EditLocalAlias();
        Viewport.Changed += () =>
        {
            if (usedArtworkOnly.IsChecked == true && (usedArtworkDocument != Viewport.Document || usedArtworkRevision != Viewport.Document.PickupRevision))
            { searchDelay.Stop(); searchDelay.Start(); }
        };
    }

    private void EditLocalAlias()
    {
        if (ActiveAssetReference() is not { } asset) return;
        var dialog = new EditorWindow { Owner = this, Title = LocalText.T("Edit local alias…"), Width = 480, Height = 240,
            WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Margin = new Thickness(20), Spacing = 10 };
        panel.Children.Add(new TextBlock { Text = LocalText.T("Display name only. Resource IDs and files stay unchanged. Leave empty to remove."), TextWrapping = TextWrapping.Wrap });
        var name = new TextBox { Name = "LocalAssetAlias", Text = assetHistory.Alias(asset), MaxLength = 80 };
        panel.Children.Add(name);
        var save = new Button { Content = LocalText.T("Save"), IsDefault = true };
        save.Click += (_, _) => { if (!(name.Text ?? "").Any(char.IsControl)) dialog.DialogResult = true; };
        panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8,
            Children = { save, new Button { Content = LocalText.T("Cancel"), IsCancel = true } } });
        dialog.Content = panel;
        if (dialog.ShowDialog() != true) return;
        assetHistory.SetAlias(asset, name.Text ?? ""); SaveAssetHistory(); BuildPage(); UpdateLabels();
    }

    private void RefreshUsedArtwork()
    {
        if (usedArtworkDocument == Viewport.Document && usedArtworkRevision == Viewport.Document.PickupRevision) return;
        usedArtworkDocument = Viewport.Document; usedArtworkRevision = Viewport.Document.PickupRevision; usedArtwork.Clear();
        foreach (var cell in Viewport.Document.Cells.Select(pair => pair.Value))
        {
            int ground = cell.BackImage & 0x1FFFFFFF;
            if (ground > 0) usedArtwork.Add((MapLayer.Ground, cell.BackLibrary, ground - 1));
            if (cell.MiddleImage > 0) usedArtwork.Add((MapLayer.Middle, cell.MiddleLibrary, cell.MiddleImage - 1));
            foreach (var front in cell.ForegroundImages)
                if ((front.Image & 0x7FFF) > 0) usedArtwork.Add((MapLayer.Objects, front.Library, (front.Image & 0x7FFF) - 1));
        }
    }

    private HashSet<int>? UsedArtworkIndices(TilePatternLayout layout)
    {
        if (usedArtworkOnly.IsChecked != true) return null;
        RefreshUsedArtwork();
        return usedArtwork.Where(item => item.Layer == Viewport.Layer && item.Library == Viewport.Library)
            .Select(item => layout == TilePatternLayout.None ? item.Index :
                item.Index / AutoTileBrush.BlockSize(layout) * AutoTileBrush.BlockSize(layout) + (layout == TilePatternLayout.Mir3 && item.Index % 30 is >= 5 and <= 9 ? 5 : 0)).ToHashSet();
    }
}
