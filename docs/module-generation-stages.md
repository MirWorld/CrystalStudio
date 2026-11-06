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

## 阶段 11：自动生成失败原因诊断（2026-11-06）

## 修改内容

- 针对“目标 12 个、最多放置 0 个”的笼统报错，增加结构化原因：区域尺寸不足、地面格子无法对齐、已有地图内容占用、搜索次数达到上限。
- 区域不足提示当前大小和模块尺寸；占用提示区域坐标、大小和已有内容格数，说明地面、对象及碰撞均算占用，应使用空白地图或空白区域。
- 找到可放置的首个模块后，若整个模块池没有方向相反、类型相同且宽度相等的接口对，立即说明匹配条件，避免继续无效搜索和更换种子。
- 保留地图内容保护规则，未引入覆盖已有地图的行为；接口、素材和目录格式不变。失败通过现有窗口反馈，不产生可应用的部分布局。
- 排查时确认运行的是旧 worktree 的 Debug 程序；该目录内 3 个自建模块的接口宽度没有相对方向匹配对。仅只读检查数据，没有修改这些模块、切换运行中的程序或恢复 stash。缺少当时地图及选区参数，未断言本次“0 个”的具体成因。
- 更新生成使用说明。本阶段实现位于 `D:/Code/Mir` 的 `codex/automatic-module-generation` 分支。

## 验证结果

- 仅运行 `ModuleGenerationTests`，**36／36 通过**，包含新增 7 项诊断与窗口验收；未执行全量测试。
- 新增覆盖小区域、已有地面、碰撞标记、地面奇偶对齐、搜索未完成时不得误报无解、接口宽度不匹配，以及窗口显示失败并禁止应用。
- 原有三种布局、种子复现、负坐标、精确目标数量、地图内容保护、取消、应用及整体撤销测试继续通过。
- CrystalStudio Release 构建通过，0 个错误；保留原有依赖 NU1903 警告。
- `git diff --check` 与本地化 JSON 解析检查通过。
- 原有 stash 保留，未推送或创建 PR。本轮未进行真实地图人工操作验收。

本阶段验收命令：

```powershell
dotnet test CrystalStudio.Tests/CrystalStudio.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~ModuleGenerationTests' --nologo --logger 'console;verbosity=normal'
dotnet build CrystalStudio/CrystalStudio.csproj -c Release --no-restore --nologo -v:q
git diff --check
```

## 阶段 12：自动生成独立新地图（2026-11-06）

## 修改内容

- 用户确认报错时使用原地图且保留了截取选区，并明确自动生成应创建新地图。入口改为独立新地图流程，不再把当前地图和选区传入生成器，也不受原地图图层锁限制。
- 生成窗口直接设置新地图宽度／高度，默认 256×256；单边 16～4096，总格数不超过 100 万。每次预览使用新的空白文档，不复制原地图内容。
- 确认按钮改为“创建新地图”，固定在窗口左下方，避免长提示把按钮挤出可视区域。预览成功后点击按钮才新增标签页；取消或失败不创建地图。
- 新标签页保留整次布局的单步撤销／重做；原地图内容、选区、图层锁、未保存状态和编辑历史保留。新地图通过现有 Ctrl+S 保存流程保存。
- 更新使用说明及中英文文案，不再要求用户先手动新建空白地图或取消原选区；接口匹配及连通规则继续保留。

## 验证结果

