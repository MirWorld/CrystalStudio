namespace CrystalStudio;

internal static class MapPreview
{
    // Crystal client: GameScene.DrawObjects/DrawLights and DXManager.CreateLights.
    internal static int FrontImage(Cell cell, Front front, bool doorsOpen)
    {
        int image = (front.Image & 0x7FFF) - 1;
        return image < 0 ? image : image + (doorsOpen && (cell.Door & 0x7F) != 0 ? cell.DoorOffset : 0);
    }

    internal static bool HasLight(Cell cell) => cell.Light != 0;
    internal static bool HasDoor(Cell cell) => cell.Door != 0 || cell.DoorOffset != 0;
    internal static bool EmitsLight(Cell cell) => cell.Light is > 0 and < 10 &&
        cell.Foreground is { Library: >= 0 } front && (front.Image & 0x7FFF) != 0;

    private static readonly Size[] lightSizes =
    [new(125,95), new(205,156), new(285,217), new(365,277), new(445,338),
        new(525,399), new(605,460), new(685,521), new(765,581), new(845,642), new(925,703)];

    internal static Rect LightBounds(int x, int y, byte light, Vector offset)
    {
        int index = Math.Min(light * 3, 9);
        // The client creates textures starting at LightSizes[1], but positions
        // them using LightSizes[index]. Preserve that one-entry difference.
        var positionSize = lightSizes[index]; var textureSize = lightSizes[index + 1];
        return new(x * 48 - positionSize.Width / 2 - 14 + offset.X,
            y * 32 + 11 - positionSize.Height / 2 + offset.Y, textureSize.Width, textureSize.Height);
    }

    private static readonly Dictionary<int, (byte[] Pixels, int Width, int Height)> textures = [];
    internal static (byte[] Pixels, int Width, int Height) LightTexture(byte light)
    {
        int width = light == 1 ? 445 : light == 2 ? 685 : 925;
        if (textures.TryGetValue(width, out var texture)) return texture;
        // Generated with the client's GDI+ PathGradientBrush setup, including
        // its alpha falloff. Premultiplication reproduces SourceAlpha * colour.
        using var stream = typeof(MapPreview).Assembly.GetManifestResourceStream($"CrystalStudio.PreviewLights.{width}.png")!;
        var image = RasterImage.Load(stream);
        var source = new ConvertedRasterImage(image, RasterFormats.Pbgra32, null, 0);
        byte[] rgba = new byte[width * source.PixelHeight * 4]; source.CopyPixels(rgba, width * 4, 0);
        byte[] intensity = new byte[width * source.PixelHeight];
        for (int i = 0; i < intensity.Length; i++) intensity[i] = rgba[i * 4];
        texture = (intensity, width, source.PixelHeight); textures[width] = texture; return texture;
    }
}
