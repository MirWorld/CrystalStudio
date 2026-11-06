namespace CrystalStudio;

/// <summary>
/// 供命令行工具调用的地图渲染入口：把整张地图按编辑器的图层顺序画成一张 PNG。
/// 内部复用 MinimapExporter，所以输出与编辑器 / 小地图导出完全一致。
/// </summary>
public static class MapRenderExport
{
    public static void Export(string mapPath, string assetRoot, string output, bool crystalLayout = false,
        int pixelsPerCell = 12, Rect? region = null)
    {
        using var assets = AssetStore.Load(assetRoot, AssetSourceFormat.Default);
        var document = MapDocument.Load(mapPath);
        Console.WriteLine($"[诊断] 地图 {document.Width}x{document.Height}，读取时识别到的源格式 = {document.SourceMir2Kind?.ToString() ?? "null（走 Crystal 流程）"}");
        Console.WriteLine($"[诊断] 渲染流程 = {MapSpriteLayout.For(document).Name}，输出 {pixelsPerCell}px/格" +
            (region is { } r ? $"，区域 {r.X / 48:F0},{r.Y / 32:F0} 起 {r.Width / 48:F0}x{r.Height / 32:F0} 格" : "，整图"));
        // 对照用：强制走 Crystal 流程，忽略文档记住的 Mir2 源格式。
        if (crystalLayout) document.SourceMir2Kind = null;
        var bitmap = MinimapExporter.CreateAsync(document, assets, region, pixelsPerCell).GetAwaiter().GetResult();
        AtomicFile.Write(output, bitmap.Save);
    }
}
