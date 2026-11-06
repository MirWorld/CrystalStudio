namespace CrystalStudio;

public partial class MainWindow
{
    private async void GenerateModulesClick(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        if (assets == null) { Status.Text = LocalText.T("Load game libraries in Settings before generating a module map."); return; }
        await Run(async () =>
        {
            var catalogue = await Task.Run(() => StampCategories.Read(StampCategories.FindIndex()
                ?? throw new System.IO.FileNotFoundException(LocalText.T("The stamp category catalogue was not found."))));
            if (closed) return;
            ShowModuleGeneration(catalogue);
        });
    }

    private void ShowModuleGeneration(StampCategoryIndex catalogue)
        => ShowModuleGenerationPreview(catalogue, false);

    private void ShowModuleGenerationPreview(StampCategoryIndex catalogue, bool generatePreview)
    {
        var dialog = new ModuleGenerationWindow(catalogue, assets) { Owner = this };
        if (generatePreview) dialog.Opened += async (_, _) => await dialog.GeneratePreviewAsync();
        if (dialog.ShowDialog() != true || dialog.Plan == null || closed) return;
        // The plan owns a fresh blank document. Switching tabs preserves the original editor state.
        AddMapTab(dialog.Plan.Placement.Source);
        Viewport.ApplyModuleJoin(dialog.Plan.Placement);
        Status.Text = LocalText.Format("Created a new map with {0} connected modules. Ctrl+Z to undo the layout.", dialog.Plan.Placement.Placements.Count);
    }

    private async void SplitMapModulesClick(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        if (assets == null) { Status.Text = LocalText.T("Load game libraries in Settings before generating a module map."); return; }
        var picker = new OpenFileDialog { Filter = "Mir map (*.map)|*.map" };
        if (picker.ShowDialog(this) != true) return;
        await Run(async () =>
        {
            string indexPath = StampCategories.FindIndex() ?? throw new System.IO.FileNotFoundException(LocalText.T("The stamp category catalogue was not found."));
            var map = await Task.Run(() => MapDocument.Load(picker.FileName, settings.AssetFormat));
            if (closed) return;
            if ((long)map.Width * map.Height > 4_000_000 || map.Width < 2 || map.Height < 2)
                throw new System.IO.InvalidDataException(LocalText.T("Split maps of at least 2 × 2 and at most 4,000,000 cells."));
            var dialog = new ModuleMapSplitWindow(map, picker.FileName, indexPath, assets) { Owner = this };
            dialog.Opened += async (_, _) => await dialog.SplitAsync();
            dialog.ShowDialog();
            if (dialog.Saved == null || closed) return;
            categoryIndex = StampCategories.Read(indexPath); selectedStampPath = null; selectedStampPreview = null;
            await DiscoverStampsAsync(); if (closed) return;
            if (CategoryPalette) BuildPage(); UpdateLabels(); UpdateMapTabLabels();
            Status.Text = LocalText.Format("Saved {0} modules from the map.", dialog.Saved.Count);
            if (dialog.ContinueToGeneration)
                ShowModuleGenerationPreview(categoryIndex with { Objects = dialog.Saved.ToList() }, true);
        });
    }
}
