using System.IO;
namespace CrystalStudio;

public partial class MainWindow
{
    private readonly RecentMapStore recentMaps;

    private void InitializeRecentMaps()
    {
        try { recentMaps.Load(); }
        catch (Exception ex) { ToolTip.SetTip(OpenRecentButton, LocalText.Format("Could Not Read Recent Maps: {0}",ex.Message)); }
        OpenRecentButton.SubmenuOpened += (_, e) => { if (e.Source == OpenRecentButton) BuildRecentMapMenu(); };
        BuildRecentMapMenu();
    }

    private void RememberMap(string path)
    {
        try { recentMaps.Remember(path); ToolTip.SetTip(OpenRecentButton, (null)); }
        catch (Exception ex) { ToolTip.SetTip(OpenRecentButton, LocalText.Format("Could Not Save Recent Maps: {0}",ex.Message)); }
        BuildRecentMapMenu();
    }

    private void BuildRecentMapMenu()
    {
        OpenRecentButton.Items.Clear();

        foreach (string path in recentMaps.Maps)
        {
            bool exists = File.Exists(path);
            var item = new MenuItem
            {
                Header = new TextBlock { Text = (Path.GetFileName(path))},
                IsEnabled = exists, Tag = path
            };
            ToolTip.SetTip(item, (exists ? (path) : LocalText.Format("{0}\nFile no longer exists.",path)));
            item.Click += async (_, _) => { if (!busy) await Run(() => OpenMap(path)); };
            OpenRecentButton.Items.Add(item);
        }
        if (recentMaps.Maps.Count == 0) OpenRecentButton.Items.Add(new MenuItem { Header = LocalText.T("No Recent Maps"), IsEnabled = false });
    }
}
