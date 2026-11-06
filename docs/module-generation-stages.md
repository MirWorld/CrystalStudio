# 模块生成工作记录

## 阶段 0：现有能力核对（2026-11-06）

核对基线：本地分支 `codex/editor-reliability-layer-counts`，HEAD `1c816b2`，包含当前未提交内容。未更新远端，因此此结论针对当前工作区，不代表远端最新状态。此前克隆为浅克隆，不能根据单条合并提交判定全部文件作者。

## 修改内容

- 新增本记录，纠正此前把已有生成能力列为待开发项的计划。
- 核对菜单入口、生成器、素材目录模型、预览应用、配方和现有测试源码。
- 本阶段不修改程序代码；保留工作区已有的地砖反查、界面、本地化及 README 改动。

## 验证结果

- 源码核对完成；没有运行应用或进行真实素材视觉验收。
- 本阶段仅新增文档，无程序代码修改，因此不运行编译或自动化功能测试。
- `git diff --check` 通过；新增文档另行执行 `git diff --no-index --check -- NUL docs/module-generation-stages.md`，通过。

## 已有功能与证据

| 能力 | 现有实现 | 结论 |
| --- | --- | --- |
| 新建、编辑、分类及保存对象 | `CrystalStudio/MainWindow.StampEditing.cs` | 已有 .X 编辑流程，无需重新制作基础对象编辑器 |
| 选区导出对象 | `CrystalStudio/MainWindow.axaml.cs` 的 ExportClick | 已有导出入口 |
| 自动提取、人工复核及导出对象 | `CrystalStudio/ObjectExtraction.Service.cs`、`ObjectExtraction.Export.cs` | 已有对象生产流程 |
| 城镇模块 | `CrystalStudio/TownCatalogue.cs`、`TownGenerator.cs` | 已有建筑角色、占地、方向和摆放规则 |
| 墙体模块 | `CrystalStudio/WallBrush.cs`、`WallEntrances.cs` | 已有变体、连接方向、墙角和入口 |
| 河岸模块拼接 | `CrystalStudio/RiverCoastPieces.cs` | 已有 Port、Piece、Link 和 Chain，不能声称项目完全没有模块连接模型 |
| 地牢及洞穴生成 | `CrystalStudio/TempleGenerator.cs`、`TempleGeneratorWindow.axaml.cs` | 已有房间、走廊、种子及天宫/海底/异星主题 |
| 省域及场景生成 | `CrystalStudio/ProvinceGenerator.cs` | 已有村镇、森林遗迹、矿区等主题 |
| 预览与应用、图层锁、过期计划检查及撤销 | `CrystalStudio/MapViewport.Temple.cs`、`MapViewport.Province.cs` | 已有，后续接入应复用 |
| 生成参数及配方 | `CrystalStudio/MapCreationStore.cs` | 已有种子/参数保存、目录哈希及版本信息 |
| 地图资源和连通性检查 | `CrystalStudio/MapCheck.cs` | 已有缺失图片、门及独立可行走区域检查 |

## 待补能力与实施边界

- 当前检查范围内未找到面向用户的通用连接点编辑界面：为任意 .X 定义入口/出口、方向、通路宽度和匹配类型。
- 河岸和墙体存在专用连接模型；尚未找到任意用户 .X 模块共用的目录及拼接入口。不能直接以新通用模型替换这些专用模型。
- 未找到完整的用户模块包导入/导出和批量生成入口；这些是后续候选，不应与首阶段捆绑。
- 原计划中的预览、撤销、种子、配方和一般地图检查从新增清单移除，仅在新功能使用这些能力时做针对性验证。
- 旋转/镜像不能仅变换单元格坐标：现有图片是固定朝向美术，应优先使用明确配套的方向变体，避免图块和碰撞不一致。
- 尚未对照实际 996 地图盒子操作或功能规范，以上是项目能力核对，不是产品完整对标。

## 已确认实施范围

