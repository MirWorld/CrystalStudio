namespace CrystalStudio;

public partial class MainWindow
{
    private void EditModuleConnections()
    {
        if (activeMap is not { IsStamp: true } map || busy) return;
        try
        {
            var cells = Viewport.Document.Cells.Select(pair => new StampCell(
                pair.Key % Viewport.Document.Width + map.StampOriginX,
                pair.Key / Viewport.Document.Width + map.StampOriginY, pair.Value)).ToList();
            var dialog = new ModuleConnectionsWindow(cells, map.Category?.Connections) { Owner = this };
            if (dialog.ShowDialog() != true || dialog.Result == null) return;
            var category = map.Category ?? new CategorizedStamp("", "", map.Path == null ? map.Name : Path.GetFileNameWithoutExtension(map.Path),
                null, null, [], [], [], [], null, "unknown", null, false, "unreviewed");
            if (category.Connections is { } previous && previous.Version == dialog.Result.Version && previous.Points.SequenceEqual(dialog.Result.Points)) return;
            map.Category = category with { Connections = dialog.Result };
            map.CategoryDirty = true; map.RecoveryVersion++;
            UpdateLabels(); UpdateMapTabLabels();
        }
        catch (Exception exception) { MessageBox.Show(this, exception.Message, "Module connections"); }
    }
}
