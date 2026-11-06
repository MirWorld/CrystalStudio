namespace CrystalStudio;

/// <summary>
/// 地图格式自动识别入口。
///
/// 编辑器默认（Crystal）格式配置下打开地图时，先按原生格式解析；如果文件实际是
/// Mir2 经典格式（Legend of mir 52 字节头 + 12/14 字节单元，或 Map 2010 增强格式），
/// 则由 <see cref="Formats.Mir2ClassicMap"/> 直接转换成本地 Cell 模型读取，无需先另存。
/// </summary>
public sealed partial class MapDocument
{
    /// <summary>识别并读取地图；无法识别 Mir2 经典格式时回落到原生读取。</summary>
    public static MapDocument LoadAuto(string path, AssetSourceFormat format = AssetSourceFormat.Default) =>
        Formats.Mir2ClassicMap.Detect(path) is { } info
            ? Formats.Mir2ClassicMap.Load(path, info, format)
            : Load(path, format);

    /// <summary>探测地图格式；返回 null 表示不是 Mir2 经典格式。</summary>
    public static Formats.Mir2MapInfo? DetectFormat(string path) => Formats.Mir2ClassicMap.Detect(path);
}
