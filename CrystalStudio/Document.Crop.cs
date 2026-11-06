namespace CrystalStudio;

public sealed partial class MapDocument
{
    internal MapDocument Crop(Rect selection)
    {
        var area = AlignSelection(selection);
        int left = (int)area.X, top = (int)area.Y, right = (int)area.Right, bottom = (int)area.Bottom;
        var result = new MapDocument((int)area.Width, (int)area.Height) { SourceFormat = SourceFormat };
        foreach (var pair in cells)
        {
            int x = pair.Key % Width, y = pair.Key / Width;
            if (Inside(x, y)) result.Set(x - left, y - top, pair.Value.Copy());
        }
        // Partial objects retain their visible cells, without ownership metadata
        // that could subsequently restore content outside the cropped map.
        result.RestoreStamps(placedStamps.Where(s => s.Cells.All(c => Inside(s.X + c.X, s.Y + c.Y)) &&
            s.Changes.All(c => Inside(s.X + c.X, s.Y + c.Y)))
            .Select(s => s with { X = s.X - left, Y = s.Y - top }));
        return result;

        bool Inside(int x, int y) => x >= left && x < right && y >= top && y < bottom;
    }

    internal Rect AlignSelection(Rect selection)
    {
        int left = Math.Max(0, (int)Math.Floor(selection.Left)) & ~1;
        int top = Math.Max(0, (int)Math.Floor(selection.Top)) & ~1;
        int right = Math.Min(Width, ((int)Math.Ceiling(selection.Right) + 1) & ~1);
        int bottom = Math.Min(Height, ((int)Math.Ceiling(selection.Bottom) + 1) & ~1);
        if (left >= right || top >= bottom) throw new ArgumentException("Select an area inside the map before cropping.");
        return new(left, top, right - left, bottom - top);
    }

    internal void ReplaceContents(MapDocument replacement)
    {
        Width = replacement.Width; Height = replacement.Height; SourceFormat = replacement.SourceFormat;
        cells.Clear(); presentationCells.Clear(); Array.Clear(layerCounts);
        foreach (var pair in replacement.Cells) Set(pair.Key % Width, pair.Key / Width, pair.Value.Copy());
        RestoreStamps(replacement.PlacedStamps);
        MetadataWarning = replacement.MetadataWarning;
        PickupRevision++;
    }
}
