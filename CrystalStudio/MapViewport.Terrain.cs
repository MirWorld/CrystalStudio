namespace CrystalStudio;

public sealed partial class MapViewport
{
    public int ApplyTerrain(TerrainPlan plan)
    {
        RequireAllLayersEditable();
        if (!ReferenceEquals(Document, plan.Source) || Document.PickupRevision != plan.SourceRevision) throw new InvalidOperationException("The map changed after the terrain preview. Generate a fresh preview.");
        var replaced = TerrainCoastObjects.Replaced(Document, plan.Area).ToArray();
        MapDocument? checkDocument = null;
        if (plan.CoastObjects.Count != 0)
        {
            checkDocument = TerrainCoastObjects.WorkingCopy(Document, plan.Area);
            if (checkDocument.PlacedStamps.Count + plan.CoastObjects.Count > 100000) throw new InvalidOperationException("Too many stamp instances.");
            foreach (var coast in plan.CoastObjects)
                if (!TerrainCoastObjects.CanPlace(checkDocument, coast.Layout, coast.X, coast.Y)) throw new InvalidOperationException("A saved coast layout no longer fits. Generate a fresh preview.");
        }
        if (plan.Water != null && plan.Water.Banks.Any(b=>!Document.Contains(b.X,b.Y))) throw new InvalidOperationException("A bank anchor is outside the map.");
        Document.CheckTerrainBankBoundary(plan.Area);
        if (plan.Water != null)
        {
            var bankDocument = checkDocument ?? (replaced.Length != 0 || Document.PlacedStamps.Any(s => s.GeneratedBanks != null)
                ? TerrainCoastObjects.WorkingCopy(Document, plan.Area) : Document);
            if (plan.Water.Banks.Any(b => bankDocument.Get(b.X,b.Y).HasForeground && bankDocument.Get(b.X,b.Y).Foreground != new Front(checked((short)b.Library),checked((short)b.Image))))
                throw new InvalidOperationException("A bank cell is occupied by an existing object. Generate a fresh preview in a clear area.");
        }
        if (dragging) End();
        int count = 0;
        History.Begin();
        foreach (var stamp in replaced) Document.DeleteStamp(stamp.Id,History,InvalidateCell);
        Document.RemoveGeneratedBanks(plan.Area,History,InvalidateCell);
        foreach (var tile in plan.Tiles)
        {
            var original = Document.Get(tile.X, tile.Y);
            if (original.BackLibrary == tile.Library && (original.BackImage & 0x1FFFFFFF) == tile.Index + 1 && original.TileAnimation == 0 && original.TileOffset == 0 && original.TileFrames == 0) continue;
            History.Record(Document, tile.X, tile.Y);
            var cell = original.Copy(); cell.BackLibrary = checked((short)tile.Library);
            cell.BackImage = (cell.BackImage & unchecked((int)0xE0000000)) | (tile.Index + 1);
            // The generated ground is static; preserve collisions and other layers.
            cell.TileAnimation = 0; cell.TileOffset = 0; cell.TileFrames = 0;
            Document.Set(tile.X, tile.Y, cell); InvalidateCell(tile.X, tile.Y); count++;
        }
        var bankChanges = new List<StampDelta>();
        if (plan.Water != null)
            foreach (var bank in plan.Water.Banks)
            {
                if (!Document.Contains(bank.X,bank.Y)) throw new InvalidOperationException("A bank anchor is outside the map.");
                var original = Document.Get(bank.X,bank.Y);
                var front = new Front(checked((short)bank.Library),checked((short)bank.Image));
                if (original.Foreground == front) continue;
                History.Record(Document,bank.X,bank.Y);
                var cell = original.Copy(); cell.Foreground = front;
                bankChanges.Add(new(bank.X,bank.Y,original.Copy(),cell.Copy(),[front],[0]));
                Document.Set(bank.X,bank.Y,cell); InvalidateCell(bank.X,bank.Y); count++;
            }
        if (plan.Water != null) Document.RememberGeneratedBanks(bankChanges,plan.Water.Catalogue,History);
        foreach (var coast in plan.CoastObjects)
        {
            if (Document.PlaceStamp(coast.Layout.Cells,coast.X,coast.Y,TerrainCoastObjects.StampPrefix+coast.Layout.Name,History,InvalidateCell) == null)
                throw new InvalidOperationException("Could not place the complete saved coast object.");
            count += coast.Layout.Cells.Count;
        }
        History.Commit(); brushPreviewKey = null; chunks.Clear(); frontRows.Clear();
        InvalidateVisual(); Changed?.Invoke(); return count;
    }
}
