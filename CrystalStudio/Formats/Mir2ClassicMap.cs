using System.IO;
using System.Text;

namespace CrystalStudio.Formats;

/// <summary>Mir2 经典地图的三种单元布局。</summary>
public enum Mir2MapKind
{
    /// <summary>52 字节头 + 12 字节 TOldMapInfo（Legend of mir 标题，刀光传奇等）。</summary>
    OldMap,
    /// <summary>52 字节头 + 14 字节 TMapInfo（标题以 CRLF 结尾，含 BkIndex/MidIndex）。</summary>
    NewMap,
    /// <summary>54 字节 TENMapHeader + 15 字节 TENMapInfo（"Map 2010 Ver 1.0"，宽高与 0xAA38 异或）。</summary>
    EnhancedMap,
}

/// <summary>一次格式探测的结果。</summary>
public sealed record Mir2MapInfo(
    Mir2MapKind Kind,
    string Path,
    string Title,
    int Width,
    int Height,
    int HeaderBytes,
    int CellBytes,
    long FileLength,
    int HeaderWidth = 0,
    int HeaderHeight = 0)
{
    public long CellCount => (long)Width * Height;

    /// <summary>头部记录的尺寸与按文件长度推导出的真实尺寸不一致。</summary>
    public bool SizeDerived => HeaderWidth > 0 && (HeaderWidth != Width || HeaderHeight != Height);

    /// <summary>头部声明的格数据之后多出来的字节数（部分地图被写大了，客户端同样只读头部尺寸）。</summary>
    public long ExtraBytes => Math.Max(0, FileLength - HeaderBytes - CellCount * CellBytes);

    public string KindName => Kind switch
    {
        Mir2MapKind.OldMap => "Mir2 经典地图（12 字节单元）",
        Mir2MapKind.NewMap => "Mir2 新式地图（14 字节单元）",
        _ => "Mir2 增强地图（15 字节单元，宽高异或）",
    };
}

/// <summary>地图内容统计，用于“读取分析分类”。</summary>
public sealed record Mir2MapReport(
    Mir2MapInfo Info,
    int GroundCells,
    int MiddleCells,
    int ForegroundCells,
    int Doors,
    int AnimatedCells,
    int LitCells,
    int LowCollisionCells,
    int HighCollisionCells,
    IReadOnlyList<KeyValuePair<int, int>> ObjectAreas,
    int GroundImageMin,
    int GroundImageMax,
    int ForegroundImageMin,
    int ForegroundImageMax)
{
    public string Summary =>
        $"{Info.KindName} {Info.Width}x{Info.Height}（{Info.CellCount} 格，{Info.CellBytes} 字节/格，标题“{Info.Title}”）；" +
        $"地面 {GroundCells} 格、中景 {MiddleCells} 格、前景 {ForegroundCells} 格；" +
        $"门 {Doors}、动画 {AnimatedCells}、光照 {LitCells}；" +
        $"低层碰撞 {LowCollisionCells}、高层碰撞 {HighCollisionCells}；" +
        $"前景库 {string.Join("/", ObjectAreas.Select(pair => pair.Key))}";
}

/// <summary>
/// Mir2 经典地图格式的探测、分类与转换。
///
/// 结构依据《地图编辑器》EdMain.pas：
/// <code>
/// TMapHeader = packed record            // 52 字节
///   Width, Height: Word;
///   Title: string[15];                  // 1 字节长度 + 15 字节
///   UpdateDate: TDateTime;              // 8 字节
///   Reserved: array[0..23] of char;     // 24 字节
/// end;
///
/// TOldMapInfo = packed record           // 12 字节
///   BkImg, MidImg, FrImg: Word;         // 图像索引 + 1，高位 $8000 为阻挡标记
///   DoorIndex, DoorOffset: Byte;        // DoorIndex 的 $80 表示有门
///   AniFrame, AniTick: Byte;            // AniFrame 的 $80 表示动画
///   Area: Byte;                         // 前景图所属 Objects 库
///   light: Byte;
/// end;
/// </code>
/// 图像索引一律为“索引 + 1”，0 表示空，与编辑器 GetBkImg = (BkImg and $7FFF) - 1 一致。
/// </summary>
public static class Mir2ClassicMap
{
    public const int HeaderBytes = 52;
    public const int EnhancedHeaderBytes = 54;
    public const int OldCellBytes = 12;
    public const int NewCellBytes = 14;
    public const int EnhancedCellBytes = 15;
    public const short CollisionXor = unchecked((short)0xAA38);
    public const string LegacyTitle = "Legend of mir";
    public const string EnhancedTitle = "Map 2010 Ver 1.0";
    public const int MaxDimension = 1000;
    private const int LowCollision = 0x20000000;
    private const int HighCollision = 0x40000000;

