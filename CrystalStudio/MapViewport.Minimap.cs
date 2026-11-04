namespace CrystalStudio;

public sealed partial class MapViewport
{
    internal event Action? MinimapInvalidated;
    internal event Action? NavigationCancelled;
    internal SceneGroup? OverviewScene => overviewResult?.Scene;
    internal double OverviewDepth => overviewDepth;
    public Rect VisibleWorldBounds => new(-origin.X / Zoom, -origin.Y / Zoom, ActualWidth / Zoom, ActualHeight / Zoom);

    public void PanBy(Vector offset)
    {
        if (dragging || !double.IsFinite(offset.X) || !double.IsFinite(offset.Y)) return;
        origin += offset;
        if (IsPointerOver)
        {
            hover = CellAt(mouse);
            Hover?.Invoke(hover.X, hover.Y);
        }
        // Moving the camera must redraw the map as well as the pointer overlay.
        InvalidateVisual();
    }

    internal void EnsureMinimapOverview()
    {
        if (Zoom < .25) return; // The canvas prepares the shared overview at distant zoom levels.
        int settings = RenderLayerSettings;
        if (overviewDirty || overviewSettings != settings || !ReferenceEquals(overviewAssets, Assets))
            StartOverview(settings, overviewLevel >= 2 ? overviewLevel : 4);
        else if (!overviewBusy && overviewEditedBands.Count > 0) StartOverview(settings, overviewLevel, editsOnly: true);
    }

    public void CenterOnWorld(Point point)
    {
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y)) return;
        double width = Document.Width * 48.0, height = Document.Height * 32.0;
        double halfWidth = ActualWidth / Zoom / 2, halfHeight = ActualHeight / Zoom / 2;
        double x = halfWidth >= width / 2 ? width / 2 : Math.Clamp(point.X, halfWidth, width - halfWidth);
        double y = halfHeight >= height / 2 ? height / 2 : Math.Clamp(point.Y, halfHeight, height - halfHeight);
        origin = new(ActualWidth / 2 - x * Zoom, ActualHeight / 2 - y * Zoom);
        InvalidateVisual();
    }
}
