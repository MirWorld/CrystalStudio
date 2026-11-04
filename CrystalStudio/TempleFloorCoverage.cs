using System.IO;
namespace CrystalStudio;

internal static class TempleFloorCoverage
{
    // Four artwork pixels per sample: twelve by eight samples per map cell.
    // This follows native masks (including concave corners), independently of
    // collision flags, which describe where actors can stand, not sprite coverage.
    internal static HashSet<(int X, int Y)> FindMissing(MapDocument map,
        IReadOnlyDictionary<(int X, int Y), Cell> walls, IReadOnlySet<(int X, int Y)> floorRegion,
        AssetStore assets, CancellationToken cancellation)
    {
        const int sample = 4, cellWidth = 48 / sample, cellHeight = 32 / sample;
        int width = map.Width * cellWidth, height = map.Height * cellHeight;
        var coverage = new byte[checked(width * height)];
        foreach (var entry in map.Cells)
        {
            if ((entry.Value.BackImage & 0x1fffffff) == 0) continue;
            int x = entry.Key % map.Width * cellWidth, y = entry.Key / map.Width * cellHeight;
            for (int dy = y; dy < Math.Min(height, y + 2 * cellHeight); dy++)
                coverage.AsSpan(dy * width + x, Math.Min(2 * cellWidth, width - x)).Fill(1);
        }
        var fronts = walls.SelectMany(p => p.Value.ForegroundImages.Select(f => (p.Key.X, p.Key.Y, Front: f))).ToArray();
        var keys = fronts.Select(p => (Library: (int)p.Front.Library, Image: (p.Front.Image & 0x7fff) - 1)).Distinct().ToArray();
        var timeout = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            bool ready = true;
            foreach (var key in keys) { assets.Get(key.Library, key.Image); ready &= assets.IsResolved(key.Library, key.Image); }
            if (ready) break;
            if (timeout.Elapsed.TotalSeconds > 20) throw new InvalidDataException("Dungeon wall masks did not load.");
            Thread.Sleep(15);
        }
        var sprites = keys.ToDictionary(k => k, k =>
        {
            var sprite = assets.Get(k.Library, k.Image) ?? throw new InvalidDataException("Dungeon wall artwork is missing.");
            var bitmap = sprite.Bitmap.Format == RasterFormats.Pbgra32 ? sprite.Bitmap :
                new ConvertedRasterImage(sprite.Bitmap, RasterFormats.Pbgra32, null, 0);
            var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
            bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
            return (Sprite: sprite, Pixels: pixels, Width: bitmap.PixelWidth, Height: bitmap.PixelHeight);
        });
        foreach (var front in fronts)
        {
            cancellation.ThrowIfCancellationRequested();
            int image = (front.Front.Image & 0x7fff) - 1;
            var data = sprites[(front.Front.Library, image)];
            var bounds = MapSpriteDrawing.Bounds(data.Sprite, front.X, front.Y, front.Front.Library, image, true, true);
            int left = Math.Max(0, (int)Math.Floor(bounds.Left / sample));
            int top = Math.Max(0, (int)Math.Floor(bounds.Top / sample));
            int right = Math.Min(width, (int)Math.Ceiling(bounds.Right / sample));
            int bottom = Math.Min(height, (int)Math.Ceiling(bounds.Bottom / sample));
            for (int y = top; y < bottom; y++)
            for (int x = left; x < right; x++)
            {
                int px = (int)(x * sample + sample / 2 - bounds.X), py = (int)(y * sample + sample / 2 - bounds.Y);
                if (px >= 0 && px < data.Width && py >= 0 && py < data.Height && data.Pixels[(py * data.Width + px) * 4 + 3] >= 128)
                    coverage[y * width + x] = 3;
            }
        }
        // Flood only transparent exterior. Then fill enclosed components touching
        // visible floor and contained by the planned floor footprint. A native
        // wall can have small transparent seams, letting a courtyard touch floor;
        // that contact alone does not make the courtyard part of the temple.
        var queue = new Queue<int>();
        void Visit(int index) { if (coverage[index] == 0) { coverage[index] = 2; queue.Enqueue(index); } }
        for (int x = 0; x < width; x++) { Visit(x); Visit((height - 1) * width + x); }
        for (int y = 0; y < height; y++) { Visit(y * width); Visit(y * width + width - 1); }
        int processed = 0;
        while (queue.TryDequeue(out int index))
        {
            if ((processed++ & 0xffff) == 0) cancellation.ThrowIfCancellationRequested();
            int x = index % width;
            if (x > 0) Visit(index - 1);
            if (x + 1 < width) Visit(index + 1);
            if (index >= width) Visit(index - width);
            if (index < coverage.Length - width) Visit(index + width);
        }
        var missing = new HashSet<(int X, int Y)>();
        for (int y = 0; y < height; y++)
        {
            cancellation.ThrowIfCancellationRequested();
            for (int x = 0; x < width; x++)
            {
                int start = y * width + x;
                if (coverage[start] != 0) continue;
                var tiles = new HashSet<(int X, int Y)>();
                bool touchesFloor = false, reachesOutside = false;
                coverage[start] = 2; queue.Enqueue(start);
                void Follow(int index)
                {
                    if (coverage[index] == 1) touchesFloor = true;
                    else Visit(index);
                }
                while (queue.TryDequeue(out int index))
                {
                    if ((processed++ & 0xffff) == 0) cancellation.ThrowIfCancellationRequested();
                    int px = index % width, py = index / width;
                    var tile = (px / (2 * cellWidth) * 2, py / (2 * cellHeight) * 2);
                    if (tiles.Add(tile) && !floorRegion.Contains(tile)) reachesOutside = true;
                    if (px > 0) Follow(index - 1);
                    if (px + 1 < width) Follow(index + 1);
                    if (py > 0) Follow(index - width);
                    if (py + 1 < height) Follow(index + width);
                }
                if (touchesFloor && !reachesOutside) missing.UnionWith(tiles);
            }
        }
        return missing;
    }
}