    /// <summary>只读取文件头判断格式，不做全量解码。</summary>
    public static Mir2MapInfo? Detect(string path)
    {
        if (!File.Exists(path)) return null;
        using var stream = File.OpenRead(path);
        return Detect(stream, path);
    }

    public static Mir2MapInfo? Detect(Stream stream, string path = "")
    {
        long length = stream.Length;
        if (length < HeaderBytes) return null;
        var header = new byte[EnhancedHeaderBytes];
        int read = stream.Read(header, 0, header.Length);
        if (read < HeaderBytes) return null;

        // 增强格式：string[16] 标题 + 宽高与 0xAA38 异或
        if (header[0] == (byte)EnhancedTitle.Length && Matches(header, 1, EnhancedTitle))
        {
            int width = BitConverter.ToInt16(header, 21) ^ CollisionXor;
            int height = BitConverter.ToInt16(header, 25) ^ CollisionXor;
            if (Valid(width, height) && length == EnhancedHeaderBytes + (long)width * height * EnhancedCellBytes)
                return new(Mir2MapKind.EnhancedMap, path, EnhancedTitle, width, height,
                    EnhancedHeaderBytes, EnhancedCellBytes, length);
        }

        int w = BitConverter.ToInt16(header, 0);
        int h = BitConverter.ToInt16(header, 2);
        if (!Valid(w, h)) return null;
        string title = ReadTitle(header);
        long cells = (long)w * h;

        if (length >= HeaderBytes + cells * OldCellBytes)
            return new(Mir2MapKind.OldMap, path, title, w, h, HeaderBytes, OldCellBytes, length);
        if (length >= HeaderBytes + cells * NewCellBytes)
            return new(Mir2MapKind.NewMap, path, title, w, h, HeaderBytes, NewCellBytes, length);
        return null;
    }

    /// <summary>
    /// 头部尺寸偏小时按总格数反推真实尺寸：取 W≥头宽、H≥头高、W×H=总格数，
    /// 且宽高比最接近头部记录的那个解。
    /// </summary>
    internal static (int Width, int Height) DeriveDimensions(int headerWidth, int headerHeight, long cells)
    {
        double target = Math.Log((double)headerWidth / headerHeight);
        int bestWidth = headerWidth, bestHeight = headerHeight;
        double best = double.MaxValue;
        for (int width = headerWidth; (long)width * headerHeight <= cells && width <= MaxDimension * 2; width++)
        {
            if (cells % width != 0) continue;
            long height = cells / width;
            if (height < headerHeight || height > MaxDimension * 2) continue;
            double score = Math.Abs(Math.Log((double)width / height) - target);
            if (score >= best) continue;
            best = score; bestWidth = width; bestHeight = (int)height;
        }
        return (bestWidth, bestHeight);
    }

    private static bool Valid(int width, int height) =>
        width > 0 && height > 0 && width <= MaxDimension && height <= MaxDimension;

    private static bool Matches(byte[] buffer, int offset, string text)
    {
        if (buffer.Length < offset + text.Length) return false;
        for (int i = 0; i < text.Length; i++) if (buffer[offset + i] != (byte)text[i]) return false;
        return true;
    }

