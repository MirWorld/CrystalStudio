using System.IO;

namespace CrystalStudio;

public partial class MainWindow
{
    private async void CaptureModuleClick(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        if (activeMap is { IsStamp: true }) { Status.Text = LocalText.T("Open a map tab to join modules."); return; }
        if (Viewport.Selection is not { } selection)
        { Status.Text = LocalText.T("Press M and drag a rectangular map selection, then use Tools → Create module from selection."); return; }
        var area = new ModuleArea((int)selection.X, (int)selection.Y, (int)selection.Width, (int)selection.Height);
        await Run(async () =>
        {
            string index = StampCategories.FindIndex() ?? throw new FileNotFoundException(LocalText.T("The stamp category catalogue was not found."));
            var draft = ModuleCapture.Capture(Viewport.Document, area);
            var dialog = new ModuleCaptureWindow(draft, index, assets) { Owner = this };
            dialog.ShowDialog();
            if (dialog.Saved == null) return;
            categoryIndex = StampCategories.Read(index); selectedStampPath = null; selectedStampPreview = null;
            await DiscoverStampsAsync(); if (CategoryPalette) BuildPage(); UpdateLabels(); UpdateMapTabLabels();
            Status.Text = LocalText.T("Module saved. Open a blank map and use Tools → Generate modules automatically.");
        });
    }
}
