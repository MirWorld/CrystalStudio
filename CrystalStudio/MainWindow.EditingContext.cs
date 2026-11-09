namespace CrystalStudio;

public partial class MainWindow
{
    private readonly TextBlock editingContext = new() { Name = "EditingContext", TextTrimming = TextTrimming.CharacterEllipsis,
        FontSize = 12, Margin = new Thickness(0, 5, 0, 4) };
    private readonly TextBlock saveContext = new() { Name = "SaveContext", FontSize = 10, Margin = new Thickness(12, 5, 0, 4), VerticalAlignment = VerticalAlignment.Center };

    private void InitializeEditingContext()
    {
        var statusGrid = (Grid)Status.Parent!;
        statusGrid.RowDefinitions = new RowDefinitions("Auto,Auto");
        foreach (var child in statusGrid.Children) Grid.SetRow(child, 1);
        Grid.SetColumnSpan(editingContext, 2); statusGrid.Children.Add(editingContext);
        Grid.SetColumn(saveContext, 2); statusGrid.Children.Add(saveContext);
        ((Grid)statusGrid.Parent!.Parent!).RowDefinitions[4].Height = GridLength.Auto;
    }

    private void UpdateEditingContext()
    {
        string[] layers = ["Ground", "Middle", "Objects", "Low Collisions", "High Collisions"];
        bool visible = Viewport.Layer switch
        {
            MapLayer.LowCollision => Viewport.LowCollisionVisible,
            MapLayer.HighCollision => Viewport.HighCollisionVisible,
            _ => Viewport.VisibleLayers[(int)Viewport.Layer]
        };
        string scope = Viewport.Tool switch
        {
            EditorTool.Pan => LocalText.T("Navigation only"),
            EditorTool.Eyedropper => LocalText.T("Sample artwork only"),
            EditorTool.Bucket => LocalText.T("Connected matching tiles"),
            EditorTool.Select => LocalText.T("Selection only"),
            EditorTool.Stamp or EditorTool.Pickup or EditorTool.Clone or EditorTool.TownStamp => LocalText.T("Stamp footprint"),
            _ => LocalText.Format("Brush {0} × {0}", Viewport.Tool == EditorTool.Pencil ? 1 : Viewport.BrushSize)
        };
        if (Viewport.Selection is Rect area) scope += " · " + LocalText.Format("Selection {0} × {1}", area.Width, area.Height);
        string tool = Viewport.Tool switch { EditorTool.SpecialBrush => "Special Brush", EditorTool.TownStamp => "Town Stamp", _ => Viewport.Tool.ToString() };
        editingContext.Text = string.Join(" · ", LocalText.T(tool),
            LocalText.Format("Editing: {0}", LocalText.T(layers[(int)Viewport.Layer])),
            LocalText.T(visible ? "Visible" : "Hidden"), LocalText.T(Viewport.IsLayerLocked(Viewport.Layer) ? "Locked" : "Unlocked"),
            SelectedLabel.Text, scope);
        saveContext.Text = LocalText.Format("File: {0} · Snapshot: {1}",
            activeMap?.LastSavedAt?.ToString("T") ?? "—", activeMap?.RecoveryFailed == true ? LocalText.T("Failed") : activeMap?.LastRecoveryAt?.ToString("T") ?? "—")
            + (Viewport.History.Dirty || activeMap?.CategoryDirty == true ? " · " + LocalText.T("Unsaved changes") : "");
        saveContext.SetTip(SaveStateDescription() + "\n" + LocalText.T("A recovery snapshot does not save the map file."));
        if (ActiveAssetReference() is { } active)
        {
            string alias = assetHistory.Alias(active);
            if (alias.Length > 0) editingContext.Text += " · " + alias;
            SelectedLabel.SetTip(active.Source + "\n" + alias);
        }
        else SelectedLabel.SetTip(null);
        editingContext.SetTip(editingContext.Text);
        usedArtworkOnly.IsVisible = !StampMode && !SavedPalette && !CategoryPalette && !Viewport.IsCollisionLayer;
        editAssetAlias.IsEnabled = !busy && ActiveAssetReference() != null;
        mapCheckWindow?.RefreshValidity();
    }

    private string SaveStateDescription() => LocalText.Format("Map file saved: {0} · Recovery snapshot: {1}",
        activeMap?.LastSavedAt?.ToString("T") ?? LocalText.T("Not saved this session"),
        activeMap?.LastRecoveryAt?.ToString("T") ?? LocalText.T("Not created this session"));
}