用户已选择“自定义模块连接点编辑，允许少量界面接入”。阶段 1／2 复用现有 .X 编辑器新增连接点定义与校验；随后用户确认“开始继续完善”，阶段 3／4 补齐双模块匹配、预览、应用和撤销。用户进一步要求自动选取、连续拼接、布局控制和整体连通性，阶段 5／6 实现独立的自动模块生成入口，不替换现有城镇／地牢等生成器。

本次已有文件接入集中在 `MainWindow.StampEditing.cs`、`StampCategories.cs`、`Localization/Strings.json`，阶段 4 在 `MainWindow.axaml` 额外增加一个工具菜单项。保留工作区原有地砖反查、`MainWindow.axaml.cs`、`MapViewport.cs` 及 README 改动；不把它们计入本次成果。未创建提交、推送或 PR。

后续每阶段均使用“修改内容”和“验证结果”记录，只执行对应修改的测试、必要编译及差异检查，不默认运行全量回归。不将历史测试报告写作本次通过结果。

## 阶段 1：连接点数据、校验与保存（2026-11-06）

## 修改内容

- 新增连接点定义：编号、对象局部坐标、北／东／南／西边界、连接类型和开口宽度。
- 为目录对象增加可选 `connections` 元数据；没有连接点的旧对象不额外序列化该字段，保持旧目录兼容。
- 校验重复编号、重叠、边界位置、宽度、碰撞及向模块内部延伸一格的通路；最多 64 个连接点，每个宽度 1～64 格。
- 保存对象时复用已有裁剪规则，同步平移连接点和编辑器局部原点，保留地面层偶数网格对齐。
- 对象形状或碰撞改变导致连接点失效时，在写入文件前拒绝保存；可修正或清空连接点后再保存。
- 复用现有分类目录写入、失败回滚和恢复快照机制；不修改 .X 二进制格式。

## 验证结果

- `ModuleConnectionTests` 中 26 个数据及存储测试用例通过，包括四方向开口、碰撞、重复／越界输入、负坐标和地面网格对齐。
- 临时目录中的 .X 文件与目录元数据保存／重新读取通过；重复保存的坐标保持稳定。
- 旧目录兼容、清空连接点以及恢复快照携带连接点和原点测试通过。
- 本阶段与阶段 2 合并验收；仅运行新增连接点测试类，没有运行全量测试。
- 本次没有对文件系统故障注入进行验收，未声称验证整个既有保存回滚流程。

## 阶段 2：连接点编辑界面与现有入口接入（2026-11-06）

## 修改内容

- 在 .X 对象编辑器的分类按钮旁新增“模块连接点”按钮，普通地图不显示该工具栏。
- 新增单元格示意图：显示对象内容、碰撞和连接点，并支持点击定位。
- 支持添加、选择编辑、删除连接点；应用时包含当前表单修改，取消不修改原对象。
- 将已应用的修改接入未保存标记及恢复版本；重新编辑对象分类时保留连接点。
- 补充中文／英文文案和使用说明 `docs/module-connections.md`。
- 针对性测试发现并修复删除后再次应用会重新添加连接点的问题：草稿比较改为按方向枚举值比较。

## 验证结果

- CrystalStudio 项目 Release 编译通过，0 个错误；最终针对性测试构建也通过。
- 4 个 Avalonia 无界面交互测试通过：添加／更新／删除／取消、无效输入与空对象清空、示意图点击定位、主界面入口与分类保留。
- 最终连接点测试 **30／30 通过**，0 失败、0 跳过。
- `git diff --check` 通过；新增源文件、测试和文档另行进行空白差异检查。
- 编译仍报告既有 nullable／XAML 警告，以及 `Tmds.DBus.Protocol 0.21.2` 的 NU1903 漏洞警告（GHSA-xrw6-gwf8-vvr9）；本次未修改该依赖。
- 验收限于自动化数据测试、临时文件往返及 Avalonia 无界面交互；没有进行真实素材的人工视觉验收，也未验证生成器消费连接点，因为本次尚未接入生成器。

验收命令：

