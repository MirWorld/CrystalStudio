namespace CrystalStudio;

public sealed partial class MapViewport
{
    internal void ApplyModuleJoin(ModuleJoinPlan plan)
    {
        RequireAllLayersEditable();
        if (!ReferenceEquals(Document, plan.Source) || Document.PickupRevision != plan.SourceRevision)
            throw new InvalidOperationException(LocalText.T("The map changed after the module preview. Generate a fresh preview."));
        CancelInteraction();
        if (Document.PickupRevision != plan.SourceRevision)
            throw new InvalidOperationException(LocalText.T("The map changed after the module preview. Generate a fresh preview."));
        History.Begin("Generate module layout"); History.RecordStamps(Document);
        foreach (var cell in plan.Cells)
        {
            History.Record(Document, cell.X, cell.Y);
            Document.Set(cell.X, cell.Y, cell.Cell.Copy()); InvalidateCell(cell.X, cell.Y);
        }
        Document.RestoreStamps(plan.Generated.PlacedStamps);
        History.Commit(); brushPreviewKey = null; chunks.Clear(); frontRows.Clear();
        InvalidateVisual(); Changed?.Invoke();
    }
}
