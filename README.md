# Crystal Studio

基于 Avalonia 11 和 .NET 10 的 Crystal 地图编辑器，保留原版紧凑界面、地图编辑、对象分类、面板停靠和生成器。

The Crystal map editor based on Avalonia 11 and .net 10 retains the original compact interface, map editing, object classification, panel docking, and generator.

> 原作者: https://github.com/Lilcooldoode
> author: https://github.com/Lilcooldoode

## Build from Source

Requires the .NET 10 SDK. Keep the `CrystalStudio`, `catalogue` and
`Objects` folders together, then run these commands from their parent folder:

```powershell
dotnet build CrystalStudio.sln -c Debug
dotnet build CrystalStudio.sln -c Release
dotnet run --project CrystalStudio/CrystalStudio.csproj -c Release
./Publish-Avalonia.ps1
```

发布脚本默认先执行自动验证，然后生成包含 .NET 运行时的 Windows x64 版本，目录为项目根目录的 `Build/Release`。`catalogue` 和 `Objects` 会一并复制。可通过 `-OutputDirectory` 指定其他目录。

Windows x64 NativeAOT 使用与 `Server.Avalonia/AotBuild.win-x64.bat` 相同的 Release 发布方式。可以双击本目录的 `AotBuild.win-x64.bat`，或者执行：

```powershell
.\AotBuild.win-x64.bat
# PowerShell 入口
.\Publish-Avalonia.ps1 -Aot
# 自定义输出目录；已验证源码时可以跳过测试
.\AotBuild.win-x64.bat -OutputDirectory 'E:\Build\CrystalStudio AOT' -SkipTests
```

AOT 默认输出为 `../Build/CrystalStudioAvalonia/aot/win-x64/CrystalStudio.exe`，并保留旁边的 `catalogue`、`Objects` 和其他发布文件。脚本不依赖启动时的工作目录，默认先执行测试，测试或发布失败时返回非零退出码。普通发布继续使用 ReadyToRun；只有 `-Aot` 或独立批处理入口启用 NativeAOT。

编译环境需要 .NET 10 SDK、Visual Studio 2022 Build Tools 的“使用 C++ 的桌面开发”及 Windows SDK，并保留仓库的 `HuaXiaServer/NativeStaticLibrary/x64`。AOT 配置复用服务端的 Skia、HarfBuzz 和 GLES 静态库，不引入未使用的 SQLite。设置、语言、笔刷、分类和地图附加元数据使用 JSON 源生成，现有 JSON 字段及树木指纹保持兼容；Avalonia 的现有反射绑定通过裁剪描述文件保留。

界面沿用原版 32×32 工具按钮、16–18 图标、26 高度的图层行以及紧凑页签。地图和交互覆盖层独立保存绘制结果，因此鼠标悬停不会重新提交地图；缩略视图按图集批量绘制，并保留原有层次、透明叠加、动画和灯光逻辑。

`CRYSTALSTUDIO_PROFILE` 可指定独立配置目录；未设置时继续使用原版用户配置目录。当前验证目标为 Windows x64；其他平台尚未验收。游戏资源仍需要原版支持的 Crystal `.lib`，资源格式以设置中的 Default / Sanxiu 选项为准。

Palette 的 Categories 使用左侧纵向分类 Tab，右侧按面板宽度排列预览网格。卡片底部仅显示精简名称，鼠标悬停后显示完整标题、副标题及对象路径。保留 Type、Region、Environment、Facing 分组及搜索；每页 24 项，切换分类时记住页码、滚动位置和选中对象，方向键可按网格选择。

分类预览仅为可见卡片加载，切换分类或页码时取消旧请求；后台预览并发上限为 2，最近使用的缩略图最多缓存 128 张，因此无需一次生成整个分类的预览。

