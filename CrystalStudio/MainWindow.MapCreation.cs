using System.IO;

namespace CrystalStudio;

public partial class MainWindow
{
    private MapCreationStore creationStore = null!;
    private void InitializeMapCreation()
    {
        creationStore = new(Path.Combine(Path.GetDirectoryName(settingsStore.FilePath)!, "map-creation.json"));
        InitializeRecovery(NewButton.Parent as MenuItem);
        InitializeMapProduction();
        try { creationStore.Load(); }
        catch (Exception exception) { StudioLog.Error("MapCreation.Preferences", exception); }
        if (NewButton.Parent is not MenuItem menu) return;
        var quick = new MenuItem { Name = "QuickCreateMap", Header = LocalText.T("Quick Create Map…"), InputGesture = new KeyGesture(Key.N, KeyModifiers.Control | KeyModifiers.Alt) };
        quick.Click += QuickCreateClick; menu.Items.Insert(1, quick);
    }

    private async void QuickCreateClick(object? sender, RoutedEventArgs args) => await QuickCreateAsync();

    private async Task QuickCreateAsync(MapCreationRecipe? recipe = null)
    {
        if (busy) return;
        var availability = Enum.GetValues<MapCreationKind>().ToDictionary(kind => kind, kind => kind == MapCreationKind.Blank || assets != null
            ? (string?)null : "Load game libraries in Settings before generating a map.");
        foreach (var kind in new[] { MapCreationKind.Terrain, MapCreationKind.Province, MapCreationKind.Dungeon })
        {
            if (availability[kind] != null) continue;
            bool missing = !File.Exists(DataPaths.ResolveEditor("catalogue", "terrain.json")) ||
                kind == MapCreationKind.Dungeon && !File.Exists(WallCatalogue.ResolvePath());
            if (missing) availability[kind] = "Required generator catalogues are missing. Restore catalogue and Objects beside the editor.";
        }
        if (recipe != null) MapRecipeRestore.Validate(recipe);
        var initial = recipe != null ? new MapCreationSettings(recipe.Kind, recipe.Width, recipe.Height, "RecreatedMap", MapCreationPaths.DefaultDirectory(settings.LibraryFolder), false)
            : creationStore.Data.Last ?? new MapCreationSettings(availability.GetValueOrDefault(MapCreationKind.Province) == null ? MapCreationKind.Province : MapCreationKind.Blank);
        var session = new MapCreationSession(initial, recipe?.Parameters ?? creationStore.Parameters(initial.Kind)) { Design = recipe?.Design?.Copy() ?? new() };
        session.RequestedDecorationSeed = recipe?.DecorationSeed;
        session.DecorationNames = recipe?.DecorationNames?.ToList();
        session.DraftStore = new QuickMapDraftStore(Path.Combine(Path.GetDirectoryName(creationStore.FilePath)!, "quick-map-draft.json"));
        session.RecipeFactory = () => MapCreationStore.Recipe(new MapDocument(session.Settings.Width, session.Settings.Height) { SourceFormat = settings.AssetFormat },
            session.Settings.Kind, session.Parameters, settings, assets) with { Design = session.Design.Copy(), DecorationSeed = session.DecorationVariant?.Seed ?? session.RequestedDecorationSeed, DecorationNames = session.DecorationNames?.ToList() };
        using var candidates = session.Candidates;
        bool ConfigureSetup(EditorWindow owner, bool returning)
        {
            var dialog = new NewMapWindow(MapCreationPaths.DefaultDirectory(settings.LibraryFolder), availability,
                session.Settings, creationStore, returning ? session : null, !returning && recipe != null ? session.Parameters : null) { Owner = owner };
            if (!returning && recipe == null && session.DraftStore.Exists) dialog.OfferDraft(() =>
            {
                var saved = session.DraftStore.Load();
                var current = MapCreationStore.Recipe(new MapDocument(saved.Settings.Width, saved.Settings.Height) { SourceFormat = settings.AssetFormat }, saved.Settings.Kind, saved.Recipe.Parameters, settings, assets);
                var differences = MapRecipeRestore.Differences(saved.Recipe, current);
                if (differences.Count > 0 && MessageBox.Show(dialog, string.Join("\n", differences.Take(12)) + "\n\n" +
                    LocalText.T("Artwork or generator versions differ. Restore parameters, markers and favorite recipes only? Old previews, check results and decoration variants will not be restored."),
                    "Resume creation task", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return false;
                QuickMapDraftStore.Restore(saved, session, differences.Count == 0); return true;
            }, session);
            if (dialog.ShowDialog() != true || dialog.Result is not { } result) return false;
            session.Update(result, dialog.SelectedParameters, dialog.PresetNameBox.Text?.Trim() ?? "");
            return true;
        }
        if (!ConfigureSetup(this, false)) return;
        await Run(async () =>
        {
            MapDocument? document = null;
            while (document == null)
            {
                var draft = new MapDocument(session.Settings.Width, session.Settings.Height) { SourceFormat = settings.AssetFormat };
                if (session.Settings.Kind == MapCreationKind.Blank) { document = draft; break; }
                EditorWindow preview;
                if (recipe != null) session.ExpectedRecipeParameters = new Dictionary<string, string>(session.Parameters);
                if (session.Settings.Kind == MapCreationKind.Terrain)
                {
                    var catalogue = await Task.Run(() => TerrainCatalogue.Load(format: settings.AssetFormat));
                    preview = new TerrainGeneratorWindow(draft, assets!, catalogue, session.Parameters);
                }
                else if (session.Settings.Kind == MapCreationKind.Province)
                    preview = new ProvinceGeneratorWindow(draft, assets!, wallFamilies.Count > 0 ? new WallCatalogue(1, wallFamilies.ToList()) : null, session.Parameters);
                else
                {
                    var definitions = await Task.Run(() => (Terrain: TerrainCatalogue.Load(format: settings.AssetFormat), Walls: wallFamilies.Count > 0
                        ? new WallCatalogue(1, wallFamilies.ToList()) : WallCatalogue.Read(WallCatalogue.ResolvePath(), settings.AssetFormat)));
                    preview = new TempleGeneratorWindow(draft, assets!, definitions.Walls, definitions.Terrain, session.Parameters);
                }
                preview.Owner = this;
                var navigation = QuickMapPreview.Configure(preview, session, owner => ConfigureSetup(owner, true), () =>
                {
                    if (session.Settings.SaveImmediately)
                    {
                        string target = MapCreationPaths.Target(session.Settings.Directory!, session.Settings.Name);
                        if (File.Exists(target) || Directory.Exists(target))
                        {
                            MessageBox.Show(preview, "A map with this name already exists. Choose another name.", "Quick Create Map", MessageBoxButton.OK, MessageBoxImage.Warning);
                            return false;
                        }
                    }
                    return string.IsNullOrWhiteSpace(session.PresetName) || !creationStore.Data.Presets.ContainsKey(session.PresetName) ||
                        MessageBox.Show(preview, "Replace the existing preset after creating this map?", "Map preset", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
                });
                bool accepted = preview.ShowDialog() == true;
                if (navigation.RestartRequested) continue;
                if (!accepted) return;
                session.RememberParameters(GeneratorPreferences.Capture(preview));
                if (session.Output is { } chosenOutput) document = MapRecoveryStore.Capture(chosenOutput);
                else if (preview is TerrainGeneratorWindow { Plan: { } terrain })
                {
                    var staging = new MapViewport();
                    try { staging.SetDocument(draft); staging.ApplyTerrain(terrain); }
                    finally { staging.StopBackgroundWork(); }
                    document = draft;
                }
                else if (preview is ProvinceGeneratorWindow { Plan: { } province }) document = province.Generated;
                else if (preview is TempleGeneratorWindow { Plan: { } dungeon }) document = dungeon.Generated;
                else return;
            }
            var request = session.Settings;
            var parameters = session.Parameters;
            document.SourceFormat = settings.AssetFormat;
            AddMapTab(document);
            activeMap!.Name = request.Name; activeMap.KeepSourceFormat = true;
            activeMap.Design = session.Design.Copy();
            RememberGeneration(request.Kind, parameters);
            if (activeMap.Recipe is { } generatedRecipe) activeMap.Recipe = generatedRecipe with { DecorationSeed = session.DecorationVariant?.Seed, DecorationNames = session.DecorationVariant == null ? null : session.DecorationNames?.ToList() };
            activeMap.CreationReview = await Task.Run(() => new MapCreationReview(document, session.Design, activeMap.Recipe));
            try { if (session.DraftSaved) session.DraftStore.Clear(); } catch (Exception exception) { StudioLog.Error("QuickMap.ClearDraft", exception); }
            try
            {
                creationStore.Data.Last = request;
                if (!string.IsNullOrWhiteSpace(session.PresetName)) creationStore.SavePreset(session.PresetName, request, parameters);
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

    private void RememberGeneration(MapCreationKind kind, EditorWindow window) => RememberGeneration(kind, GeneratorPreferences.Capture(window), false);
    private void RememberGeneration(MapCreationKind kind, IReadOnlyDictionary<string, string> parameters, bool includeDesign = true)
    {
        try
        {
            if (activeMap != null) activeMap.Recipe = MapCreationStore.Recipe(Viewport.Document, kind, parameters, settings, assets) with
                { AssetFormat = mapPath == null && !activeMap.KeepSourceFormat ? settings.AssetFormat : Viewport.Document.SourceFormat, Design = includeDesign ? activeMap.Design.Copy() : null };
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
