namespace CrystalStudio;

public partial class MainWindow
{
    private void ExpandMapClick(object sender, RoutedEventArgs e)
    {
        if (busy || activeMap?.IsStamp == true || Viewport.AnyLayerLocked) return;
        var dialog = new MapExpansionWindow(Viewport.Document);
        if (dialog.ShowDialog(this) != true) return;
        if (Viewport.ExpandMap(dialog.LeftAmount, dialog.TopAmount, dialog.RightAmount, dialog.BottomAmount))
            Status.Text = LocalText.Format("Expanded map: {0} × {1} cells", Viewport.Document.Width, Viewport.Document.Height);
    }
}
