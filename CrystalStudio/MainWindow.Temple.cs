namespace CrystalStudio;

public partial class MainWindow
{
    private async void GenerateTempleClick(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        if (assets == null) { Status.Text = LocalText.T("Load game libraries in Settings before generating a dungeon."); return; }
        await Run(async () =>
        {
            var loadedWalls = wallFamilies.Count > 0 ? new WallCatalogue(1, wallFamilies.ToList()) : null;
            var definitions = await Task.Run(() => (Walls: loadedWalls ?? WallCatalogue.Read(WallCatalogue.ResolvePath()), Terrain: TerrainCatalogue.Load()));
            if (closed) return;
            var dialog = new TempleGeneratorWindow(Viewport.Document, assets, definitions.Walls, definitions.Terrain) { Owner = this };
            if (dialog.ShowDialog() != true || dialog.Plan == null) return;
            Viewport.ApplyTemple(dialog.Plan);
            Status.Text = LocalText.Format("Generated {0} dungeon · {1} · Seed {2} · Ctrl+Z to undo",dialog.Plan.Options.Type,dialog.Plan.Options.Temple.Name,dialog.Plan.Options.Seed);
        });
    }
}