```powershell
dotnet build CrystalStudio/CrystalStudio.csproj -c Release --no-restore --nologo -v:q
dotnet test CrystalStudio.Tests/CrystalStudio.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~ModuleConnectionTests --nologo --logger 'console;verbosity=normal'
git diff --check
```

## 阶段 3：双模块匹配与拼接计划（2026-11-06）

## 修改内容

- 从已保存目录读取 .X 和连接点，按类型、宽度和相对方向匹配两个模块；类型不区分大小写。
- 根据第一个模块原点和两侧连接点自动计算第二个模块的位置，保持原有图片朝向。
- 在地图副本上生成拼接结果，检查地图边界、模块占地、已有内容、地面偶数网格以及接缝和向内一格的通路。
- 保守保护两个模块的完整占地矩形，包括空洞；不覆盖已有内容。单次占地上限为 1,000,000 格。
- 复用现有对象实例元数据，为后续保存、拾取和撤销保留两个独立实例。
- 增加取消检查与源地图版本记录，预览不写入原地图，输入对象修改不改变已生成的快照。

## 验证结果

- 新增 20 个匹配／计划测试用例全部通过：四方向逐格相邻、负局部坐标、不兼容连接、阻塞通路、越界和整数溢出、已有内容与空洞保护、地面网格及取消。
- 验证预览后原地图单元格和对象实例数量不变。
- 与阶段 4 合并执行最终验收，仅运行连接点与双模块拼接测试。
- 通路验证仅覆盖接缝及相邻内侧格，不宣称模块内所有入口整体连通。

## 阶段 4：拼接预览、应用与撤销（2026-11-06）

## 修改内容

- 新增“工具 → 拼接模块”，选择两个已保存模块、匹配连接点及第一模块原点，自动计算第二模块位置。
- 提供素材地图预览和连接示意图；参数改变立即使旧预览失效，关闭窗口取消未完成任务。
- 应用前检查图层锁和源地图版本，一次写入两个实例，支持整体撤销／重做；取消不修改地图。
- 支持拼接后的地图保存并重开，复用原有对象实例存储格式。
- 更新连接点编辑提示、中文／英文文案和使用说明。
- 针对性测试发现初始 TextChanged 延迟事件会误取消新预览，改为同步监听文本属性变化，避免初始事件和真实参数修改混淆。

## 验证结果

- CrystalStudio Release 编译通过，0 个错误；最终针对性测试构建通过。
- 新增 7 个 Avalonia 无界面交互测试全部通过：整体撤销／重做与地图文件往返、五类图层锁／过期计划、匹配筛选和参数失效、取消与关闭、重新读取文件、素材预览与示意图、工具菜单入口。
- 仅运行 `ModuleConnectionTests`（30 个）和 `ModuleJoiningTests`（27 个）：**57／57 通过**，0 失败、0 跳过；没有运行全量测试。
- 预览测试等待合成素材解码完成，并检查画面中实际出现对应素材像素；检查了导出的界面截图，表单、预览、应用及取消按钮显示正常。
- `git diff --check` 通过；新增源文件、测试和文档另行检查空白差异。
- 编译保留既有 107 个警告，包括 nullable／XAML 和 `Tmds.DBus.Protocol 0.21.2` 的 NU1903 警告。本次未修改这些范围外代码。
- 未对真实用户素材库进行人工试玩；当前仅支持两个新模块放入空白区域，不包含整图批量生成、已有实例自动接续、旋转素材或连接网络持久化。

本阶段验收命令：

```powershell
dotnet build CrystalStudio/CrystalStudio.csproj -c Release --no-restore --nologo -v:q
dotnet test CrystalStudio.Tests/CrystalStudio.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~ModuleJoiningTests|FullyQualifiedName~ModuleConnectionTests' --nologo --logger 'console;verbosity=normal'
git diff --check
```

## 阶段 5：自动选取、连续拼接与整体连通性（2026-11-06）

