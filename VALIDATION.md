编辑保护验收：2026-11-05，Windows x64。

本次依次统一地图总格数限制、修复选区属性覆盖、增加自动恢复和上一版备份、增加地图检查与定位、增加按标签保留的图层锁定。使用方法和检查范围见 README 的“编辑保护与地图检查”。

验证结果：

- 新增 21 项回归用例；最终完整测试 99 项全部通过，0 失败、0 跳过。覆盖尺寸边界、非法尺寸不覆盖原文件、混合属性保留和撤销、备份轮换与写入失败保护、活跃会话隔离、恢复快照失效、地图与对象元数据恢复、恢复内容必须保存、检查结果及数量上限、取消检查、跨标签锁定和锁定后的剪切／粘贴。
- 通过 Avalonia Headless 实际打开属性和地图检查对话框，验证只改 Light 时保留不同门属性、图层行锁定控件生效、检查结果能够选中并定位单元格；验证定时恢复入口能同时捕获活动和非活动脏标签，以及未保存地图的资源格式。
- Debug 测试构建与 Release 主程序构建均成功。Release 为 0 错误、106 个原有警告。
- 使用独立配置和测试资源启动 Release 原生程序，主窗口标题为“未命名 • 256 × 256 格 — Crystal Studio”，正常响应并以退出码 0 关闭。未关闭或重启用户原有的未保存地图窗口，也未修改用户素材或原地图。
- 验证期间一次与 Release 编译并行的完整运行出现原有分类卡片界面测试失败；该用例单独复验通过，随后不并行编译的完整运行 99 项全部通过。未修改分类卡片实现或放宽断言。

地图连通性检查基于地图高低碰撞和八方向邻接，独立区域需要人工复核，不等同于服务端实际寻路或外部传送点配置检查。原生启动检查使用隔离测试资源，没有在用户未保存地图上执行生成或覆盖保存。

---

验收日期：2026-11-04。验收平台：Windows x64。

本次目标是将 Crystal Studio 从 WPF 迁移至 Avalonia，并尽量恢复原版紧凑 GUI，同时改善地图交互性能。必须保留地图和对象读写、图层及透明叠加、绘画与选择、缩放与平移、撤销重做、多文档、面板停靠和生成器。验收依据包括自动测试、原生窗口截图、真实地图保存后重新读取，以及相同操作序列的原版对照。

已完成的主要修改：

- 使用 Avalonia 11.3.4 和 .NET 10，源项目已移除 WPF 依赖。
- 工具按钮恢复为 32×32，图标为 16–18，图层行高为 26，并调整页签、下拉框和滑块模板。初次迁移验收的主窗口外框与原版截图均为 1500×950；由于字体和控件渲染框架不同，因此不承诺整个界面逐像素一致。
- 地图内容与交互覆盖层独立缓存，因此悬停不再重新提交地图。平移复用场景记录并更新相机变换，缩略视图使用图集批量绘制和按物理分辨率生成的表面缓存。
- Categories 由分类树改为左侧纵向 Tab，分类内使用自适应预览网格。卡片底部显示去除文件编号的名称，完整标题、副标题及对象路径通过 Tooltip 显示。保留四种分组、搜索、单击选择及双击编辑的事件逻辑，每页 24 项，并记住各分类的页码、滚动位置和选中对象；方向键按网格列数选择。
- 分类预览使用后台线程，仅为视口中的卡片生成预览，滚出视口时暂停，切换分类或页码时取消旧请求。预览并发上限为 2，最多缓存 128 张缩略图，资源或索引变化时清空缓存；不可变位图避免重复复制像素。
- 保留原版用户配置目录，同时支持通过 `CRYSTALSTUDIO_PROFILE` 指定独立配置目录。本次原生验证均使用隔离配置。
- GUI 提供简体中文与英文，主菜单和设置窗口均可保存语言选择，并明确提示重启后生效。启动时读取语言，当前会话不刷新语言；实现没有语言变更通知、实时界面重建或语言动态绑定。821 条中英文资源保存在 `CrystalStudio/Localization/Strings.json`，地图、物体名称、文件路径和分类保存标识保持原值。

