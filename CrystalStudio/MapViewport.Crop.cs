namespace CrystalStudio;

public sealed partial class MapViewport
{
    public bool CropSelection()
    {
        if (Selection is not Rect area || !AllowEdit()) return false;
        CancelInteraction();
        var cropped = Document.Crop(area);
        if (cropped.Width == Document.Width && cropped.Height == Document.Height) return false;
        History.Resize(Document, cropped);
        RefreshAfterResize();
        return true;
    }

    private void RefreshAfterResize()
    {
        var history = History;
        var locks = (bool[])layerLocks.Clone();
        SetDocument(Document, preserveZoom: true);
        History = history;
        Array.Copy(locks, layerLocks, locks.Length);
        hover = (-1, -1);
        Fit();
    }
}
