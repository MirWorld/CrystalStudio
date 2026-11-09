using System.IO;
using Avalonia.LogicalTree;

namespace CrystalStudio;

public partial class MainWindow
{
    private void InitializeMapProduction()
    {
        if (GenerateTerrainButton.Parent is MenuItem tools)
        {
            tools.Items.Add(new Separator());
            var production = new MenuItem { Name = "MapProductionTools", Header = LocalText.T("Map production tools…") };
            production.Click += MapProductionClick; tools.Items.Add(production);
        }
        if (ExportMinimapButton.Parent is MenuItem file)
        {
            var export = new MenuItem { Name = "ExportDeliveryPackage", Header = LocalText.T("Export map delivery package…") };
            export.Click += async (_, _) => { if (!busy) await Run(() => ExportDeliveryAsync(this, CancellationToken.None)); };
            file.Items.Insert(file.Items.IndexOf(ExportMinimapButton) + 1, export);
        }
    }

    private async void MapProductionClick(object? sender, RoutedEventArgs args)
    {
        if (busy || activeMap == null || activeMap.IsStamp) return;
        try
        {
            var map = activeMap; var document = Viewport.Document;
            var dialog = new MapProductionWindow(document, assets, map.Design, map.Recipe, recipe =>
            {
                MapRecipeRestore.Validate(recipe);
                return MapRecipeRestore.Differences(recipe, MapCreationStore.Recipe(document, recipe.Kind, recipe.Parameters, settings, assets));
            }, () =>
            {
                Viewport.History.MarkUnsaved(); map.RecoveryRevision = -1; map.RecoveryVersion++; UpdateMapTabLabels(); UpdateLabels();
            }, (column, row) => Viewport.LocateCell(column, row), () => Viewport.Selection, RegenerateSelectionAsync, ExportDeliveryAsync) { Owner = this };
            if (dialog.ShowDialog() == true && dialog.SelectedRecipe is { } restored) await QuickCreateAsync(restored);
        }
        catch (Exception exception) { StudioLog.Error("MapProduction", exception); MessageBox.Show(this, exception.Message, "Map production tools"); }
    }

    private async Task<EditorWindow> ProductionGeneratorAsync(MapDocument source, MapCreationKind kind, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellation)
    {
        var loadedAssets = assets ?? throw new InvalidOperationException(LocalText.T("Load game libraries in Settings before generating a map."));
        if (MapDimensions.CreationError(source.Width, source.Height, kind) is { } error) throw new InvalidOperationException(LocalText.T(error));
        if (kind == MapCreationKind.Province) return new ProvinceGeneratorWindow(source, loadedAssets, wallFamilies.Count > 0 ? new WallCatalogue(1, wallFamilies.ToList()) : null, parameters);
        var definitions = await Task.Run(() => (Terrain: TerrainCatalogue.Load(format: loadedAssets.Format), Walls: kind == MapCreationKind.Dungeon
            ? wallFamilies.Count > 0 ? new WallCatalogue(1, wallFamilies.ToList()) : WallCatalogue.Read(WallCatalogue.ResolvePath(), loadedAssets.Format) : null), cancellation);
        cancellation.ThrowIfCancellationRequested();
        return kind == MapCreationKind.Terrain ? new TerrainGeneratorWindow(source, loadedAssets, definitions.Terrain, parameters)
            : new TempleGeneratorWindow(source, loadedAssets, definitions.Walls!, definitions.Terrain, parameters);
    }

