namespace CrystalStudio;

public sealed partial class MapViewport
{
    private MapDocument? metadataDocument;
    private long metadataRevision = -1;
    private List<(int X, int Y, Cell Cell)> metadataCells = [];
    private RasterImage? darknessMask;
    private (MapDocument Document, long Revision, Point Origin, double Zoom, int Width, int Height,
        AssetStore? Assets, int Cached, bool Objects, MapLighting Lighting)? darknessKey;
    private static readonly Brush lightMarker = FrozenBrush(Color.FromArgb(100, 255, 195, 45));
    private static readonly Brush doorMarker = FrozenBrush(Color.FromArgb(100, 45, 205, 255));
    private static Brush FrozenBrush(Color colour) { var brush = new ImmutableSolidColorBrush(colour);  return brush; }

    private void ResetPresentation()
    {
        metadataDocument = null; metadataRevision = -1; metadataCells.Clear();
        darknessMask = null; darknessKey = null;
    }

    private void EnsureMetadataCells()
    {
        if (metadataDocument == Document && metadataRevision == Document.PickupRevision) return;
        metadataCells = Document.PresentationCells
            .Select(pair => (pair.Key % Document.Width, pair.Key / Document.Width, pair.Value)).ToList();
        metadataDocument = Document; metadataRevision = Document.PickupRevision;
    }

    private void DrawPresentation(SceneContext dc)
    {
        var map = new Rect(origin.X, origin.Y, Document.Width * 48.0 * Zoom, Document.Height * 32.0 * Zoom);
        dc.PushClip(new RectangleGeometry(map));
        if (LightCellsVisible || DoorCellsVisible || DarknessVisible) EnsureMetadataCells();
        if (DarknessVisible && ActualWidth > 0 && ActualHeight > 0)
            dc.DrawImage(DarknessMask(), new Rect(0, 0, ActualWidth, ActualHeight));
        dc.PushTransform(new SceneTranslation(origin.X, origin.Y)); dc.PushTransform(new SceneScale(Zoom, Zoom));
        int x0 = Math.Max(0, (int)Math.Floor(-origin.X / Zoom / 48)), y0 = Math.Max(0, (int)Math.Floor(-origin.Y / Zoom / 32));
        int x1 = Math.Min(Document.Width, (int)((ActualWidth - origin.X) / Zoom / 48) + 1);
        int y1 = Math.Min(Document.Height, (int)((ActualHeight - origin.Y) / Zoom / 32) + 1);
        if (Zoom >= .25 && CollisionVisible)
            for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++)
                CollisionMarkers.Draw(dc, CollisionMarkers.Type(Document.Get(x, y).BackImage & VisibleCollisionMask), new(x * 48, y * 32, 48, 32));
        if (LightCellsVisible || DoorCellsVisible)
            foreach (var (x, y, cell) in metadataCells)
            {
                if (x < x0 || x >= x1 || y < y0 || y >= y1) continue;
                bool light = LightCellsVisible && MapPreview.HasLight(cell), door = DoorCellsVisible && MapPreview.HasDoor(cell);
                if (light) dc.DrawRectangle(lightMarker, null, new(x * 48, y * 32, door ? 24 : 48, 32));
                if (door) dc.DrawRectangle(doorMarker, null, new(x * 48 + (light ? 24 : 0), y * 32, light ? 24 : 48, 32));
            }
        if (GridVisible && Zoom >= .4)
        {
            for (int x = x0; x <= x1; x++) dc.DrawLine(grid, new(x * 48, y0 * 32), new(x * 48, y1 * 32));
            for (int y = y0; y <= y1; y++) dc.DrawLine(grid, new(x0 * 48, y * 32), new(x1 * 48, y * 32));
        }
        dc.Pop(); dc.Pop(); dc.Pop();
    }

    private RasterImage DarknessMask()
    {
        // Keep work bounded on large displays. The smooth light mask does not
        // need one texel per physical pixel; it is independent of the sprite scene.
        double scale = Math.Min(1, 1536 / Math.Max(ActualWidth, ActualHeight));
        int width = Math.Max(1, (int)Math.Ceiling(ActualWidth * scale)), height = Math.Max(1, (int)Math.Ceiling(ActualHeight * scale));
        var key = (Document, Document.PickupRevision, origin, Zoom, width, height, Assets, Assets?.Cached ?? 0, VisibleLayers[2], Lighting);
        if (darknessKey == key && darknessMask != null) return darknessMask;
        byte[] pixels = new byte[width * height * 4];
        // Crystal's DrawLights multiplies the scene by ambient RGB plus additive
        // map lights: dawn/evening = 50, day = 255, standard night = 0.
        byte darkness = Lighting switch { MapLighting.DawnDusk => 205, MapLighting.Day => 0, _ => 255 };
        for (int i = 3; i < pixels.Length; i += 4) pixels[i] = darkness;
        double sx = width / ActualWidth, sy = height / ActualHeight;
        if (VisibleLayers[2]) foreach (var (x, y, cell) in metadataCells)
        {
            if (!MapPreview.EmitsLight(cell)) continue;
            var front = cell.ForegroundImages.First(f => f.Library >= 0 && (f.Image & 0x7FFF) != 0);
            Vector offset = new();
            if (front.Frames > 0 && Assets?.Get(front.Library, (front.Image & 0x7FFF) - 1) is Sprite sprite)
                offset = new(sprite.X, sprite.Y);
            var world = MapPreview.LightBounds(x, y, cell.Light, offset);
            var texture = MapPreview.LightTexture(cell.Light);
            double rx = world.Width * Zoom * sx / 2, ry = world.Height * Zoom * sy / 2;
            double cx = (origin.X + (world.X + world.Width / 2) * Zoom) * sx;
            double cy = (origin.Y + (world.Y + world.Height / 2) * Zoom) * sy;
            int left = Math.Max(0, (int)Math.Floor(cx - rx)), right = Math.Min(width, (int)Math.Ceiling(cx + rx));
            int top = Math.Max(0, (int)Math.Floor(cy - ry)), bottom = Math.Min(height, (int)Math.Ceiling(cy + ry));
            for (int py = top; py < bottom; py++) for (int px = left; px < right; px++)
            {
                int tx = Math.Clamp((int)((px + .5 - cx + rx) / (rx * 2) * texture.Width), 0, texture.Width - 1);
                int ty = Math.Clamp((int)((py + .5 - cy + ry) / (ry * 2) * texture.Height), 0, texture.Height - 1);
                int i = (py * width + px) * 4 + 3;
                pixels[i] = (byte)Math.Max(0, pixels[i] - texture.Pixels[ty * texture.Width + tx]);
            }
        }
        darknessMask = RasterImage.Create(width, height, 96, 96, RasterFormats.Pbgra32, null, pixels, width * 4);
         darknessKey = key; return darknessMask;
    }
}