- 仅测试修改及直接受影响功能，**40／40 通过**：`ModuleGenerationTests` 37 项，生成交互相关测试 2 项，制作模块入库后生成测试 1 项。未运行其他功能的全量测试。
- 实际鼠标点击“创建新地图”后新增标签页，尺寸正确；整体撤销回到空白地图，重做恢复全部实例。切回原标签页后，原内容、选区、图层锁及未保存历史保持。
- 菜单入口在原地图有内容、有选区及锁层时仍可打开；取消后不新增标签页，原状态保持。
- 非整数、过小、单边过大及总格数超限尺寸被拒绝；尺寸变化使旧预览过期，失败保留旧画面且禁止创建。
- 已导出并查看合成素材截图 `crystal-module-ux/generation-new-map.png`，确认新地图尺寸输入及固定确认按钮可见；不是实际用户素材的人工试玩。
- Release 编译通过，0 个错误；保留现有依赖 NU1903 警告。本地化 JSON 解析与 `git diff --check` 通过。
- 修改位于 `D:/Code/Mir` 的 `codex/automatic-module-generation` 分支；原 stash 保留，未提交、推送或创建 PR，没有替换正在运行的旧 Debug 程序及其自建模块数据。

本阶段验收命令：

```powershell
dotnet test CrystalStudio.Tests/CrystalStudio.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~ModuleGenerationTests|FullyQualifiedName~SearchPreservesSelectionAndStaleOrFailedPreviewsCannotApply|FullyQualifiedName~InvalidModuleDiagnosticsIdentifyTheModuleConnectionAndCell|FullyQualifiedName~ReviewWindowUpdatesPreviewRejectsEmptySelectionAndSavesTheReviewedPorts' --nologo --logger 'console;verbosity=normal'
dotnet build CrystalStudio/CrystalStudio.csproj -c Release --no-restore --nologo -v:q
git diff --check
```

## 阶段 13：根据模块占地自动调整地图尺寸（2026-11-06）

## 修改内容

- 将新地图宽高改为参考尺寸，结合所选模块的完整占地及生成实例数量估算起始地图；保留空白边缘及负局部坐标，按偶数地面格对齐。
- 参考宽高接受正整数；过小值由模块占地与数量推高，过大值归一化，不再直接因为参考尺寸超过 100 万格而拒绝。
- 初次放置失败时尝试更大区域，并尝试横向／纵向长条区域以适应长走廊。最多尝试 8 种尺寸，每轮最多 25,000 次放置搜索，始终限制单边 4096、总格数 100 万。
- 窗口显示当前尝试尺寸；预览顶部显示参考值和实际新地图大小。参考输入保持原值，过期预览仍不能创建新地图，停止生成仍有效。
- 拆分实例数量、区域、搜索参数及最小占地容量诊断；明确 2～128 是生成实例数，一种模块也能重复使用。接口不匹配不触发无效扩图。
- 搜索未找到布局改为内部结果，自动尝试下一尺寸时不抛出 InvalidDataException；最终失败汇总本轮最佳实例数量，不返回可应用的部分地图，不声称绝对无解。
- 本次仍在 `D:/Code/Mir` 实施。再次确认用户运行的是旧 worktree 的 Debug 程序；没有替换该进程或迁移其自建模块数据。

## 验证结果

- **49／49 定向测试通过**：`ModuleGenerationTests` 37 项、`ModuleMapGenerationTests` 9 项、生成窗口交互 2 项、制作模块入库后生成 1 项；未运行全量测试。
- 覆盖三种布局的占地估算、空白边缘、负坐标与地面对齐；长走廊经多轮扩大后达到精确数量，同种子复现结果。
- 超大参考整数归一化不溢出；最小占地超过容量和实例数量超限分别报错；接口不匹配仅尝试一轮。
- 尺寸重试中取消立即终止；耗尽尝试汇总最佳数量并保留旧预览，无法创建部分地图。新标签页、原地图状态保留及整体撤销／重做回归通过。
- 已查看合成素材截图 `crystal-module-ux/generation-auto-size.png`：参考 16×16，实际 18×18；顶部显示实际尺寸，固定创建按钮可见。未描述为真实用户素材的人工验收。
- Release 编译通过，0 个错误；保留现有 NU1903 依赖警告。本地化 JSON 解析及 `git diff --check` 通过。
- 原 stash 保留，尚未提交、推送或创建 PR。

本阶段验收命令：

