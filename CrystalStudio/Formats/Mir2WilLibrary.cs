using System.IO;
using System.Text;

namespace CrystalStudio.Formats;

/// <summary>Mir2 经典素材库（.wil + .wix）的单张图像头。</summary>
public readonly record struct Mir2ImageHeader(int Width, int Height, int OffsetX, int OffsetY);

/// <summary>
/// Mir2 经典 .wil/.wix 素材库读取器（8 位调色板版本，刀光传奇等客户端使用）。
///
/// 布局依据《地图编辑器》o_WIL.pas 的 TWMImageHeader / TWMImageInfo：
/// <list type="bullet">
/// <item>WIL 头 56 字节：44 处 ImageCount、48 处调色板颜色数 256、52 处调色板字节数 1024。</item>
/// <item>调色板 56..1079，256 项 BGRA（TRgbQuad）。</item>
/// <item>每张图像 8 字节头：Width、Height、OffsetX、OffsetY 各 Int16，随后 Width*Height 字节调色板索引。</item>
/// <item>索引 0 为透明色（LoadDxImage 的 TransparentColor := 0）。</item>
/// <item>WIX 头 48 字节：44 处 IndexCount，48 起为每张图像的 Int32 文件偏移。</item>
/// </list>
/// </summary>
public sealed class Mir2WilLibrary : IDisposable
{
    public const int WilHeaderBytes = 56;
    public const int PaletteOffset = 56;
    public const int PaletteBytes = 1024;
    public const int ImageHeaderBytes = 8;
    public const int WixHeaderBytes = 48;
    private const string WilSignature = "#ILIB v1.0-WEMADE Entertainment inc.";
    private const string WixSignature = "#INDX v1.0-WEMADE Entertainment inc.";

    private readonly FileStream stream;
    private readonly int[] offsets;
    private readonly byte[] palette = new byte[PaletteBytes];

    public string Path { get; }
    public int Count => offsets.Length;
    public ReadOnlySpan<byte> Palette => palette;

    private Mir2WilLibrary(string wilPath, FileStream stream, int[] offsets)
    {
        Path = wilPath;
        this.stream = stream;
        this.offsets = offsets;
    }

    /// <summary>尝试按 Mir2 经典格式打开素材库；不是该格式时返回 null，不抛异常。</summary>
    public static Mir2WilLibrary? TryOpen(string wilPath)
    {
        if (!File.Exists(wilPath)) return null;
        var stream = File.OpenRead(wilPath);
        try
        {
            if (stream.Length < WilHeaderBytes + PaletteBytes) { stream.Dispose(); return null; }
            var header = new byte[WilHeaderBytes];
            stream.ReadExactly(header, 0, header.Length);
            if (!HasSignature(header, WilSignature)) { stream.Dispose(); return null; }

            int count = BitConverter.ToInt32(header, 44);
            if (count <= 0 || count > 2_000_000) { stream.Dispose(); return null; }

            string wixPath = FindWix(wilPath);
            if (wixPath is null) { stream.Dispose(); return null; }
            var offsets = ReadWix(wixPath, count);
            if (offsets is null) { stream.Dispose(); return null; }

            var library = new Mir2WilLibrary(wilPath, stream, offsets);
            stream.Position = PaletteOffset;
            stream.ReadExactly(library.palette, 0, PaletteBytes);
            return library;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EndOfStreamException)
        {
            stream.Dispose();
            return null;
        }
    }

    /// <summary>打开素材库，格式不符时抛出 InvalidDataException。</summary>
    public static Mir2WilLibrary Open(string wilPath) =>
        TryOpen(wilPath) ?? throw new InvalidDataException("Not a Mir2 .wil/.wix library: " + wilPath);

    private static bool HasSignature(byte[] buffer, string signature)
    {
        // 文件头首字节是 '#'（#ILIB / #INDX），个别版本省略该前缀，两种都接受。
        if (Matches(buffer, 0, signature)) return true;
        return signature.Length > 1 && Matches(buffer, 0, signature[1..]);
    }

