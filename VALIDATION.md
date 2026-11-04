验收日期：2026-11-04。验收平台：Windows x64。

本次目标是将 Crystal Studio 从 WPF 迁移至 Avalonia，并尽量恢复原版紧凑 GUI，同时改善地图交互性能。必须保留地图和对象读写、图层及透明叠加、绘画与选择、缩放与平移、撤销重做、多文档、面板停靠和生成器。验收依据包括自动测试、原生窗口截图、真实地图保存后重新读取，以及相同操作序列的原版对照。

已完成的主要修改：

- 使用 Avalonia 11.3.4 和 .NET 10，源项目已移除 WPF 依赖。
- 工具按钮恢复为 32×32，图标为 16–18，图层行高为 26，并调整页签、分类树、下拉框和滑块模板。主窗口外框与原版截图均为 1500×950；由于字体和控件渲染框架不同，因此不承诺整个界面逐像素一致。
- 地图内容与交互覆盖层独立缓存，因此悬停不再重新提交地图。平移复用场景记录并更新相机变换，缩略视图使用图集批量绘制和按物理分辨率生成的表面缓存。
- 分类预览使用后台线程，并在控件离开视觉树时取消；不可变位图避免重复复制像素。
- 保留原版用户配置目录，同时支持通过 `CRYSTALSTUDIO_PROFILE` 指定独立配置目录。本次原生验证均使用隔离配置。

Release 自动测试通过 14 项，失败 0 项，跳过 0 项。测试覆盖地图与对象格式、元数据、撤销重做、透明及 additive 合成、超过 8,000 个图块的图集绘制逐像素一致性、缩略视图缓存逐像素一致性、绘画与选择、缩放与平移、悬停缓存复用、各窗口构造、主窗口页签切换、分类树展开、尺寸约束、模态取消，以及滑块的键盘和鼠标操作。测试使用 Avalonia Headless，因此另外启动了实际 Windows 发布程序，核对窗口、资源预览和 Categories 分类页签，并保存截图。

真实地图使用 `D:\App\LomcnClient\Map\0.map`，尺寸为 700×700。将其另存后重新读取，对比全部 490,000 个单元格，差异为 0；原始地图未修改。

性能对照使用原版 WPF 与当前 Avalonia 的独立原生测试程序，资源均为 `D:\App\LomcnClient\Data`，视口均为 1264×761。两者使用同一地图、相同区域及操作序列，等待资源和缩略视图准备完成后测量。完整显示的细节区域为 `(3,4)`，两者均缓存了 382 个细节图片。

| 场景 | 操作次数 | 原版累计 CPU 时间（ms） | Avalonia 累计 CPU 时间（ms） | 变化 |
| --- | ---: | ---: | ---: | --- |
| 100% 缩放平移 | 120 | 1109.375 | 781.250 | 减少 29.6% |
| 100% 缩放悬停 | 180 | 140.625 | 109.375 | 减少 22.2% |
| 8% 缩放平移 | 120 | 312.500 | 234.375 | 减少 25.0% |
| 8% 缩放悬停 | 180 | 125.000 | 171.875 | 增加 37.5% |

这些数据来自一次本机采样，表示操作序列期间的进程累计 CPU 时间，不代表 GPU 帧耗时或 FPS。8% 悬停期间两者均未重新构建地图，但是 Avalonia 累计 CPU 时间多 46.875 ms，具体原因未单独测量，因此不能声称所有交互均快于原版。缓存也会增加部分内存占用：8% 平移时进程工作集为 477,245,440 字节，原版为 427,610,112 字节；长时间编辑时的内存峰值尚未验证。

实际发布目录为 `HuaXiaServer\Build\CrystalStudioAvalonia\win-x64`，入口为 `CrystalStudio.exe`。该版本包含 .NET 运行时、ReadyToRun 代码及 `catalogue`、`Objects` 数据目录。通过当前目录中的 `Publish-Avalonia.ps1` 可重新测试并发布；发布后的 EXE、DLL 和运行时配置文件 SHA-256 均已与验收记录核对一致。

剩余限制：

- 部分真实地图资源提示 `Asset error: Unexpected sprite pixel length.`，而原版 WPF 使用相同输入时也出现同一错误，因此本次没有修改这部分资源解码规则。资源仍要求原版支持的 Crystal `.lib`，未增加 `.lib6` 支持。
- 编译成功，但是仍有 nullable 警告和六个参数化窗口的 AVLN3001 提示；这些窗口的实际构造测试已通过。
- Windows 以外的平台和 NativeAOT 尚未验收。当前发布是包含运行时的 ReadyToRun 版本。

本次证据位于仓库根目录的 `_codex_artifacts\crystalstudio-avalonia`：

- `tests\final.trx`、`test-final.log`、`publish-final.log`：最终测试与发布记录。
- `baseline-original.png`、`published-main-final.png`、`published-categories-final.png`：原版及最终发布版 GUI；最终发布版分类页签已经通过原生 UI Automation 选择并确认选中。
- `baseline-real.png`、`published-main.png`：原版及 Avalonia 真实地图截图；后者生成于最终滑块样式调整前，地图渲染实现与最终版本一致。
- `wpf-final-performance.json`、`avalonia-gpu-performance.json`、`performance-comparison.json`：以上性能表的原始数据；其他 Avalonia 性能 JSON 属于中间测量，不作为最终对照。
- `avalonia-gpu-performance.stdout.log`、`real-map-roundtrip.map`：真实地图另存和重新读取的结果。
- `benchmark\Program.cs`：对照测试程序；`published-sha256.json`：发布文件哈希。