```powershell
dotnet test CrystalStudio.Tests/CrystalStudio.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~ModuleMapGenerationTests|FullyQualifiedName~ModuleGenerationTests|FullyQualifiedName~SearchPreservesSelectionAndStaleOrFailedPreviewsCannotApply|FullyQualifiedName~InvalidModuleDiagnosticsIdentifyTheModuleConnectionAndCell|FullyQualifiedName~ReviewWindowUpdatesPreviewRejectsEmptySelectionAndSavesTheReviewedPorts' --nologo --logger 'console;verbosity=normal'
dotnet build CrystalStudio/CrystalStudio.csproj -c Release --no-restore --nologo -v:q
git diff --check
```

## 阶段 14：模块菜单独立分组（2026-11-06）

## 修改内容

- 在“工具”菜单中，将“从选区生成模块”和“自动生成模块地图”归为同一组，前后用分隔线与其他工具区分。

## 验证结果

- 菜单 XML 结构检查通过：分隔线 → 从选区生成模块 → 自动生成模块地图 → 分隔线。
- CrystalStudio Release 编译通过，0 个错误；保留现有编译警告。
- `git diff --check` 通过。仅检查本次菜单改动，未运行无关功能测试。

## 阶段 15：从 .map 批量拆分模块并随机生成（2026-11-06）

## 修改内容

- 按用户选择实现可调整尺寸的网格分块，在模块菜单分组中新增“从地图批量拆分模块”。选择一个 `.map` 文件后读取独立副本，给出分块尺寸建议并自动预览，不修改原文件或当前地图。
- 分块宽高为 4～512 的偶数，最多 256 块，源地图最多 400 万格；一格边缘余数并入前一块，保留源格子及地面网格对齐。
- 相邻块的接口成对计算，检查共享边界两侧和各自向内一格的碰撞；避开角点重叠，长接口按最多 64 格拆分。空白块、内部不连通、接口过多或没有有效相邻接口的块标明原因并禁止入库。
- 列表与画布双向定位，显示分块边界、当前块接口及不可用原因；可勾选分块、批量选择有效块、调整尺寸重新预览。过期结果不能保存。
- 支持“所选模块入库”和“入库并随机生成”。后者只使用本批保存的模块，自动进入生成预览；沿用实例数量、种子、布局和自适应地图尺寸，确认后创建新地图。
- 批量保存复用单模块保存入口，统一持有目录写锁，先准备并校验所有模块，最后一次提交目录。失败、取消或源内容变化时回滚本批新增文件，不覆盖已有模块，保留目录扩展字段。
- 保存中关闭窗口会等待事务结果，关闭后不自动继续生成。补齐中英文提示和使用文档，并修正旧入库提示中“先打开空白地图”的过时步骤。

## 验证结果

- 定向验收 **88／88 通过**：`ModuleMapSplitTests` 16 项、`ModuleCaptureTests` 33 项、`ModuleGenerationTests` 37 项、生成交互相关 2 项；未运行全量测试。
- `.map` 读取 → 分块 → 批量保存 → 模块重新加载 → 随机生成 → 应用／撤销／重做 → 保存重开流程通过，源地图文件字节保持不变。
- 覆盖边界及向内格碰撞、前景碰撞、余数边缘、空白块、断连分块及孤立接口清理、地面对齐、超量限制、取消和输入错误。
- 故障注入覆盖写到第二个对象时失败、取消、目录并发变化和源数据变化；确认所有本批文件回滚，旧目录和旧模块保留。
- 实际鼠标检查画布选择、列表勾选及“入库并随机生成”按钮；最后针对按钮点击及关闭行为单独复验 **1／1 通过**。尺寸变化禁用保存、无效分块不可勾选、错误原因持续显示和关闭时事务结果明确的测试通过。
- 已导出并查看合成素材截图 `crystal-module-ux/map-split-preview.png`，确认分块范围、选中高亮、接口、计数和底部操作按钮可见；未进行真实用户地图的人工效果验收。
- Release 编译通过，0 个错误；保留现有 nullable／XAML／NU1903 警告。菜单分组结构、本地化 JSON 及 `git diff --check` 检查通过。
- 修改位于 `D:/Code/Mir` 的 `codex/automatic-module-generation` 分支，原 stash 保留；未提交、推送或创建 PR，未迁移旧 Debug 目录里的用户数据。

