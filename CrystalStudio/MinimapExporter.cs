using System.IO;
namespace CrystalStudio;

internal static class MinimapExporter
{
    internal static string DefaultFileName(string? mapPath) =>
        (mapPath == null ? "Untitled" : Path.GetFileNameWithoutExtension(mapPath)) + ".png";

    internal static async Task ExportAsync(MapDocument document, AssetStore assets, string path, int? width = null, int? height = null)
    {
        using var bitmap = await CreateAsync(document, assets, width, height).ConfigureAwait(false);
        AtomicFile.Write(path, bitmap.Save);
    }

    internal static bool ValidSize(int width, int height) =>
        width > 0 && height > 0 && (long)width * height <= Array.MaxLength / 4;

    internal static async Task<RasterImage> CreateAsync(MapDocument document, AssetStore assets, int? outputWidth = null, int? outputHeight = null)
    {
        int width = outputWidth ?? checked(document.Width * 12), height = outputHeight ?? checked(document.Height * 8);
        // Validate before decoding artwork or allocating the output buffer.
        if (!ValidSize(width, height))
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
                if (library >= 0 && image >= 0 && !MapSpriteDrawing.SkipFront(library, document.SourceFormat)) commands.Add((x, y, layer, library, image, blend));
            }
        }

        // Decode at export resolution, retaining native dimensions and offsets.
        double scale = Math.Min(width / (document.Width * 48.0), height / (document.Height * 32.0));
        int level = scale >= 1 ? 0 : Math.Clamp((int)Math.Floor(Math.Log2(1 / scale)), 0, 16);
        var sprites = await assets.PrepareOverviewAsync(commands.Select(c => (c.Library, c.Image)), level, default).ConfigureAwait(false);
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