GUI 支持简体中文和 English。通过 **View → Language (restart required)**（**视图 → 语言（重启后生效）**）或 **Edit → Settings**（**编辑 → 设置**）选择语言。选择后自动保存，下次启动生效，当前窗口继续使用原语言。首次启动时默认使用系统界面语言；非中文系统默认使用英文。

语言设置独立于资源目录保存，首次配置资源目录前也可选择语言。界面文字、内置分类及选项提供中英文显示；物体名称、资源文件名、文件路径和分类保存标识保留原文，因此地图与物体格式保持兼容。外部异常详情及未登记的目录名称可能仍显示原文。

解决方案为根目录的 `CrystalStudio.sln`，包含编辑器和测试项目。Debug、Release 主程序分别输出到 `Build/Debug/CrystalStudio.exe`、`Build/Release/CrystalStudio.exe`；测试程序位于各自的 `Tests` 子目录。此前的 GUI、语言重启、地图保存和性能验证记录见 [VALIDATION.md](VALIDATION.md)。

Game artwork is not included. Select the game client root containing **Data**
(Crystal `.lib` libraries) and **Map** (`.map` files).

设置中选择客户端根目录，例如 `D:\App\LomcnClient`。素材从 `Data` 或 `Data\Map` 加载，地图从同级 `Map` 读取；旧 Data 配置会转换为根目录显示，保存设置时也使用根目录。独立的平铺素材目录继续支持。

左侧「工具 / 地图」页签分别显示现有工具、工具选项、图层、单元格检查器，以及 Map 及子目录中的 `.map` 列表。地图页顶部支持按文件名过滤，忽略大小写，并提供刷新和完整路径提示。单击打开，重复点击切回已有标签并保留未保存内容；素材无法读取时仍可浏览地图。界面随现有语言设置显示中英文。拖动侧栏与画布之间的竖线调整宽度，拖动工具面板之间的横线调整高度；拖标题可改变位置或成为浮动窗口，已有布局会保留。若已锁定布局，请先在「视图」中取消「锁定面板布局」。

生成器不再要求外部素材像素指纹与原始素材一致，允许替换图片。仍检查素材能否读取、图片编号、必要尺寸和模板结构；内置目录及地图编辑元数据的完整性校验保留。

## Getting Started

1. Start the editor using the command above, or run `CrystalStudio.exe` from a published release.
2. Open **Edit > Settings** and select your game client root containing **Data** and **Map**. Libraries load from Data; legacy Data settings display and save as the client root. Standalone library folders remain supported.
3. Select the left **Maps** tab and click a `.map` file to open it. The list includes subfolders and supports filename search and refresh. The **Tools** tab contains tools, tool options, layers and the cell inspector. **File > Open Map** and **File > New Map** remain available.
4. Choose a tool and an asset, then edit the map.
5. Save with **Ctrl+S**.

For published releases, keep the `catalogue` and `Objects` folders beside the
executable. They contain the editor's brushes, objects and generator data.

## Basic Controls

- **Mouse wheel:** Zoom.
- **Middle mouse button** or **Space + drag:** Move around the map.
- **Ctrl+Z:** Undo.
- **Ctrl+Shift+Z:** Redo.
- **F1:** Show all keyboard shortcuts.

Use the **Layers** panel to show or hide map layers and collisions.

The **Tools** menu contains the town, terrain, province and dungeon generators. Check the preview before applying a result. Province and dungeon generation replace the current map and can be undone.

----

# 笔刷原理

当前实现中，**Library 的 Set 是按图片编号规则组织的自动地形笔刷；Categories 的物体是由多个地图单元格组成的 `.X` 模板。** 两者都引用 `.lib` 中的图片，但是组织方式和使用方式不同。

**Library 的 Set 如何提取**

程序先根据资源目录和文件名注册 Library ID，然后读取 `.lib` 的图片偏移表。需要某张图片时，按偏移读取尺寸、坐标和压缩数据，通过 GZip 解压为 BGRA 像素。对应 [Assets.cs](CrystalStudio/Assets.cs)。

