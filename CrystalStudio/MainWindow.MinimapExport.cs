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

        var document = Viewport.Document;
        var dimensions = new MinimapExportWindow(document);
        if (dimensions.ShowDialog(this) != true) return;
        int outputWidth = dimensions.OutputWidth, outputHeight = dimensions.OutputHeight;
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

        var exportAssets = assets;
        await Run(async () =>
        {
            Status.Text = LocalText.T("Exporting minimap…");
            await Task.Run(() => MinimapExporter.ExportAsync(document, exportAssets, picker.FileName, outputWidth, outputHeight));
            Status.Text = LocalText.Format("Exported {0} • {1} × {2} pixels",Path.GetFileName(picker.FileName),outputWidth,outputHeight);
        });
    }
}
