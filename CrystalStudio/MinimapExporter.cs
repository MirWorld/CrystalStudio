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

    internal static async Task<RasterImage> CreateAsync(MapDocument document, AssetStore assets)
    {
        int width = checked(document.Width * 12), height = checked(document.Height * 8);
        // Validate before decoding artwork or allocating the output buffer.
        if (width <= 0 || height <= 0 || (long)width * height * 4 > Array.MaxLength)
            throw new InvalidOperationException("This map is too large to export as a single PNG.");

        var cells = document.Cells.OrderBy(c => c.Key).ToArray();
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
                Add(cell.MiddleLibrary, cell.MiddleImage - 1, MapSpriteDrawing.MiddleBlend(cell.MiddleFrames));
            else if (layer == 2) foreach (var front in cell.ForegroundImages)
                Add(front.Library, MapPreview.FrontImage(cell, front, false), MapSpriteDrawing.FrontBlend(front.Frames));

            void Add(int library, int image, bool blend)
            {
                if (library >= 0 && image >= 0) commands.Add((x, y, layer, library, image, blend));
            }
        }

        // Decode at export resolution, retaining native dimensions and offsets.
        var sprites = await assets.PrepareOverviewAsync(commands.Select(c => (c.Library, c.Image)), 2, default).ConfigureAwait(false);
        var drawings = commands.Where(c => sprites.ContainsKey((c.Library, c.Image))).Select(c =>
        {
            var sprite = sprites[(c.Library, c.Image)];
            var bounds = MapSpriteDrawing.Bounds(sprite, c.X, c.Y, c.Library, c.Image, c.Layer > 0, c.Layer == 2, c.Blend, document.SourceFormat);
            return (sprite, bounds, c.Blend);
        });
        return PreviewRaster.Create(drawings, new Rect(0, 0, document.Width * 48.0, document.Height * 32.0),
            width, height, default, padding: 0, opaque: true);
    }
}
