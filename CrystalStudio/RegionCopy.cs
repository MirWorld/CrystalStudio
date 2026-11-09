namespace CrystalStudio;

// A detached snapshot: layer choices are captured when copying, not when pasting.
public sealed class RegionCopy
{
    public int Width { get; }
    public int Height { get; }
    private readonly int sourceX, sourceY;
    private readonly bool ground, middle, objects;
    private readonly int collisionMask;
    private readonly Cell[] cells;
    private readonly List<StampCell> previewCells = [];
    public RegionCopy(MapDocument document, int x, int y, int width, int height, bool ground, bool middle, bool objects, bool collision)
        : this(document, x, y, width, height, ground, middle, objects, collision, collision) { }
    public RegionCopy(MapDocument document, int x, int y, int width, int height, bool ground, bool middle, bool objects, bool lowCollision, bool highCollision)
    {
        if (width <= 0 || height <= 0 || !document.Contains(x, y) || !document.Contains(x + width - 1, y + height - 1)) throw new ArgumentOutOfRangeException(nameof(width));
        Width = width; Height = height; sourceX = x; sourceY = y;
        this.ground = ground; this.middle = middle; this.objects = objects;
        collisionMask = (lowCollision ? 0x20000000 : 0) | (highCollision ? 0x40000000 : 0);
        cells = new Cell[checked(width * height)];
        for (int dy = 0; dy < height; dy++) for (int dx = 0; dx < width; dx++)
        {
            var cell = document.Get(x + dx, y + dy).Copy(); cells[dy * width + dx] = cell;
            if (ground && (cell.BackImage & 0x1FFFFFFF) != 0 || middle && cell.MiddleImage != 0 || objects && cell.HasForeground || (cell.BackImage & collisionMask) != 0)
                previewCells.Add(new(dx, dy, cell));
        }
    }
    internal void DrawPreview(SceneContext dc, int x, int y, Action<SceneContext, int, int, int, int, bool> drawSprite)
    {
        // Draw entire layers in map painter order so tall objects cover tiles correctly.
        if (ground) foreach (var s in previewCells)
            if (((x + s.X) & 1) == 0 && ((y + s.Y) & 1) == 0)
                drawSprite(dc, x + s.X, y + s.Y, s.Cell.BackLibrary, (s.Cell.BackImage & 0x1FFFFFFF) - 1, false);
        if (middle) foreach (var s in previewCells)
            drawSprite(dc, x + s.X, y + s.Y, s.Cell.MiddleLibrary, s.Cell.MiddleImage - 1, true);
        if (objects) foreach (var s in previewCells) foreach (var f in s.Cell.ForegroundImages)
            drawSprite(dc, x + s.X, y + s.Y, f.Library, (f.Image & 0x7FFF) - 1, true);
        if (collisionMask != 0)
        {
            foreach (var s in previewCells)
                CollisionMarkers.Draw(dc, CollisionMarkers.Type(s.Cell.BackImage & collisionMask), new Rect((x + s.X) * 48, (y + s.Y) * 32, 48, 32));
        }
    }
    internal void CutSource(MapDocument document, EditHistory history, Action<int, int>? changed = null)
    {
        history.Commit();
        history.Begin("Cut selection");
        foreach (var s in previewCells)
        {
            int x = sourceX + s.X, y = sourceY + s.Y;
            var target = document.Get(x, y).Copy();
            if (ground && (s.Cell.BackImage & 0x1FFFFFFF) != 0)
            {
                target.BackLibrary = 0; target.BackImage &= 0x60000000;
                target.TileAnimation = 0; target.TileOffset = 0; target.TileFrames = 0;
            }
            if (middle && s.Cell.MiddleImage != 0)
            {
                target.MiddleLibrary = 0; target.MiddleImage = 0; target.MiddleFrames = 0; target.MiddleTick = 0;
            }
            if (objects && s.Cell.HasForeground)
            {
                target.Foreground = null; target.Door = 0; target.DoorOffset = 0;
            }
            target.BackImage &= ~(s.Cell.BackImage & collisionMask);
            history.Record(document, x, y); document.Set(x, y, target); changed?.Invoke(x, y);
        }
        history.Commit();
    }
    public (int X, int Y) Anchor(int x, int y) => ground ? (x - ((x - sourceX) & 1), y - ((y - sourceY) & 1)) : (x, y);
    public bool CanPaste(MapDocument document, int x, int y) => document.Contains(x, y) && document.Contains(x + Width - 1, y + Height - 1);
    public bool Paste(MapDocument document, EditHistory history, int x, int y, Action<int, int>? changed = null)
    {
        if (!CanPaste(document, x, y)) return false;
        history.Begin("Paste selection");
        for (int dy = 0; dy < Height; dy++) for (int dx = 0; dx < Width; dx++)
        {
            var source = cells[dy * Width + dx];
            bool pasteGround = ground && (source.BackImage & 0x1FFFFFFF) != 0;
            bool pasteMiddle = middle && source.MiddleImage != 0;
            bool pasteObjects = objects && source.HasForeground;
            bool pasteCollision = (source.BackImage & collisionMask) != 0;
            if (!pasteGround && !pasteMiddle && !pasteObjects && !pasteCollision) continue;
            var target = document.Get(x + dx, y + dy).Copy();
            if (pasteGround) { target.BackLibrary = source.BackLibrary; target.BackImage = (target.BackImage & 0x60000000) | (source.BackImage & ~0x60000000); target.TileAnimation = source.TileAnimation; target.TileOffset = source.TileOffset; target.TileFrames = source.TileFrames; }
            if (pasteMiddle) { target.MiddleLibrary = source.MiddleLibrary; target.MiddleImage = source.MiddleImage; target.MiddleFrames = source.MiddleFrames; target.MiddleTick = source.MiddleTick; }
            if (pasteObjects) { target.Foreground = source.Foreground; target.Door = source.Door; target.DoorOffset = source.DoorOffset; }
            if (pasteCollision) target.BackImage |= source.BackImage & collisionMask;
            history.Record(document, x + dx, y + dy); document.Set(x + dx, y + dy, target); changed?.Invoke(x + dx, y + dy);
        }
        history.Commit(); return true;
    }
}

