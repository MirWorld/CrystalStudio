namespace CrystalStudio;

public static class TilePatternPreview
{
    // Representative tiles and arrangement match GetTilesPreview in the reference editor.
    public static IReadOnlyList<int> Indices(TilePatternLayout layout, int spriteIndex)
    {
        int block = AutoTileBrush.BlockSize(layout), start = spriteIndex / block * block;
        int[] offsets = layout switch
        {
            TilePatternLayout.Mir2 => [5, 15, 6, 20, 0, 21, 7, 17, 8],
            TilePatternLayout.Mir3 when spriteIndex % 30 == 5 => [18, 22, 17, 26, 5, 27, 16, 20, 15],
            TilePatternLayout.Mir3 => [10, 20, 11, 25, 0, 26, 12, 22, 13],
            TilePatternLayout.Small => [39, 11, 15, 35, 0, 19, 31, 25, 23],
            TilePatternLayout.Mir3Mini => [0, 1, 2, 3],
            _ => [spriteIndex - start]
        };
        return offsets.Select(i => start + i).ToArray();
    }
    public static RasterImage? Create(TilePatternLayout layout, int spriteIndex, Func<int, Sprite?> get, Func<int, bool> resolved)
    {
        var indices = Indices(layout, spriteIndex);
        var sprites = indices.Select(get).ToArray();
        // Request every component, then wait for decoding (including empty/invalid sprites).
        if (indices.Any(i => !resolved(i))) return null;
        int columns = layout == TilePatternLayout.Mir3Mini ? 2 : layout == TilePatternLayout.None ? 1 : 3;
        int step = layout == TilePatternLayout.Small ? 1 : 2;
        double width = columns * step * 48, height = columns * step * 32;
        var visual = new SceneVisual();
        
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new SceneScale(96 / width, 64 / height));
            for (int i = 0; i < sprites.Length; i++)
                if (sprites[i] is Sprite sprite)
                    dc.DrawImage(sprite.Bitmap, new Rect(i % columns * step * 48, i / columns * step * 32, sprite.Bitmap.PixelWidth, sprite.Bitmap.PixelHeight));
            dc.Pop();
        }
        var bitmap = new RasterTarget(96, 64, 96, 96, RasterFormats.Pbgra32);
        bitmap.Render(visual);  return bitmap;
    }
}
