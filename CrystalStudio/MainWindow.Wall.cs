using System.IO;
namespace CrystalStudio;

public partial class MainWindow
{
    private IReadOnlyList<WallFamily> wallFamilies = [];
    private WallBrushWindow? wallBrushWindow;

    private async Task InitializeWallsAsync()
    {
        if (closed) return;
        wallFamilies = [];
        Viewport.WallFamily = null;
        Viewport.WallCatalogueError = null;
        try
        {
            // Reading, deserializing and validating all native wall definitions
            // takes seconds on a cold launch. Only publish the result on the UI thread.
            var catalogue = await Task.Run(() => WallCatalogue.Read(WallCatalogue.ResolvePath()));
            if (closed) return;
            wallFamilies = catalogue.Families;
            Viewport.WallFamily = wallFamilies.FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidOperationException)
        { if (!closed) Viewport.WallCatalogueError = "Could not load wall catalogue: " + ex.Message; }
    }

    private void WallBrushClick(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        if (wallBrushWindow != null) { wallBrushWindow.Activate(); return; }
        var dialog = new WallBrushWindow(wallFamilies, assets, Viewport.WallFamily, Viewport.WallCatalogueError) { Owner = this };
        wallBrushWindow = dialog;
        try
        {
            if (dialog.ShowDialog() != true || dialog.SelectedFamily == null) { UpdateWorkspace(); return; }
            Viewport.WallFamily = dialog.SelectedFamily;
            LayerList.SelectedIndex = (int)MapLayer.Objects;
            ObjectsVisible.IsChecked = true;
            SetTool(EditorTool.Wall);
            Status.Text = LocalText.Format("Wall · {0} · Click or drag to connect sections",dialog.SelectedFamily.DisplayName);
            Viewport.Focus();
        }
        finally { wallBrushWindow = null; }
    }

    private void WallEraseChanged(object sender, RoutedEventArgs e)
    {
        if (Viewport == null) return;
        Viewport.EraseWall = WallEraseToggle.IsChecked == true;
        Viewport.InvalidateVisual();
    }
}
