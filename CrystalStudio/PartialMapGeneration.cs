namespace CrystalStudio;

internal enum PartialGenerationLayer { Ground, Decoration }
internal sealed record PartialMapPlan(MapDocument Source, long Revision, MapDocument Generated,
    IReadOnlyList<int> Changed, int ProtectedCells, int SkippedStamps, Rect Area, PartialGenerationLayer Layer);

internal static class PartialMapGeneration
{
    internal static async Task<(RasterImage Before, RasterImage After)> CreatePreviewAsync(PartialMapPlan plan, AssetStore assets, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        EnsureCurrent();
        var before = await MinimapExporter.CreateAsync(plan.Source, assets, 1024, 768).ConfigureAwait(false);
        RasterImage? after = null;
        try
        {
            cancellation.ThrowIfCancellationRequested();
            EnsureCurrent();
            after = await MinimapExporter.CreateAsync(plan.Generated, assets, 1024, 768).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested(); EnsureCurrent(); return (before, after);
        }
        catch { before.Dispose(); after?.Dispose(); throw; }

        void EnsureCurrent()
        {
            if (plan.Source.PickupRevision != plan.Revision)
                throw new InvalidOperationException(LocalText.T("The map changed after the preview. Generate a fresh preview."));
        }
    }

    internal static PartialMapPlan Plan(MapDocument source, MapDocument generated, Rect selection,
        MapDesignSettings design, PartialGenerationLayer layer, CancellationToken cancellation = default)
    {
        if (source.Width != generated.Width || source.Height != generated.Height || source.SourceFormat != generated.SourceFormat)
            throw new InvalidOperationException("Generated map dimensions or source format do not match.");
        design.Validate(source.Width, source.Height);
        long revision = source.PickupRevision;
        if (!Enum.IsDefined(layer)) throw new ArgumentOutOfRangeException(nameof(layer));
        var area = source.AlignSelection(selection);
        int count = checked(source.Width * source.Height); var writable = new bool[count];
        var protectedRegions = design.Regions.Where(region => region.Purpose == MapRegionPurpose.Protect).ToArray();
        int protectedCells = 0;
        for (int row = (int)area.Top; row < area.Bottom; row++) for (int column = (int)area.Left; column < area.Right; column++)
        {
            if ((column & 1023) == 0) cancellation.ThrowIfCancellationRequested();
            bool border = column < area.Left + 2 || column >= area.Right - 2 || row < area.Top + 2 || row >= area.Bottom - 2;
            bool protectedCell = border || protectedRegions.Any(region => region.Contains(column, row));
            if (protectedCell) protectedCells++;
            else writable[row * source.Width + column] = true;
        }
        foreach (var stamp in source.PlacedStamps) Protect(stamp);
        var accepted = new List<PlacedStamp>(); var claimed = new HashSet<int>(); int skipped = 0;
        foreach (var stamp in generated.PlacedStamps)
        {
            bool supported = layer == PartialGenerationLayer.Decoration && stamp.Wall == null && stamp.GeneratedBanks == null &&
                stamp.Changes.Count > 0 && stamp.Changes.All(change =>
                    source.Contains(stamp.X + change.X, stamp.Y + change.Y) && writable[(stamp.Y + change.Y) * source.Width + stamp.X + change.X] &&
                    SameGround(change.Before, change.After) && !claimed.Contains((stamp.Y + change.Y) * source.Width + stamp.X + change.X) &&
                    EmptyDecoration(source.Get(stamp.X + change.X, stamp.Y + change.Y)));
            if (supported) { accepted.Add(stamp); foreach (var change in stamp.Changes) claimed.Add((stamp.Y + change.Y) * source.Width + stamp.X + change.X); }
            else { Protect(stamp); skipped++; }
        }
        bool removed;
        do
        {
            removed = false;
            foreach (var stamp in accepted.ToArray())
                if (stamp.Changes.Any(change => !writable[(stamp.Y + change.Y) * source.Width + stamp.X + change.X]))
                { accepted.Remove(stamp); Protect(stamp); skipped++; removed = true; }
        } while (removed);
        if (layer == PartialGenerationLayer.Ground)
        {
            for (int row = 0; row < source.Height; row += 2) for (int column = 0; column < source.Width; column += 2)
            {
                bool complete = true;
                for (int offsetY = 0; offsetY < 2; offsetY++) for (int offsetX = 0; offsetX < 2; offsetX++)
                    if (source.Contains(column + offsetX, row + offsetY) && !writable[(row + offsetY) * source.Width + column + offsetX]) complete = false;
                if (!complete) writable[row * source.Width + column] = false;
            }
        }
        var output = MapRecoveryStore.Capture(source); var changed = new List<int>();
        var ownedDecoration = accepted.SelectMany(stamp => stamp.Changes.Select(change => (stamp.Y + change.Y) * source.Width + stamp.X + change.X)).ToHashSet();
        foreach (var pair in generated.Cells)
        {
            cancellation.ThrowIfCancellationRequested();
            int column = pair.Key % source.Width, row = pair.Key / source.Width;
            if (!writable[pair.Key]) continue;
            var before = source.Get(column, row); var replacement = before.Copy(); var next = pair.Value;
            if (layer == PartialGenerationLayer.Ground)
            {
                if ((column & 1) != 0 || (row & 1) != 0) continue;
                replacement.BackLibrary = next.BackLibrary;
                replacement.BackImage = (before.BackImage & unchecked((int)0xE0000000)) | (next.BackImage & 0x1FFFFFFF);
                replacement.TileAnimation = next.TileAnimation; replacement.TileOffset = next.TileOffset; replacement.TileFrames = next.TileFrames;
            }
            else
            {
                if (!ownedDecoration.Contains(pair.Key) || before.HasForeground || before.MiddleImage != 0 || before.Door != 0 || before.Light != 0) continue;
                CopyDecoration(replacement, next);
            }
            if (before.SameContents(replacement)) continue;
            output.Set(column, row, replacement); changed.Add(pair.Key);
        }
        if (layer == PartialGenerationLayer.Decoration)
        {
            var stamps = source.PlacedStamps.ToList();
            foreach (var stamp in accepted)
            {
                bool complete = stamp.Changes.All(change =>
                {
                    var cell = source.Get(stamp.X + change.X, stamp.Y + change.Y);
                    return !cell.HasForeground && cell.MiddleImage == 0 && cell.Door == 0 && cell.Light == 0;
                });
                if (!complete)
                {
                    foreach (var change in stamp.Changes)
                    {
                        int column = stamp.X + change.X, row = stamp.Y + change.Y;
                        output.Set(column, row, source.Get(column, row).Copy()); changed.Remove(row * source.Width + column);
                    }
                    skipped++; continue;
                }
                var deltas = stamp.Changes.Select(change =>
                {
                    int column = stamp.X + change.X, row = stamp.Y + change.Y;
                    var after = output.Get(column, row).Copy();
                    return new StampDelta(change.X, change.Y, source.Get(column, row).Copy(), after,
                        after.Foreground == null ? null : [after.Foreground], after.Foreground == null ? null : [0]);
                }).ToList();
                if (deltas.All(change => change.Before.SameContents(change.After))) continue;
                stamps.Add(new(Guid.NewGuid(), stamp.Name, stamp.X, stamp.Y,
                    deltas.Select(change =>
                    {
                        var decoration = new Cell(); CopyDecoration(decoration, change.After);
                        return new StampCell(change.X, change.Y, decoration);
                    }).ToList(), deltas));
            }
            output.RestoreStamps(stamps);
        }
        if (output.PlacedStamps.Count > 100000) throw new InvalidOperationException("Too many stamp instances.");
        if (source.PickupRevision != revision) throw new InvalidOperationException(LocalText.T("The map changed after the preview. Generate a fresh preview."));
        return new(source, revision, output, changed, protectedCells, skipped, area, layer);

        void Protect(PlacedStamp stamp)
        {
            foreach (var cell in stamp.Cells) Block(stamp.X + cell.X, stamp.Y + cell.Y);
            foreach (var change in stamp.Changes) Block(stamp.X + change.X, stamp.Y + change.Y);
        }
        void Block(int column, int row)
        {
            if (!source.Contains(column, row)) return;
            int index = row * source.Width + column;
            if (writable[index]) { writable[index] = false; protectedCells++; }
        }
    }

    private static bool SameGround(Cell before, Cell after) => before.BackImage == after.BackImage && before.BackLibrary == after.BackLibrary &&
        before.TileAnimation == after.TileAnimation && before.TileOffset == after.TileOffset && before.TileFrames == after.TileFrames;

    private static bool EmptyDecoration(Cell cell) => !cell.HasForeground && cell.MiddleImage == 0 && cell.Door == 0 && cell.Light == 0;

    private static void CopyDecoration(Cell target, Cell source)
    {
        target.MiddleLibrary = source.MiddleLibrary; target.MiddleImage = source.MiddleImage;
        target.MiddleFrames = source.MiddleFrames; target.MiddleTick = source.MiddleTick;
        target.Foreground = source.Foreground; target.Door = source.Door; target.DoorOffset = source.DoorOffset; target.Light = source.Light;
    }
}
