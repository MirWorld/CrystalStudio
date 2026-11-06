using System.IO;
namespace CrystalStudio;

internal static class MinimapExporter
{
    internal static string DefaultFileName(string? mapPath) =>
        (mapPath == null ? "Untitled" : Path.GetFileNameWithoutExtension(mapPath)) + ".png";

    internal static async Task ExportAsync(MapDocument document, AssetStore assets, string path)
    {
        var bitmap = await CreateAsync(document, assets).ConfigureAwait(false);
        AtomicFile.Write(path, bitmap.Save);
    }

    internal static async Task<RasterImage> CreateAsync(MapDocument document, AssetStore assets, Rect? region = null, int pixelsPerCell = 12)
    {
        // region 用世界坐标（48×32/格）给出要渲染的范围；默认整图。
        // pixelsPerCell 是每格的输出像素宽度，48 即编辑器 100% 缩放，24 即 50%。
        var world = region ?? new Rect(0, 0, document.Width * 48.0, document.Height * 32.0);
        double scale = pixelsPerCell / 48.0;
        int width = checked((int)Math.Round(world.Width * scale)), height = checked((int)Math.Round(world.Height * scale));
        // Validate before decoding artwork or allocating the output buffer.
        if (width <= 0 || height <= 0 || (long)width * height * 4 > Array.MaxLength)
            throw new InvalidOperationException("This map is too large to export as a single PNG.");

        var cells = document.Cells.OrderBy(c => c.Key).ToArray();
        // 渲染流程按地图源格式分叉：Mir2 经典地图走 Mir2 锚点，不套 Crystal 的库号特判。
        var layout = MapSpriteLayout.For(document);
        var commands = new List<(int X, int Y, int Layer, int Library, int Image, bool Blend)>();
        // Match the editor's layer and row order, using the first animation frame
        // and closed doors. Export every artwork layer regardless of view settings.
        for (int layer = 0; layer < 3; layer++) foreach (var entry in cells)
        {
            int x = entry.Key % document.Width, y = entry.Key / document.Width;
            var cell = entry.Value;
            if (layer == 0 && x % 2 == 0 && y % 2 == 0)
                Add(cell.BackLibrary, (cell.BackImage & 0x1FFFFFFF) - 1, false);
            else if (layer == 1)
                Add(cell.MiddleLibrary, cell.MiddleImage - 1, layout.MiddleBlend(cell.MiddleFrames));
            else if (layer == 2) foreach (var front in cell.ForegroundImages)
                Add(front.Library, MapPreview.FrontImage(cell, front, false), layout.FrontBlend(front.Frames));

            void Add(int library, int image, bool blend)
            {
                if (library >= 0 && image >= 0) commands.Add((x, y, layer, library, image, blend));
            }
        }

        // 按输出分辨率选择解码级别：12px/格的小地图用 2 级缩略图省内存，
        // 放大到 24px/格以上时必须用原图，否则会拿缩小 4 倍的位图去放大，画面发虚。
        int level = pixelsPerCell >= 24 ? 0 : 2;
        var sprites = await assets.PrepareOverviewAsync(commands.Select(c => (c.Library, c.Image)), level, default).ConfigureAwait(false);
        var drawings = commands.Where(c => sprites.ContainsKey((c.Library, c.Image))).Select(c =>
        {
            var sprite = sprites[(c.Library, c.Image)];
            var bounds = MapSpriteDrawing.Bounds(layout, sprite, c.X, c.Y, c.Library, c.Image, c.Layer > 0, c.Layer == 2, c.Blend);
            return (sprite, bounds, c.Blend);
        });
        return PreviewRaster.Create(drawings, world, width, height, default, padding: 0, opaque: true);
    }
}
