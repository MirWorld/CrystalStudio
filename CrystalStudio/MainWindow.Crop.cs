namespace CrystalStudio;

public partial class MainWindow
{
    private void CropClick(object sender, RoutedEventArgs e)
    {
        if (busy || activeMap?.IsStamp == true) return;
        if (Viewport.CropSelection())
            Status.Text = LocalText.Format("Cropped map to {0} × {1} cells", Viewport.Document.Width, Viewport.Document.Height);
    }
}