Release 自动测试通过 22 项，失败 0 项，跳过 0 项。测试覆盖地图与对象格式、元数据、撤销重做、透明及 additive 合成、超过 8,000 个图块的图集绘制逐像素一致性、缩略视图缓存逐像素一致性、绘画与选择、缩放与平移、悬停缓存复用、各窗口构造、主窗口页签切换、尺寸约束、模态取消，以及滑块的键盘和鼠标操作。分类交互测试确认纵向 Tab、网格、底部精简标题、打开后的完整 Tooltip、方向键选择、分类切换后的分页与位置恢复、搜索与分组，以及可见项预览请求和隐藏取消。测试还确认选择和分类浏览不会使地图变为未保存状态。

其中新增 6 项语言测试：菜单保存语言后当前窗口、未保存地图和撤销记录保持原状态；中文启动后的分类搜索、分页、滚动和选择；设置窗口独立保存语言及再次启动时读取；中文分类选项保存原始标识；六种辅助窗口分别使用中英文启动且保留选项值；全部中英文资源的格式占位符一致。最终发布脚本再次执行全部 22 项测试并通过。

测试使用 Avalonia Headless，因此另外启动了实际 Windows 发布程序，使用隔离配置和 `D:\App\LomcnClient\Data` 资源目录，通过 UI Automation 验证 Categories。真实 Building 分类有 271 项，每页 24 项，共 12 页；切换至 Boat 再返回 Building 后仍为第 2 页。Boat 有 13 项，首两张卡片处于同一行且横向并列，底部标题为 `Boat`，选择后 Active Asset 显示完整名称 `Boat — mir_02468`，程序保持响应。保存了当前网格界面的原生窗口截图；双击编辑在本轮保留事件逻辑，未通过原生鼠标操作单独验证。

真实地图使用 `D:\App\LomcnClient\Map\0.map`，尺寸为 700×700。将其另存后重新读取，对比全部 490,000 个单元格，差异为 0；原始地图未修改。

国际化版本另以隔离配置启动实际 Windows 程序。中文启动显示“当前资源”“第 1 页，共 1 页”和“未命名”窗口标题；将语言配置保存为英文时，当前界面继续显示中文；关闭并重新启动后显示 `Active Asset`、`Page 1 of 1` 和 `Untitled`。两种语言均能选择 13 项 Boat 分类，完整资源名称 `Boat — mir_02468` 保持原文，资源目录保持 `D:\App\LomcnClient\Data`。原生验证直接写入隔离语言配置以检查进程重启行为；菜单和设置选择事件的保存行为由上述自动测试验证。

性能对照使用原版 WPF 与当前 Avalonia 的独立原生测试程序，资源均为 `D:\App\LomcnClient\Data`，视口均为 1264×761。两者使用同一地图、相同区域及操作序列，等待资源和缩略视图准备完成后测量。完整显示的细节区域为 `(3,4)`，两者均缓存了 382 个细节图片。

| 场景 | 操作次数 | 原版累计 CPU 时间（ms） | Avalonia 累计 CPU 时间（ms） | 变化 |
| --- | ---: | ---: | ---: | --- |
| 100% 缩放平移 | 120 | 1109.375 | 781.250 | 减少 29.6% |
| 100% 缩放悬停 | 180 | 140.625 | 109.375 | 减少 22.2% |
| 8% 缩放平移 | 120 | 312.500 | 234.375 | 减少 25.0% |
| 8% 缩放悬停 | 180 | 125.000 | 171.875 | 增加 37.5% |