Set 分组依赖固定的编号布局：

| 布局 | 典型资源 | 每组编号跨度 | 用途 |
| :--- | :--- | ---: | :--- |
| Mir2 | `Tiles.lib` | 50 | 地表自动过渡 |
| Mir3 | `Tiles30c.lib` | 30 | 一组包含 A、B 两种地形方向 |
| Mir3Mini | `Tiles5c.lib` | 5 | 随机中心图块，不处理过渡边缘 |
| Small | `SmTiles.lib` | 60 | 中间层自动过渡 |

例如 Mir2 的 `Set n`，起始图片编号为：

```text
start = n × 50
```

组内部分编号有固定含义：

- `+0～4`：中心区域的随机变体。
- `+5～8`：四个外角。
- `+10～13`：四个内凹角。
- `+15、16`：上边；`+17、18`：下边。
- `+20、22`：左边；`+21、23`：右边。

因此，资源必须遵循这些布局规则，任意连续 50 张图片不一定能组成有效 Set。规则见 [AutoTileBrush.cs](CrystalStudio/AutoTileBrush.cs)。

Palette 中的 Set 缩略图也是临时组合出来的。Mir2 使用以下九张图，排列成一个完整地形区域，再缩小到 96×64：

```text
+5     +15     +6
+20    +0      +21
+7     +17     +8
```

对应 [TilePatternPreview.cs](CrystalStudio/TilePatternPreview.cs)。

实际检查当前使用的 `Tiles.lib`：共有 **155,305 张图片**，按整数除法显示 **3,106 个 Set**，末尾不足一组的 5 张不会单独列为 Set。列表生成逻辑见 [MainWindow.axaml.cs](CrystalStudio/MainWindow.axaml.cs)。

**接缝处边缘平滑的原理**

平滑主要由**美术资源预先绘制的过渡轮廓，以及程序对边、角图块的正确选择**实现。

程序将每张地形图块的四个顶点表示为四个位：

```text
左上 1 ───── 右上 2
  │             │
左下 4 ───── 右下 8
```

每个位表示该顶点是否被当前地形覆盖。例如：

| 覆盖掩码 | 选择的形状 |
| ---: | :--- |
| 15：四角都有 | 中心 |
| 12：下面两角有 | 上边缘 |
| 3：上面两角有 | 下边缘 |
| 8：只有右下角有 | 左上外角 |
| 14：只有左上角没有 | 左上内凹角 |

绘制时，算法执行以下步骤：

1. 从周围已有图块恢复共享顶点的覆盖状态。
2. 将笔刷内部涉及的顶点标记为覆盖。
3. 重新计算受影响图块及周围图块的四角掩码。
4. 根据掩码选择中心、边缘或角图，并在同类变体中随机选取。

因为相邻图块使用同一组共享顶点，所以边缘形状能够保持连续。对于掩码 `6`、`9` 这种仅对角覆盖的情况，现有图集没有对应形状，代码会将其填为完整覆盖，并继续更新邻居。

地表图块通常是 96×64，覆盖 **2×2 个 48×32 地图单元格**，因此绘制坐标还会对齐到偶数格。

另外，我抽查了当前资源 **Set 1 的中心、外角、内角和边缘共五张图，像素 Alpha 全部为 255**。因此，这一组的过渡背景已经绘制在图片中；放到不匹配的底色上，仍可能出现明显边界。普通 Set 笔刷不会自动为任意两种材质生成新的过渡图。

**Categories 中的物体如何取得**

当前加载过程是：

```text
catalogue/palette.json
    → 分类、名称、文件路径
    → Objects/对应对象.X
    → 相对坐标和各层图片引用
    → 从当前 .lib 读取图片
    → 组合显示并作为物体模板放置
```

分类来自 JSON 中的 `type`、`subtype`、`regions`、`regionGroups`、`environments`、`tags`、`direction` 等字段，并非根据图片内容实时识别。加载与分组见 [StampCategories.cs](CrystalStudio/StampCategories.cs)。