    private static bool Matches(byte[] buffer, int offset, string text)
    {
        if (buffer.Length < offset + text.Length) return false;
        for (int i = 0; i < text.Length; i++) if (buffer[offset + i] != (byte)text[i]) return false;
        return true;
    }

    private static string? FindWix(string wilPath)
    {
        string stem = System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(wilPath) ?? string.Empty,
            System.IO.Path.GetFileNameWithoutExtension(wilPath));
        foreach (string extension in new[] { ".WIX", ".wix", ".Wix" })
        {
            string candidate = stem + extension;
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static int[]? ReadWix(string wixPath, int expected)
    {
        using var wix = File.OpenRead(wixPath);
        if (wix.Length < WixHeaderBytes) return null;
        var header = new byte[WixHeaderBytes];
        wix.ReadExactly(header, 0, header.Length);
        if (!HasSignature(header, WixSignature)) return null;
        int count = BitConverter.ToInt32(header, 44);
        if (count <= 0 || count > 2_000_000) return null;
        if (WixHeaderBytes + (long)count * 4 > wix.Length) return null;
        var table = new byte[count * 4];
        wix.ReadExactly(table, 0, table.Length);
        var offsets = new int[count];
        for (int i = 0; i < count; i++) offsets[i] = BitConverter.ToInt32(table, i * 4);
        return offsets.Length == expected || expected <= 0 ? offsets : offsets;
    }

    /// <summary>读取单张图像头；索引越界或数据损坏时返回 null。</summary>
    public Mir2ImageHeader? ReadHeader(int index)
    {
        if (index < 0 || index >= offsets.Length) return null;
        Span<byte> buffer = stackalloc byte[ImageHeaderBytes];
        if (!ReadAt(offsets[index], buffer)) return null;
        int width = BitConverter.ToInt16(buffer[..2]);
        int height = BitConverter.ToInt16(buffer[2..4]);
        if (width <= 0 || height <= 0 || width > 8192 || height > 8192) return null;
        return new Mir2ImageHeader(width, height, BitConverter.ToInt16(buffer[4..6]), BitConverter.ToInt16(buffer[6..8]));
    }

    /// <summary>把一张图像解码为 BGRA32 像素（索引 0 为全透明）。</summary>
    public byte[]? Decode(int index, out Mir2ImageHeader header)
    {
        header = default;
        if (ReadHeader(index) is not { } info) return null;
        header = info;
        int pixels = info.Width * info.Height;
        var raw = new byte[pixels];
        if (!ReadAt(offsets[index] + ImageHeaderBytes, raw)) return null;
        var bgra = new byte[pixels * 4];
        // WIL 的像素行是自下而上存放的：客户端 o_WIL.pas 的 LoadDxImage 从
        // imginfo.Height-1 倒着往 DirectDraw 表面拷，LoadBmpImage 则直接填进
        // bottom-up 的 TDib。转成自上而下的 BGRA 必须翻转行序，漏掉这一步所有
        // 素材都会上下颠倒（旗帜倒挂、树根朝上）。
        for (int row = 0; row < info.Height; row++)
        {
            int src = (info.Height - 1 - row) * info.Width;
            int dst = row * info.Width;
            for (int col = 0; col < info.Width; col++)
            {
                byte entry = raw[src + col];
                if (entry == 0) continue; // 索引 0 是透明色（TransparentColor := 0）
                int p = entry * 4, o = (dst + col) * 4;
                bgra[o + 0] = palette[p + 0];
                bgra[o + 1] = palette[p + 1];
                bgra[o + 2] = palette[p + 2];
                bgra[o + 3] = 255;
            }
        }
        return bgra;
    }

    private bool ReadAt(int position, Span<byte> destination)
    {
        if (position < 0 || (long)position + destination.Length > stream.Length) return false;
        stream.Position = position;
        int read = 0;
        while (read < destination.Length)
        {
            int chunk = stream.Read(destination[read..]);
            if (chunk <= 0) return false;
            read += chunk;
        }
        return true;
    }

    public void Dispose() => stream.Dispose();
}
