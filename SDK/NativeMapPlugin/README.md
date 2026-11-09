# 原生地图格式插件 ABI v1

私有地图实现单独编译成 C/C++ DLL，无需公开解析源码。编辑器普通 .NET 版和
NativeAOT 版都通过同一套 C ABI 调用，不引用插件的 C++ 类、STL 或托管程序集。
本目录头文件是公共 SDK，example.cpp 是可独立编译的演示，未实现真正加密。

## 使用

1. 实现 `crystal_map_plugin.h` 中的导出函数，使用 `extern "C"`、cdecl 和固定宽度类型。
   插件架构必须与编辑器一致（Windows 发布版为 x64）。可以只读或只写；对应能力为
   false 时，可不导出 probe/read 或 write。版本、信息、free 始终必须导出。
2. 在“文件 → 地图格式插件 → 加载 DLL…”选择 DLL。路径记录在用户配置目录的
   map-plugins.json，下次使用自动加载。依赖 DLL 放在插件旁，按系统加载规则解析；
   推荐静态链接 C++ 运行库。自动部署可放在程序的 `Plugins/MapFormats/*.dll`，或
   `Plugins/MapFormats/插件目录/*.plugin.dll`（子目录内其他 DLL 视为依赖，不当作入口）。
3. “打开地图”按文件内容调用 probe，再调用唯一匹配插件的 read。无匹配时交给
   内置读取器；多个插件同时匹配会提示冲突，需修正 probe 或移除冲突插件后重启。
4. Ctrl+S 保持原插件格式；“另存为”先明确选择格式，支持相同扩展名的多个版本。
   只读或缺失插件不能覆盖保存，可以另存为内置 Crystal 格式。新地图也可另存为插件格式。
5. DLL 在进程内执行，必须可信；C++ 异常必须在导出函数内捕获并转换成错误码，
   内存越界、访问冲突和原生进程崩溃无法由编辑器隔离。更新 DLL 需关闭编辑器后替换。
   在配置中移除记录并重启可卸载注册插件；自动部署插件还需移走对应入口 DLL。

示例构建（VS 开发者命令行，或已配置 MinGW）：

```powershell
cmake -S SDK/NativeMapPlugin -B Build/NativePluginExample -A x64
cmake --build Build/NativePluginExample --config Release
# MinGW 也可直接编译：
g++ -std=c++17 -shared -static-libgcc -static-libstdc++ SDK/NativeMapPlugin/example.cpp -o Build/example.plugin.dll
```

## 函数与内存

`cs_map_api_version()` 返回 1；`cs_map_info()` 返回进程生命周期内有效的静态 UTF-8
JSON 字符串，内容见头文件。id 全局唯一、稳定，不能是保留值 crystal；扩展名为点加
ASCII 字母数字。更改不兼容协议时需升级 ABI，而不是复用版本号。

probe 输入为完整原文件，只读、仅在调用期间有效；返回 0/1，负值表示识别异常。
read 输入原文件、输出 CSMP；write 输入 CSMP、输出完整目标文件。两者返回 0 成功，
非 0 失败，同时可返回 UTF-8 错误缓冲。必须初始化两个输出缓冲；失败时已分配的
缓冲也由宿主调用 free。free 对空缓冲必须安全。宿主复制后调用 free，绝不使用
自己的分配器释放插件内存。不能保存输入指针供后台线程使用。

每个插件实例调用串行化，多个插件可能分别执行。不能假设调用在 UI 线程。
单个原文件/结果/错误缓冲上限 1,000,000,000 字节。保存先完整转换，再原子替换目标，
原文件保留 `.bak`。插件不直接操作目标文件，因此不要创建旁文件或依赖输入路径。

## CSMP 二进制交换格式

所有数字小端，线格式紧密排列，没有对齐填充。buffer 参数本身使用平台默认对齐。

| 顺序 | 类型 | 说明 |
|---|---|---|
| 1 | byte[4] | ASCII CSMP |
| 2 | int32 | 版本 1 |
| 3 | int32 × 2 | width、height，均 1–32767，乘积最多 16,000,000 |
| 4 | int32 | asset_format：0 Crystal，1 Sanxiu（编辑器规范化素材 ID） |
| 5 | int32 + byte[] | 私有附加数据长度及内容 |
| 6 | int32 | 稀疏非空格子数量，不超过 width × height |
| 7 | cs_map_cell[] | 每格 35 字节，字段顺序/位宽见头文件，坐标不能重复或越界 |

未列出的格子为空。has_front 为 0/1，前景图片只有一层。图片索引保留原始位模式，
不能把高位清零：BackImage 的 0x20000000/0x40000000 是编辑器的低/高阻挡位。
旧格式前景的 0x8000 等版本差异应在插件转换时处理，参照本项目
LegacyMapReader.cs、Document.LayerCounts.cs，不能直接照搬私有格式位定义。素材 ID 必须映射到编辑器所用
素材库；本接口只扩展地图格式，不扩展图片素材库解码。

私有附加数据由插件自行定义，例如原始头、未知字段或原文件副本；编辑器原样保存，
并随恢复快照保留，重新保存时原样回传。编辑、选区移动、裁剪、扩边可能改变尺寸和
坐标，插件应以当前格子为准，用附加数据补充未知字段，不能直接把旧格子覆盖回来。
转换到另一插件时不会传递前一个插件的私有数据。完整物件归属是编辑器专用信息，
不属于此 ABI，私有格式重新打开后可使用“识别物件”重新建立归属。

插件必须拒绝无法无损表示的数据并返回可读错误，不能默默截断 ID、忽略图层或保存
旧尺寸。read/write 必须验证自己格式的长度、版本、校验和等，宿主另验证 CSMP 布局。
推荐测试：同扩展名不同魔数、未知版本、截断输入、全部格子字段、只读/只写、
私有数据往返、编辑后重载、裁剪/扩边、保存失败原文件不变、缺少依赖、x86/x64不匹配。

## 验证 SDK（源码仓库）

常规测试不依赖 C++ 编译器，两个真实 DLL 集成测试默认明确跳过。安装 g++ 后，可从
仓库根目录执行以下命令启用它们；这些测试使用真正的原生 DLL，而非托管模拟：

```powershell
g++ -std=c++17 -shared -static-libgcc -static-libstdc++ SDK/NativeMapPlugin/example.cpp -o Build/example.plugin.dll
g++ -std=c++17 -shared -static-libgcc -static-libstdc++ CrystalStudio.Tests/Fixtures/NativePlugins/readonly.cpp -o Build/readonly.plugin.dll
g++ -std=c++17 -shared -static-libgcc -static-libstdc++ CrystalStudio.Tests/Fixtures/NativePlugins/failed.cpp -o Build/failed.plugin.dll
$env:CS_NATIVE_PLUGIN_TEST_DIR = (Resolve-Path Build).Path
dotnet test CrystalStudio.Tests/CrystalStudio.Tests.csproj --filter FullyQualifiedName~NativeMapPluginTests
```

仓库还包含链接同一宿主调用代码的最小 NativeAOT 程序，可在安装 Windows C++ 构建工具后
验证 AOT 实际执行原生插件调用（不仅是编译）：

```powershell
dotnet publish SDK/NativeMapPlugin/AotSmoke/AotSmoke.csproj -c Release -r win-x64 -o Build/NativePluginAotSmoke
./Build/NativePluginAotSmoke/AotSmoke.exe (Resolve-Path Build/example.plugin.dll).Path
```