这些数据来自一次本机采样，表示操作序列期间的进程累计 CPU 时间，不代表 GPU 帧耗时或 FPS。8% 悬停期间两者均未重新构建地图，但是 Avalonia 累计 CPU 时间多 46.875 ms，具体原因未单独测量，因此不能声称所有交互均快于原版。缓存也会增加部分内存占用：8% 平移时进程工作集为 477,245,440 字节，原版为 427,610,112 字节；长时间编辑时的内存峰值尚未验证。

当前中英文版本发布于 `HuaXiaServer\Build\CrystalStudioAvalonia\i18n-restart\win-x64`，入口为 `CrystalStudio.exe`。之前的分类网格版本位于 `HuaXiaServer\Build\CrystalStudioAvalonia\categories-grid\win-x64`；初次迁移发布目录为 `HuaXiaServer\Build\CrystalStudioAvalonia\win-x64`。新版包含 .NET 运行时、ReadyToRun 代码及 `catalogue`、`Objects` 数据目录。通过当前目录中的 `Publish-Avalonia.ps1 -OutputDirectory <目标目录>` 可重新测试并发布。分类网格与国际化修改未涉及地图渲染器，因此上面的地图性能数据沿用初次迁移测量，本轮未重复采样。

剩余限制：

- 部分真实地图资源提示 `Asset error: Unexpected sprite pixel length.`，而原版 WPF 使用相同输入时也出现同一错误，因此本次没有修改这部分资源解码规则。资源仍要求原版支持的 Crystal `.lib`，未增加 `.lib6` 支持。
- 编译成功，但是仍有 nullable 警告和六个参数化窗口的 AVLN3001 提示；这些窗口的实际构造测试已通过。
- Windows 以外的平台和 NativeAOT 尚未验收。当前发布是包含运行时的 ReadyToRun 版本。
- 程序管理的界面文字和内置分类提供中英文；外部异常详情及未登记的目录数据名称可能保留原文。本地化格式文字中的数字使用选定语言，文件读写没有修改线程 Culture。

国际化验收证据位于仓库根目录的 `_codex_artifacts\crystalstudio-i18n`：

- `publish-final.log`：最终 22 项自动测试及 Windows x64 发布记录。
- `verify-restart.ps1`、`verify-native.ps1`、`native-validation.json`：中文启动、等待重启期间的界面状态、英文重启和原始资源名称验证。
- `chinese-final.png`、`english-final.png` 及对应 `.uia.json`：最终发布程序的中英文原生窗口与自动化控件记录。
- `published-sha256.json`：最终发布文件的 SHA-256。

分类网格验收证据位于仓库根目录的 `_codex_artifacts\crystalstudio-categories-grid`：

- `tests\categories-grid.trx`、`tests-final.log`、`publish.log`：当前 16 项测试及 Windows x64 发布记录。
- `categories-final.png`、`categories-final.png.uia.json`：当前网格界面及原生自动化控件记录。
- `verify-native.ps1`、`native-validation.json`：真实分类数量、网格位置、标题、Active Asset 选择及分页恢复的验证程序和结果。
- `native.stdout.log`、`native.stderr.log`：原生程序输出；`native-launch.json`：独立配置和验证进程信息。
- `published-sha256.json`：当前发布文件的 SHA-256。

初次迁移证据位于仓库根目录的 `_codex_artifacts\crystalstudio-avalonia`：

- `tests\final.trx`、`test-final.log`、`publish-final.log`：最终测试与发布记录。
- `baseline-original.png`、`published-main-final.png`、`published-categories-final.png`：原版及初次迁移 GUI，其中 Categories 仍为旧分类树，不代表当前网格布局。
- `baseline-real.png`、`published-main.png`：原版及 Avalonia 真实地图截图；后者生成于最终滑块样式调整前，地图渲染实现与最终版本一致。
- `wpf-final-performance.json`、`avalonia-gpu-performance.json`、`performance-comparison.json`：以上性能表的原始数据；其他 Avalonia 性能 JSON 属于中间测量，不作为最终对照。
- `avalonia-gpu-performance.stdout.log`、`real-map-roundtrip.map`：真实地图另存和重新读取的结果。
- `benchmark\Program.cs`：对照测试程序；`published-sha256.json`：发布文件哈希。