工作区说明：执行期间，原目录被外部切换到新版 master，阶段 1～4 改动进入 GitHub Desktop 暂存记录 `7a92d31`。已在独立工作区 `C:/Users/10335/.codex/worktrees/automatic-module-generation/Mir` 基于 `ce1b47a` 恢复模块功能相关文件及少量界面接入。没有弹出／删除暂存记录，也没有把其他任务的地砖反查和 README 改动带入本任务。原主目录不被本次开发覆盖。

## 修改内容

- 新增自动生成器，从用户勾选的已保存模块池中自动选择起始模块、位置和后续实例，按类型、宽度和相对方向匹配接口；同一种模块可重复使用。
- 支持 2～128 个模块、随机种子、生成区域及紧凑／分支／链式布局策略，不要求用户逐一选择模块或接口。
- 增加有界回退搜索和起始位置重试；先排除出口位置已占用的不可能接口，再尝试匹配实例，避免无效候选耗尽搜索预算。
- 检查每个模块内部所有可行走格的四邻格连通性，包括低碰撞、高碰撞和前景阻塞；拒绝内部隔断和孤立可行走区域。
- 对实际生成结果再次执行限制在模块占地内的整体连通性检查，禁止借用外部空白绕路，也不把对角接触算作连通。
- 保持既有内容、完整模块占地、负局部坐标、地面偶数网格和单接口只用一次的约束。只有达到请求数量后才产出可应用计划，失败时报告本轮最多放置数量。
- 对模块池数量和累计范围设置上限，并在逐个读入时检查，避免先读入过多文件再拒绝。

## 验证结果

- 自动模块类型选择、实例重复使用、三种布局、精确数量、接口匹配和不重叠测试通过。
- 相同种子复现、不同种子变化、中心位置放不下后重试起点、16×16 区域密铺 16 个模块测试通过。
- 128 个模块的自动生成测试通过。初次测试曾因无效接口反复消耗搜索预算而只达到 84 个；修正搜索排除规则后保持原目标数量通过。
- 模块内部隔断（地面／前景碰撞）、孤立可行走小块、外部绕路、对角接触、已有内容保护及地面网格测试通过。
- 无解、预算耗尽、无效参数、无效模块池和取消测试通过，确认不返回可应用半成品且不修改源地图。
- 与阶段 6 合并执行最终针对性验收，没有运行全量测试。

## 阶段 6：自动生成界面与完整应用流程（2026-11-06）

## 修改内容

- 新增“工具 → 自动生成模块地图”，提供模块池勾选、数量、种子、区域和布局设置；有选区时带入选区，否则默认使用地图中心最多 128×128 格。
- 点击生成后自动完成选取和连续连接，提供素材地图预览、连接示意图和整体连通性结果。
- 支持停止生成、参数失效、重新读取模块文件，以及取消／关闭时丢弃后台结果。
- 复用既有一次应用／整体撤销／重做和地图实例存储机制；图层锁、源地图变化和错误地图都会阻止旧计划写入。
- 更新中文／英文文案及使用说明，说明模块准备、布局策略、搜索失败处理和当前边界。

## 验证结果

- CrystalStudio Release 编译通过，0 个错误；最终针对性测试构建通过。
- 仅运行本任务的三个测试类：`ModuleConnectionTests`（30）、`ModuleJoiningTests`（27）、`ModuleGenerationTests`（29），**86／86 通过**，0 失败、0 跳过。前两类验证暂存代码恢复到新版基线后的模块功能兼容性。
- 自动生成的 7 个 Avalonia 界面／集成测试通过，覆盖无手动选点生成、参数及模块池变化、取消关闭、文件变化、地图版本、五类图层锁、整体撤销／重做、保存重开和主菜单选区传入。
- 使用合成素材检查了实际预览像素及连接示意图，导出的自动生成界面截图中 12 个模块、11 处接口连接和 192 个可行走格显示正常；未声称完成真实用户素材库的人工试玩。
- 暂存、未暂存及新增文件的空白差异检查通过。
- 编译仍包含现有 nullable／XAML 警告及 `Tmds.DBus.Protocol 0.21.2` 的 NU1903 警告，本任务未升级依赖。
- 当前生成区域内的新模块整体连通已验证；不包含原地图其他区域的全局改造、未使用出口封闭、图片旋转、连接网络持久化或后续手动编辑后的自动修复。种子不会自动保存为地图配方。
- 未创建提交、推送或 PR。