本阶段验收命令：

```powershell
dotnet test CrystalStudio.Tests/CrystalStudio.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~ModuleMapSplitTests|FullyQualifiedName~ModuleCaptureTests|FullyQualifiedName~ModuleGenerationTests|FullyQualifiedName~SearchPreservesSelectionAndStaleOrFailedPreviewsCannotApply|FullyQualifiedName~InvalidModuleDiagnosticsIdentifyTheModuleConnectionAndCell' --nologo --logger 'console;verbosity=normal'
dotnet test CrystalStudio.Tests/CrystalStudio.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~SplitPreviewSupportsCanvasSelectionCheckboxesAndInvalidatesBeforeSaving' --nologo --logger 'console;verbosity=normal'
dotnet build CrystalStudio/CrystalStudio.csproj -c Release --no-restore --nologo -v:q
git diff --check
```

## 阶段 16：通道拆分规划与真实地图分析（2026-11-06）

## 修改内容

- 明确升级现有“从地图批量拆分模块”功能；用户所指是候选模块选择与拆分，不是选择 AI 模型。
- 新增 `docs/module-channel-splitting-plan.md`，按地面兼容性与诊断、通道矩形分区、人工选择与调整、入库生成验收四个阶段组织后续工作。
- 只读分析用户指定的 `D:/客户端/全文件/WWW/Map/632.map`，把实际失败原因纳入方案。此次只增加规划与分析记录，没有把规划中的算法写入产品。

## 验证结果

- 源图为 200×200；模块通行规则下共有 7,509 个可通行格，主通路 7,504 格，另有三处小岛共 5 格。
- 使用现有代码，以 48×32 分块复现 35 块全部不可用：22 块首先被地面对齐检查拒绝，13 块没有可用接口。其他多组固定尺寸也全部不可用。
- 检出 10,218 个非偶偶坐标位置的地面图片引用，现有地面奇偶一致性校验会拒绝包含这些数据的模块。
- 仅对内存副本进行地面引用对照处理后，48×32 网格可用数为 12／35，32×32 为 22／49；这不是正式兼容修复，尚未验证真实素材渲染等价。
- 已生成并查看碰撞／通行分析图，标出三处孤岛。原 `.map` 分析前后 SHA-256 一致，未保存或覆盖源文件，也未将地图复制进仓库。
- 只运行临时只读分析程序及文档差异检查，没有重跑无关功能测试；`git diff --check` 通过。
- 当前产品算法、未提交功能改动及原 stash 保留。后续阶段仍需按方案实施和验收。


## 阶段 17：地面兼容与可定位诊断（2026-11-06）

## 修改内容

- 统一模块捕获、接口、拼接与生成的碰撞判断；通路分析按四邻域计算连通区域及距墙距离，保留空白格可通行语义。
- 地面引用不再因旧地图同时含多个奇偶坐标而全部被拒绝，保留原图片引用和碰撞位。截取模块采用偶数格平移，旧对象继续按原有地面相位对齐。
- 为区分“原图网格”与旧对象的主动坐标偏移，增加可选 `connections.mapGroundGrid`；目录版本、`.X` 格式及 `footprint` 不变，旧记录无需迁移。接口编辑与保存重开保留该标记。
- 内部断连及地面诊断携带模块、原因和坐标；画布可显示主通路、其他连通区域与阻挡，并通过区域列表或问题定位检查。
- 模块缩略图使用主画布的地面格规则，并在绘制前归一化偶数坐标原点，消除裁剪前后负坐标缩放的像素取整差异。

