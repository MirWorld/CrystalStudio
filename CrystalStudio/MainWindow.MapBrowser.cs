using System.IO;

namespace CrystalStudio;

public partial class MainWindow
{
    private readonly ListBox mapFileList = new() { Name = "MapFileList", BorderThickness = new Thickness(0), Background = Brushes.Transparent };
    private readonly TextBox mapFileSearch = new() { Name = "MapFileSearch", Watermark = LocalText.T("Search map filenames…"), Height = 28, Padding = new Thickness(6, 3), Margin = new Thickness(0) };
    private readonly Button mapFolderButton = new() { Name = "MapFolderButton", Content = LocalText.T("Folder settings…"), FontSize = 11, Padding = new Thickness(7, 4), Margin = new Thickness(0) };
    private readonly Button mapRefreshButton = new() { Name = "MapRefreshButton", Content = LocalText.T("Refresh"), FontSize = 11, Padding = new Thickness(7, 4), Margin = new Thickness(5, 0, 0, 0) };
    private readonly TextBlock mapFolderLabel = new() { Text = LocalText.T("No game folder selected"), FontSize = 10, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 5, 0, 6) };
    private readonly TextBlock mapFileStatus = new() { Text = LocalText.T("Choose a game folder containing Data and Map."), FontSize = 10, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 0) };
    private IReadOnlyList<MapFileEntry> mapFileEntries = [];
    private CancellationTokenSource? mapIndexCancellation;
    private bool mapIndexLoading;

    private Control CreateMapBrowser()
    {
        var panel = new Grid { Margin = new Thickness(6), RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*,Auto") };
        var toolbar = new WrapPanel { Orientation = Orientation.Horizontal, Children = { mapFolderButton, mapRefreshButton } };
        panel.Children.Add(toolbar);
        Grid.SetRow(mapFolderLabel, 1); panel.Children.Add(mapFolderLabel);
        Grid.SetRow(mapFileSearch, 2); panel.Children.Add(mapFileSearch);
        Grid.SetRow(mapFileList, 3); panel.Children.Add(mapFileList);
        Grid.SetRow(mapFileStatus, 4); panel.Children.Add(mapFileStatus);
        mapFolderLabel.SetResource(TextBlock.ForegroundProperty, "Muted");
        mapFileStatus.SetResource(TextBlock.ForegroundProperty, "Muted");
        mapFileList.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        mapFileList.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
        mapFileList.ItemContainerTheme = new ControlTheme(typeof(ListBoxItem))
        {
            BasedOn = (ControlTheme)this.FindResource("LayerRow"),
            Setters = { new Setter(Layoutable.HeightProperty, 22.0), new Setter(Layoutable.MinHeightProperty, 22.0) }
        };
        mapFileList.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<MapFileEntry>((entry, _) =>
        {
            var label = new TextBlock { Text = entry?.RelativePath, FontSize = 11, Margin = new Thickness(3, 0), TextTrimming = TextTrimming.CharacterEllipsis };
            label.SetTip(entry?.FilePath);
            return label;
        });
        Avalonia.Automation.AutomationProperties.SetName(mapFileList, LocalText.T("Map file list"));
        mapFolderButton.SetTip(LocalText.T("Select a game folder containing Data and Map"));
        mapFolderButton.Click += (_, e) => SettingsClick(mapFolderButton, e);
        mapRefreshButton.Click += async (_, _) => await RefreshMapBrowserAsync(settings.LibraryFolder);
        mapFileSearch.TextChanged += (_, _) => FilterMapFiles();
        // Tapped 同时覆盖首次选择和再次点击已选地图；OpenMap 负责复用已有标签。
        mapFileList.Tapped += async (_, e) =>
        {
            if (e.Source is not Visual source) return;
            var item = source as ListBoxItem ?? source.GetVisualAncestors().OfType<ListBoxItem>().FirstOrDefault();
            if (item?.DataContext is MapFileEntry entry) await OpenBrowserMapAsync(entry);
        };
        mapFileList.KeyDown += async (_, e) =>
        {
            if (e.Key != Key.Enter || mapFileList.SelectedItem is not MapFileEntry entry) return;
            e.Handled = true;
            await OpenBrowserMapAsync(entry);
        };
        Closed += (_, _) => { mapIndexCancellation?.Cancel(); mapIndexCancellation = null; };
        UpdateMapBrowserControls();
        return panel;
    }

    private async Task OpenBrowserMapAsync(MapFileEntry entry)
    {
        if (busy || mapIndexLoading) return;
        await Run(() => OpenMap(entry.FilePath));
    }

    private async Task RefreshMapBrowserAsync(string? selectedFolder)
    {
        mapIndexCancellation?.Cancel();
        var request = mapIndexCancellation = new CancellationTokenSource();
        var token = request.Token;
        mapIndexLoading = true; mapFileEntries = [];
        mapFileList.ItemsSource = mapFileEntries;
        mapFileStatus.Text = LocalText.T("Scanning maps…"); mapFileStatus.SetTip(null);
        UpdateMapBrowserControls();
        try
        {
            string? folder = string.IsNullOrWhiteSpace(selectedFolder) ? null : DataPaths.FindMapFolder(selectedFolder);
            mapFolderLabel.Text = folder ?? LocalText.T("Map folder not found"); mapFolderLabel.SetTip(folder);
            if (folder == null) { mapFileStatus.Text = LocalText.T("Choose a game folder containing Data and Map."); return; }
            var entries = await Task.Run(() => MapFileCatalog.Read(folder, token), token);
            if (closed || token.IsCancellationRequested) return;
            mapFileEntries = entries;
            FilterMapFiles();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            StudioLog.Error("MapBrowser.Scan",ex,$"Root={selectedFolder}");
            if (!closed && !token.IsCancellationRequested)
            {
                mapFileStatus.Text = LocalText.T("Could not read map folder"); mapFileStatus.SetTip(ex.Message);
            }
        }
        finally
        {
            if (!closed && ReferenceEquals(mapIndexCancellation, request))
            {
                mapIndexCancellation = null; mapIndexLoading = false; UpdateMapBrowserControls();
            }
            request.Dispose();
        }
    }

    private void FilterMapFiles()
    {
        string query = mapFileSearch.Text?.Trim() ?? "";
        string? selected = (mapFileList.SelectedItem as MapFileEntry)?.FilePath;
        var filtered = mapFileEntries.Where(entry => Path.GetFileName(entry.FilePath).Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        mapFileList.ItemsSource = filtered;
        mapFileList.SelectedItem = filtered.FirstOrDefault(entry => string.Equals(entry.FilePath, selected, StringComparison.OrdinalIgnoreCase));
        mapFileStatus.Text = mapFileEntries.Count == 0 ? LocalText.T("No .map files in the Map folder.") :
            query.Length == 0 ? LocalText.Format("{0:N0} maps", mapFileEntries.Count) : LocalText.Format("{0:N0} / {1:N0} maps", filtered.Length, mapFileEntries.Count);
    }

    private void UpdateMapBrowserControls()
    {
        mapFolderButton.IsEnabled = !busy;
        mapRefreshButton.IsEnabled = !busy && !mapIndexLoading && !string.IsNullOrWhiteSpace(settings.LibraryFolder);
        mapFileSearch.IsEnabled = mapFileList.IsEnabled = !busy && !mapIndexLoading;
    }
}
