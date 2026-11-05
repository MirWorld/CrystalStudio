namespace CrystalStudio;

public sealed partial class MapViewport
{
    public void ApplyTemple(TemplePlan plan)
    {
        RequireAllLayersEditable();
        if (!ReferenceEquals(Document, plan.Source) || Document.PickupRevision != plan.SourceRevision)
            throw new InvalidOperationException("The map changed after the dungeon preview. Generate a fresh preview.");
        if (dragging) End();
        History.Begin(); History.RecordStamps(Document);
        for (int y = 0; y < Document.Height; y++)
        for (int x = 0; x < Document.Width; x++)
        {
            var cell = plan.Generated.Get(x, y);
            if (Document.Get(x, y).SameContents(cell)) continue;
            History.Record(Document, x, y); Document.Set(x, y, cell.Copy()); InvalidateCell(x, y);
        }
        Document.RestoreStamps(plan.Generated.PlacedStamps);
        History.Commit(); brushPreviewKey = null; chunks.Clear(); frontRows.Clear();
        InvalidateVisual(); Changed?.Invoke();
    }
}