## 验证结果

- 新增 `ModuleTerrainTests` 5 项通过：原始地面引用保留、奇数起点捕获、负坐标、保存重开、旧对象奇数地面对齐、可选字段与编辑保留、前景碰撞和空白通路。
- 真实 `632.map` 模块保存前与 `.X` 重开后的素材图逐像素相同；原图 SHA-256 保持 `A1AAE49399A94F5EC4E21A47A76222D1A58A17D3E5177D7D4F62BCC7835EA347`。
- 相关捕获、接口、生成和拼接回归通过；没有修改普通地图检查功能的邻接规则。

## 阶段 18：按通道提出矩形分区（2026-11-06）

## 修改内容

- 原拆分窗口增加“固定网格／按通道拆分”，通道模式将尺寸作为参考。
- 按距墙距离、开口宽度、横截面稳定性和局部连通情况排序水平／垂直切线；有限深度搜索允许尚未完成细分的中间节点不连通，最终叶子统一校验。
- 共用矩形分区和接口配对，支持不同大小矩形之间的部分公共边。共享开口两侧及各自向内一格共同通过碰撞检查，避开角点、按 64 格切分长开口，并移除失效邻居的孤立接口。
- 保证整图覆盖、无重叠与无漏格，最多 256 块；保护已知对象和人工框选区域，无法合法细分的块保留原因，不删除小岛或改写碰撞。

## 验证结果

- 10 种通路样例通过：直道、L/S 弯、T/十字岔路、平行通道、环路、孤岛、尽头及宽度变化；检查完整覆盖、原始格子、有效模块连通性及每个保留接口的实际配对位置。
- 保护范围、取消、奇数切线、不合法矩形合并、缺口与重叠校验通过。
- `632.map` 对比（可用模块数／覆盖原图通行格）：48×32 固定网格 **12／4,059**，通道 **33／7,453**；32×32 固定 **22／4,681**，通道 **40／7,455**；64×64 固定 **4／3,391**，通道 **19／7,473**。
- 48×32 通道模式共 64 个候选，余下 30 块无有效配对接口、1 块内部断连；均保留诊断且不可入库，未声称全部区域都能成为模块。

## 阶段 19：候选选择与人工调整（2026-11-06）

## 修改内容

- 模块列表显示按需加载的素材缩略图、尺寸、接口及候选类型；列表与画布选择联动，勾选独立控制入库。
- 添加横向／竖向切线、双侧 A/B 开口草稿、完整公共边移动、相邻矩形合并、保护区反向框选和清除。
- 橙色虚线预览操作，校验失败保留红色草稿及原因并禁止保存；松开提交合法操作，Esc 或失去捕获取消。修改不会绘制或移动地图素材。
- 局部撤销／重做按一次手势记录，按操作数和格子数限制历史内存。文本框保留文本编辑快捷键；未改变矩形的勾选状态在局部调整及重新预览后保留。
- 支持网格和通路覆盖开关、通行区域定位、缩放平移与适应画面；参数变化保留旧画面并标记过期，禁止保存或通过撤销恢复过期结果。

## 验证结果

- 实际鼠标／键盘事件验证反向切线拖动、移动、合并、保护、非法切口、Esc、捕获丢失、列表选择、勾选保留、撤销／重做及文本框快捷键。
- 检查缩放平移后的命中坐标，确认画布只读；草稿与提交前后截图有可辨识变化，双侧开口方向和宽度匹配。
- 已查看 `channel-cut-draft.png`、`channel-cut-rejected.png` 和真实矿洞的 `channel-real-map-preview.png`，核对线条、开口、高亮、缩略图、中文提示及操作区。
- 最后将双侧草稿与保存使用的接口计算合并为同一实现，直接受影响的 **42／42** 项复验通过。

## 阶段 20：批量入库与新地图生成验收（2026-11-06）

## 修改内容

