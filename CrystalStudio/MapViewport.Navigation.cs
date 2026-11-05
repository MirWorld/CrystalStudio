namespace CrystalStudio;

public sealed partial class MapViewport
{
    internal void LocateCell(int x, int y)
    {
        if (!Document.Contains(x, y)) return;
        CancelInteraction();
        Zoom = Math.Max(Zoom, .5);
        Selection = new Rect(x, y, 1, 1);
        CenterOnWorld(new Point(x * 48 + 24, y * 32 + 16));
        InvalidateVisual(); Changed?.Invoke();
    }
}
