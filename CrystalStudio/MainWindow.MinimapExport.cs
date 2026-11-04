using System.IO;
namespace CrystalStudio;

public partial class MainWindow
{
    private async void ExportMinimapClick(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        if (assets == null)
        {
            MessageBox.Show(this, "Choose your Crystal library folder in Settings before exporting map artwork.", "Export Minimap");
            return;
        }

        var picker = new SaveFileDialog
        {
            Title = "Export Minimap",
            Filter = "PNG image (*.png)|*.png",
            FileName = MinimapExporter.DefaultFileName(mapPath),
            InitialDirectory = mapPath == null ? "" : Path.GetDirectoryName(mapPath),
            DefaultExt = ".png",
            AddExtension = true,
            OverwritePrompt = true
        };
        if (picker.ShowDialog(this) != true) return;

        var document = Viewport.Document;
        var exportAssets = assets;
        await Run(async () =>
        {
            Status.Text = "Exporting minimap…";
            await Task.Run(() => MinimapExporter.ExportAsync(document, exportAssets, picker.FileName));
            Status.Text = $"Exported {Path.GetFileName(picker.FileName)} • {document.Width * 12} × {document.Height * 8} pixels";
        });
    }
}