public sealed partial class MapViewport
{
    private RegionCopy? copiedRegion;
    public bool CanPasteRegion => copiedRegion != null;
    internal Size CopiedRegionSize => copiedRegion == null ? new Size(0, 0) : new Size(copiedRegion.Width, copiedRegion.Height);
    public bool PastingRegion { get; private set; }
    public void CancelPaste() { PastingRegion = false; InvalidateVisual(); }
    public void CopySelection() => CaptureSelection(false);
    public void CutSelection() => CaptureSelection(true);
    private void CaptureSelection(bool cut)
    {
        if (cut && !AllowEdit()) return;
        if (Selection is not Rect r) { Message?.Invoke(LocalText.T("Select a region with M first")); return; }
        if (!VisibleLayers.Any(v => v) && !CollisionVisible) { Message?.Invoke(LocalText.T("Show at least one layer to copy")); return; }
        copiedRegion = new(Document, (int)r.X, (int)r.Y, (int)r.Width, (int)r.Height, VisibleLayers[0], VisibleLayers[1], VisibleLayers[2], LowCollisionVisible, HighCollisionVisible);
        if (cut) { CancelPickup(); copiedRegion.CutSource(Document, History, InvalidateCell); }
        CancelPaste(); Changed?.Invoke(); Message?.Invoke(LocalText.Format("{0} {1} × {2} cells from visible layers",LocalText.T(cut ? "Cut" : "Copied"),(int)r.Width,(int)r.Height));
    }
    public void BeginPaste()
    {
        if (copiedRegion == null) { Message?.Invoke(LocalText.T("Copy a selected region first (Ctrl+C)")); return; }
        CancelPickup(); Deselect(); PastingRegion = true; InvalidateVisual(); Message?.Invoke(LocalText.T("Click to paste region · Escape to cancel"));
    }
    private void PlaceRegion()
    {
        if (!AllowEdit()) return;
        if (copiedRegion == null) return;
        var (x, y) = copiedRegion.Anchor(hover.X, hover.Y);
        if (!copiedRegion.Paste(Document, History, x, y, InvalidateCell)) { Message?.Invoke(LocalText.T("Region extends outside the map")); return; }
        Selection = Tool == EditorTool.Select ? new Rect(x, y, copiedRegion.Width, copiedRegion.Height) : null; CancelPaste(); Changed?.Invoke(); Message?.Invoke(LocalText.T("Region pasted · Ctrl+V to paste again"));
    }
    private void DrawRegionPreview(SceneContext dc)
    {
        if (!PastingRegion || copiedRegion == null || panning) return;
        var (x, y) = copiedRegion.Anchor(hover.X, hover.Y);
        var color = copiedRegion.CanPaste(Document, x, y) ? Colors.CornflowerBlue : Colors.IndianRed;
        dc.PushOpacity(copiedRegion.CanPaste(Document, x, y) ? 0.65 : 0.3);
        copiedRegion.DrawPreview(dc, x, y, DrawSprite);
        dc.Pop();
        dc.DrawRectangle(new ImmutableSolidColorBrush(Color.FromArgb(20, color.R, color.G, color.B)), new ScenePen(new ImmutableSolidColorBrush(color), 2), new Rect(x * 48, y * 32, copiedRegion.Width * 48, copiedRegion.Height * 32));
    }
}
