namespace CrystalStudio;

public partial class MainWindow
{
    private async void GenerateTerrainClick(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        if (assets == null) { Status.Text = "Load game libraries in Settings before generating terrain."; return; }
        await Run(async () =>
        {
            var catalogue = await Task.Run(() => TerrainCatalogue.Load());
            if (closed) return;
            var dialog = new TerrainGeneratorWindow(Viewport.Document, assets, catalogue) { Owner = this };
            if (dialog.ShowDialog() != true || dialog.Plan == null) return;
            Viewport.ApplyTerrain(dialog.Plan);
            Status.Text = $"Generated {dialog.Plan.Tiles.Count:N0} ground tiles"+(dialog.Plan.Water==null?"":$" + {dialog.Plan.Water.Banks.Count:N0} bank sprites")+
                string.Concat(dialog.Plan.CoastObjects.Select(c=>$" + {c.Layout.Name}"))+$" · Seed {dialog.Plan.Seed} · Ctrl+Z to undo";
        });
    }
}
