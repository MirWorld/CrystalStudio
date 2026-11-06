using CrystalStudio.Formats;

namespace CrystalStudio;

/// <summary>
/// 保存格式分流：默认保存用编辑器自己的 Crystal 格式，另存时可以选择写回
/// Mir2 经典地图的 12 / 14 / 15 字节单元布局。
///
/// 放在独立的 partial 文件里，现有保存流程只多一行调用。
/// </summary>
public partial class MainWindow
{
    /// <summary>默认保存格式对应的对话框过滤器。</summary>
    internal const string SaveFilter =
        "Crystal map (*.map)|*.map|" +
        "Mir2 经典地图 12 字节 (*.map)|*.map|" +
        "Mir2 新式地图 14 字节 (*.map)|*.map|" +
        "Mir2 增强地图 15 字节 (*.map)|*.map";

    /// <summary>把编辑器设置里的保存格式换成经典地图的单元布局。</summary>
    internal static Mir2MapKind ToMapKind(MapSaveFormat format) => format switch
    {
        MapSaveFormat.Mir2OldMap => Mir2MapKind.OldMap,
        MapSaveFormat.Mir2NewMap => Mir2MapKind.NewMap,
        MapSaveFormat.Mir2EnhancedMap => Mir2MapKind.EnhancedMap,
        _ => Mir2MapKind.OldMap,
    };

    /// <summary>
    /// 解析实际写入的格式：选择“跟随源格式”时按读取时的布局保存
    /// （12 字节存回 12 字节、14 字节存回 14 字节），其余情况用下拉里指定的格式。
    /// </summary>
    internal static MapSaveFormat Resolve(MapSaveFormat configured, MapDocument document) =>
        configured != MapSaveFormat.FollowSource
            ? configured
            : document.SourceMir2Kind switch
            {
                Mir2MapKind.OldMap => MapSaveFormat.Mir2OldMap,
                Mir2MapKind.NewMap => MapSaveFormat.Mir2NewMap,
                Mir2MapKind.EnhancedMap => MapSaveFormat.Mir2EnhancedMap,
                _ => MapSaveFormat.Crystal,
            };

    /// <summary>按指定格式写盘；Crystal 走原有的原生保存。</summary>
    internal static void SaveDocumentAs(MapDocument document, string path, MapSaveFormat format)
    {
        format = Resolve(format, document);
        if (format == MapSaveFormat.Crystal)
        {
            document.Save(path);
            return;
        }
        string? title = Path.GetFileNameWithoutExtension(path);
        Mir2ClassicMapWriter.Save(document, path, ToMapKind(format), title);
    }

    /// <summary>保存后在状态栏显示的格式名。</summary>
    internal static string SaveFormatName(MapSaveFormat format) => format switch
    {
        MapSaveFormat.Mir2OldMap => "Mir2 经典地图 12 字节",
        MapSaveFormat.Mir2NewMap => "Mir2 新式地图 14 字节",
        MapSaveFormat.Mir2EnhancedMap => "Mir2 增强地图 15 字节",
        MapSaveFormat.FollowSource => "源格式",
        _ => "Crystal 地图",
    };
}
