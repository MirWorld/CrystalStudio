namespace CrystalStudio;

public partial class MainWindow
{
    private async void GenerateTerrainClick(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        if (assets == null) { Status.Text = LocalText.T("Load game libraries in Settings before generating terrain."); return; }
        await Run(async () =>
        {
            var catalogue = await Task.Run(() => TerrainCatalogue.Load(format: settings.AssetFormat));
            if (closed) return;
            var dialog = new TerrainGeneratorWindow(Viewport.Document, assets, catalogue, creationStore.Parameters(MapCreationKind.Terrain)) { Owner = this };
            if (dialog.ShowDialog() != true || dialog.Plan == null) return;
            Viewport.ApplyTerrain(dialog.Plan);
            RememberGeneration(MapCreationKind.Terrain, dialog);
            Status.Text = string.Concat(string.Concat(string.Concat(LocalText.Format("Generated {0:N0} ground tiles",dialog.Plan.Tiles.Count), ((dialog.Plan.Water==null ? ("") : LocalText.Format(" + {0:N0} bank sprites",dialog.Plan.Water.Banks.Count)))), (string.Concat(dialog.Plan.CoastObjects.Select(c=>$" + {c.Layout.Name}")))), LocalText.Format(" · Seed {0} · Ctrl+Z to undo",dialog.Plan.Seed));
        });
    }
}
