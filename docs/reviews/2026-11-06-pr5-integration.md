# PR #5 功能整合与冲突分析

## 范围与基线

- 仓库：fluttergo/CrystalStudio。
- 初始基线：master `f1845120506dcd3d1a9b4db58af9f79381b04e6f`，已包含 PR #6、#7。
- 发布前 master 更新至 `f57c28f621118cdd1ed1d281fe410f64555f91e4`；重新整合并保留附件图库加载、编号冲突检查和三主题美术规格，StudioJson 的双方注册项均保留。
- 唯一开放 PR：[#5](https://github.com/fluttergo/CrystalStudio/pull/5)，源提交 `2c58aa72b8cd8f3eaafff0fdc07803c397843235`。
- 保留两个分支的历史；不强制覆盖远端，不删除分支。

## 保留的功能

PR 的返回地图设置、用途模板、尺寸快捷选择、临时地图、预设管理和导入导出、素材预检与可选资源降级、最多五个候选和会话收藏、预览缩放平移与局部细节均保留。

同时保留 master 的村镇郊野、森林遗迹、矿区、天宫/海底/异星地牢主题及参数，quick-map-v5 配方与素材哈希、自动恢复 JSON 注册、Sanxiu 偏移和编辑器可靠性功能。

## 冲突与处理

1. `Localization/Strings.json`：双方在同一位置新增不同词条。合并为并集，逐项确认两个分支原有词条和值均保持。
2. `ProvinceGeneratorWindow.axaml.cs`：master 增加主题选择与遗迹/矿区资源；PR 增加异步可选素材加载、预检和预览缓存。保留主题处理函数，将预检后的 options 传入生成器，并继续传递 towns、ruins、mines。
3. `TempleGeneratorWindow.axaml.cs`：master 增加世界主题；PR 增加素材预检和局部预览。保留 SelectedDefinition、themes 和主题说明，并组合预检、原请求缓存及完成事件。
4. 语义冲突：旧测试要求缺少树素材时抛异常，与 PR 明确的“可选素材缺失仍可生成”目标冲突。改为验证空装饰列表，保留有效树加载与取消检查。主题核心素材的强制检查不变。

以上均可同时保留两侧意图，不涉及需要用户二选一的功能取舍。

## 整合期间修正

- 原固定高级区切分索引会拆开新地牢类型的标签和下拉框。改为按 StyleList、TempleList、BaseList 的实际位置划分完整基础设置分组。
- 可选素材检查漏捕获 InvalidDataException，导致缺少城镇图像时仍抛错。补齐该异常，仍传播取消与其它未预期异常。
- 一条 PR 新测试把输出路径写死为 Windows 分隔符，改用 Path.Combine，保持对完整路径的断言。
- 新增三项兼容测试：基础选择器完整分组；地牢与省域主题、数量、种子往返恢复；降级后保留主题和遗迹/矿区数量。

## 初始基线实际验证

Linux x64、.NET SDK 10.0.403；使用单进程 MSBuild，关闭构建遥测；未更改项目依赖或屏蔽编译诊断。

- master 原始基线 Debug 全套：212 项，207 通过、5 失败。
- 整合候选 Debug 全套：246 项，241 通过、5 失败；失败名称与基线完全一致。
- Release 构建：0 错误、110 警告。
- Release 功能专项：65 项全部通过，覆盖 PR 新功能、主题、素材降级和合并兼容。
- JSON 解析、双方词条保持检查、git diff --check 通过。

全套仍有以下原有失败，不能称为“全绿”：

- StudioLogTests.RotationLimitsOwnedLogsAndPreservesOtherFilesAndActiveSessions
- MigrationTests.CompactTemplatesRetainOriginalToolLayerAndPaletteDimensions
- AssetSourceFormatTests.SanxiuCategoryPreviewsResolveAllThreeLayerLibraryIds
- AssetSourceFormatTests.SanxiuModeRejectsV2AndProtectedV5
- AiTests.CustomTemplateEscapesPromptAndUsesResponsePath

复现命令（使用可写的用户/缓存目录）：

```sh
DOTNET_CLI_TELEMETRY_OPTOUT=1 dotnet test CrystalStudio.sln -c Debug -m:1 -p:UseSharedCompilation=false -p:UsedAvaloniaProducts=
DOTNET_CLI_TELEMETRY_OPTOUT=1 dotnet build CrystalStudio.sln -c Release -m:1 -p:UseSharedCompilation=false -p:UsedAvaloniaProducts=
DOTNET_CLI_TELEMETRY_OPTOUT=1 dotnet test CrystalStudio.sln --no-build -c Release -m:1 -p:UsedAvaloniaProducts= --filter 'FullyQualifiedName~MapCreationUsabilityTests|FullyQualifiedName~QuickMapRefinementTests|FullyQualifiedName~QuickMapMergeCompatibilityTests|FullyQualifiedName~ProvinceSceneryLoadingTests|FullyQualifiedName~DungeonThemeTests|FullyQualifiedName~MiningSceneTests|FullyQualifiedName~ForestRuinTests'
```

## 验证边界与后续风险

- 未执行 Windows 实机验收、真实游戏素材全流程或 NativeAOT 发布。Headless 测试通过不等于这些验收已完成。
- 现有间接依赖 Tmds.DBus.Protocol 0.21.2 存在 NU1903：[GHSA-xrw6-gwf8-vvr9](https://github.com/advisories/GHSA-xrw6-gwf8-vvr9)。此次 PR 未引入或升级此依赖，保留提示，未扩大整合范围修改包版本。

## 最新基线重新验证

对 master `f57c28f621118cdd1ed1d281fe410f64555f91e4` 重新整合后：

- 最新原始基线 Debug：219 项，214 通过、5 失败。
- 最终候选 Debug：253 项，248 通过、5 失败；失败名称与最新原始基线完全一致。
- 最终候选 Release 编译通过；原 65 项专项加 7 项 AttachedAssetTests，共 72 项全部通过。
- 继承新增附件图库实现、附件清单及三主题美术规格，不覆盖上游新文件。
- 仍未进行 Windows 实机或真实游戏素材全流程验收。