当前有 **9,159 条分类记录**，对应文件全部存在；`Objects` 目录实际有 **9,170 个 `.X` 文件**，其中 11 个未登记在分类索引中。因此，仅向目录复制 `.X` 文件，不会自动使其出现在 Categories。

当前分类缩略图会读取 `.X`，使用实际 Library 图片按图层和坐标重新绘制。虽然索引包含 `preview` PNG 路径，但这个界面的预览使用实时组合流程，见 [CategoryPalette.cs](CrystalStudio/CategoryPalette.cs)。

项目提供两种生成物体的方法：

- **Export Selection**：选择地图区域，导出其中非空单元格，将坐标转换为相对选区中心的坐标，保存为 `.X`。它保留单元格各层内容，因此选区中的地面也可能一起导出。
- **New Stamp**：创建对象编辑页，可以绘制或粘贴复制的地图内容；保存时裁剪空白、调整相对原点，并同步更新分类索引。包含地表图块时，原点按偶数格对齐。见 [MainWindow.StampEditing.cs](CrystalStudio/MainWindow.StampEditing.cs)。

`Identify Objects` 使用已有 `.X` 模板，通过图片编号和相对位置在地图中匹配对象。当前项目没有找到最初生成这批对象和分类的批处理脚本，因此无法确认最初的 9,159 个对象由人工整理还是外部工具生成。

**物体 `.X` 的保存格式**

`.X` 保存的是**相对坐标、图片引用和单元格属性**。图片像素仍在 `.lib` 中，名称和分类仍在 `palette.json` 中。

当前保存器写入 **v2、小端序二进制格式**，现有 `Objects` 目录中的 9,170 个文件也全部是 v2。读写实现见 [Document.cs](CrystalStudio/Document.cs)。

文件头共 7 字节：

```text
43 23          ASCII "C#"
02             版本号
CellCount      Int32，单元格数量
```

随后依次保存每个单元格：

| 字段，按写入顺序 | 类型／字节数 | 含义 |
| :--- | :--- | :--- |
| X、Y | Int32 × 2，8 字节 | 相对放置原点的格子坐标，可以为负 |
| BackLibrary | Int16，2 字节 | 地表资源库 ID |
| BackImage | Int32，4 字节 | 地表图号及标志 |
| MiddleLibrary | Int16，2 字节 | 中间层资源库 ID |
| MiddleImage | UInt16，2 字节 | 中间层图号 |
| ForegroundCount | Byte，1 字节 | 当前实现允许 0 或 1 |
| 前景记录，可选 | 6 字节 | Library:Int16、Image:Int16、Frames:Byte、Tick:Byte |
| Door、DoorOffset | Byte × 2 | 门及偏移 |
| MiddleFrames、MiddleTick | Byte × 2 | 中间层动画 |
| TileAnimation、TileOffset | Int16 × 2 | 地表动画参数 |
| TileFrames、Light | Byte × 2 | 地表动画帧数、灯光值 |

因此，每个单元格占 **29 字节，无前景图**，或 **35 字节，有前景图**。

还有几个格式细节：

- 图号使用 **1 起始编号**，0 表示无图；访问 `.lib` 时减 1。
- `BackImage` 的低 29 位保存图号；`0x20000000`、`0x40000000` 分别表示低、高阻挡。
- 前景图号取 `Image & 0x7FFF`，需要保留其他标志位。
- 文件没有独立的宽高字段，通过单元格坐标范围计算边界。
- 读取器兼容 v0、v1、v2，重新保存统一写为 v2。

例如实际文件 `mir_00000.X` 是一个摊位模板，包含 **25 个单元格、8 个前景图片引用、18 个带阻挡属性的单元格**，没有地表或中间层图片，文件大小为：

```text
7 + 25 × 29 + 8 × 6 = 780 字节
```