本阶段验收命令（在独立工作区执行）：

```powershell
dotnet build CrystalStudio/CrystalStudio.csproj -c Release --nologo -v:q
dotnet test CrystalStudio.Tests/CrystalStudio.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~ModuleGenerationTests|FullyQualifiedName~ModuleJoiningTests|FullyQualifiedName~ModuleConnectionTests' --nologo --logger 'console;verbosity=normal'
git diff --check
git diff --cached --check
```

## 阶段 7：从选区制作模块和安全入库（2026-11-06）

## 修改内容

- 新增地图矩形选区采集，复制所有图层及碰撞信息，不修改源地图；限制选区尺寸、总格数并拒绝完全空白选区。
- 自动识别四边通路：边界和向内一格均可行走才作为候选，合并连续通路、按 64 格拆分宽接口，避免角点被重复占用。超过 64 个候选时保留全部候选供用户筛选。
- 增加可选 `connections.footprint` 保存原选区占地，保留空白边缘；裁剪居中时同步平移接口与占地，保持地面偶数网格。旧连接元数据无需迁移。
- 将占地接入连接点校验、编辑窗口、手动拼接、自动生成和整体连通检查，避免识别与放置采用不同边界。
- 保存前复用自动生成器的内部连通及地面网格校验，拒绝无出口、出口非法和内部隔断模块。
- 新增创建式入库：生成独立文件名，写入 `.X` 和素材目录；保留目录未知字段和已有文件，复用目录写锁，提交前检查源地图版本与目录内容，失败或取消时回滚本次新文件。

## 验证结果

- 新增模块制作的 28 个核心测试通过，覆盖角点、低／高／前景碰撞、向内阻挡、连续通路拆分、候选过多、空选区／越界／尺寸限制及名称校验。
- 空白边缘、原选区尺寸、奇数起点下的地面网格、归一化后二次保存、旧元数据兼容及非法占地测试通过。
- 临时目录完成“采集 → 保存 → 重读 `.X` 与元数据 → 加载模块池 → 自动生成 12 个模块”测试，得到 11 处连接和 768 个连通可行走格；源地图未被修改。
- 文件名隔离、同名模块并存、未知目录字段保留和已有文件不覆盖测试通过。
- 对象写入／目录提交故障、取消、源地图变化和目录并发修改测试通过，确认没有残留新 `.X` 或临时文件，没有覆盖外部目录变更。

## 阶段 8：制作入口、预览确认与流程验收（2026-11-06）

## 修改内容

- 新增“工具 → 从选区生成模块”，无选区时提示按 M 框选，避免无意导出整张地图。
- 新增模块名称、接口勾选、素材地图预览和连接示意图；复用连接点编辑器手动调整识别结果，校验通过后允许“保存模块入库”。
- 保存后刷新素材目录并提示打开空白地图进行自动生成；自动生成窗口没有模块时直接指引新的制作入口。
- 保存期间关闭窗口会请求取消并等待结果，保证已完成的入库能反映在主窗口目录中。
- 增加中文／英文文案并更新使用说明，说明碰撞识别依据、空白格行为、接口宽度及地面网格要求。

## 验证结果

