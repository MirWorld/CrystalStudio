namespace CrystalStudio;

public partial class MainWindow
{
    private async void JoinModulesClick(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        if (activeMap is { IsStamp: true }) { Status.Text = LocalText.T("Open a map tab to join modules."); return; }
        if (assets == null) { Status.Text = LocalText.T("Load game libraries in Settings before joining modules."); return; }
        if (Viewport.AnyLayerLocked) { Status.Text = LocalText.T("Unlock all layers before editing multiple layers."); return; }
        Viewport.CancelInteraction();
        await Run(async () =>
        {
            var catalogue = await Task.Run(() => StampCategories.Read(StampCategories.FindIndex()
                ?? throw new System.IO.FileNotFoundException(LocalText.T("The stamp category catalogue was not found."))));
            if (closed) return;
            var dialog = new ModuleJoinWindow(Viewport.Document, catalogue, assets) { Owner = this };
            if (dialog.ShowDialog() != true || dialog.Plan == null) return;
            Viewport.ApplyModuleJoin(dialog.Plan);
            Status.Text = LocalText.T("Joined two modules. Ctrl+Z to undo.");
        });
    }
}