## 地图物体提取验收（2026-11-04）

当前发布路径为 `HuaXiaServer/Build/CrystalStudioAvalonia/object-extraction/win-x64/CrystalStudio.exe`，采用 Windows x64、自包含运行时和 ReadyToRun。包内包含 9,170 个现有 `.X` 文件和 9,159 个分类条目。

完整验证共 **46 项测试通过**。其中提取工具验证覆盖：多部件物体、连续树木、不同资源库与序号范围、较大模板优先、有效和过期元数据、前景动画与碰撞变体、跨选区保护、跨地图去重、分类阈值、分组范围限制、资源缺失、递归目录中的无效地图、取消扫描与导出、拆分/合并/排除及撤销重做、保留独立碰撞单元格和原地图上下文。

保存验证逐字段比较导出前后的相对坐标、前景引用、动画参数、碰撞、门和灯光；此外在实际 Palette 控件中验证新物体的预览、选择、放置和撤销。保存到已有模板时验证文件复用、名称及分类更新、来源去重和自定义字段保留。分别在物体文件、报告、索引阶段注入故障，验证索引内容恢复及新文件清理；另有并发索引变化保护测试。

所有 UI 测试共享 Avalonia 应用、启动语言和弹窗焦点，因此采用串行执行，避免一个测试关闭另一个测试的 Tooltip。产品语言设置仍然在重启后生效。

真实资源使用 `D:/App/LomcnClient/Data`，地图使用 `0.map`、`0128.map`、`0129.map`。三图归并为 **735 个内容候选、4,035 处来源**，其中 481 个已有物体候选、254 个新候选。选择 20 个候选进行隔离目录中的保存与重新读取验证，内容指纹全部一致。三张源地图的 SHA-256 保持不变；测试不向游戏资源目录或源项目的物体目录写入内容。

本机的一次三图扫描测量约为 2.51 秒；受控取消在进入模板匹配时发出请求，额外响应约为 0.38 毫秒。这些是本机样本，实际耗时随地图、目录大小、磁盘和资源完整性变化。详细时间以 `real-resources.json` 的最终记录为准，`batchSeconds` 仅包含扫描，`elapsedSeconds` 还包含导出、重新读取和第二次受控取消。

最终发布程序经过中英文分别启动、载入 `0.map`、通过工具菜单打开提取窗口、扫描、切换建筑分类和选择物体的原生验证。每页显示 48 项，独立物体和地图上下文预览均可用；源地图窗口没有未保存标记，文件 SHA-256 保持不变。辅助原生程序的界面计时器在扫描期间持续响应；其单次测量的最大间隔约 162 毫秒，包含快照和界面初始化，不作为其他机器的性能保证。

剩余限制：自动分组与分类建议需要人工确认，不能保证还原任意地图的原始美术物体边界。真实资源样本记录了 34 条告警，因此缺少可用图片的候选不能视为完整美术资源已验证。预览使用第一处来源的有限上下文，其他来源保留坐标供检查。Windows 以外平台和 NativeAOT 本次未验证。现有 nullable 和 AVLN3001 编译提示仍然存在。

本次证据位于仓库根目录 `_codex_artifacts/crystalstudio-object-extraction`：

- `publish-final.log`：最终完整测试与发布记录；`real-resources-final.trx` / `.log`：真实资源与取消计时验证。
- `real-resources.json`：三图候选、来源、时间、保存数量及源地图哈希。
- `verify-published.ps1`、`published-native.json`、`published-verification.log`：发布程序菜单、扫描和中英文原生验证。
- `published-zh-CN.png`、`published-en-US.png` 及各自 `.uia.json`：发布程序的最终窗口。
- `published-sha256.json`：发布程序、主程序集、运行时配置和分类索引的 SHA-256。
- `native/Program.cs`、`native-zh-CN.json`：辅助原生窗口与界面计时器测量。

