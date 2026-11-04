namespace CrystalStudio;

public partial class MainWindow
{
    private void GenerateProvinceClick(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        if (assets == null) { Status.Text = "Load game libraries in Settings before generating a province."; return; }
        try
        {
            var dialog = new ProvinceGeneratorWindow(Viewport.Document, assets,
                wallFamilies.Count > 0 ? new WallCatalogue(1, wallFamilies.ToList()) : null) { Owner = this };
            if (dialog.ShowDialog() != true || dialog.Plan == null) return;
            Viewport.ApplyProvince(dialog.Plan);
            Status.Text = $"Generated {dialog.Plan.Style.Name} · Seed {dialog.Plan.Options.Seed} · Ctrl+Z to undo";
        }
        catch (Exception ex) { Status.Text = "Could not generate province: " + ex.Message; }
    }
}
