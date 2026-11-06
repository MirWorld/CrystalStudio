using System.Globalization;

namespace CrystalStudio;

internal sealed record ModulePreviewRegion(string Label, ModuleConnectionRules.Bounds Bounds, bool Selected = false);
internal sealed record ModuleCutPreview(ModuleArea Area, bool Vertical, int Position, bool Invalid = false);

internal sealed class ModuleConnectionCanvas : Grid, IDisposable
{
    private readonly IReadOnlyList<StampCell> cells;
    private readonly AssetStore? assets;
    private readonly Overlay overlay;
    private int offsetX, offsetY;
    private bool disposed, fitted;
    internal MapViewport Viewport { get; } = new() { IsHitTestVisible = false, Focusable = false, IsModulePreview = true };
    internal ModuleConnectionRules.Bounds Area { get; private set; }
    internal IReadOnlyList<ModuleConnection> Points { get; set; } = [];
    internal IReadOnlySet<string> Disabled { get; set; } = new HashSet<string>();
    internal string? SelectedId { get; set; }
    internal string? InvalidId { get; set; }
    internal ModuleConnection? Draft { get; set; }
    internal string? DraftTarget { get; set; }
    internal bool InvalidDraft { get; set; }
    internal bool ShowConnections { get; set; } = true;
    internal bool ShowRegions { get; set; } = true;
    internal Point PointerCellPosition { get; private set; }
    internal IReadOnlyList<ModulePreviewRegion> Regions { get; set; } = [];
    internal ModuleTerrainAnalysis? Terrain { get; set; }
    internal bool ShowTerrain { get; set; }
    internal IReadOnlyList<ModuleArea> ProtectedRegions { get; set; } = [];
    internal ModuleArea? ProtectionDraft { get; set; }
    internal ModuleCutPreview? CutPreview { get; set; }
    internal IReadOnlyList<ModuleConnection> CutOpenings { get; set; } = [];
    internal event Action<int, int>? Pressed, Moved, Released;
    internal event Action? CaptureCancelled;
    internal event Action<Key, KeyModifiers>? Shortcut;
    internal ModuleConnectionCanvas(IReadOnlyList<StampCell> cells, ModuleConnectionRules.Bounds area, AssetStore? assets = null)
    {
        this.cells = cells; this.assets = assets; Area = area; ClipToBounds = true;
        offsetX = area.Left & ~1; offsetY = area.Top & ~1;
        var map = new MapDocument(area.Right - offsetX + 1, area.Bottom - offsetY + 1);
        foreach (var cell in cells) map.Set(cell.X - offsetX, cell.Y - offsetY, cell.Cell.Copy());
        Viewport.Assets = assets; Viewport.SetDocument(map);
        overlay = new Overlay(this) { Focusable = true, ClipToBounds = true };
        Children.Add(Viewport); Children.Add(overlay);
        Viewport.Changed += Refresh;
        SizeChanged += (_, _) => { if (!fitted && Bounds.Width > 0) { fitted = true; Fit(); } };
        if (assets != null) assets.Updated += AssetsUpdated;
    }
    internal void SetMap(MapDocument map, ModuleConnectionRules.Bounds area)
    { offsetX = offsetY = 0; Area = area; Viewport.SetDocument(map); Fit(); }
    internal Point Screen(double x, double y) => Viewport.PreviewScreen(new((x - offsetX) * 48, (y - offsetY) * 32));
    internal (int X, int Y) CellAt(Point screen)
    {
        var world = Viewport.PreviewWorld(screen);
        return ((int)Math.Floor(world.X / 48) + offsetX, (int)Math.Floor(world.Y / 32) + offsetY);
    }
    internal void Fit()
        => Frame(new(Area.Left, Area.Top, Area.Right - Area.Left + 1, Area.Bottom - Area.Top + 1));
    internal void Frame(ModuleArea area)
    {
        if (Bounds.Width <= 0 || Bounds.Height <= 0) return;
        Viewport.SetPreviewZoom(Math.Min((Bounds.Width - 80) / (area.Width * 48.0), (Bounds.Height - 80) / (area.Height * 32.0)));
        Viewport.CenterOnWorld(new((area.X - offsetX + area.Width / 2.0) * 48, (area.Y - offsetY + area.Height / 2.0) * 32));
        Refresh();
    }
    internal void Locate(int x, int y)
    {
        Viewport.SetPreviewZoom(Math.Max(.5, Viewport.Zoom));
        Viewport.CenterOnWorld(new((x - offsetX + .5) * 48, (y - offsetY + .5) * 32)); Refresh();
    }
    internal void Refresh() { overlay.InvalidateVisual(); }
    internal void FocusCanvas() => overlay.Focus();
    private void AssetsUpdated() => Dispatcher.UIThread.Post(() => { if (!disposed) { Viewport.RefreshAssets(); Refresh(); } });
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        if (assets != null) assets.Updated -= AssetsUpdated;
        Viewport.Changed -= Refresh; Viewport.StopBackgroundWork();
    }
    private sealed class Overlay(ModuleConnectionCanvas owner) : Control
    {
        private bool pan, drawing, space;
        private Point previous;
        private Rect CellRect(double x, double y, double width = 1, double height = 1)
        {
            var a = owner.Screen(x, y); var b = owner.Screen(x + width, y + height);
            return new Rect(a, b);
        }
        public override void Render(DrawingContext dc)
        {
            // Transparent hit surface keeps every drawing tool in the source viewport inaccessible.
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(Bounds.Size));
            if (owner.assets == null)
            {
                foreach (var cell in owner.cells)
                {
                    var rect = CellRect(cell.X, cell.Y); if (!rect.Intersects(new Rect(Bounds.Size))) continue;
                    bool blocked = (cell.Cell.BackImage & 0x60000000) != 0 || cell.Cell.Foreground is { } f && (f.Image & 0x8000) != 0;
                    dc.DrawRectangle(blocked && owner.Viewport.CollisionVisible ? Brushes.IndianRed : Brushes.SteelBlue, null, rect);
                }
            }
            // Keep empty margins and enlarged previews legible too; the map renderer's
            // detail grid is optimized for its normal editing zoom range.
            if (owner.Viewport.GridVisible && owner.Viewport.Zoom >= .15)
            {
                var a = owner.CellAt(default); var b = owner.CellAt(new(Bounds.Width, Bounds.Height));
                var pen = new Pen(new SolidColorBrush(Color.FromArgb(75, 240, 240, 240)), 1);
                for (int x = Math.Max(owner.Area.Left, a.X); x <= Math.Min(owner.Area.Right + 1, b.X + 1); x++)
                    dc.DrawLine(pen, owner.Screen(x, owner.Area.Top), owner.Screen(x, owner.Area.Bottom + 1));
                for (int y = Math.Max(owner.Area.Top, a.Y); y <= Math.Min(owner.Area.Bottom + 1, b.Y + 1); y++)
                    dc.DrawLine(pen, owner.Screen(owner.Area.Left, y), owner.Screen(owner.Area.Right + 1, y));
            }
            void Text(string label, Point point, Brush color)
            {
                var text = new FormattedText(label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 12, color);
                dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(210, 15, 20, 26)), null, new Rect(point.X - 2, point.Y - 1, text.Width + 4, text.Height + 2));
                dc.DrawText(text, point);
            }
            if (owner.ShowTerrain && owner.Terrain is { } terrain)
            {
                var a = owner.CellAt(default); var b = owner.CellAt(new(Bounds.Width, Bounds.Height));
                int main = terrain.MainId;
                for (int y = Math.Max(0, a.Y); y <= Math.Min(terrain.Height - 1, b.Y); y++)
                    for (int x = Math.Max(0, a.X); x <= Math.Min(terrain.Width - 1, b.X);)
                    {
                        int id = terrain.Labels[y * terrain.Width + x], end = x + 1;
                        while (end <= Math.Min(terrain.Width - 1, b.X) && terrain.Labels[y * terrain.Width + end] == id) end++;
                        var color = id <= 0 ? Color.FromArgb(70, 220, 50, 50) : id == main ? Color.FromArgb(95, 40, 220, 120) : Color.FromArgb(150, 255, 175, 40);
                        dc.DrawRectangle(new SolidColorBrush(color), null, CellRect(x, y, end - x)); x = end;
                    }
                foreach (var region in terrain.Regions.Where(r => r.Id != main))
                {
                    var rect = CellRect(region.X, region.Y);
                    if (!rect.Intersects(new Rect(Bounds.Size))) continue;
                    dc.DrawEllipse(null, new Pen(Brushes.Orange, 2), rect.Center, 7, 7);
                    Text($"! {region.Id} · {region.Count}", rect.TopLeft, Brushes.Orange);
                }
            }
            foreach (var area in owner.ProtectedRegions.Concat(owner.ProtectionDraft is { } pd ? [pd] : []))
            {
                var rect = CellRect(area.X, area.Y, area.Width, area.Height);
                dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(45, 180, 130, 240)), new Pen(Brushes.Violet, 2, DashStyle.Dash), rect);
                Text(LocalText.T("Protected"), rect.BottomLeft - new Vector(0, 18), Brushes.Violet);
            }
            if (owner.ShowRegions)
            {
                var regions = owner.Regions.Count == 0 ? new[] { new ModulePreviewRegion("", owner.Area) } : owner.Regions;
                foreach (var region in regions)
                {
                    var b = region.Bounds; var rect = CellRect(b.Left, b.Top, b.Right - b.Left + 1, b.Bottom - b.Top + 1);
                    dc.DrawRectangle(null, new Pen(region.Selected ? Brushes.Cyan : Brushes.White, region.Selected ? 3 : 1, DashStyle.Dash), rect);
                    if (region.Label.Length > 0) Text(region.Label, rect.TopLeft + new Vector(4, 4), Brushes.White);
                }
            }
            if (owner.CutPreview is { } cut)
            {
                var area = cut.Area; Brush color = cut.Invalid ? Brushes.Red : Brushes.Orange;
                var a = owner.Screen(cut.Vertical ? cut.Position : area.X, cut.Vertical ? area.Y : cut.Position);
                var b = owner.Screen(cut.Vertical ? cut.Position : area.X + area.Width, cut.Vertical ? area.Y + area.Height : cut.Position);
                dc.DrawLine(new Pen(color, 3, DashStyle.Dash), a, b);
                Text((cut.Invalid ? "! " : "+ ") + cut.Position, a + new Vector(4, 4), color);
            }
            if (!owner.ShowConnections) return;
            void Opening(ModuleConnection p, bool draft, bool cut = false)
            {
                if (p.Width is < 1 or > 4096 || !Enum.IsDefined(p.Edge)) return;
                bool selected = p.Id == owner.SelectedId, disabled = owner.Disabled.Contains(p.Id);
                Brush color = draft ? (cut ? owner.CutPreview?.Invalid == true : owner.InvalidDraft) ? Brushes.Red : Brushes.Orange : p.Id == owner.InvalidId ? Brushes.Red : disabled ? Brushes.Gray : selected ? Brushes.Cyan : Brushes.LimeGreen;
                bool horizontal = p.Edge is ModuleEdge.North or ModuleEdge.South;
                var rect = CellRect(p.X, p.Y, horizontal ? p.Width : 1, horizontal ? 1 : p.Width);
                var pen = new Pen(color, selected || draft ? 3 : 2, draft || disabled ? DashStyle.Dash : null);
                dc.DrawRectangle(null, pen, rect.Deflate(1));
                var center = rect.Center;
                Vector direction = p.Edge switch { ModuleEdge.North => new(0, -1), ModuleEdge.South => new(0, 1), ModuleEdge.East => new(1, 0), _ => new(-1, 0) };
                var tip = center + direction * 14; var perpendicular = new Vector(-direction.Y, direction.X);
                dc.DrawLine(new Pen(color, 2), center, tip);
                dc.DrawLine(new Pen(color, 2), tip, tip - direction * 5 + perpendicular * 4);
                dc.DrawLine(new Pen(color, 2), tip, tip - direction * 5 - perpendicular * 4);
                if (selected || draft)
                {
                    var ends = p.Width == 1
                        ? horizontal ? new[] { new Point(rect.Left + 4, rect.Center.Y), new Point(rect.Right - 4, rect.Center.Y) }
                            : new[] { new Point(rect.Center.X, rect.Top + 4), new Point(rect.Center.X, rect.Bottom - 4) }
                        : new[] { CellRect(p.X, p.Y).Center, CellRect(p.X + (horizontal ? p.Width - 1 : 0), p.Y + (horizontal ? 0 : p.Width - 1)).Center };
                    foreach (var end in ends)
                        dc.DrawRectangle(color, new Pen(Brushes.Black, 1), new Rect(end.X - 4, end.Y - 4, 8, 8));
                }
                Text((draft ? "+ " : disabled ? "○ " : "") + p.Id, new(rect.Left, rect.Top - 19), color);
            }
            foreach (var point in owner.Points)
                if (owner.Draft == null || point.Id != owner.DraftTarget) Opening(point, false);
            if (owner.Draft is { } draft) Opening(draft, true);
            foreach (var point in owner.CutOpenings) Opening(point, true, true);
        }
        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e); Focus(); previous = e.GetPosition(this);
            var props = e.GetCurrentPoint(this).Properties;
            pan = props.IsMiddleButtonPressed || space && props.IsLeftButtonPressed;
            drawing = !pan && props.IsLeftButtonPressed;
            if (pan || drawing) { e.Pointer.Capture(this); e.Handled = true; }
            if (drawing)
            {
                var world = owner.Viewport.PreviewWorld(previous);
                owner.PointerCellPosition = new(world.X / 48 + owner.offsetX, world.Y / 32 + owner.offsetY);
                var c = owner.CellAt(previous); owner.Pressed?.Invoke(c.X, c.Y);
            }
        }
        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e); var point = e.GetPosition(this);
            if (pan) owner.Viewport.PanPreview(point - previous);
            else { var c = owner.CellAt(point); owner.Moved?.Invoke(c.X, c.Y); }
            previous = point;
        }
        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            bool wasDrawing = drawing; drawing = pan = false;
            if (wasDrawing) { var c = owner.CellAt(e.GetPosition(this)); owner.Released?.Invoke(c.X, c.Y); }
            e.Pointer.Capture(null); e.Handled = true;
        }
        protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
        {
            base.OnPointerCaptureLost(e); if (drawing) owner.CaptureCancelled?.Invoke(); drawing = pan = false;
        }
        protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
        { owner.Viewport.ZoomPreview(e.GetPosition(this), e.Delta.Y > 0 ? 1.15 : 1 / 1.15); e.Handled = true; }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Key == Key.Space) { space = true; e.Handled = true; }
            else if (e.Key is Key.Escape or Key.Delete || (e.Key is Key.Z or Key.Y && e.KeyModifiers.HasFlag(KeyModifiers.Control)))
            { owner.Shortcut?.Invoke(e.Key, e.KeyModifiers); e.Handled = true; }
        }
        protected override void OnKeyUp(KeyEventArgs e) { base.OnKeyUp(e); if (e.Key == Key.Space) space = false; }
        protected override void OnLostFocus(RoutedEventArgs e) { base.OnLostFocus(e); space = false; }
    }
}