使用方法见 README 的“地图物体提取工具”。

## Windows x64 NativeAOT 编译脚本（2026-11-05）

新增 `AotBuild.win-x64.bat`，参照 `Server.Avalonia/AotBuild.win-x64.bat` 使用 Release、win-x64 和 NativeAOT，并调用统一的 `Publish-Avalonia.ps1 -Aot`。普通 PowerShell 发布方式继续使用 ReadyToRun。AOT 配置独立保存于 `CrystalStudio/Properties/PublishProfiles/NativeAot-win-x64.pubxml`，复用服务端的 Skia、HarfBuzz、GLES 静态库，并保留国际化和现有 Avalonia 反射绑定元数据。默认输出为 `../Build/CrystalStudioAvalonia/aot/win-x64`。

脚本从项目之外的工作目录实际执行成功，默认先验证再发布，支持 `-OutputDirectory` 和 `-SkipTests`；含空格的自定义目录实际发布成功，可执行文件与默认目录的 SHA-256 一致。传入不支持的 `linux-x64` 时，批处理退出码为 1，因此失败不会输出成功结论。发布内容包含分类索引、预览、生成器数据及 9,170 个 `.X` 文件；发布可执行文件为 x64 原生 PE，CLR 目录 RVA 为 0，大小为 61,679,616 字节。

首次尝试仅保留应用反射元数据，虽然可以生成原生文件，但是实际启动时 `LibrarySettings` 的 JSON 参数构造转换器缺少原生代码。因此，最终改为显式 JSON 源生成，关闭默认 JSON 反射，覆盖语言、设置、工作区、收藏、最近地图、分类、生成器目录、笔刷列表、地图附加元数据及提取报告。匿名指纹和报告模型改为具名记录，字段名称和顺序保持一致；墙体及河流模型的可选属性默认值有兼容性验证。

最终批处理执行 **59 项测试，失败 0、跳过 0**。其中新增 3 项验证覆盖旧 JSON 字节表示、树木 SHA-256 指纹、缺省墙体参数及全部内置分类/生成器目录读取。独立的辅助 NativeAOT 程序确认 `RuntimeFeature.IsDynamicCodeSupported=false`、`JsonSerializer.IsReflectionEnabledByDefault=false`，并实际执行设置与语言、工作区、内置目录、Tree/Bush/Rock 列表、收藏、最近地图、带放置元数据的地图保存与重新读取，以及物体扫描、`.X` 导出、来源报告与分类索引更新；这些数据验证全部通过。

最终发布程序在隔离配置下分别以中英文读取 `D:/App/LomcnClient/Map/0.map`，检查地图、小地图、Library 预览、Categories 纵向分类及预览网格、设置窗口，并正常退出。工作区均保存 7 个面板记录，地图没有未保存标记，源地图与原用户设置的 SHA-256 保持一致。没有终止或替换用户已经运行的旧发布程序。

最终原生编译仍有 **48 条 IL2026 裁剪提示及 1 条 MicroCom IL3053 提示**，主要涉及现有 Avalonia 反射绑定；应用整体及主题保留必要元数据，已验证上述实际界面。原有 nullable 和 AVLN3001 提示继续存在。未验证的功能不能仅凭编译成功认定全部兼容；其他平台和架构仍未验证。真实 LIB 资源中的既有像素长度错误继续显示，本次没有更改图片读取或资源数据。

证据位于仓库根目录 `_codex_artifacts/crystalstudio-aot-build`：

