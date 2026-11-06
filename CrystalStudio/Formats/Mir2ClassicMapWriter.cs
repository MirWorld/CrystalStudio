using System.IO;
using System.Text;

namespace CrystalStudio.Formats;

/// <summary>
/// 把编辑器内的 <see cref="MapDocument"/> 写回 Mir2 经典地图的三种单元布局。
///
/// 与 <see cref="Mir2ClassicMap.ReadCells"/> 严格互逆：
/// <code>
/// TOldMapInfo = packed record          // 12 字节
///   BkImg, MidImg, FrImg: Word;        // 索引 + 1；BkImg/FrImg 的 $8000 位是阻挡标记
///   DoorIndex, DoorOffset: Byte;
///   AniFrame, AniTick: Byte;           // AniFrame 的 $80 位是前景混合标记
///   Area: Byte;                        // Objects 库序号，编辑器库表里 Objects = Area + 2
///   light: Byte;
/// end;
/// TMapInfo    = TOldMapInfo + BkIndex, MidIndex   // 14 字节
/// TENMapInfo  = 15 字节，三个图像字与 0xAA38 异或，并多一个 BkImgNot 备份
/// </code>
/// </summary>
public static class Mir2ClassicMapWriter
{
    private const int LowCollision = 0x20000000;
    private const int HighCollision = 0x40000000;
    private const short CollisionXor = unchecked((short)0xAA38);
    private const string LegacyTitle = "Legend of mir";
    private const string EnhancedTitle = "Map 2010 Ver 1.0";

    /// <summary>该格式使用的单元格字节数。</summary>
    public static int CellBytes(Mir2MapKind kind) => kind switch
    {
        Mir2MapKind.OldMap => Mir2ClassicMap.OldCellBytes,
        Mir2MapKind.NewMap => Mir2ClassicMap.NewCellBytes,
        _ => Mir2ClassicMap.EnhancedCellBytes,
    };

    /// <summary>该格式使用的文件头字节数。</summary>
    public static int HeaderBytes(Mir2MapKind kind) =>
        kind == Mir2MapKind.EnhancedMap ? Mir2ClassicMap.EnhancedHeaderBytes : Mir2ClassicMap.HeaderBytes;

    /// <summary>格式显示名，供保存对话框和设置下拉使用。</summary>
    public static string DisplayName(Mir2MapKind kind) => kind switch
    {
        Mir2MapKind.OldMap => "Mir2 经典地图（12 字节单元）",
        Mir2MapKind.NewMap => "Mir2 新式地图（14 字节单元）",
        _ => "Mir2 增强地图（15 字节单元）",
    };

    /// <summary>把文档写成指定的 Mir2 经典格式。</summary>
    public static void Save(MapDocument document, string path, Mir2MapKind kind, string? title = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.Width <= 0 || document.Height <= 0) throw new InvalidDataException("Map dimensions are invalid.");
        if (document.Width > Mir2ClassicMap.MaxDimension || document.Height > Mir2ClassicMap.MaxDimension)
            throw new InvalidDataException($"Mir2 maps are limited to {Mir2ClassicMap.MaxDimension} cells per side.");

        int headerBytes = HeaderBytes(kind);
        int cellBytes = CellBytes(kind);
        long total = (long)headerBytes + (long)document.Width * document.Height * cellBytes;
        if (total > int.MaxValue) throw new InvalidDataException("Map is too large for the Mir2 layout.");

        byte[] buffer = new byte[total];
        WriteHeader(buffer, document, kind, title);

        int offset = headerBytes;
        // 与读取端一致：列优先，x 外层、y 内层。
        for (int x = 0; x < document.Width; x++)
        {
            for (int y = 0; y < document.Height; y++, offset += cellBytes)
                EncodeCell(buffer, offset, kind, document.Get(x, y));
        }

