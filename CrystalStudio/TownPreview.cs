namespace CrystalStudio;

public static class TownPreview
{
    public static RasterImage Create(TownPlan plan, IReadOnlyDictionary<(int Library, int Image), Sprite> sprites, bool detail = false, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        int size = plan.Options.Size;
        double width = detail ? Math.Min(size, 48) : size, height = detail ? Math.Min(size, 40) : size;
        double left = (plan.Offset + (size - width) / 2) * 48, top = (plan.Offset + (size - height) / 2) * 32;
        double scale = Math.Min(1400 / (width * 48), 950 / (height * 32));
        var visual = new SceneVisual();
        
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new ImmutableSolidColorBrush(Color.FromRgb(56, 68, 48)), null, new(0, 0, width * 48 * scale, height * 32 * scale));
            dc.PushTransform(new SceneScale(scale, scale));
            dc.PushTransform(new SceneTranslation(-left, -top));
            void Draw(StampCell cell, int library, int image, bool small, bool front, bool blend)
            {
                cancellation.ThrowIfCancellationRequested();
                if (!sprites.TryGetValue((library, image), out var sprite)) return;
                var bounds = MapSpriteDrawing.Bounds(sprite, cell.X, cell.Y, library, image - 1, small, front, blend);
                if (bounds.Right < left || bounds.Bottom < top || bounds.Left > left + width * 48 || bounds.Top > top + height * 32) return;
                dc.DrawImage(sprite.Bitmap, bounds);
            }
            foreach (var cell in plan.Cells)
                if ((cell.Cell.BackImage & 0x1FFFFFFF) > 0) Draw(cell, cell.Cell.BackLibrary, cell.Cell.BackImage & 0x1FFFFFFF, false, false, false);
            foreach (var cell in plan.Cells.OrderBy(c => c.Y).ThenBy(c => c.X))
                foreach (var f in cell.Cell.ForegroundImages) Draw(cell, f.Library, f.Image & 0x7FFF, true, true, MapSpriteDrawing.FrontBlend(f.Frames));
            dc.Pop(); dc.Pop();
        }
        var bitmap = new RasterTarget((int)Math.Ceiling(width * 48 * scale), (int)Math.Ceiling(height * 32 * scale), 96, 96, RasterFormats.Pbgra32);
        cancellation.ThrowIfCancellationRequested();
        bitmap.Render(visual); 
        cancellation.ThrowIfCancellationRequested();
        return bitmap;
    }
}
