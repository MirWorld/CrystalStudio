using Avalonia.Rendering.SceneGraph;
using Avalonia.Platform;
using Avalonia.Skia;
using SkiaSharp;

namespace CrystalStudio.Rendering;

// Independent retained visuals let pointer changes invalidate only their own
// overlay. Immutable Skia pictures avoid serializing every sprite during pans.
internal sealed class SceneLayer(bool useSkia) : Control
{
    private SceneGroup? scene;
    private RecordedPicture? picture;
    private bool hasContent;
    private object? worldKey;
    private IBrush? worldPaper;
    private Rect recordingBounds;
    private Rect worldCoverage;
    private Matrix camera = Matrix.Identity;
    public int CameraUpdates { get; private set; }
    public int Submissions { get; private set; }
    public int RecordingBuilds { get; private set; }

    public void SetWorldScene(SceneGroup value, object key, IBrush paper, Point origin, double zoom, Rect visibleWorld)
    {
        var nextCamera = Matrix.CreateScale(zoom, zoom) * Matrix.CreateTranslation(origin.X, origin.Y);
        if (camera != nextCamera)
        {
            camera = nextCamera; CameraUpdates++;
            RenderTransformOrigin = new RelativePoint(0, 0, RelativeUnit.Relative);
            RenderTransform = new MatrixTransform(camera);
        }
        scene = value;
        if (ReferenceEquals(worldKey, key) && ReferenceEquals(worldPaper, paper) && picture != null && worldCoverage.Contains(visibleWorld)) return;
        worldKey = key;
        worldPaper = paper;
        worldCoverage = visibleWorld.Inflate(new Thickness(visibleWorld.Width / 4, visibleWorld.Height / 4));
        recordingBounds = worldCoverage;
        using var recorder = new SKPictureRecorder();
        var canvas = recorder.BeginRecording(ToSkRect(worldCoverage));
        canvas.ClipRect(ToSkRect(worldCoverage));
        // The scene's camera is canceled while recording; the compositor owns
        // camera motion independently of the immutable world-space picture.
        canvas.Scale((float)(1 / zoom));
        canvas.Translate((float)-origin.X, (float)-origin.Y);
        value.Raster(canvas);
        var previous = picture;
        picture = new RecordedPicture(recorder.EndRecording(), zoom < .25 ? worldCoverage : null);
        RecordingBuilds++;
        previous?.Release(); hasContent = true;
        InvalidateVisual();
    }
    private static SKRect ToSkRect(Rect bounds) => new((float)bounds.X, (float)bounds.Y, (float)bounds.Right, (float)bounds.Bottom);

    public void SetScene(SceneGroup value)
    {
        var sceneBounds = value.Bounds;
        bool nextHasContent = sceneBounds.Width > 0 && sceneBounds.Height > 0;
        scene = value;
        if (!hasContent && !nextHasContent) return;
        hasContent = nextHasContent;
        if (useSkia)
        {
            using var recorder = new SKPictureRecorder();
            var bounds = Bounds.Width > 0 && Bounds.Height > 0 ? new Rect(Bounds.Size) : sceneBounds;
            recordingBounds = bounds;
            var canvas = recorder.BeginRecording(new SKRect((float)bounds.X, (float)bounds.Y, (float)bounds.Right, (float)bounds.Bottom));
            canvas.ClipRect(new SKRect((float)bounds.X, (float)bounds.Y, (float)bounds.Right, (float)bounds.Bottom));
            value.Raster(canvas);
            var previous = picture;
            picture = new RecordedPicture(recorder.EndRecording());
            previous?.Release();
        }
        InvalidateVisual();
    }
    public override void Render(Avalonia.Media.DrawingContext context)
    {
        Submissions++;
        if (useSkia && picture != null)
            context.Custom(new PictureOperation(recordingBounds, picture));
        else scene?.Render(context);
    }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        picture?.Release(); picture = null;
        worldKey = null;
    }
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (scene != null && camera.IsIdentity) SetScene(scene);
    }
}

internal sealed class RecordedPicture(SKPicture picture, Rect? rasterBounds = null)
{
    private int references = 1;
    private SKImage? raster;
    private GRContext? rasterContext;
    private float rasterScale;
    private SKRect rasterDestination;
    public SKPicture Picture { get; } = picture;
    public RecordedPicture Retain() { Interlocked.Increment(ref references); return this; }
    public void Release()
    {
        if (Interlocked.Decrement(ref references) != 0) return;
        raster?.Dispose(); Picture.Dispose();
    }
    public void Draw(SKCanvas canvas, GRContext? gpu)
    {
        if (rasterBounds is not { } bounds) { canvas.DrawPicture(Picture); return; }
        var matrix = canvas.TotalMatrix;
        float scale = Math.Abs(matrix.ScaleX);
        int width = (int)Math.Ceiling(bounds.Width * scale), height = (int)Math.Ceiling(bounds.Height * scale);
        // Retain full physical resolution. Large surfaces use the original
        // vector recording instead of silently reducing artwork resolution.
        if (scale <= 0 || width <= 0 || height <= 0 || width > 8192 || height > 8192)
        { canvas.DrawPicture(Picture); return; }
        if (raster == null || rasterContext != gpu || Math.Abs(rasterScale - scale) > 0.000001)
        {
            var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var surface = gpu == null ? SKSurface.Create(info) : SKSurface.Create(gpu, true, info);
            if (surface == null) { canvas.DrawPicture(Picture); return; }
            surface.Canvas.Clear(SKColors.Transparent);
            surface.Canvas.Scale(scale);
            surface.Canvas.Translate((float)-bounds.X, (float)-bounds.Y);
            surface.Canvas.DrawPicture(Picture);
            var previous = raster;
            raster = surface.Snapshot();
            rasterContext = gpu; rasterScale = scale;
            rasterDestination = new((float)bounds.X, (float)bounds.Y, (float)(bounds.X + width / scale), (float)(bounds.Y + height / scale));
            previous?.Dispose();
        }
        canvas.DrawImage(raster, rasterDestination);
    }
}

internal sealed class PictureOperation(Rect bounds, RecordedPicture picture) : ICustomDrawOperation
{
    private RecordedPicture? recording = picture.Retain();
    public Rect Bounds { get; } = bounds;
    public bool HitTest(Point point) => false;
    public bool Equals(ICustomDrawOperation? other) => ReferenceEquals(this, other);
    public void Dispose() { Interlocked.Exchange(ref recording, null)?.Release(); }
    public void Render(ImmediateDrawingContext context)
    {
        var feature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
        if (feature == null || recording == null) return;
        using var lease = feature.Lease();
        recording.Draw(lease.SkCanvas, lease.GrContext);
    }
}

// The immutable group and any in-flight operations share the picture wrapper.
// Skia releases its native recording when neither retains it, including when an
// old document or atlas is evicted. An operation must not dispose the cache.
internal sealed class CachedPictureOperation(Rect bounds, SKPicture picture) : ICustomDrawOperation
{
    private SKPicture? recording = picture;
    public Rect Bounds { get; } = bounds;
    public bool HitTest(Point point) => false;
    public bool Equals(ICustomDrawOperation? other) => ReferenceEquals(this, other);
    public void Dispose() => recording = null;
    public void Render(ImmediateDrawingContext context)
    {
        if (context.TryGetFeature<ISkiaSharpApiLeaseFeature>() is not { } feature || recording == null) return;
        using var lease = feature.Lease();
        lease.SkCanvas.DrawPicture(recording);
    }
}
