using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using CrystalStudio;
using CrystalStudio.Formats;

// Mir2（刀光传奇等）素材与地图转换工具。
//   scan    <地图目录>                       分类列出目录中的地图
//   convert <Data 目录> <输出根目录>          把 .wil/.wix 转成 Crystal .lib（写入 输出根/WemadeMir2）
//   map     <地图文件> [输出文件]              把 Mir2 经典地图写成 Crystal v1 地图
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0) { Usage(); return 1; }
        try
        {
            switch (args[0].ToLowerInvariant())
            {
                case "scan": return Scan(args);
                case "convert": return ConvertAssets(args);
                case "map": return ConvertMap(args);
                case "save": return SaveMap(args);
                case "check": return CheckRead(args);
                case "layout": return CompareLayout(args);
                case "render": return RenderMap(args);
                default: Usage(); return 1;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("错误: " + ex.Message);
            return 2;
        }
    }

    private static void Usage() => Console.WriteLine(
        """
        用法:
          scan    <地图目录>                    分类列出 Mir2 经典地图
          convert <Data 目录> <输出根目录>        .wil/.wix → Crystal .lib（输出到 输出根/WemadeMir2）
          map     <地图文件> [输出文件]           Mir2 地图 → Crystal v1 地图
          save    <源地图> <输出文件> [格式]       按 old / new / enhanced / crystal 写出地图
          check   <地图文件>                     对比两条读取路径的解析差异
          layout  <地图文件> <素材根>             对比 Crystal 锚点与 Mir2 编辑器锚点的逐格差异
        """);

    private static int Scan(string[] args)
    {
        if (args.Length < 2) { Usage(); return 1; }
        var maps = Mir2ClassicMap.Scan(args[1]);
        Console.WriteLine($"识别到 {maps.Count} 个 Mir2 经典地图：");
        foreach (var group in maps.GroupBy(m => m.Kind).OrderBy(g => g.Key))
        {
            Console.WriteLine($"  [{group.Key}] {group.Count()} 个");
            foreach (var info in group.Take(8))
                Console.WriteLine($"    {Path.GetFileName(info.Path),-16} {info.Width}x{info.Height}  {info.CellBytes}字节/格  标题“{info.Title}”");
            if (group.Count() > 8) Console.WriteLine($"    …… 其余 {group.Count() - 8} 个");
        }
        var first = maps.FirstOrDefault(m => m.Kind == Mir2MapKind.OldMap) ?? maps.FirstOrDefault();
        if (first is not null)
        {
            var report = Mir2ClassicMap.Analyze(first);
            Console.WriteLine();
            Console.WriteLine($"样例分析 {Path.GetFileName(first.Path)}:");
            Console.WriteLine("  " + report.Summary);
        }
        return 0;
    }

    private static int ConvertAssets(string[] args)
    {
        if (args.Length < 3) { Usage(); return 1; }
        string data = args[1], output = args[2];
        var only = args.Skip(3).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var watch = Stopwatch.StartNew();
        string targetDirectory = Path.Combine(output, Mir2WilConverter.DefaultLibraryFolder);
        Directory.CreateDirectory(targetDirectory);
        var results = new List<(string Source, string Target, int Images)>();

        foreach (string wil in Directory.EnumerateFiles(data, "*.wil").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            if (Mir2WilConverter.MapLibrary(wil) is not { } mapping) { Console.WriteLine($"跳过 {Path.GetFileName(wil)}（不在编辑器库表内）"); continue; }
            if (only.Count > 0 && !only.Contains(mapping.Name)) { Console.WriteLine($"跳过 {Path.GetFileName(wil)}（未指定）"); continue; }
            if (Mir2WilLibrary.TryOpen(wil) is null) { Console.WriteLine($"跳过 {Path.GetFileName(wil)}（不是 Mir2 经典格式）"); continue; }
            string target = Path.Combine(targetDirectory, mapping.Name + ".lib");
            results.Add((wil, target, Mir2WilConverter.ConvertLibrary(wil, target, Console.WriteLine)));
        }

        Console.WriteLine();
        foreach (var (source, target, images) in results)
            Console.WriteLine($"  {Path.GetFileName(source),-14} → {Path.GetFileName(target),-14} {images,7} 张");
        long bytes = results.Sum(r => new FileInfo(r.Target).Length);
        Console.WriteLine($"合计 {results.Count} 个库、{results.Sum(r => r.Images)} 张、{bytes / 1048576.0:F1} MB，用时 {watch.Elapsed.TotalSeconds:F1} 秒");
        return 0;
    }

    private static int ConvertMap(string[] args)
    {
        if (args.Length < 2) { Usage(); return 1; }
        string source = args[1];
        string target = args.Length > 2 ? args[2] : Path.ChangeExtension(source, ".crystal.map");
        if (Mir2ClassicMap.Detect(source) is not { } info) { Console.Error.WriteLine("不是 Mir2 经典地图: " + source); return 3; }
        var document = Mir2ClassicMap.ConvertToCrystal(source, target);
        Console.WriteLine($"转换 {Path.GetFileName(source)} → {Path.GetFileName(target)}：{document.Width}x{document.Height}，{document.Cells.Count()} 个非空格");
        return 0;
    }

    private static int SaveMap(string[] args)
    {
        if (args.Length < 3) { Usage(); return 1; }
        string source = args[1];
        string target = args[2];
        string kind = args.Length > 3 ? args[3].ToLowerInvariant() : "old";

        var document = Mir2ClassicMap.Detect(source) is { } info
            ? Mir2ClassicMap.Load(source, info)
            : MapDocument.Load(source);

        switch (kind)
        {
            case "crystal":
                document.Save(target);
                break;
            case "old":
                Mir2ClassicMapWriter.Save(document, target, Mir2MapKind.OldMap, Path.GetFileNameWithoutExtension(target));
                break;
            case "new":
                Mir2ClassicMapWriter.Save(document, target, Mir2MapKind.NewMap, Path.GetFileNameWithoutExtension(target));
                break;
            case "enhanced":
                Mir2ClassicMapWriter.Save(document, target, Mir2MapKind.EnhancedMap);
                break;
            default:
                Console.Error.WriteLine("未知格式: " + kind);
                return 1;
        }

        if (Mir2ClassicMap.Detect(target) is { } written)
            Console.WriteLine($"已写入 {Path.GetFileName(target)}：{written.KindName} {written.Width}x{written.Height}，{written.FileLength} 字节，标题“{written.Title}”");
        else
            Console.WriteLine($"已写入 {Path.GetFileName(target)}（Crystal 格式）");
        return 0;
    }

    /// <summary>对比编辑器读取路径与格式读取路径，定位解析层差异。</summary>
    private static int CheckRead(string[] args)
    {
        if (args.Length < 2) { Usage(); return 1; }
        string path = args[1];
        if (Mir2ClassicMap.Detect(path) is not { } info) { Console.Error.WriteLine("不是 Mir2 经典地图: " + path); return 3; }

        var legacy = MapDocument.Load(path);           // 编辑器打开的路径（LegacyMapReader）
        var classic = Mir2ClassicMap.Load(path, info); // 新增的格式路径

        int diffBack = 0, diffMid = 0, diffFront = 0, total = 0;
        int firstX = -1, firstY = -1;
        for (int x = 0; x < legacy.Width; x++)
            for (int y = 0; y < legacy.Height; y++)
            {
                var a = legacy.Get(x, y);
                var b = classic.Get(x, y);
                total++;
                if ((a.BackImage & 0x1FFFFFFF) != (b.BackImage & 0x1FFFFFFF))
                {
                    diffBack++;
                    if (firstX < 0) { firstX = x; firstY = y; }
                }
                if (a.MiddleImage != b.MiddleImage) diffMid++;
                if ((a.Foreground?.Image ?? 0) != (b.Foreground?.Image ?? 0)) diffFront++;
            }

        Console.WriteLine($"格数 {total}：背景差 {diffBack}、中景差 {diffMid}、前景差 {diffFront}");
        Console.WriteLine($"编辑器路径记住的源格式: {legacy.SourceMir2Kind?.ToString() ?? "(无)"}");
        if (firstX >= 0)
        {
            var a = legacy.Get(firstX, firstY);
            var b = classic.Get(firstX, firstY);
            Console.WriteLine($"首例 ({firstX},{firstY})：编辑器 BackImage=0x{a.BackImage:X8} 新路径 BackImage=0x{b.BackImage:X8}");
        }
        return 0;
    }

    /// <summary>
    /// 逐格对比两套锚点规则：当前 Crystal 渲染管线（MapSpriteDrawing.Bounds）与
    /// Mir2 经典编辑器的绘制规则（EdMain.pas / o_WIL.pas）。
    /// 用来判断偏移到底来自素材、来自地图解析，还是来自渲染流程本身的特判。
    /// </summary>
    private static int CompareLayout(string[] args)
    {
        if (args.Length < 3) { Usage(); return 1; }
        string target = args[1], assetRoot = args[2];
        string libDir = Path.Combine(assetRoot, Mir2WilConverter.DefaultLibraryFolder);
        var libraries = new Dictionary<int, CrystalLibrary>();
        foreach (var (id, name) in LibraryTable())
        {
            string path = Path.Combine(libDir, name + ".lib");
            if (File.Exists(path)) libraries[id] = new CrystalLibrary(path);
        }
        Console.WriteLine($"素材库 {libraries.Count} 个");

        if (Directory.Exists(target))
        {
            long totalCells = 0, totalDiff = 0;
            int maps = 0, affected = 0;
            var byRule = new Dictionary<string, long>();
            var worst = new List<(string Name, long Diff, long Cells)>();
            foreach (var file in Directory.EnumerateFiles(target, "*.map").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                if (Mir2ClassicMap.Detect(file) is not { } info) continue;
                var document = Mir2ClassicMap.Load(file, info);
                var report = Measure(document, libraries);
                maps++; totalCells += report.Cells; totalDiff += report.Diff;
                if (report.Diff > 0) affected++;
                foreach (var pair in report.ByRule) byRule[pair.Key] = byRule.GetValueOrDefault(pair.Key) + pair.Value;
                worst.Add((Path.GetFileName(file), report.Diff, report.Cells));
            }
            Console.WriteLine($"扫描 {maps} 张地图、{totalCells} 格：Crystal 流程与 Mir2 客户端规则锚点不一致 {totalDiff} 处（{totalDiff * 100.0 / Math.Max(1, totalCells):F4}%），涉及 {affected} 张地图");
            foreach (var pair in byRule.OrderByDescending(p => p.Value).Take(20))
                Console.WriteLine($"  {pair.Key,-34} {pair.Value,9} 处");
            Console.WriteLine("差异最多的地图：");
            foreach (var entry in worst.OrderByDescending(w => w.Diff).Take(10))
                Console.WriteLine($"  {entry.Name,-18} {entry.Diff,7} / {entry.Cells} 格");
            return 0;
        }

        if (Mir2ClassicMap.Detect(target) is not { } single) { Console.Error.WriteLine("不是 Mir2 经典地图: " + target); return 3; }
        var doc = Mir2ClassicMap.Load(target, single);
        var result = Measure(doc, libraries);
        Console.WriteLine($"地图 {doc.Width}x{doc.Height}，{result.Cells} 格：背景 {result.Back}、中景 {result.Middle}、前景 {result.Front}，素材缺失 {result.Missing}");
        if (result.Diff == 0) { Console.WriteLine("两套流程锚点完全一致。"); return 0; }
        Console.WriteLine($"锚点不一致 {result.Diff} 处：");
        foreach (var pair in result.ByRule.OrderByDescending(p => p.Value).Take(40))
            Console.WriteLine($"  {pair.Key,-34} {pair.Value,9} 处");
        return 0;
    }

    private sealed record LayoutReport(long Cells, long Back, long Middle, long Front, long Missing, long Diff,
        Dictionary<string, long> ByRule);

    private static LayoutReport Measure(MapDocument document, Dictionary<int, CrystalLibrary> libraries)
    {
        long cells = 0, back = 0, middle = 0, front = 0, missing = 0, diff = 0;
        var byRule = new Dictionary<string, long>();

        void Compare(int layer, SpriteHeader s, int lib, int crystalX, int crystalY, int mir2X, int mir2Y)
        {
            int dx = crystalX - mir2X, dy = crystalY - mir2Y;
            if (dx == 0 && dy == 0) return;
            diff++;
            string rule = $"{LayerName(layer)} {s.Width}x{s.Height} 库{lib}";
            byRule[rule] = byRule.GetValueOrDefault(rule) + 1;
        }

        for (int y = 0; y < document.Height; y++)
            for (int x = 0; x < document.Width; x++)
            {
                var cell = document.Get(x, y); cells++;
                if (x % 2 == 0 && y % 2 == 0 && (cell.BackImage & 0x1FFFFFFF) != 0)
                {
                    back++;
                    int index = (cell.BackImage & 0x1FFFFFFF) - 1;
                    if (Header(libraries, cell.BackLibrary, index) is { } s)
                        Compare(0, s, cell.BackLibrary, x * 48, y * 32, x * 48, y * 32);
                    else missing++;
                }
                if (cell.MiddleImage > 0 && cell.MiddleLibrary >= 0)
                {
                    middle++;
                    int index = cell.MiddleImage - 1;
                    if (Header(libraries, cell.MiddleLibrary, index) is { } s)
                    {
                        // 当前 Crystal 流程：anchored=true、front=false、blend=false
                        bool tile = (s.Width == 48 && s.Height == 32) || (s.Width == 96 && s.Height == 64);
                        int cy = y * 32 + (tile ? 0 : 32 - s.Height);
                        // Mir2 客户端 DrawTileMap / 编辑器 DrawZoomEx(TRUE)：左上角
                        Compare(1, s, cell.MiddleLibrary, x * 48, cy, x * 48, y * 32);
                    }
                    else missing++;
                }
                if (cell.Foreground is { Library: >= 0 } fr)
                {
                    front++;
                    if (Header(libraries, fr.Library, fr.Image) is { } s)
                    {
                        bool tile = (s.Width == 48 && s.Height == 32) || (s.Width == 96 && s.Height == 64);
                        bool blend = (fr.Frames & 0x80) != 0;
                        int cx = x * 48, cy = y * 32;
                        int lib = fr.Library;
                        if (blend && (lib is 14 or 27 || lib is > 99 and < 199))
                        { cx += s.X; cy += 32 - 96 + s.Y; }
                        else if (blend)
                        { cy += 32 - s.Height; if (fr.Image is >= 2723 and <= 2732) { cx += s.X; cy += s.Y; } }
                        else if (lib == 28 && (s.X != 0 || s.Y != 0)) { cx += s.X; cy += s.Y; }
                        else if (!tile || blend) cy += 32 - s.Height;
                        // Mir2 客户端 PlayScene：非 blend 用 m + UNITY - Height，blend 走 n+ax-2 / m+ay-68
                        int mir2Y = blend ? y * 32 + s.Y - 68 : y * 32 + 32 - s.Height;
                        int mir2X = blend ? x * 48 + s.X - 2 : x * 48;
                        Compare(2, s, lib, cx, cy, mir2X, mir2Y);
                    }
                    else missing++;
                }
            }
        return new(cells, back, middle, front, missing, diff, byRule);
    }

    private static string LayerName(int layer) => layer switch { 0 => "背景", 1 => "中景", _ => "前景" };

    private static SpriteHeader? Header(Dictionary<int, CrystalLibrary> libraries, int lib, int index)
        => libraries.TryGetValue(lib, out var library) && index >= 0 ? library.ReadHeader(index) : null;

    private static IEnumerable<(int Id, string Name)> LibraryTable()
    {
        yield return (0, "Tiles");
        yield return (1, "SmTiles");
        yield return (2, "Objects");
        for (int i = 2; i < 99; i++) yield return (i + 1, "Objects" + i);
    }

    /// <summary>用编辑器自己的渲染管线导出整张地图，用于和参考渲染对照。</summary>
    private static int RenderMap(string[] args)
    {
        if (args.Length < 3) { Usage(); return 1; }
        string map = args[1];
        string assets = args[2];
        string output = args.Length > 3 && !args[3].StartsWith("--", StringComparison.Ordinal)
            ? args[3] : Path.ChangeExtension(map, ".png");
        bool crystal = args.Contains("--crystal", StringComparer.OrdinalIgnoreCase);
        int pixelsPerCell = 12;
        int zoomIndex = Array.FindIndex(args, a => a.Equals("--zoom", StringComparison.OrdinalIgnoreCase));
        if (zoomIndex >= 0 && zoomIndex + 1 < args.Length && int.TryParse(args[zoomIndex + 1], out int zoom) && zoom is > 0 and <= 96)
            pixelsPerCell = zoom;
        Avalonia.Rect? region = null;
        int regionIndex = Array.FindIndex(args, a => a.Equals("--region", StringComparison.OrdinalIgnoreCase));
        if (regionIndex >= 0 && regionIndex + 4 < args.Length &&
            int.TryParse(args[regionIndex + 1], out int rx) && int.TryParse(args[regionIndex + 2], out int ry) &&
            int.TryParse(args[regionIndex + 3], out int rw) && int.TryParse(args[regionIndex + 4], out int rh) &&
            rw > 0 && rh > 0)
            region = new Avalonia.Rect(rx * 48.0, ry * 32.0, rw * 48.0, rh * 32.0);
        MapRenderExport.Export(map, assets, output, crystal, pixelsPerCell, region);
        Console.WriteLine($"已渲染 {Path.GetFileName(map)} → {output}");
        return 0;
    }
}
