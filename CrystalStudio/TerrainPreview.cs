using System.IO;
namespace CrystalStudio;

public static class TerrainPreview
{
    internal static Rect Region(TerrainPlan plan, bool detail, Point? centre)
    {
        var area = new Rect(plan.Area.X, plan.Area.Y, plan.Area.Width, plan.Area.Height);
        if (!detail) return area;
        var focus = centre ?? (plan.CoastObjects.FirstOrDefault() is { } coast
            ? new Point(coast.X + (coast.Layout.Cells.Max(cell => cell.X) + 1) / 2.0, coast.Y + (coast.Layout.Cells.Max(cell => cell.Y) + 1) / 2.0)
            : area.Center);
        return MapPreviewRegion.Detail(area, focus, 32, 24);
    }
    public static RasterImage Create(TerrainPlan plan, AssetStore assets, bool detail = false,CancellationToken cancellation=default, Point? centre = null)
    {
        if (plan.CoastObjects.Count != 0) return SavedObjectPreview(plan, assets, detail,cancellation,centre);
        var region = Region(plan, detail, centre);
        int x = (int)region.X, y = (int)region.Y, width = (int)region.Width, height = (int)region.Height;
        double scale = Math.Min(1, Math.Min(1600.0 / (width * 48), 1000.0 / (height * 32)));
        int outputWidth = Math.Max(1, (int)Math.Ceiling(width * 48 * scale)), outputHeight = Math.Max(1, (int)Math.Ceiling(height * 32 * scale));
        var sprites = new Dictionary<(int, int), byte[]>();
        var tiles = plan.Tiles.Where(t => t.X >= x && t.Y >= y && t.X < x + width && t.Y < y + height).ToDictionary(t => (t.X, t.Y));
        foreach (var tile in tiles.Values.DistinctBy(t => (t.Library, t.Index)))
        {
            cancellation.ThrowIfCancellationRequested();
            var sprite = assets.Libraries[tile.Library].Decode(tile.Index) ?? throw new InvalidDataException("A catalogue tile is missing.");
            if (sprite.Width != 96 || sprite.Height != 64) throw new InvalidDataException("Unexpected terrain tile dimensions.");
            byte[] pixels = new byte[96 * 64 * 4]; sprite.Bitmap.CopyPixels(pixels, 96 * 4, 0);
            sprites[(tile.Library, tile.Index)] = pixels;
        }
        int columns = (width + 1) / 2;
        var tilePixels = new byte[columns * ((height + 1) / 2)][];
        foreach (var tile in tiles.Values)
            tilePixels[(tile.Y - y) / 2 * columns + (tile.X - x) / 2] = sprites[(tile.Library, tile.Index)];
        var sampleColumns = new int[outputWidth];
        var sampleOffsets = new int[outputWidth];
        for (int px = 0; px < outputWidth; px++)
        {
            int sx = (int)((px + .5) * width * 48 / outputWidth);
            sampleColumns[px] = sx / 96;
            sampleOffsets[px] = sx % 96 * 4;
        }
        // Sample the assembled terrain in one pass. Sampling individual
        // images at fractional zoom can draw artificial lines between tiles.
        byte[] output = new byte[outputWidth * outputHeight * 4];
        for (int py = 0; py < outputHeight; py++)
        {
            cancellation.ThrowIfCancellationRequested();
            int sy = (int)((py + .5) * height * 32 / outputHeight);
            int tileRow = sy / 64 * columns, sourceRow = sy % 64 * 96 * 4;
            for (int px = 0; px < outputWidth; px++)
            {
                byte[] source = tilePixels[tileRow + sampleColumns[px]];
                int src = sourceRow + sampleOffsets[px], dst = (py * outputWidth + px) * 4;
                int alpha = source[src + 3];
                output[dst] = (byte)((source[src] * alpha + 28 * (255 - alpha)) / 255);
                output[dst+1] = (byte)((source[src+1] * alpha + 26 * (255 - alpha)) / 255);
                output[dst+2] = (byte)((source[src+2] * alpha + 24 * (255 - alpha)) / 255);
                output[dst+3] = 255;
            }
        }
        if (plan.Water != null)
        {
            var banks = new Dictionary<(int Library, int Image), (Sprite Sprite, byte[] Pixels)>();
            double scaleX = outputWidth / (width * 48.0), scaleY = outputHeight / (height * 32.0);
            foreach (var bank in plan.Water.Banks)
            {
                cancellation.ThrowIfCancellationRequested();
                if (!banks.TryGetValue((bank.Library, bank.Image), out var decoded))
                {
                    var image = assets.Libraries[bank.Library].Decode(bank.Image - 1) ?? throw new InvalidDataException("A native bank sprite is missing.");
                    byte[] data = new byte[image.Width * image.Height * 4];
                    image.Bitmap.CopyPixels(data, image.Width * 4, 0);
                    banks.Add((bank.Library, bank.Image), decoded = (image, data));
                }
                var sprite = decoded.Sprite;
                int bx = bank.X * 48, by = bank.Y * 32 + 32 - sprite.Height;
                int left = Math.Max(0,(int)Math.Floor((bx-x*48)*scaleX)), top = Math.Max(0,(int)Math.Floor((by-y*32)*scaleY));
                int right = Math.Min(outputWidth,(int)Math.Ceiling((bx+sprite.Width-x*48)*scaleX)), bottom = Math.Min(outputHeight,(int)Math.Ceiling((by+sprite.Height-y*32)*scaleY));
                if (left >= right || top >= bottom) continue;
                byte[] pixels = decoded.Pixels;
                for (int py = top; py < bottom; py++) for (int px = left; px < right; px++)
                {
                    int sx = (int)((px+.5)/scaleX+x*48-bx), sy = (int)((py+.5)/scaleY+y*32-by);
                    if (sx < 0 || sy < 0 || sx >= sprite.Width || sy >= sprite.Height) continue;
                    int src = (sy*sprite.Width+sx)*4, dst = (py*outputWidth+px)*4, alpha = pixels[src+3];
                    for (int channel = 0; channel < 3; channel++) output[dst+channel] = (byte)((pixels[src+channel]*alpha+output[dst+channel]*(255-alpha))/255);
                }
            }
        }
        var bitmap = RasterImage.Create(outputWidth, outputHeight, 96, 96, RasterFormats.Bgra32, null, output, outputWidth * 4);
         return bitmap;
    }

    private static RasterImage SavedObjectPreview(TerrainPlan plan, AssetStore assets, bool detail,CancellationToken cancellation, Point? centre)
    {
        // Use the existing object preview renderer; do not invent a second coast drawing order.
        var cells = plan.Tiles.ToDictionary(t => (t.X,t.Y), t => new Cell { BackLibrary = checked((short)t.Library), BackImage = t.Index + 1 });
        foreach (var coast in plan.CoastObjects) foreach (var saved in coast.Layout.Cells)
        {
            var key = (coast.X+saved.X,coast.Y+saved.Y);
            var cell = saved.Cell.Copy();
            if ((cell.BackImage & 0x1fffffff) == 0 && cells.TryGetValue(key,out var ground)) cell.BackImage |= ground.BackImage;
            cells[key] = cell;
        }
        cancellation.ThrowIfCancellationRequested();
        var selected = cells.Select(pair => new StampCell(pair.Key.Item1, pair.Key.Item2, pair.Value)).ToArray();
        var region = Region(plan, detail, centre);
        return StampPreview.Create(selected, assets, 1600, 1000, cancellation,
            new Rect(region.X * 48, region.Y * 32, region.Width * 48, region.Height * 32), waitForAssets: true, fastRaster: !detail)!;
    }
}