        AtomicFile.Write(path, stream => stream.Write(buffer, 0, buffer.Length));
    }

    private static void WriteHeader(byte[] buffer, MapDocument document, Mir2MapKind kind, string? title)
    {
        int headerBytes = HeaderBytes(kind);
        double stamp = DateTime.Now.ToOADate();

        BitConverter.TryWriteBytes(buffer.AsSpan(0, 2), (ushort)document.Width);
        BitConverter.TryWriteBytes(buffer.AsSpan(2, 2), (ushort)document.Height);

        if (kind == Mir2MapKind.EnhancedMap)
        {
            // 增强头布局与 52 字节头不同：0 = 标题长度，1..16 = 标题，21/25 = 与 0xAA38
            // 异或后的宽高。其余字节保留为 0，Detect 只依赖标题和宽高。
            buffer[0] = (byte)EnhancedTitle.Length;
            WriteFixed(buffer, 1, 16, EnhancedTitle);
            BitConverter.TryWriteBytes(buffer.AsSpan(21, 2), (ushort)(document.Width ^ unchecked((ushort)CollisionXor)));
            BitConverter.TryWriteBytes(buffer.AsSpan(25, 2), (ushort)(document.Height ^ unchecked((ushort)CollisionXor)));
            BitConverter.TryWriteBytes(buffer.AsSpan(30, 8), stamp);
            return;
        }

        WriteTitle(buffer, 4, string.IsNullOrWhiteSpace(title) ? LegacyTitle : title!);
        BitConverter.TryWriteBytes(buffer.AsSpan(20, 8), stamp);
        // Reserved[24] 用于标注写入端，方便日后核对来源。
        WriteFixed(buffer, 28, Math.Min(24, headerBytes - 28), "CrystalStudio");
    }

    private static void WriteTitle(byte[] buffer, int offset, string title)
    {
        byte[] bytes = TitleEncoding.GetBytes(title);
        int length = Math.Min(15, bytes.Length);
        buffer[offset] = (byte)length;
        Array.Copy(bytes, 0, buffer, offset + 1, length);
    }

    private static void WriteFixed(byte[] buffer, int offset, int room, string text)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(text);
        Array.Copy(bytes, 0, buffer, offset, Math.Min(room, bytes.Length));
    }

    private static void EncodeCell(byte[] buffer, int offset, Mir2MapKind kind, Cell cell)
    {
        int backRaw = cell.BackImage & 0x7FFF;
        if ((cell.BackImage & LowCollision) != 0) backRaw |= 0x8000;
        int middleRaw = cell.MiddleImage & 0x7FFF;

        var front = cell.Foreground;
        int frontRaw = front is null ? 0 : front.Image & 0x7FFF;
        if ((cell.BackImage & HighCollision) != 0 && frontRaw != 0) frontRaw |= 0x8000;

        // DoorIndex 原样写回：编辑器只读取低 7 位，高位由服务端语义决定，
        // 不在这里自作主张补 0x80，往返时才能保持字节一致。
        byte doorIndex = cell.Door;

        // AniFrame/AniTick 由中景和前景共用：有中景时用中景的动画，否则用前景的。
        byte aniFrame = cell.MiddleImage != 0 ? cell.MiddleFrames : front?.Frames ?? 0;
        byte aniTick = cell.MiddleImage != 0 ? cell.MiddleTick : front?.Tick ?? 0;
        if (front is not null && (front.Frames & 0x80) != 0) aniFrame |= 0x80;

        byte area = 0;
        if (front is { Library: >= 2 and <= 255 }) area = (byte)(front.Library - 2);

        if (kind == Mir2MapKind.EnhancedMap)
        {
            const ushort bx = unchecked((ushort)0xAA38);
            ushort back = (ushort)(backRaw ^ bx);
            // BkImgNot 是 BkImg 的备份，阻挡位在 Not 值里表现为 0x2000。
            ushort backNot = (ushort)(((backRaw & 0x8000) != 0 ? backRaw | 0x2000 : backRaw) ^ bx);
            BitConverter.TryWriteBytes(buffer.AsSpan(offset, 2), back);
            BitConverter.TryWriteBytes(buffer.AsSpan(offset + 2, 2), backNot);
            BitConverter.TryWriteBytes(buffer.AsSpan(offset + 4, 2), (ushort)(middleRaw ^ bx));
            BitConverter.TryWriteBytes(buffer.AsSpan(offset + 6, 2), (ushort)(frontRaw ^ bx));
            buffer[offset + 8] = doorIndex;
            buffer[offset + 9] = cell.DoorOffset;
            buffer[offset + 10] = aniFrame;
            buffer[offset + 11] = aniTick;
            buffer[offset + 12] = area;
            buffer[offset + 13] = cell.Light;
            buffer[offset + 14] = 0;
            return;
        }

        BitConverter.TryWriteBytes(buffer.AsSpan(offset, 2), (ushort)backRaw);
        BitConverter.TryWriteBytes(buffer.AsSpan(offset + 2, 2), (ushort)middleRaw);
        BitConverter.TryWriteBytes(buffer.AsSpan(offset + 4, 2), (ushort)frontRaw);
        buffer[offset + 6] = doorIndex;
        buffer[offset + 7] = cell.DoorOffset;
        buffer[offset + 8] = aniFrame;
        buffer[offset + 9] = aniTick;
        buffer[offset + 10] = area;
        buffer[offset + 11] = cell.Light;

        if (kind == Mir2MapKind.NewMap)
        {
            // BkIndex/MidIndex 是背景与中景所属的图像库编号。
            buffer[offset + 12] = (byte)Math.Clamp((int)cell.BackLibrary, 0, 255);
            buffer[offset + 13] = (byte)Math.Clamp((int)cell.MiddleLibrary, 0, 255);
        }
    }

    private static readonly Encoding TitleEncoding = CreateTitleEncoding();

    private static Encoding CreateTitleEncoding()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(936);
        }
        catch (Exception)
        {
            return Encoding.Latin1;
        }
    }
}
