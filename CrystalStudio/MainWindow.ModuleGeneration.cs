namespace CrystalStudio;

public partial class MainWindow
{
    private async void GenerateModulesClick(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        if (activeMap is { IsStamp: true }) { Status.Text = LocalText.T("Open a map tab to work with modules."); return; }
        if (assets == null) { Status.Text = LocalText.T("Load game libraries in Settings before generating a module map."); return; }
        if (Viewport.AnyLayerLocked) { Status.Text = LocalText.T("Unlock all layers before editing multiple layers."); return; }
        ModuleArea? area = Viewport.Selection is { } selection
            ? new((int)selection.X, (int)selection.Y, (int)selection.Width, (int)selection.Height) : null;
        Viewport.CancelInteraction();
        await Run(async () =>
        {
            var catalogue = await Task.Run(() => StampCategories.Read(StampCategories.FindIndex()
                ?? throw new System.IO.FileNotFoundException(LocalText.T("The stamp category catalogue was not found."))));
            if (closed) return;
            var dialog = new ModuleGenerationWindow(Viewport.Document, catalogue, assets, area) { Owner = this };
            if (dialog.ShowDialog() != true || dialog.Plan == null) return;
            Viewport.ApplyModuleJoin(dialog.Plan.Placement);
            Status.Text = LocalText.Format("Generated {0} connected modules. Ctrl+Z to undo.", dialog.Plan.Placement.Placements.Count);
        });
    }
}
