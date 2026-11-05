using System.IO;

namespace CrystalStudio;

public partial class MainWindow
{
    private MapCreationStore creationStore = null!;
    private void InitializeMapCreation()
    {
        creationStore = new(Path.Combine(Path.GetDirectoryName(settingsStore.FilePath)!, "map-creation.json"));
        InitializeRecovery(NewButton.Parent as MenuItem);
        try { creationStore.Load(); }
        catch (Exception exception) { StudioLog.Error("MapCreation.Preferences", exception); }
        if (NewButton.Parent is not MenuItem menu) return;
        var quick = new MenuItem { Name = "QuickCreateMap", Header = LocalText.T("Quick Create Map…"), InputGesture = new KeyGesture(Key.N, KeyModifiers.Control | KeyModifiers.Alt) };
        quick.Click += QuickCreateClick; menu.Items.Insert(1, quick);
    }

    private async void QuickCreateClick(object? sender, RoutedEventArgs args)
    {
        if (busy) return;
        var availability = Enum.GetValues<MapCreationKind>().ToDictionary(kind => kind, kind => kind == MapCreationKind.Blank || assets != null
            ? (string?)null : "Load game libraries in Settings before generating a map.");
        foreach (var kind in new[] { MapCreationKind.Terrain, MapCreationKind.Province, MapCreationKind.Dungeon })
        {
            if (availability[kind] != null) continue;
            bool missing = !File.Exists(DataPaths.ResolveEditor("catalogue", "terrain.json")) ||
                kind != MapCreationKind.Terrain && !File.Exists(WallCatalogue.ResolvePath()) ||
                kind == MapCreationKind.Province && StampCategories.FindIndex() == null;
            if (missing) availability[kind] = "Required generator catalogues are missing. Restore catalogue and Objects beside the editor.";
        }
        var dialog = new NewMapWindow(MapCreationPaths.DefaultDirectory(settings.LibraryFolder), availability, creationStore.Data.Last, creationStore) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result is not { } request) return;
        await Run(async () =>
        {
            MapDocument? document = new(request.Width, request.Height) { SourceFormat = settings.AssetFormat };
            var parameters = dialog.SelectedParameters ?? creationStore.Parameters(request.Kind);
            if (request.Kind == MapCreationKind.Terrain)
            {
                var catalogue = await Task.Run(() => TerrainCatalogue.Load());
                var preview = new TerrainGeneratorWindow(document, assets!, catalogue, parameters) { Owner = this };
                if (preview.ShowDialog() != true || preview.Plan == null) return;
                parameters = GeneratorPreferences.Capture(preview);
                var staging = new MapViewport();
                try { staging.SetDocument(document); staging.ApplyTerrain(preview.Plan); }
                finally { staging.StopBackgroundWork(); }
            }
            else if (request.Kind == MapCreationKind.Province)
            {
                var preview = new ProvinceGeneratorWindow(document, assets!, wallFamilies.Count > 0 ? new WallCatalogue(1, wallFamilies.ToList()) : null, parameters) { Owner = this };
                if (preview.ShowDialog() != true || preview.Plan == null) return;
                document = preview.Plan.Generated;
                parameters = GeneratorPreferences.Capture(preview);
            }
            else if (request.Kind == MapCreationKind.Dungeon)
            {
                var definitions = await Task.Run(() => (Terrain: TerrainCatalogue.Load(), Walls: wallFamilies.Count > 0
                    ? new WallCatalogue(1, wallFamilies.ToList()) : WallCatalogue.Read(WallCatalogue.ResolvePath())));
                var preview = new TempleGeneratorWindow(document, assets!, definitions.Walls, definitions.Terrain, parameters) { Owner = this };
                if (preview.ShowDialog() != true || preview.Plan == null) return;
                document = preview.Plan.Generated;
                parameters = GeneratorPreferences.Capture(preview);
            }
            document.SourceFormat = settings.AssetFormat;
            AddMapTab(document);
            activeMap!.Name = request.Name; activeMap.KeepSourceFormat = true;
            RememberGeneration(request.Kind, parameters);
            try
            {
                creationStore.Data.Last = request;
                if (!string.IsNullOrWhiteSpace(dialog.PresetNameBox.Text)) creationStore.SavePreset(dialog.PresetNameBox.Text, request, parameters);
                else creationStore.Save();
            }
            catch (Exception exception) { StudioLog.Error("MapCreation.SavePreferences", exception); }
            Viewport.History.MarkUnsaved(); UpdateMapTabLabels(); UpdateLabels();
            if (request.SaveImmediately)
            {
                string target = MapCreationPaths.Target(request.Directory!, request.Name);
                if (File.Exists(target) || Directory.Exists(target))
                { Status.Text = LocalText.T("The output path now exists. The map remains unsaved; use Save As."); return; }
                SaveMapTo(target, false);
            }
            else Status.Text = LocalText.T("New map created");
        });
    }

    private void RememberGeneration(MapCreationKind kind, EditorWindow window) => RememberGeneration(kind, GeneratorPreferences.Capture(window));
    private void RememberGeneration(MapCreationKind kind, IReadOnlyDictionary<string, string> parameters)
    {
        try
        {
            if (activeMap != null) activeMap.Recipe = MapCreationStore.Recipe(Viewport.Document, kind, parameters, settings, assets) with
                { AssetFormat = mapPath == null && !activeMap.KeepSourceFormat ? settings.AssetFormat : Viewport.Document.SourceFormat };
            creationStore.SetParameters(kind, parameters); creationStore.Save();
        }
        catch (Exception exception) { StudioLog.Error("MapCreation.Recipe", exception); }
    }

    private async Task RefreshSavedMapAsync(string target)
    {
        try
        {
            await RefreshMapBrowserAsync(settings.LibraryFolder);
            if (closed) return;
            var entry = mapFileEntries.FirstOrDefault(item => string.Equals(item.FilePath, Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase));
            if (entry == null) return;
            if (!mapFileList.Items.Contains(entry)) mapFileSearch.Text = "";
            mapFileList.SelectedItem = entry; mapFileList.ScrollIntoView(entry);
        }
        catch (Exception exception) { StudioLog.Error("MapBrowser.SavedMap", exception, $"Path={target}"); }
    }
}
