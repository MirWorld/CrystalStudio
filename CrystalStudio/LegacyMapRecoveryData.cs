using System.IO;

namespace CrystalStudio;

internal sealed record MapRecoveryData(Guid Id, string Name, string? OriginalPath, DateTime SavedUtc,
    int Width, int Height, AssetSourceFormat Format, List<StampCell> Cells, List<PlacedStamp> Stamps,
    bool IsStamp = false, int OriginX = 0, int OriginY = 0, CategorizedStamp? Category = null, bool CategoryDirty = false)
{
    internal MapDocument Restore()
    {
        if (Cells == null || Stamps == null || !MapDimensions.CanRoundTrip(Width, Height) || Cells.Count > (long)Width * Height)
            throw new InvalidDataException("Invalid recovery dimensions.");
        var document = new MapDocument(Width, Height) { SourceFormat = Format };
        var seen = new HashSet<int>();
        foreach (var cell in Cells)
        {
            if (!document.Contains(cell.X, cell.Y) || !seen.Add(cell.Y * Width + cell.X))
                throw new InvalidDataException("Invalid recovery cell coordinates.");
            document.Set(cell.X, cell.Y, cell.Cell.Copy());
        }
        if (Stamps.Count > 100000 || Stamps.Any(s => s.Changes.Any(c =>
            (long)s.X + c.X < 0 || (long)s.Y + c.Y < 0 || (long)s.X + c.X >= Width || (long)s.Y + c.Y >= Height)))
            throw new InvalidDataException("Invalid recovery stamp coordinates.");
        document.RestoreStamps(Stamps);
        return document;
    }
}