    private static string ReadTitle(byte[] header)
    {
        int length = Math.Clamp((int)header[4], 0, 15);
        if (length == 0) return string.Empty;
        return TitleEncoding.GetString(header, 5, length).TrimEnd('\0', '\r', '\n', ' ');
    }

    // 标题是 GBK 字节串（如“Mir2地图 文件”）；缺少代码页提供程序时退回 Latin1，只影响显示。
    private static readonly Encoding TitleEncoding = CreateTitleEncoding();

    private static Encoding CreateTitleEncoding()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(936);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or TypeInitializationException)
        {
            return Encoding.Latin1;
        }
    }

    /// <summary>解码全部格子并统计，用于分类报告。</summary>
    public static Mir2MapReport Analyze(Mir2MapInfo info)
    {
        byte[] data = File.ReadAllBytes(info.Path);
        int ground = 0, middle = 0, front = 0, doors = 0, animated = 0, lit = 0, low = 0, high = 0;
        int groundMin = int.MaxValue, groundMax = -1, frontMin = int.MaxValue, frontMax = -1;
        var areas = new Dictionary<int, int>();
        foreach (var (_, _, cell) in ReadCells(info, data))
        {
            int back = cell.BackImage & 0x1FFFFFFF;
            if (back != 0)
            {
                ground++;
                groundMin = Math.Min(groundMin, back - 1);
                groundMax = Math.Max(groundMax, back - 1);
            }
            if (cell.MiddleImage != 0) middle++;
            if (cell.Foreground is { } f && (f.Image & 0x7FFF) != 0)
            {
                front++;
                frontMin = Math.Min(frontMin, (f.Image & 0x7FFF) - 1);
                frontMax = Math.Max(frontMax, (f.Image & 0x7FFF) - 1);
                areas[f.Library] = areas.GetValueOrDefault(f.Library) + 1;
            }
            if (cell.Door != 0) doors++;
            if (cell.MiddleFrames != 0) animated++;
            if (cell.Light != 0) lit++;
            if ((cell.BackImage & LowCollision) != 0) low++;
            if ((cell.BackImage & HighCollision) != 0) high++;
        }
        return new Mir2MapReport(info, ground, middle, front, doors, animated, lit, low, high,
            areas.OrderBy(pair => pair.Key).ToList(),
            groundMax < 0 ? 0 : groundMin, groundMax,
            frontMax < 0 ? 0 : frontMin, frontMax);
    }

    /// <summary>按列优先（x 外层、y 内层）解码全部格子，与编辑器 aMapFile.Read(MArr[i, 0], ...) 一致。</summary>
    public static IEnumerable<(int X, int Y, Cell Cell)> ReadCells(Mir2MapInfo info, byte[] data)
    {
        int stride = info.CellBytes;
        long required = (long)info.HeaderBytes + (long)info.Width * info.Height * stride;
        if (data.LongLength < required) throw new InvalidDataException("Map cell data is truncated.");
        int offset = info.HeaderBytes;
        for (int x = 0; x < info.Width; x++)
        {
            for (int y = 0; y < info.Height; y++, offset += stride)
            {
                yield return (x, y, DecodeCell(info.Kind, data, offset));
            }
        }
    }

    private static Cell DecodeCell(Mir2MapKind kind, byte[] data, int offset)
    {
        int backRaw, middleRaw, frontRaw;
        byte doorIndex, doorOffset, aniFrame, aniTick, area, light;
        byte backIndex = 0, middleIndex = 1;

        if (kind == Mir2MapKind.EnhancedMap)
        {
            backRaw = BitConverter.ToUInt16(data, offset) ^ CollisionXor;
            int backNot = BitConverter.ToUInt16(data, offset + 2) ^ CollisionXor;
            middleRaw = BitConverter.ToUInt16(data, offset + 4) ^ CollisionXor;
            frontRaw = BitConverter.ToUInt16(data, offset + 6) ^ CollisionXor;
            doorIndex = data[offset + 8]; doorOffset = data[offset + 9];
            aniFrame = data[offset + 10]; aniTick = data[offset + 11];
            area = data[offset + 12]; light = data[offset + 13];
            // BkImgNot 的 0x2000 位还原为 BkImg 的阻挡标记（编辑器 LoadFromFile 的做法）。
            if ((backNot ^ 0xAA38) == 0x2000) backRaw |= 0x8000;
        }
        else
        {
            backRaw = BitConverter.ToUInt16(data, offset);
            middleRaw = BitConverter.ToUInt16(data, offset + 2);
            frontRaw = BitConverter.ToUInt16(data, offset + 4);
            doorIndex = data[offset + 6]; doorOffset = data[offset + 7];
            aniFrame = data[offset + 8]; aniTick = data[offset + 9];
            area = data[offset + 10]; light = data[offset + 11];
            if (kind == Mir2MapKind.NewMap) { backIndex = data[offset + 12]; middleIndex = data[offset + 13]; }
        }

        var cell = new Cell
        {
            BackLibrary = backIndex,
            BackImage = backRaw & 0x7FFF,
            MiddleLibrary = middleIndex,
            MiddleImage = (ushort)(middleRaw & 0x7FFF),
            Door = (byte)(doorIndex & 0x7F),
            DoorOffset = (byte)(doorOffset & 0x7F),
            MiddleFrames = (byte)(aniFrame & 0x7F),
            MiddleTick = aniTick,
            Light = light,
        };
        if ((backRaw & 0x8000) != 0) cell.BackImage |= LowCollision;
        if ((frontRaw & 0x7FFF) != 0)
        {
            // Area 是 Objects 库序号，编辑器库表中 Objects 从 2 开始（0=Tiles，1=SmTiles）。
            cell.Foreground = new((short)(area + 2), (short)(frontRaw & 0x7FFF), (byte)(aniFrame & 0x7F), aniTick);
            if ((frontRaw & 0x8000) != 0) cell.BackImage |= HighCollision;
        }
        return cell;
    }

    /// <summary>把 Mir2 经典地图转换成编辑器的 MapDocument。</summary>
    public static MapDocument Load(string path, Mir2MapInfo info, AssetSourceFormat format = AssetSourceFormat.Default)
    {
        byte[] data = File.ReadAllBytes(path);
        var document = new MapDocument(info.Width, info.Height) { SourceFormat = format, SourceMir2Kind = info.Kind };
        foreach (var (x, y, cell) in ReadCells(info, data)) document.Set(x, y, cell);
        return document;
    }

    /// <summary>检测并转换；不是该格式时返回 null。</summary>
    public static MapDocument? TryLoad(string path, AssetSourceFormat format = AssetSourceFormat.Default) =>
        Detect(path) is { } info ? Load(path, info, format) : null;

    /// <summary>把该格式写成编辑器原生的 Crystal v1（C#）地图，之后可按当前格式直接读取。</summary>
    public static MapDocument ConvertToCrystal(string path, string? target = null)
    {
        if (Detect(path) is not { } info) throw new InvalidDataException("Not a Mir2 classic map: " + path);
        var document = Load(path, info);
        document.Save(target ?? System.IO.Path.ChangeExtension(path, ".crystal.map"));
        return document;
    }

    /// <summary>批量检测目录中的地图并给出分类清单。</summary>
    public static IReadOnlyList<Mir2MapInfo> Scan(string directory)
    {
        var found = new List<Mir2MapInfo>();
        foreach (string path in Directory.EnumerateFiles(directory))
        {
            string extension = System.IO.Path.GetExtension(path);
            if (!extension.Equals(".map", StringComparison.OrdinalIgnoreCase) &&
                !extension.Equals(".ycl", StringComparison.OrdinalIgnoreCase)) continue;
            if (Detect(path) is { } info) found.Add(info);
        }
        found.Sort((a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));
        return found;
    }
}
