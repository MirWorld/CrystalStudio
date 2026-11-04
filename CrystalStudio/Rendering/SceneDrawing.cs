using SkiaSharp;

namespace CrystalStudio.Rendering;

// A platform-independent retained scene. Worker threads build previews without
// creating UI controls; the same scene renders through Avalonia or an offscreen raster.
internal class ScenePen(IBrush brush, double thickness)
{
    public IBrush Brush { get; } = brush;
    public double Thickness { get; } = thickness;
    public DashStyle? DashStyle { get; set; }
    public IPen ToPen() => new Pen(Brush, Thickness, DashStyle);
}

internal abstract class SceneTransform
{
    public abstract Matrix Value { get; }
}
internal sealed class SceneScale(double x, double y) : SceneTransform
{
    public override Matrix Value => Matrix.CreateScale(x, y);
}
internal sealed class SceneTranslation(double x, double y) : SceneTransform
{
    public override Matrix Value => Matrix.CreateTranslation(x, y);
}

internal sealed class SceneCollection : List<SceneDrawing>;

internal abstract class SceneDrawing
{
    public abstract Rect Bounds { get; }
    public abstract void Render(Avalonia.Media.DrawingContext context);
    public abstract void Raster(SKCanvas canvas);
    protected static SKRect SkRect(Rect r) => new((float)r.X, (float)r.Y, (float)r.Right, (float)r.Bottom);
    protected static SKPaint Paint(IBrush brush, ScenePen? pen = null)
    {
        var color = brush is ISolidColorBrush solid ? solid.Color : Colors.Transparent;
        var paint = new SKPaint { Color = new SKColor(color.R, color.G, color.B, (byte)(color.A * brush.Opacity)), IsAntialias = false,
            Style = pen == null ? SKPaintStyle.Fill : SKPaintStyle.Stroke, StrokeWidth = (float)(pen?.Thickness ?? 0) };
        return paint;
    }
}

internal sealed class SceneGroup : SceneDrawing
{
    // Only completed, immutable world-space groups opt in. Camera and pointer
    // groups remain mutable and never retain a stale recording.
    public bool RetainRaster { get; set; }
    private SKPicture? rasterPicture;
    private Rect? retainedBounds;
    public SceneCollection Children { get; } = [];
    public double Opacity { get; set; } = 1;
    public SceneTransform? Transform { get; set; }
    public Geometry? ClipGeometry { get; set; }
    public IBrush? OpacityMask { get; set; }
    public object? GuidelineSet => null;
    public override Rect Bounds
    {
        get
        {
            if (RetainRaster && retainedBounds is { } cached) return cached;
            Rect result = default; bool any = false;
            foreach (var child in Children) { result = any ? result.Union(child.Bounds) : child.Bounds; any = true; }
            result = Transform == null ? result : result.TransformToAABB(Transform.Value);
            if (RetainRaster) retainedBounds = result;
            return result;
        }
    }
    public SceneContext Open() { RetainRaster = false; rasterPicture = null; retainedBounds = null; Children.Clear(); return new(this); }
    public override void Render(Avalonia.Media.DrawingContext context)
    {
        if (RetainRaster)
        {
            context.Custom(new CachedPictureOperation(Bounds, GetRasterPicture()));
            return;
        }
        using var transform = context.PushTransform(Transform?.Value ?? Matrix.Identity);
        using var opacity = context.PushOpacity(Opacity);
        if (ClipGeometry != null)
        {
            using var clip = context.PushGeometryClip(ClipGeometry);
            foreach (var child in Children) child.Render(context);
        }
        else foreach (var child in Children) child.Render(context);
    }
    public override void Raster(SKCanvas canvas)
    {
        var clip = canvas.LocalClipBounds;
        var bounds = Bounds;
        if (bounds.Width > 0 && bounds.Height > 0 &&
            (bounds.Left >= clip.Right || bounds.Top >= clip.Bottom || bounds.Right <= clip.Left || bounds.Bottom <= clip.Top)) return;
        if (RetainRaster)
        {
            canvas.DrawPicture(GetRasterPicture());
        }
        else RasterCore(canvas);
    }
    private SKPicture GetRasterPicture()
    {
        if (rasterPicture != null) return rasterPicture;
        using var recorder = new SKPictureRecorder();
        var recording = recorder.BeginRecording(SkRect(Bounds));
        RasterCore(recording);
        return rasterPicture = recorder.EndRecording();
    }
    private void RasterCore(SKCanvas canvas)
    {
        canvas.Save();
        if (Transform is { Value: var m })
        {
            var matrix = new SKMatrix { ScaleX = (float)m.M11, SkewX = (float)m.M21, TransX = (float)m.M31,
                SkewY = (float)m.M12, ScaleY = (float)m.M22, TransY = (float)m.M32, Persp2 = 1 };
            canvas.Concat(ref matrix);
        }
        if (ClipGeometry != null) canvas.ClipRect(SkRect(ClipGeometry.Bounds));
        if (Opacity < 1)
        {
            using var paint = new SKPaint { Color = SKColors.White.WithAlpha((byte)(255 * Opacity)) };
            canvas.SaveLayer(paint);
            foreach (var child in Children) child.Raster(canvas);
            canvas.Restore();
        }
        else foreach (var child in Children) child.Raster(canvas);
        canvas.Restore();
    }
}

