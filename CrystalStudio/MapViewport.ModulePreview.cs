namespace CrystalStudio;

public sealed partial class MapViewport
{
    internal bool IsModulePreview { get; set; }
    // Used only by the read-only module overlay; the underlying viewport never receives editing input.
    internal Point PreviewScreen(Point world) => new(origin.X + world.X * Zoom, origin.Y + world.Y * Zoom);
    internal Point PreviewWorld(Point screen) => new((screen.X - origin.X) / Zoom, (screen.Y - origin.Y) / Zoom);
    internal void PanPreview(Vector delta) { origin += delta; InvalidateVisual(); Changed?.Invoke(); }
    internal void ZoomPreview(Point screen, double factor)
    {
        var world = PreviewWorld(screen);
        double zoom = Zoom * factor;
        if (!double.IsFinite(zoom) || zoom <= 0) return;
        Zoom = Math.Clamp(zoom, .001, 4);
        origin = new(screen.X - world.X * Zoom, screen.Y - world.Y * Zoom);
        InvalidateVisual(); Changed?.Invoke();
    }
    internal void SetPreviewZoom(double zoom) => ZoomPreview(new(ActualWidth / 2, ActualHeight / 2), zoom / Zoom);
}