- `publish-final.log`：最终批处理的完整测试与 NativeAOT 编译。
- `native-pe.json`、`published-sha256.json`、`aot-warnings.json`：原生 PE、发布文件哈希及剩余提示。
- `failure-verification.json`、`invalid-runtime.log`：失败退出码验证。
- `custom-output-verification.json`、`custom-output.log`：含空格目录和跳过测试参数验证。
- `native-verification.json`、`native-verification.log`、`verify-native.ps1`：实际程序中英文原生验证。
- `native-map-*.png`、`native-categories-*.png`、`native-settings-*.png`：地图、分类及设置截图。
- `smoke/`、`smoke-runtime.log`、`native-data/passed.txt`：辅助原生数据验证源码及结果。
- `native-stderr.log`：最初反射序列化启动失败的诊断记录，最终程序已通过重新验证。

## 客户端根目录与左侧页签（2026-11-05）

先验证更新后的 `1d9ad24`：Release 解决方案编译成功，原有 67 项测试全部通过。该版本已经提供根目录素材加载、地图索引、搜索、刷新和可拖动面板，因此本次复用这些实现。左侧增加「工具 / 地图」页签，默认工具页保留工具、工具选项、图层及单元格检查器。控件实例在切换时保留，侧栏宽度、面板高度、停靠、浮动、布局保存和锁定行为继续有效。已有自定义面板位置会继续恢复；可以通过「重置面板布局」使用默认位置。

设置读取时将旧 `Data`、`Data\Map` 及其素材子目录转换为客户端根目录，保存时也使用根目录，JSON 字段 `LibraryFolder` 保持兼容。独立素材目录继续支持。根目录下 `Data` 用于素材，同级 `Map` 用于地图；地图搜索只匹配文件名，忽略大小写。素材加载失败时仍显示地图索引，列表中的键盘操作不会触发画布工具快捷键。

修改后 **71 项测试全部通过，失败 0、跳过 0**。新增验证覆盖旧设置路径转换及语言/素材格式保留、三修根目录素材优先级、素材缺失时浏览地图，以及页签中的宽度调整、分隔条、面板停靠和布局锁定。已有地图交互测试补充验证四组工具所属页、按文件名过滤、控件实例及工具状态保持，并继续验证切换已打开地图时保留未保存内容及避免重复标签。

实际执行 `AotBuild.win-x64.bat -SkipTests -OutputDirectory ...\aot-root-tabs\win-x64`；此前已完成完整测试，所以本次发布跳过重复测试。由于开始发布时旧程序仍在运行，因此使用独立目录 `../Build/CrystalStudioAvalonia/aot-root-tabs/win-x64/CrystalStudio.exe`。程序为 AMD64 原生 PE，CLR 目录 RVA 为 0，大小为 61,736,448 字节，包含 9,170 个 `.X` 文件。

原生程序分别以中英文使用 `D:\App\LomcnClient`，实际列出 **1,624 张地图**；中文验证从旧 `Data` 配置开始，英文验证直接使用根目录。两种语言均检查工具页四个面板、地图页、无匹配过滤、大写 `0.MAP` 过滤、激活已打开的 `0.map`、设置显示根目录及保存根目录、返回工具页，并正常退出。源地图及原用户 settings、recent-maps、assets 配置文件的 SHA-256 均保持一致。

原有 **48 条 IL2026、1 条 MicroCom IL3053** 以及 nullable、AVLN3001 提示仍存在。真实客户端中部分图片仍有既有的 `Unexpected sprite pixel length` 错误，本次没有修改资源文件或图片解码逻辑。

证据位于仓库根目录 `_codex_artifacts/crystalstudio-root-tabs`：`baseline-build.log`、`baseline-tests.log`、`final-tests.log`、`aot-publish.log`、`published-evidence.json`、`native-verification.json`、`verify-native.ps1`，以及中英文工具页、过滤列表、设置和切换后界面的截图与 UI Automation 记录。

## 提取候选预览闪退与日志（2026-11-05）