- 候选命名改为“前缀-序号”，不再依赖固定网格行列；复用批量事务、目录锁、失败回滚和本批模块生成流程。
- 补齐使用说明、实施限制与本地真实素材验收入口；测试默认使用合成地图，可通过环境变量指定真实地图和素材目录。

## 验证结果

- 定向模块回归 **178／178** 通过：地形兼容 5、通道拆分 14、网格拆分 16、捕获 33、生成 37、自适应尺寸 9、接口 30、共用拼接 22、原接口／生成交互 12。没有运行仓库全量测试。
- 最后的配对草稿改动另对受影响 42 项复验，全通过；无外部地图环境变量的合成验收独立 **1／1** 通过。
- 真实 `632.map`：33 个有效模块批量入库，在新地图生成 12 个实例，实际尺寸 **98×98**；整体应用、撤销、重做、保存重开通过。
- 素材截图验证模块 `.X` 往返及生成地图 `.map` 往返像素一致；生成地图保存重开后的碰撞逐格一致。原文件哈希和内存修订号不变，实际入库写入临时测试目录，未污染用户模块库。
- Release 项目编译通过，0 个错误；保留原有 nullable／XAML 编译警告及 `Tmds.DBus.Protocol 0.21.2` 的 NU1903 警告。`git diff --check` 通过。
- 工作保存在 `D:/Code/Mir` 的 `codex/automatic-module-generation` 分支；原 stash 保留，未提交、推送或创建 PR。

验收报告与截图：`%TEMP%/crystal-module-ux/channel-acceptance.json`、`channel-real-map-preview.png`、`channel-source-module.png`、`channel-reloaded-module.png`、`channel-generated-map.png`；测试结果 `module-channel-final.trx`。生成结果不自动补墙、封闭未使用开口或修补素材接缝，仍需地图设计检查。

```powershell
$env:CRYSTALSTUDIO_REAL_SPLIT_MAP = 'D:\客户端\全文件\WWW\Map\632.map'
$env:CRYSTALSTUDIO_REAL_SPLIT_ASSETS = 'D:\客户端\全文件\WWW\Data'
$env:CRYSTALSTUDIO_MODULE_UX_ARTIFACTS = "$env:TEMP\crystal-module-ux"
dotnet test CrystalStudio.Tests/CrystalStudio.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~ModuleTerrainTests|FullyQualifiedName~ModuleChannelSplitTests|FullyQualifiedName~ModuleMapSplitTests|FullyQualifiedName~ModuleCaptureTests|FullyQualifiedName~ModuleGenerationTests|FullyQualifiedName~ModuleJoiningTests|FullyQualifiedName~ModuleConnectionTests|FullyQualifiedName~ModuleInteractionTests|FullyQualifiedName~ModuleMapGenerationTests' --nologo
dotnet build CrystalStudio/CrystalStudio.csproj -c Release --no-restore --nologo -v:q
git diff --check
```


## 阶段 21：同步上游与 PR 提交验收（2026-11-06）

## 修改内容

- 将已验收的模块制作、通道拆分及独立新地图生成改动整理为提交。
- 同步 `fluttergo/CrystalStudio` 的 `master`（`5427356`），合并菜单与本地化冲突，同时保留上游地砖悬浮信息、AI 提取及发布脚本更新。
- PR 目标为上游 `master`，来源为 fork 的 `codex/automatic-module-generation`；原 stash 保留。

## 验证结果

- 合并后的定向验收 **180／180** 通过：原 178 项模块测试及与菜单合并直接相关的 2 项地砖悬浮测试，包含真实 `632.map` 入库生成验收；未执行无关全量测试。
- Release 项目编译通过，0 个错误；现有 NU1903 依赖警告保留。
- JSON 无重复键，模块菜单和上游悬浮菜单均各保留一份；`git diff --check` 通过。
- 原 stash `de0cee03d21f4e67e70340bc461e69556f2ab72e` 保留。
