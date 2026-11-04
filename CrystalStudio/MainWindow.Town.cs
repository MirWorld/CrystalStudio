namespace CrystalStudio;

public partial class MainWindow
{
    private async void TownStampClick(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        if (assets == null) { Status.Text = LocalText.T("Load game libraries in Settings before generating a town."); UpdateWorkspace(); return; }
        await Run(async () =>
        {
            var catalogue = await Task.Run(() => TownCatalogue.Load());
            if (closed) return;
            var dialog = new TownStampWindow(assets, catalogue, Viewport.Town) { Owner = this };
            if (dialog.ShowDialog() != true || dialog.Plan == null) { UpdateWorkspace(); return; }
            Viewport.Town = dialog.Plan;
            Viewport.Layer = MapLayer.Objects; LayerList.SelectedIndex = (int)MapLayer.Objects;
            SetTool(EditorTool.TownStamp);
            Status.Text = LocalText.Format("{0} · Click to place · Town Settings to shuffle",dialog.Plan.Name);
        });
    }
}
