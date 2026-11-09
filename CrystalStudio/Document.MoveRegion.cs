namespace CrystalStudio;

public sealed partial class MapDocument
{
    internal bool MoveRegion(Rect selection, int dx, int dy, EditHistory history, Action<int, int> changed)
    {
        var source = AlignSelection(selection);
        if ((dx & 1) != 0 || (dy & 1) != 0) throw new ArgumentException("Region moves must align to 2×2 ground tiles.");
        var target = new Rect(source.X + dx, source.Y + dy, source.Width, source.Height);
        if (dx == 0 && dy == 0 || target.Left < 0 || target.Top < 0 || target.Right > Width || target.Bottom > Height) return false;
        bool Inside(Rect area, int x, int y) => x >= area.Left && y >= area.Top && x < area.Right && y < area.Bottom;
        var captured = cells.Where(p => Inside(source, p.Key % Width, p.Key / Width))
            .Select(p => (X: p.Key % Width, Y: p.Key / Width, Cell: p.Value.Copy())).ToArray();
        var keys = cells.Keys.Where(k => Inside(source, k % Width, k / Width) || Inside(target, k % Width, k / Width)).ToHashSet();
        foreach (var cell in captured) keys.Add((cell.Y + dy) * Width + cell.X + dx);
        bool Touches(PlacedStamp stamp, Rect area) => stamp.Cells.Any(c => Inside(area, stamp.X + c.X, stamp.Y + c.Y)) ||
            stamp.Changes.Any(c => Inside(area, stamp.X + c.X, stamp.Y + c.Y));
        bool Complete(PlacedStamp stamp) => stamp.Cells.All(c => Inside(source, stamp.X + c.X, stamp.Y + c.Y)) &&
            stamp.Changes.All(c => Inside(source, stamp.X + c.X, stamp.Y + c.Y));
        var stamps = placedStamps.Where(s => !Touches(s, source) && !Touches(s, target))
            .Concat(placedStamps.Where(s => Touches(s, source) && Complete(s)).Select(s => s with { X = s.X + dx, Y = s.Y + dy })).ToList();
        bool metadataChanged = !stamps.SequenceEqual(placedStamps);
        if (keys.Count == 0 && !metadataChanged) return false;
        history.Commit();
        history.Begin("Move selection");
        foreach (int key in keys) history.Record(this, key % Width, key / Width);
        if (metadataChanged) history.RecordStamps(this);
        foreach (int key in keys) Set(key % Width, key / Width, new());
        foreach (var cell in captured) Set(cell.X + dx, cell.Y + dy, cell.Cell);
        if (metadataChanged) RestoreStamps(stamps);
        history.Commit();
        foreach (int key in keys) changed(key % Width, key / Width);
        return true;
    }
}
