using System.IO;
namespace CrystalStudio;

public partial class MainWindow
{
    private readonly RecentMapStore recentMaps;

    private void InitializeRecentMaps()
    {
        try { recentMaps.Load(); }
        catch (Exception ex) { OpenRecentButton.SetTip($"Could Not Read Recent Maps: {ex.Message}"); }
        OpenRecentButton.SubmenuOpened += (_, e) => { if (e.Source == OpenRecentButton) BuildRecentMapMenu(); };
        BuildRecentMapMenu();
    }

    private void RememberMap(string path)
    {
        try { recentMaps.Remember(path); OpenRecentButton.SetTip(null); }
        catch (Exception ex) { OpenRecentButton.SetTip($"Could Not Save Recent Maps: {ex.Message}"); }
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
                Header = new TextBlock { Text = Path.GetFileName(path) },
                IsEnabled = exists, Tag = path
            };
            item.SetTip(exists ? path : $"{path}\nFile no longer exists.");
            item.Click += async (_, _) => { if (!busy) await Run(() => OpenMap(path)); };
            OpenRecentButton.Items.Add(item);
        }
        if (recentMaps.Maps.Count == 0) OpenRecentButton.Items.Add(new MenuItem { Header = "No Recent Maps", IsEnabled = false });
    }
}