    private async Task<string?> RegenerateSelectionAsync(EditorWindow owner, MapCreationKind kind, PartialGenerationLayer layer, CancellationToken cancellation)
    {
        if (layer == PartialGenerationLayer.Decoration && kind == MapCreationKind.Terrain)
            throw new InvalidOperationException(LocalText.T("Choose a province or dungeon layout for complete decoration stamps. Terrain banks and coasts are protected, not clipped."));
        if (Viewport.AnyLayerLocked) throw new InvalidOperationException(LocalText.T("Unlock all layers before editing multiple layers."));
        var map = activeMap ?? throw new InvalidOperationException("No map is open.");
        var source = Viewport.Document; long revision = source.PickupRevision;
        var area = Viewport.Selection ?? throw new InvalidOperationException(LocalText.T("Select an area on the map first."));
        var design = map.Design.Copy(); design.Validate(source.Width, source.Height);
        var draft = MapRecoveryStore.Capture(source);
        var parameters = map.Recipe?.Kind == kind ? new Dictionary<string, string>(map.Recipe.Parameters) : creationStore.Parameters(kind);
        var generator = await ProductionGeneratorAsync(draft, kind, parameters, cancellation); generator.Owner = owner;
        var apply = generator.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "ApplyButton");
        apply.Content = LocalText.T("Review local changes");
        if (generator.GetLogicalDescendants().OfType<TextBlock>().FirstOrDefault(text => text.Name == "PreviewMapInfo") is { } info)
            info.Text = LocalText.T("Source layout only. The next step reviews the protected selected-area composite; this preview does not modify the working map.");
        cancellation.ThrowIfCancellationRequested();
        if (generator.ShowDialog() != true || QuickMapProduction.CurrentPlan(generator) is not { } generated) return null;
        cancellation.ThrowIfCancellationRequested();
        if (activeMap != map || Viewport.Document != source || source.PickupRevision != revision || !design.SameAs(map.Design))
            throw new InvalidOperationException(LocalText.T("The map or design markers changed after the preview. Generate a fresh preview."));
        var plan = await Task.Run(() => PartialMapGeneration.Plan(source, QuickMapProduction.Materialize(generated, cancellation), area, design, layer, cancellation), cancellation);
        if (plan.Changed.Count == 0) return LocalText.T("No safe local changes were found. Nothing was applied; try another layout or selection.");
        var loadedAssets = assets!;
        var images = await Task.Run(() => PartialMapGeneration.CreatePreviewAsync(plan, loadedAssets, cancellation), cancellation);
        using var beforeImage = images.Before; using var afterImage = images.After;
        cancellation.ThrowIfCancellationRequested();
        var review = new PartialGenerationReviewWindow(plan, beforeImage, afterImage) { Owner = owner };
        if (review.ShowDialog() != true) return null;
        cancellation.ThrowIfCancellationRequested();
        if (activeMap != map || Viewport.Document != source || source.PickupRevision != revision || !design.SameAs(map.Design) || !ReferenceEquals(assets, loadedAssets))
            throw new InvalidOperationException(LocalText.T("The map or design markers changed after the preview. Generate a fresh preview."));
        Viewport.ApplyPartialGeneration(plan);
        Status.Text = LocalText.Format("Applied {0:N0} local changes · Ctrl+Z to undo", plan.Changed.Count);
        return Status.Text;
    }

    private async Task<string?> ExportDeliveryAsync(EditorWindow owner, CancellationToken cancellation)
    {
        if (activeMap?.IsStamp == true) throw new InvalidOperationException(LocalText.T("Open a map, not an object stamp, to export a delivery package."));
        var picker = new SaveFileDialog { Title = "Export map delivery package", Filter = "Map delivery archive (*.zip)|*.zip",
            FileName = Path.GetFileNameWithoutExtension(mapPath ?? activeMap?.Name ?? "Untitled") + "-delivery.zip",
            DefaultExt = ".zip", AddExtension = true, OverwritePrompt = true, InitialDirectory = mapPath == null ? "" : Path.GetDirectoryName(mapPath) };
        if (picker.ShowDialog(owner) != true) return null;
        if (!string.Equals(Path.GetExtension(picker.FileName), ".zip", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(LocalText.T("Choose a .zip filename for the delivery package."));
        var snapshot = MapRecoveryStore.Capture(Viewport.Document); var loadedAssets = assets;
        var design = activeMap?.Design.Copy() ?? new(); var recipe = activeMap?.Recipe;
        bool unsaved = Viewport.History.Dirty || mapPath == null;
        if (unsaved && MessageBox.Show(owner, "The package will include current unsaved edits, not the last saved file. Continue?", "Export map delivery package", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return null;
        Status.Text = LocalText.T("Exporting delivery package…");
        await Task.Run(() => MapDeliveryPackage.ExportAsync(picker.FileName, snapshot, loadedAssets, recipe, design, unsaved, cancellation), cancellation);
        if (!closed) Status.Text = LocalText.Format("Exported {0}", Path.GetFileName(picker.FileName));
        return LocalText.Format("Exported {0}", Path.GetFileName(picker.FileName));
    }
}
