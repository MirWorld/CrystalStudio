namespace CrystalStudio;

public sealed partial class MapViewport
{
    public TownPlan? Town { get; set; }
    private (int X,int Y) PickedTarget(int x,int y)
    {
        x-=pickupOffset.X;y-=pickupOffset.Y;
        if(PickedStamp is { } picked && picked.Name.StartsWith("Town Stamp ·",StringComparison.Ordinal) && picked.Cells.Any(c=>(c.Cell.BackImage&0x1FFFFFFF)>0))
        {x&=~1;y&=~1;}
        return (x,y);
    }
    public bool PlaceTownAt(int x, int y)
    {
        if (Town == null) return false;
        x &= ~1; y &= ~1;
        var check = TownPlacement.Check(Document, Town, x, y);
        if (!check.Allowed) { Message?.Invoke(check.Feedback); return false; }
        if (Document.PlaceStamp(Town.Cells, x, y, Town.Name, History, InvalidateCell) == null)
        { Message?.Invoke("Town placement blocked · Stamp instance limit reached"); return false; }
        History.Commit(); InvalidateVisual(); Changed?.Invoke(); Message?.Invoke("Town placed · Ctrl+Z to undo"); return true;
    }
}

public static class TownPlacement
{
    public static StampPlacementResult Check(MapDocument document, TownPlan plan, int x, int y)
    {
        var result = document.CheckStampPlacement(plan.Cells, x, y);
        var blocked = result.BlockedCells.ToList();
        foreach (var s in plan.Cells)
        {
            long sx = (long)x + s.X, sy = (long)y + s.Y;
            if (sx >= 0 && sy >= 0 && sx < document.Width && sy < document.Height && (document.Get((int)sx, (int)sy).BackImage & 0x60000000) != 0)
                blocked.Add(new(sx, sy, StampBlockReason.OccupiedCollision));
        }
        return new(blocked, result.Error);
    }
}
