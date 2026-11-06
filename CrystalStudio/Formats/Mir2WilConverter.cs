using System.IO;
using System.IO.Compression;

namespace CrystalStudio.Formats;

/// <summary>
/// 把 Mir2 经典 .wil/.wix 素材库转换成编辑器默认（Crystal）格式的 .lib 文件。
///
/// 输出的 .lib 为 Crystal v3：<c>int32 版本(3) + int32 数量 + int32 保留 + int32 偏移表</c>，
/// 每张图像为 <c>int16 w,h,x,y + int16 保留 ×2 + byte 保留 + int32 长度 + gzip(BGRA32)</c>，
/// 与 <see cref="CrystalLibrary"/> 的读取逻辑一致。转换后放入 <c>WemadeMir2</c> 子目录即可被
/// <see cref="AssetStore"/> 的默认库表直接识别（0=Tiles、1=SmTiles、2=Objects、3=Objects2…）。
///
/// <para>像素行序：WIL 是自下而上存放的（客户端 o_WIL.pas 的 LoadDxImage 从 Height-1 倒序拷入
/// DirectDraw 表面，LoadBmpImage 直接填 bottom-up 的 TDib），所以
/// <see cref="Mir2WilLibrary.Decode"/> 会翻转行序后再写成自上而下的 BGRA32。
/// 漏掉这一步会让所有素材上下颠倒——屋顶翻到地基下面、树根朝上。</para>
/// </summary>
public static class Mir2WilConverter
{
    public const int LibVersion = 3;
    public const int ImageRecordHeaderBytes = 17;
    public const string DefaultLibraryFolder = "WemadeMir2";

    /// <summary>按编辑器库表把库文件名映射到输出名与库序号；无法识别时返回 null。</summary>
    public static (int Id, string Name)? MapLibrary(string libraryName)
    {
        string name = System.IO.Path.GetFileNameWithoutExtension(libraryName);
        if (name.Equals("Tiles", StringComparison.OrdinalIgnoreCase)) return (0, "Tiles");
        if (name.Equals("SmTiles", StringComparison.OrdinalIgnoreCase)) return (1, "SmTiles");
        if (name.Equals("Objects", StringComparison.OrdinalIgnoreCase)) return (2, "Objects");
        const string prefix = "Objects";
        if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(name.AsSpan(prefix.Length), out int number) && number >= 2 && number <= 1000)
            return (number + 1, "Objects" + number);
        return null;
    }

    /// <summary>转换单个素材库，返回写入的图像数量。</summary>
    public static int ConvertLibrary(string wilPath, string targetPath, Action<string>? log = null, CancellationToken cancellation = default)
    {
        using var library = Mir2WilLibrary.Open(wilPath);
        int count = library.Count;
        if (count <= 0) throw new InvalidDataException("Empty Mir2 library: " + wilPath);

        using var body = new MemoryStream(capacity: Math.Max(1 << 20, count * 4096));
        var offsets = new int[count];
        var record = new byte[ImageRecordHeaderBytes];
        int converted = 0;
        // 偏移表记录的是文件绝对位置：头部 = 版本(4) + 数量(4) + 保留(4) + 数量×4。
        int headerSize = 12 + count * 4;

        for (int i = 0; i < count; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            offsets[i] = checked(headerSize + (int)body.Position);
            var header = library.ReadHeader(i);
            byte[]? pixels = header is { } info ? library.Decode(i, out _) : null;
            int width = pixels is null ? 0 : header!.Value.Width;
            int height = pixels is null ? 0 : header!.Value.Height;
            // 原样保留 WIL 记录里的 px/py。绘制层由各自的流程决定怎么用：
            //  · Mir2 客户端 PlayScene 只在 blend 前景用 (n + ax - 2, m + ay - 68)，
            //    非 blend 前景/中景/背景一律按格子坐标算，不看 px/py；
            //  · Crystal 流程在特定库号与 blend 分支里读 X/Y。
            // 清零会丢掉 blend 前景的唯一定位信息，所以这里必须照抄。
            int offsetX = header?.OffsetX ?? 0;
            int offsetY = header?.OffsetY ?? 0;

            using var compressed = new MemoryStream();
            if (pixels is not null)
            {
                using (var gzip = new GZipStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
                    gzip.Write(pixels, 0, pixels.Length);
                converted++;
            }

            Array.Clear(record);
            BitConverter.TryWriteBytes(record.AsSpan(0, 2), (short)width);
            BitConverter.TryWriteBytes(record.AsSpan(2, 2), (short)height);
            BitConverter.TryWriteBytes(record.AsSpan(4, 2), (short)offsetX);
            BitConverter.TryWriteBytes(record.AsSpan(6, 2), (short)offsetY);
            // 记录头 17 字节：w(0) h(2) x(4) y(6) 保留(8) 保留(10) 保留(12) 长度(13)。
            BitConverter.TryWriteBytes(record.AsSpan(13, 4), (int)compressed.Length);
            body.Write(record, 0, record.Length);
            compressed.Position = 0;
            compressed.CopyTo(body);

            if ((i & 511) == 0) log?.Invoke($"{System.IO.Path.GetFileName(wilPath)}: {i}/{count}");
        }

        string? directory = System.IO.Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        using var output = File.Create(targetPath);
        using var writer = new BinaryWriter(output);
        writer.Write(LibVersion);
        writer.Write(count);
        writer.Write(0);
        foreach (int offset in offsets) writer.Write(offset);
        writer.Flush();
        body.Position = 0;
        body.CopyTo(output);
        output.Flush();

        log?.Invoke($"{System.IO.Path.GetFileName(targetPath)}: {count} 张（有效 {converted} 张）");
        return converted;
    }

    /// <summary>把客户端 Data 目录中的全部 Mir2 素材库转换到 <paramref name="outputRoot"/>/WemadeMir2。</summary>
    public static IReadOnlyList<(string Source, string Target, int Images)> ConvertFolder(
        string dataDirectory, string outputRoot, Action<string>? log = null, CancellationToken cancellation = default)
    {
        if (!Directory.Exists(dataDirectory)) throw new DirectoryNotFoundException(dataDirectory);
        string targetDirectory = System.IO.Path.Combine(outputRoot, DefaultLibraryFolder);
        Directory.CreateDirectory(targetDirectory);
        var results = new List<(string, string, int)>();

        foreach (string wil in Directory.EnumerateFiles(dataDirectory, "*.wil").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            cancellation.ThrowIfCancellationRequested();
            if (MapLibrary(wil) is not { } mapping) { log?.Invoke($"跳过 {System.IO.Path.GetFileName(wil)}（不在编辑器库表内）"); continue; }
            if (Mir2WilLibrary.TryOpen(wil) is null) { log?.Invoke($"跳过 {System.IO.Path.GetFileName(wil)}（不是 Mir2 经典格式）"); continue; }
            string target = System.IO.Path.Combine(targetDirectory, mapping.Name + ".lib");
            int images = ConvertLibrary(wil, target, log, cancellation);
            results.Add((wil, target, images));
        }
        return results;
    }
}