Windows 应用程序事件在 13:23:56 记录旧 `aot-root-tabs/win-x64/CrystalStudio.exe` 的未处理 `NullReferenceException`。堆栈指向 `ObjectExtractionWindow.ShowDetails` 内碎片行模板，并包含 `ContentPresenter.ContentChanged`、`ClearContainerForItemOverride` 和 `VirtualizingStackPanel.RecycleElement`。因此，候选预览期间的闪退源于列表回收时模板收到空数据，但是原代码仍读取 `r!.Text`。对应的 Windows 原生退出事件是未处理托管异常的结果，不能据此认定是图片或原生内存故障。

碎片行模板改为容许空数据，候选卡片模板增加相同保护。新增回归测试使用 96 个碎片，反复滚动首尾、选择碎片和重新选择候选，并验证源地图内容保留。恢复旧模板后该测试出现同一空引用堆栈；使用修复模板后通过。最终 NativeAOT 编译脚本执行完整测试，**74 项通过、失败 0、跳过 0**。

新增文件日志及进程、UI 调度器、未观察任务的异常监听。UI 监听在平台服务初始化后注册，避免提前访问 `Dispatcher.UIThread` 创建不能执行桌面消息循环的调度器。所有致命异常保留终止行为，资源和操作错误保留现有提示，并增加完整堆栈及上下文。日志单元测试验证 300 次并发写入、关闭前可读的完整 UTF-8 内容、内部异常与堆栈、文件轮换、并行会话及非日志文件保留。

辅助 JIT 子进程分别注入后台线程和 UI 线程未处理异常，均以非零退出码结束，日志包含对应全局处理器及异常堆栈；后台线程异常还包含内部异常。未观察任务异常被记录后以 0 退出。实际 NativeAOT 程序使用无效配置注入启动异常，日志记录 `Program.Main` 堆栈并以 1 退出；另用同名普通文件阻止创建主日志目录，确认程序改用临时目录，并记录原始目录错误及最终异常。这些故障均使用独立测试配置。

实际 AOT 发布程序以中英文分别读取 `D:\App\LomcnClient\Map\0.map`，扫描得到 733 个候选，每页 48 项。验证候选网格滚动、分类切换、368 格木桥候选的碎片列表首尾往返 8 次、详情重新选择 4 次、碎片选择及关闭窗口。两次会话均没有致命异常，日志记录 `DynamicCode=False` 和退出码 0；源地图 SHA-256 保持不变。测试没有导出物体或保存地图。

发布文件为 `../Build/CrystalStudioAvalonia/aot-crash-fix/win-x64/CrystalStudio.exe`，大小 61,793,792 字节，AMD64 PE 的 CLR 目录 RVA 为 0。仍有原有 48 条 IL2026 和 1 条 MicroCom IL3053 提示。真实 LIB 中已有的像素长度错误及 34 条扫描警告仍存在，目前会记录文件和图片编号；本次没有更改资源数据或解码算法。强制结束进程及无法执行托管处理器的原生故障不保证留下最终异常记录，其他平台未验证。

证据位于仓库根目录 `_codex_artifacts/crystalstudio-crash-logging`：

- `windows-events-before.json`、`repro-before-fix.log`、`repro-after-fix.log`：原始闪退事件及修复前后的回归结果。
- `aot-publish.log`、`published-evidence.json`：最终完整测试、原生发布与文件哈希。
- `hook-smoke/`、`verify-hooks.ps1`、`hook-verification.json`：异常注入源码、脚本和退出结果；`hook-profile-*`、`native-startup-fatal` 及临时日志路径包含对应记录。
- `verify-native.ps1`、`native-final-verification.log`、`native-verified/native-verification.json`：最终程序中英文交互验证。
- `native-verified/native-fragments-*.png`、`native-verified/native-grid-*.png` 及对应 `.uia.json`：候选网格、碎片列表及详情预览。

日志位置与轮换规则见 README 的“日志与异常诊断”。
