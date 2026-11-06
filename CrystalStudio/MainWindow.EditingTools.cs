namespace CrystalStudio;

public partial class MainWindow
{
    private void CopyRegionClick(object sender, RoutedEventArgs e) { if (!busy) Viewport.CopySelection(); }
    private void CutRegionClick(object sender, RoutedEventArgs e) { if (!busy) Viewport.CutSelection(); }
    private void PasteRegionClick(object sender, RoutedEventArgs e) { if (!busy) Viewport.BeginPaste(); }
}
