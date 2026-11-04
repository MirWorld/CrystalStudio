using System.Runtime.InteropServices;
using Avalonia.Platform;
using SkiaSharp;

namespace CrystalStudio.Rendering;

public enum RasterFormat { Bgra32, Pbgra32 }
public static class RasterFormats
{
    public const RasterFormat Bgra32 = RasterFormat.Bgra32;
    public const RasterFormat Pbgra32 = RasterFormat.Pbgra32;
}

public class RasterImage : IImage, IDisposable
{
    private Avalonia.Media.Imaging.Bitmap? native;
    private SKBitmap? skia;
    private SKImage? skiaImage;
    protected byte[] pixels = [];
    public int PixelWidth { get; protected set; }
    public int PixelHeight { get; protected set; }
    public RasterFormat Format { get; protected set; }
    public Size Size => new(PixelWidth, PixelHeight);
    protected RasterImage() { }
    public static RasterImage Create(int width, int height, double dpiX, double dpiY, RasterFormat format, object? palette, byte[] data, int stride)
    {
        var result = new RasterImage { PixelWidth = width, PixelHeight = height, Format = format, pixels = new byte[checked(width * height * 4)] };
        for (int y = 0; y < height; y++) data.AsSpan(y * stride, width * 4).CopyTo(result.pixels.AsSpan(y * width * 4));
        return result;
    }
    internal Avalonia.Media.Imaging.Bitmap NativeBitmap
    {
        get
        {
            lock (this)
            {
                if (native != null) return native;
                var pinned = GCHandle.Alloc(pixels, GCHandleType.Pinned);
                try { native = new Avalonia.Media.Imaging.Bitmap(PixelFormat.Bgra8888,
                    Format == RasterFormat.Pbgra32 ? AlphaFormat.Premul : AlphaFormat.Unpremul,
                    pinned.AddrOfPinnedObject(), new PixelSize(PixelWidth, PixelHeight), new Vector(96, 96), PixelWidth * 4); }
                finally { pinned.Free(); }
                return native;
            }
        }
    }
    internal SKBitmap SkiaBitmap
    {
        get
        {
            lock (this)
            {
                if (skia != null) return skia;
                skia = new SKBitmap(new SKImageInfo(PixelWidth, PixelHeight, SKColorType.Bgra8888,
                    Format == RasterFormat.Pbgra32 ? SKAlphaType.Premul : SKAlphaType.Unpremul));
                Marshal.Copy(pixels, 0, skia.GetPixels(), pixels.Length);
                skia.SetImmutable();
                return skia;
            }
        }
    }
    internal SKImage SkiaImage
    {
        get { lock (this) return skiaImage ??= SKImage.FromBitmap(SkiaBitmap); }
    }
    public void CopyPixels(byte[] destination, int stride, int offset) => CopyPixels(new PixelRect(0, 0, PixelWidth, PixelHeight), destination, stride, offset);
    public void CopyPixels(PixelRect area, byte[] destination, int stride, int offset)
    {
        for (int y = 0; y < area.Height; y++)
            pixels.AsSpan(((area.Y + y) * PixelWidth + area.X) * 4, area.Width * 4).CopyTo(destination.AsSpan(offset + y * stride));
    }
    public void Draw(Avalonia.Media.DrawingContext context, Rect sourceRect, Rect destRect) => context.DrawImage(NativeBitmap, sourceRect, destRect);
    public void Save(Stream stream)
    {
        using var image = SKImage.FromBitmap(SkiaBitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        data.SaveTo(stream);
    }
    public void Dispose() { lock (this) { native?.Dispose(); native = null; skiaImage?.Dispose(); skiaImage = null; skia?.Dispose(); skia = null; } }
    public static RasterImage Load(string path) { using var stream = File.OpenRead(path); return Load(stream); }
    public static RasterImage Load(Stream stream)
    {
        using var decoded = SKBitmap.Decode(stream) ?? throw new InvalidDataException("Invalid image.");
        using var bitmap = new SKBitmap(new SKImageInfo(decoded.Width, decoded.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap)) { canvas.Clear(SKColors.Transparent); canvas.DrawBitmap(decoded, 0, 0); }
        byte[] bytes = new byte[bitmap.ByteCount]; Marshal.Copy(bitmap.GetPixels(), bytes, 0, bytes.Length);
        return Create(bitmap.Width, bitmap.Height, 96, 96, RasterFormat.Pbgra32, null, bytes, bitmap.RowBytes);
    }
}

internal sealed class ConvertedRasterImage : RasterImage
{
    public ConvertedRasterImage(RasterImage source, RasterFormat format, object? palette, double threshold)
    {
        PixelWidth = source.PixelWidth; PixelHeight = source.PixelHeight; Format = format;
        pixels = new byte[PixelWidth * PixelHeight * 4]; source.CopyPixels(pixels, PixelWidth * 4, 0);
        if (source.Format == format) return;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            int a = pixels[i + 3];
            for (int channel = 0; channel < 3; channel++) pixels[i + channel] = format == RasterFormat.Pbgra32
                ? (byte)((pixels[i + channel] * a + 127) / 255)
                : a == 0 ? (byte)0 : (byte)Math.Min(255, (pixels[i + channel] * 255 + a / 2) / a);
        }
    }
}

internal sealed class RasterTarget : RasterImage
{
    public RasterTarget(int width, int height, double dpiX, double dpiY, RasterFormat format)
    { PixelWidth = width; PixelHeight = height; Format = format; }
    public void Render(SceneVisual visual)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(PixelWidth, PixelHeight, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap)) { canvas.Clear(SKColors.Transparent); visual.Drawing.Raster(canvas); }
        pixels = new byte[bitmap.ByteCount]; Marshal.Copy(bitmap.GetPixels(), pixels, 0, pixels.Length);
    }
}

internal sealed class CroppedBitmap : RasterImage
{
    public CroppedBitmap(RasterImage source, PixelRect area)
    {
        PixelWidth = area.Width; PixelHeight = area.Height; Format = source.Format;
        pixels = new byte[checked(PixelWidth * PixelHeight * 4)];
        source.CopyPixels(area, pixels, PixelWidth * 4, 0);
    }
}
