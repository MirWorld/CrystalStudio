namespace CrystalStudio;

public sealed class MapMinimap : SceneControl
{
    private MapViewport? viewport;
    private bool subscribed, navigating;
    private Vector grabOffset;
    private SceneGroup? overviewSource, overviewDrawing;
    public MapViewport? Viewport
    {
        get => viewport;
        set { if (ReferenceEquals(viewport, value)) return; StopNavigation(); Detach(); viewport = value; Attach(); InvalidateVisual(); }
    }
    public Rect MapBounds
    {
        get
        {
            if (Viewport == null || ActualWidth <= 12 || ActualHeight <= 12) return default;
            double width = Viewport.Document.Width * 48.0, height = Viewport.Document.Height * 32.0;
            double scale = Math.Min((ActualWidth - 12) / width, (ActualHeight - 12) / height);
            return new((ActualWidth - width * scale) / 2, (ActualHeight - height * scale) / 2, width * scale, height * scale);
        }
    }
    public MapMinimap()
    {
        Focusable = true; ClipToBounds = true; Cursor = EditorCursors.Hand;
        ToolTip.SetTip(this, LocalText.T("Click to centre the map; drag the viewport rectangle to pan. Mouse wheel zooms."));
        Loaded += (_, _) => { Attach(); InvalidateVisual(); };
        Unloaded += (_, _) => { StopNavigation(); Detach(); };
    }
    private void Attach()
    {
        if (subscribed || Viewport == null) return;
        Viewport.MinimapInvalidated += InvalidateVisual; Viewport.NavigationCancelled += StopNavigation; subscribed = true;
    }
    private void Detach()
    {
        if (!subscribed || Viewport == null) return;
        Viewport.MinimapInvalidated -= InvalidateVisual; Viewport.NavigationCancelled -= StopNavigation; subscribed = false;
    }
    private protected override void RenderScene(SceneContext dc)
    {
        dc.DrawRectangle(ThemeService.Brush("CanvasBackground"), null, new(0, 0, ActualWidth, ActualHeight));
        var map = MapBounds; if (map.Width <= 0 || map.Height <= 0) return;
        var source = Viewport!; source.EnsureMinimapOverview();
        dc.DrawRectangle(ThemeService.Brush("CanvasPaper"), null, map);
        dc.PushClip(new RectangleGeometry(map));
        double scale = map.Width / (source.Document.Width * 48.0);
        dc.PushTransform(new SceneTranslation(map.X, map.Y)); dc.PushTransform(new SceneScale(scale, scale));
        if (source.OverviewScene is { } scene)
        {
            if (!ReferenceEquals(scene, overviewSource))
            {
                overviewSource = scene;
                overviewDrawing = new SceneGroup { RetainRaster = true };
                overviewDrawing.Children.Add(scene);
            }
            dc.DrawDrawing(overviewDrawing!);
        }
        dc.Pop(); dc.Pop();
        var visible = source.VisibleWorldBounds.Intersect(new Rect(0, 0, source.Document.Width * 48.0, source.Document.Height * 32.0));
        if (visible.Width > 0 && visible.Height > 0)
        {
            var rectangle = new Rect(map.X + visible.X * scale, map.Y + visible.Y * scale, visible.Width * scale, visible.Height * scale);
            dc.DrawRectangle(new ImmutableSolidColorBrush(Color.FromArgb(35, 255, 255, 255)), new ScenePen(Brushes.Black, 4), rectangle);
            dc.DrawRectangle(null, new ScenePen(Brushes.White, 2), rectangle);
        }
        dc.Pop(); dc.DrawRectangle(null, new ScenePen(ThemeService.Brush("Line"), 1), map);
    }
    private Point WorldAt(Point point)
    {
        var map = MapBounds; var source = Viewport!;
        return new(Math.Clamp((point.X - map.X) / map.Width, 0, 1) * source.Document.Width * 48.0,
            Math.Clamp((point.Y - map.Y) / map.Height, 0, 1) * source.Document.Height * 32.0);
    }
    private void MoveNavigation(Point point) { if (navigating && MapBounds.Width > 0) Viewport!.CenterOnWorld(WorldAt(point) - grabOffset); }
    private void StopNavigation() { navigating = false; ReleasePointer(); }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e); Focus(); var point = e.GetPosition(this);
        if (!IsEnabled || Viewport == null || !MapBounds.Contains(point) || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var world = WorldAt(point); var visible = Viewport.VisibleWorldBounds;
        grabOffset = visible.Contains(world) ? world - visible.Center : default;
        navigating = true; CapturePointer(e.Pointer); MoveNavigation(point); e.Handled = true;
    }
    protected override void OnPointerMoved(PointerEventArgs e) { base.OnPointerMoved(e); MoveNavigation(e.GetPosition(this)); }
    protected override void OnPointerReleased(PointerReleasedEventArgs e) { base.OnPointerReleased(e); StopNavigation(); }
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e) { base.OnPointerCaptureLost(e); navigating = false; }
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e); if (Viewport != null) Viewport.SetZoom(Viewport.Zoom * (e.Delta.Y > 0 ? 1.15 : 1 / 1.15)); e.Handled = true;
    }
}
