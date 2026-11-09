using System.IO;

namespace CrystalStudio;

internal static class MapRecipeRestore
{
    internal static MapCreationRecipe Read(string path)
    {
        if (new FileInfo(path).Length > 1_048_576) throw new InvalidDataException("Map recipe is too large.");
        var recipe = StudioJson.Deserialize<MapCreationRecipe>(File.ReadAllText(path)) ?? throw new InvalidDataException("Empty map recipe.");
        Validate(recipe); return recipe;
    }

    internal static void Validate(MapCreationRecipe recipe)
    {
        if (recipe.Version != 1 || recipe.Parameters == null || recipe.CatalogueHashes == null || recipe.LibraryVersions == null ||
            recipe.Parameters.Count > 256 || recipe.Parameters.Any(pair => pair.Key.Length > 128 || pair.Value == null || pair.Value.Length > 4096) ||
            !Enum.IsDefined(recipe.AssetFormat)) throw new InvalidDataException("Invalid or unsupported map recipe.");
        if (MapDimensions.CreationError(recipe.Width, recipe.Height, recipe.Kind) is { } error)
            throw new InvalidDataException(LocalText.T(error));
        recipe.Design?.Validate(recipe.Width, recipe.Height);
    }

    internal static IReadOnlyList<string> Differences(MapCreationRecipe recipe, MapCreationRecipe current)
    {
        var differences = new List<string>();
        if (recipe.AssetFormat != current.AssetFormat) differences.Add(LocalText.T("The library source format differs from the recipe."));
        if (recipe.GeneratorVersion != current.GeneratorVersion) differences.Add(LocalText.T("The generator version differs from the recipe."));
        foreach (string name in recipe.CatalogueHashes.Keys.Union(current.CatalogueHashes.Keys).Order())
            if (recipe.CatalogueHashes.GetValueOrDefault(name) != current.CatalogueHashes.GetValueOrDefault(name))
                differences.Add(LocalText.Format("Catalogue changed or missing: {0}", name));
        foreach (string library in recipe.LibraryVersions.Keys.Union(current.LibraryVersions.Keys).Order())
            if (recipe.LibraryVersions.GetValueOrDefault(library) != current.LibraryVersions.GetValueOrDefault(library))
                differences.Add(LocalText.Format("Library identity changed or missing: {0}", library));
        return differences;
    }
}

internal static class GeneratedMapSnapshot
{
    internal static MapDocument Terrain(TerrainPlan plan, CancellationToken cancellation = default)
    {
        plan.Source.CheckTerrainBankBoundary(plan.Area);
        var copy = TerrainCoastObjects.WorkingCopy(plan.Source, plan.Area); copy.SourceFormat = plan.Source.SourceFormat;
        var history = new EditHistory();
        foreach (var tile in plan.Tiles)
        {
            cancellation.ThrowIfCancellationRequested();
            var cell = copy.Get(tile.X, tile.Y).Copy(); cell.BackLibrary = checked((short)tile.Library);
            cell.BackImage = (cell.BackImage & unchecked((int)0xE0000000)) | (tile.Index + 1);
            cell.TileAnimation = 0; cell.TileOffset = 0; cell.TileFrames = 0; copy.Set(tile.X, tile.Y, cell);
        }
        var changes = new List<StampDelta>();
        foreach (var bank in plan.Water?.Banks ?? [])
        {
            cancellation.ThrowIfCancellationRequested();
            if (!copy.Contains(bank.X, bank.Y)) throw new InvalidOperationException("A bank anchor is outside the map.");
            var before = copy.Get(bank.X, bank.Y); var after = before.Copy();
            after.Foreground = new(checked((short)bank.Library), checked((short)bank.Image));
            if (before.HasForeground && before.Foreground != after.Foreground) throw new InvalidOperationException("A bank cell is occupied by an existing object. Generate a fresh preview in a clear area.");
            changes.Add(new(bank.X, bank.Y, before.Copy(), after.Copy(), [after.Foreground], [0])); copy.Set(bank.X, bank.Y, after);
        }
        if (plan.Water != null) copy.RememberGeneratedBanks(changes, plan.Water.Catalogue, history);
        foreach (var coast in plan.CoastObjects)
        {
            cancellation.ThrowIfCancellationRequested();
            if (copy.PlaceStamp(coast.Layout.Cells, coast.X, coast.Y, TerrainCoastObjects.StampPrefix + coast.Layout.Name, history, (_, _) => { }) == null)
                throw new InvalidOperationException("Could not materialize a complete coast object.");
        }
        return copy;
    }
}
