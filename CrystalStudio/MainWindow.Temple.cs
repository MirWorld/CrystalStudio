namespace CrystalStudio;

public partial class MainWindow
{
    private async void GenerateTempleClick(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        if (assets == null) { Status.Text = "Load game libraries in Settings before generating a dungeon."; return; }
        await Run(async () =>
        {
            var loadedWalls = wallFamilies.Count > 0 ? new WallCatalogue(1, wallFamilies.ToList()) : null;
            var definitions = await Task.Run(() => (Walls: loadedWalls ?? WallCatalogue.Read(WallCatalogue.ResolvePath()), Terrain: TerrainCatalogue.Load()));
            if (closed) return;
            var dialog = new TempleGeneratorWindow(Viewport.Document, assets, definitions.Walls, definitions.Terrain) { Owner = this };
            if (dialog.ShowDialog() != true || dialog.Plan == null) return;
            Viewport.ApplyTemple(dialog.Plan);
            Status.Text = $"Generated {dialog.Plan.Options.Type} dungeon · {dialog.Plan.Options.Temple.Name} · Seed {dialog.Plan.Options.Seed} · Ctrl+Z to undo";
        });
    }
}
