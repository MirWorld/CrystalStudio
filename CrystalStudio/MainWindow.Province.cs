namespace CrystalStudio;

public partial class MainWindow
{
    private void GenerateProvinceClick(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        if (assets == null) { Status.Text = LocalText.T("Load game libraries in Settings before generating a province."); return; }
        try
        {
            var dialog = new ProvinceGeneratorWindow(Viewport.Document, assets,
                wallFamilies.Count > 0 ? new WallCatalogue(1, wallFamilies.ToList()) : null, creationStore.Parameters(MapCreationKind.Province)) { Owner = this };
            if (dialog.ShowDialog() != true || dialog.Plan == null) return;
            Viewport.ApplyProvince(dialog.Plan);
            RememberGeneration(MapCreationKind.Province, dialog);
            Status.Text = LocalText.Format("Generated {0} · Seed {1} · Ctrl+Z to undo",dialog.Plan.Style.Name,dialog.Plan.Options.Seed);
        }
        catch (Exception ex) { Status.Text = string.Concat(LocalText.T("Could not generate province: "), (ex.Message)); }
    }
}