- CrystalStudio Release 编译通过，0 个错误；后续针对性测试构建通过。
- 仅运行模块相关测试。首次四类联合执行 **117／117 通过**；补充素材画面、编辑入口、保存关闭行为和入库后生成窗口的验收后，仅重跑 `ModuleCaptureTests`，**33／33 通过**。本任务共 **119 个不同测试通过**：连接点 30、拼接 27、自动生成 29、模块制作 33。未运行全量测试。
- 5 个模块制作 Avalonia 界面测试通过，覆盖勾选变化／无接口禁止保存、取消不写入、手动编辑后接口更新、保存中关闭、主菜单及选区传入，并实际从生成窗口读入新模块生成 4 个连通实例。
- 使用合成素材断言预览像素，导出并查看素材预览及连接示意图截图，确认布局、控件和修改后的接口显示正常。没有将合成素材验收描述为真实素材人工试玩。
- 暂存、未暂存及新增文件空白检查通过；中文／英文文案 JSON 可解析。
- 编译保留现有 nullable／XAML 及 `Tmds.DBus.Protocol 0.21.2` 的 NU1903 警告。
- 本次制作流程不包含自动补绘墙体、封闭未选接口或识别图片语义；仅保存通过内部连通校验的选区模块。没有向真实用户素材目录写入测试模块。
- 全部工作保存在独立工作区，未写回原目录、未提交、未推送、未创建 PR。

本阶段验收命令：

```powershell
dotnet build CrystalStudio/CrystalStudio.csproj -c Release --no-restore --nologo -v:q
dotnet test CrystalStudio.Tests/CrystalStudio.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~ModuleCaptureTests|FullyQualifiedName~ModuleGenerationTests|FullyQualifiedName~ModuleJoiningTests|FullyQualifiedName~ModuleConnectionTests' --nologo --logger 'console;verbosity=normal'
dotnet test CrystalStudio.Tests/CrystalStudio.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~ModuleCaptureTests' --nologo --logger 'console;verbosity=normal'
git diff --check
git diff --cached --check
```

## 阶段 9：统一画布编辑和可见操作反馈（2026-11-06）

## 修改内容

- 在 `D:/Code/Mir` 的 `codex/automatic-module-generation` 分支实施；保留切换分支前的 stash，不恢复或覆盖其中的其他修改。
- 制作模块改为左侧接口列表、中央素材画布、右侧属性的单窗口流程；已有 `.X` 的连接点窗口复用同一组件，不再从制作窗口打开嵌套弹窗。
- 分离当前选中、是否启用和未提交草稿；画布与列表双向选择。普通接口绿色，选中接口青色粗框并显示端点，草稿橙色虚线，非法草稿红色，未启用接口灰色虚线；附编号和朝外箭头。
- 新增边界点击／反向拖动建接口、主体移动、端点调整、一格接口两端手柄、属性即时预览以及局部撤销／重做。保留高级坐标输入，文本框内快捷键遵循文本编辑行为。
- 连接点越界、重叠和碰撞分别给出结构化诊断及可定位坐标。非法输入阻止选择切换和应用；Esc 取消草稿，指针捕获丢失时取消未完成拖动。
- 新增只读预览的滚轮缩放、中键／空格平移、适应画面、网格和碰撞开关。接口与素材共用坐标变换，空白边缘及放大预览也显示网格。
- 缓存编辑快照的单元格查询和固定几何的内部连通结果，避免每次拖动都重建整张模块的索引或执行完整洪泛检查；实际保存仍执行完整校验。
- `.X`、连接点和占地存储格式保持兼容；未启用接口只留在当前编辑会话，不写入保存结果。

## 验证结果

- 相关连接点和模块制作回归测试通过；已有对象应用／保存、清空旧接口、空白边缘、地面网格及入库事务的测试继续通过。
- 新增实际鼠标／键盘验收覆盖新增即时预览、反向拖动、列表与画布选择同步、复选框独立启用、移动／调整端点、一格角点接口、Delete、撤销／重做和文本框输入行为。
- 非法输入保留、切换选择被阻止、碰撞位置诊断、红色草稿、Esc 取消、鼠标捕获丢失、负局部坐标和实际滚轮／中键导航测试通过。
- 验收中修复了从输入框进入新增模式时失焦可能提交临时标记的问题，并修复了小模块受普通地图最小缩放限制、无法缩小的问题。
- 对橙色草稿、青色选中框和红色非法接口进行了像素断言；使用合成素材检查素材预览及放大后空白格的网格开关，导出并查看截图。未把合成素材验收描述为真实素材人工试玩。

