namespace CrystalStudio;

public partial class MainWindow
{
    private MapCheckWindow? mapCheckWindow;

    private void CheckMapClick(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        if (mapCheckWindow is { IsVisible: true }) { mapCheckWindow.Activate(); return; }
        var panel = new MapCheckWindow(() => (Viewport.Document, assets), issue =>
        {
            Viewport.LocateCell(issue.X, issue.Y);
            UpdateCellInspector(issue.X, issue.Y);
            Viewport.Focus();
        }, () => !busy && !closed) { Owner = this };
        mapCheckWindow = panel;
        panel.Closed += (_, _) => { if (ReferenceEquals(mapCheckWindow, panel)) mapCheckWindow = null; };
        panel.Show();
    }
}
