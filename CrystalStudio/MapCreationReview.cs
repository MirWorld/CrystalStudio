namespace CrystalStudio;

internal sealed record MapCreationCellDifference(int X, int Y, bool Ground, bool Decoration, bool Collision)
{
    public override string ToString() => $"({X}, {Y}) · " + string.Join(" / ", new[] { Ground ? LocalText.T("Ground") : null,
        Decoration ? LocalText.T("Decoration") : null, Collision ? LocalText.T("Collision") : null }.Where(value => value != null));
}
internal sealed record MapCreationDifference(int GroundCells, int DecorationCells, int CollisionCells, int ChangedCells, IReadOnlyList<MapCreationCellDifference> Samples);

internal sealed class MapCreationReview
{
    internal MapDocument Baseline { get; }
    internal MapCreationRecipe? Recipe { get; }
    internal DateTime CreatedUtc { get; } = DateTime.UtcNow;
    internal long CheckedRevision { get; private set; }
    internal MapDesignSettings CheckedDesign { get; private set; }
    internal MapConstraintResult Check { get; private set; }
    private int checkedWidth, checkedHeight;
    private AssetSourceFormat checkedFormat;
    internal MapCreationReview(MapDocument map, MapDesignSettings design, MapCreationRecipe? recipe)
    {
        Baseline = MapRecoveryStore.Capture(map); Recipe = recipe;
        CheckedRevision = map.PickupRevision; CheckedDesign = design.Copy(); Check = MapDesignConstraints.Check(Baseline, CheckedDesign);
        checkedWidth = map.Width; checkedHeight = map.Height; checkedFormat = map.SourceFormat;
    }
    internal bool Current(MapDocument map, MapDesignSettings design) => map.Width == checkedWidth && map.Height == checkedHeight &&
        map.SourceFormat == checkedFormat && map.PickupRevision == CheckedRevision && CheckedDesign.SameAs(design);
    internal void RecordCheck(MapDocument map, MapDesignSettings design, MapConstraintResult check)
    { CheckedRevision = map.PickupRevision; CheckedDesign = design.Copy(); Check = check; checkedWidth = map.Width; checkedHeight = map.Height; checkedFormat = map.SourceFormat; }

    internal MapCreationDifference Compare(MapDocument current, CancellationToken cancellation = default)
    {
        if (current.Width != Baseline.Width || current.Height != Baseline.Height || current.SourceFormat != Baseline.SourceFormat)
            throw new InvalidOperationException(LocalText.T("The map dimensions or source format changed; cell-by-cell creation comparison is unavailable."));
        int ground = 0, decoration = 0, collision = 0, changed = 0; var samples = new List<MapCreationCellDifference>();
        foreach (int index in Baseline.Cells.Select(pair => pair.Key).Union(current.Cells.Select(pair => pair.Key)).Order())
        {
            cancellation.ThrowIfCancellationRequested(); int column = index % current.Width, row = index / current.Width;
            var before = Baseline.Get(column, row); var after = current.Get(column, row);
            if (before.SameContents(after)) continue;
            bool groundChanged = before.BackLibrary != after.BackLibrary || (before.BackImage & 0x9FFFFFFF) != (after.BackImage & 0x9FFFFFFF) ||
                before.TileAnimation != after.TileAnimation || before.TileOffset != after.TileOffset || before.TileFrames != after.TileFrames;
            bool collisionChanged = (before.BackImage & 0x60000000) != (after.BackImage & 0x60000000);
            bool decorationChanged = before.MiddleLibrary != after.MiddleLibrary || before.MiddleImage != after.MiddleImage || before.MiddleFrames != after.MiddleFrames ||
                before.MiddleTick != after.MiddleTick || before.Foreground != after.Foreground || before.Door != after.Door || before.DoorOffset != after.DoorOffset || before.Light != after.Light;
            if (groundChanged) ground++; if (decorationChanged) decoration++; if (collisionChanged) collision++; changed++;
            if (samples.Count < 1000) samples.Add(new(column, row, groundChanged, decorationChanged, collisionChanged));
        }
        return new(ground, decoration, collision, changed, samples);
    }
}