## 阶段 10：收敛入口和生成地图交互（2026-11-06）

## 修改内容

- 删除独立“拼接模块”菜单、窗口和主窗口事件入口，移除 5 个仅测试旧窗口的界面测试及过时界面文案。保留自动生成依赖的加载、放置计划、应用与整体撤销逻辑及相关核心测试。
- 模块池展示按需异步加载的缩略图、名称、占地尺寸及接口摘要，路径放入提示；提供名称／标签／接口类型搜索、搜索结果批量选择和总选中数，过滤不改变已有勾选。
- 生成区域设置改为可展开面板，默认显示区域摘要；数量、种子、三种布局算法和生成范围规则保持兼容。
- 生成预览直接叠加素材、模块边界、实例编号和接口；支持缩放、平移和点击查看实例来源及接口信息。
- 区分未生成、生成中、可应用、过期和失败；生成中提供活动指示与停止按钮。参数变化、失败及取消保留旧预览，旧结果不可应用。
- 文件或接口校验失败时选中问题模块并显示接口编号和坐标，结构化诊断不依赖解析本地化错误文案。生成成功时清理旧错误定位信息。
- 更新中文／英文文案和使用说明，明确选区制作、直接编辑、禁用与选中的区别及新的生成流程。

## 验证结果

- CrystalStudio Release 构建通过，0 个错误；最终针对性测试构建通过。
- 仅执行 5 个模块测试类，共 **126／126 通过**：`ModuleConnectionTests` 30、`ModuleCaptureTests` 33、`ModuleGenerationTests` 29、`ModuleJoiningTests` 22、`ModuleInteractionTests` 12。未执行全量测试。
- 网格显示的最终调整另行定向复验 `CapturePreviewRendersArtworkAndCanEditDetectedConnections`，**1／1 通过**，包含素材像素和网格开关前后的画布像素差异断言。
- 收尾时补充“新增悬停草稿尚未放置时点击启用框，不应意外新增接口”的真实鼠标验收，修正该切换行为后定向复验 `SelectionIsBidirectionalAndDisabledConnectionsRemainEditableAndVisible`，**1／1 通过**。
- 搜索保持勾选、仅选择当前搜索结果、缩略图与尺寸加载、旧预览保留且不能应用、停止生成、实例点击选中及结构化错误定位测试通过。
- 主菜单测试确认独立拼接入口不存在，选区仍能传入自动生成；原有精确数量、内部／整体连通性、图层锁、应用／撤销／重做及保存重开测试通过。
- 已查看统一制作窗口、草稿／选中／非法状态和带缩略图的生成预览截图；截图保存在系统临时目录 `crystal-module-ux`，没有向真实素材目录写入测试模块。
- `git diff --check`、新增文件空白／冲突标记检查及本地化 JSON 检查通过。编译仍有现有 nullable／XAML 和依赖 NU1903 警告，本次未升级依赖。
- 未推送或创建 PR。原有 stash `before-module-generation-switch-2026-11-06` 保留。

本阶段验收命令：

```powershell
dotnet build CrystalStudio/CrystalStudio.csproj -c Release --no-restore --nologo -v:q
dotnet test CrystalStudio.Tests/CrystalStudio.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~ModuleInteractionTests|FullyQualifiedName~ModuleCaptureTests|FullyQualifiedName~ModuleGenerationTests|FullyQualifiedName~ModuleJoiningTests|FullyQualifiedName~ModuleConnectionTests' --nologo --logger 'console;verbosity=normal'
dotnet test CrystalStudio.Tests/CrystalStudio.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~CapturePreviewRendersArtworkAndCanEditDetectedConnections' --nologo --logger 'console;verbosity=normal'
dotnet test CrystalStudio.Tests/CrystalStudio.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~SelectionIsBidirectionalAndDisabledConnectionsRemainEditableAndVisible' --nologo --logger 'console;verbosity=normal'
git diff --check
```