这也说明，一个看起来完整的物体可以由多张图片和额外的阻挡单元格共同组成。

## 地图物体提取工具

通过 **Tools → Extract Map Objects**（工具 → 提取地图物体）打开独立窗口。选择当前地图、当前选区或地图目录，并指定 Crystal `.lib` 资源目录，然后开始扫描。目录模式递归读取 `.map`；无效文件会记录为警告，因此不会阻止其他文件的处理。

工具读取当前地图快照，或者依次读取目录中的地图。扫描、复核和导出均不修改源地图、主编辑器历史或未保存标记。主编辑器可以继续使用；后续编辑不会自动改变已经生成的候选，需要重新扫描。

识别顺序为有效放置元数据、已有 `.X` 模板、剩余前景图片。已有模板按照较大的内容优先匹配。新发现的图片仅在资源库相同、图片序号范围不超过 32、实际显示矩形相交或相距不超过 1 像素时分组；同组不重复使用同一图片引用。因此，连续树木和重复墙体不会仅凭地图格相邻就全部合并。超过 512 张图片或 64×64 格的自动分组保留为单独候选，供人工复核。缺少图片尺寸时保持单独候选，并显示警告。

已有物体继承目录分类；其他候选只有在至少 80% 的组成图片支持同一分类、且没有同票分类时才获得分类建议，否则为“待分类”。工具不推测没有证据的地区、环境或方向。所有扫描结果初始均为未确认。

使用纵向分类标签、缩略图网格、名称/来源搜索和状态筛选查看候选，每页最多 48 项。缩略图仅在可见时请求，同时最多生成两张预览。标题显示简短名称，完整名称和识别依据位于提示中。详情同时显示独立物体与地图上下文；绿色矩形表示组成图片的格子锚点，所选图片显示为金色。粉色与蓝色分别表示低、高碰撞。

- 可多选候选，批量设置名称、分类和确认状态，也可排除候选。
- 可通过图片列表或预览中的锚点选择部件，然后拆分或排除图片；锚点上的碰撞随拆分部件移动。
- 合并使用首个候选的代表位置和同图中最近的所选部件确定相对位置；相同相对位置的重复实例一起合并，没有对应部件的来源继续保留。合并后需要重新确认。
- 新物体默认仅选择其前景锚点上的实际碰撞。周围两格内的其他碰撞作为未选中的建议，可逐格调整；重复内容只提供所有来源共同存在的碰撞建议。
- “撤销/重做”仅作用于提取窗口的复核操作。
- 选区扫描使用整张地图上下文识别完整物体。跨边界物体保留完整内容，并默认不导出；可在详情中明确允许保存完整跨界物体。

确认后选择“保存已确认项至调色板”。同一内容只保存一次；不同动画、碰撞、门或灯光属性形成不同指纹。与已有目录物体完全相同的内容复用原文件，并保存人工确认的名称与分类；其他已有分类信息和自定义字段继续保留。复用文件不会因分类变化而移动。新物体使用标准 `.X` v2 格式，路径为：

```text
Objects/Extracted/<分类>/<SHA-256内容指纹>.X
catalogue/extractions/<会话编号>-<报告编号>.json
```

`catalogue/palette.json` 保留已有字段和未知字段；物体条目增加来源地图和坐标记录。报告包含全部候选的指纹、识别依据、确认状态、来源及实际输出。保存成功后主窗口 Palette 立即刷新。

保存顺序为新物体文件、报告、分类索引。索引使用原子替换；失败或提交前取消时，只移除本次创建的文件。提取窗口与物体编辑器共用写入锁，并在提交前检测其他程序造成的索引变化；检测到变化会停止保存并保留外部修改。

工具完全使用本地规则。地图通常没有可靠的物体边界信息，因此自动候选不能保证符合美术原始物体的划分，必须人工检查。预览代表去重物体的第一处来源，其他来源坐标保留在详情和报告中。语言仍在下次启动时生效。