internal sealed class SceneImage(RasterImage image, Rect rect, Rect? source = null) : SceneDrawing
{
    public RasterImage ImageSource { get; } = image;
    public Rect Rect { get; } = rect;
    public override Rect Bounds => Rect;
    public override void Render(Avalonia.Media.DrawingContext context) => context.DrawImage(ImageSource, source ?? new Rect(ImageSource.Size), Rect);
    public override void Raster(SKCanvas canvas) => canvas.DrawImage(ImageSource.SkiaImage, SkRect(source ?? new Rect(ImageSource.Size)), SkRect(Rect));
}

internal sealed class SceneGeometry(IBrush? brush, ScenePen? pen, Geometry geometry) : SceneDrawing
{
    public IBrush? Brush { get; } = brush;
    public ScenePen? Pen { get; } = pen;
    public Geometry Geometry { get; } = geometry;
    public override Rect Bounds => Geometry.Bounds.Inflate(Pen?.Thickness / 2 ?? 0);
    public override void Render(Avalonia.Media.DrawingContext context) => context.DrawGeometry(Brush, Pen?.ToPen(), Geometry);
    public override void Raster(SKCanvas canvas)
    {
        if (Geometry is RectangleGeometry rectangle)
        {
            if (Brush != null) { using var fill = Paint(Brush); canvas.DrawRect(SkRect(rectangle.Rect), fill); }
            if (Pen != null) { using var stroke = Paint(Pen.Brush, Pen); canvas.DrawRect(SkRect(rectangle.Rect), stroke); }
        }
        else if (Geometry is EllipseGeometry ellipse)
        {
            if (Brush != null) { using var fill = Paint(Brush); canvas.DrawOval(SkRect(ellipse.Rect), fill); }
            if (Pen != null) { using var stroke = Paint(Pen.Brush, Pen); canvas.DrawOval(SkRect(ellipse.Rect), stroke); }
        }
        else throw new NotSupportedException("Offscreen previews require rectangle or ellipse geometry.");
    }
}

internal sealed class SceneRectangle(IBrush? brush, ScenePen? pen, Rect rect) : SceneDrawing
{
    public IBrush? Brush { get; } = brush;
    public ScenePen? Pen { get; } = pen;
    public Rect Rect { get; } = rect;
    public override Rect Bounds => Rect.Inflate(Pen?.Thickness / 2 ?? 0);
    public override void Render(Avalonia.Media.DrawingContext context) => context.DrawRectangle(Brush, Pen?.ToPen(), Rect);
    public override void Raster(SKCanvas canvas)
    {
        if (Brush != null) { using var fill = Paint(Brush); canvas.DrawRect(SkRect(Rect), fill); }
        if (Pen != null) { using var stroke = Paint(Pen.Brush, Pen); canvas.DrawRect(SkRect(Rect), stroke); }
    }
}

internal sealed class SceneEllipse(IBrush? brush, ScenePen? pen, Rect rect) : SceneDrawing
{
    public override Rect Bounds => rect.Inflate(pen?.Thickness / 2 ?? 0);
    public override void Render(Avalonia.Media.DrawingContext context) => context.DrawEllipse(brush, pen?.ToPen(), rect);
    public override void Raster(SKCanvas canvas)
    {
        if (brush != null) { using var fill = Paint(brush); canvas.DrawOval(SkRect(rect), fill); }
        if (pen != null) { using var stroke = Paint(pen.Brush, pen); canvas.DrawOval(SkRect(rect), stroke); }
    }
}

internal sealed class SceneLine(ScenePen pen, Point start, Point end) : SceneDrawing
{
    public override Rect Bounds => new Rect(start, end).Normalize().Inflate(pen.Thickness / 2);
    public override void Render(Avalonia.Media.DrawingContext context) => context.DrawLine(pen.ToPen(), start, end);
    public override void Raster(SKCanvas canvas)
    {
        using var paint = Paint(pen.Brush, pen);
        canvas.DrawLine((float)start.X, (float)start.Y, (float)end.X, (float)end.Y, paint);
    }
}

internal sealed class SceneContext(SceneGroup root) : IDisposable
{
    private readonly Stack<SceneGroup> parents = [];
    private SceneGroup current = root;
    private void Push(SceneGroup child) { current.Children.Add(child); parents.Push(current); current = child; }
    public void PushTransform(SceneTransform transform) => Push(new SceneGroup { Transform = transform });
    public void PushOpacity(double opacity) => Push(new SceneGroup { Opacity = opacity });
    public void PushClip(Geometry geometry) => Push(new SceneGroup { ClipGeometry = geometry });
    public void Pop() { if (parents.TryPop(out var parent)) current = parent; }
    public void DrawDrawing(SceneDrawing drawing) => current.Children.Add(drawing);
    public void DrawRectangle(IBrush? brush, ScenePen? pen, Rect rect) => current.Children.Add(new SceneRectangle(brush, pen, rect));
    public void DrawEllipse(IBrush? brush, ScenePen? pen, Point centre, double rx, double ry) =>
        current.Children.Add(new SceneEllipse(brush, pen, new Rect(centre.X - rx, centre.Y - ry, rx * 2, ry * 2)));
    public void DrawGeometry(IBrush? brush, ScenePen? pen, Geometry geometry) => current.Children.Add(new SceneGeometry(brush, pen, geometry));
    public void DrawLine(ScenePen pen, Point start, Point end) => current.Children.Add(new SceneLine(pen, start, end));
    public void DrawImage(RasterImage image, Rect bounds) => current.Children.Add(new SceneImage(image, bounds));
    public void Dispose() { parents.Clear(); }
}

internal sealed class SceneVisual
{
    public SceneGroup Drawing { get; } = new();
    public SceneContext RenderOpen() => Drawing.Open();
}
