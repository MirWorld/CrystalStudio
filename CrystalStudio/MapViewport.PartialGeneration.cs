namespace CrystalStudio;

public sealed partial class MapViewport
{
    internal void ApplyPartialGeneration(PartialMapPlan plan)
    {
        RequireAllLayersEditable();
        if (!ReferenceEquals(Document, plan.Source) || Document.PickupRevision != plan.Revision)
            throw new InvalidOperationException(LocalText.T("The map changed after the preview. Generate a fresh preview."));
        if (plan.Changed.Count == 0) return;
        CancelInteraction(); History.Begin("Regenerate selected area"); History.RecordStamps(Document);
        foreach (int index in plan.Changed)
        {
            int column = index % Document.Width, row = index / Document.Width;
            History.Record(Document, column, row); Document.Set(column, row, plan.Generated.Get(column, row).Copy()); InvalidateCell(column, row);
        }
        Document.RestoreStamps(plan.Generated.PlacedStamps); History.Commit();
        brushPreviewKey = null; chunks.Clear(); frontRows.Clear(); InvalidateVisual(); Changed?.Invoke();
    }
}
