# 三主题原创素材设计

状态：美术设计与制作规格；尚未生成位图、打包图库或替换运行时素材。

目标：每个主题使用配套墙体、地表、边缘与装饰，解决当前借用原生素材造成的风格冲突。保留已有地图与原生图库，新主题使用独立资源包。

## 共同制作规格

- 项目地图单元为屏幕 48×32 像素；完整背景砖为 96×64 像素。沿用现有斜向墙体视角，以导出的原生墙段轮廓、接口和锚点为几何参考，不凭概念图猜拼接尺寸。
- 固定正交镜头、固定比例、光从屏幕左上照射。材质细节在最终 1:1 像素下清晰，避免依赖缩小后消失的细线。
- 墙与物件使用 RGBA PNG，背景真实透明；地面完整砖保持不透明。投影柔和且局部，不把大面积黑色画进砖底或墙脚；悬空边缘通过侧壁与环境色表现厚度。
- 同一墙族分别制作两个斜向直段、对应内外角、端头、门框与开门状态，按布局实际支持的连接掩码补全交叉接口。不得通过镜像改变固定光照，不用整张场景图切碎冒充可拼接墙族。
- 先交付一套直段、转角、地砖和主物件组成的拼接样板，验收后再扩展变体。概念图不等于游戏贴图；AI 初稿须经过透明边缘、接口、锚点、平铺和碰撞检查。
- 当前地图通过图库号和图片索引引用 `.lib`；PNG 不能直接成为可移交客户端的地图资源。正式包需含 PNG 源文件、切片索引、锚点、完整物件组合、碰撞定义、图库和配套目录。图库编号在实现时检查冲突后分配，不能覆盖用户现有 `.lib`。

## 天宫：白玉云阙

### 材质与颜色

主体为温润象牙白玉、浅青玉石和少量哑光金属。白玉 #EEECE3、青玉 #A6D6D0、金边 #C6A15A、阴影 #879EAD、云层 #E2EEF2。金色用于檐线、柱脚和门框重点，不铺满每块砖；阴影保留蓝灰层次，不使用纯黑墙身或黑底板。

### 结构

- 外缘：白玉矮栏、望柱、下承玉石侧壁；云海托住悬空台基。边缘有厚度，不能只剩白色线条。
- 主殿：较高的白玉宫墙、青玉柱、金色檐口；门洞为拱形云纹门框。高墙用于殿堂后侧，前侧使用矮栏，保留可视性。
- 地表：低对比大块玉砖；次级青玉几何嵌线；主殿门前设置完整云纹圆章。普通地砖提供多种污渍极轻的变体，避免重复感。
- 桥：与地砖同高的玉桥面、成对栏杆、桥头柱和云层边界，保持通行宽度与现有桥布局一致。
- 装饰：云纹灯柱、白玉香炉、莲花台、玉树、悬浮玉石；主地标为青金屋顶小殿。主路两侧成对放置，中心轴留空。

首批样板：玉栏双向直段、内外转角、桥头端柱、宫墙双向直段、云纹门框、完整玉砖与青玉嵌砖、云海背景、灯柱、香炉、小殿。

## 海底：珊瑚潮宫

### 材质与颜色

主体为被海水侵蚀的蓝灰石、珍珠贝壳和珊瑚。石材 #456B80、深水 #153F59、浅砂 #9AB9BC、珊瑚 #DC8495、荧光 #71D7CB。避免整张图叠蓝色滤镜；材质固有色应有区分，发光面积保持克制。

### 结构

- 墙体：分层侵蚀礁岩，顶部为柔软曲线；部分嵌入贝壳、藤壶和短珊瑚，接口保持可重复。前缘使用低矮礁石，后侧保留较高礁壁。
- 圣所：珍珠石门框与扇贝冠，结构语言区别于天宫方柱；破损处露出礁岩，避免直接搬用陆地砖墙。
- 地表：蓝灰细砂、石板遗迹和局部贝壳碎屑；沿墙脚有砂石过渡。焦散纹理低对比，跨砖不出现明显亮线；静态素材不宣称有动态水效果。
- 外围：深水纹理与礁壁侧面自然过渡，禁止黑色地牢围边。
- 装饰：扇形珊瑚、脑珊瑚、海葵、海草、巨蚌、珍珠灯和半埋雕像。珊瑚集中在礁壁凹部，草带沿水道延伸，中心圣所保持整洁。

首批样板：双向礁壁、内外角、孤立礁岛边缘、贝壳门框、砂地与遗迹地砖、砂岩过渡、深水背景、三类珊瑚、海草、巨蚌圣坛。

## 外星球：玄晶裂谷

### 材质与颜色

主体为冷黑紫玄武岩、矿物陶瓷和青色晶体。岩体 #343044、断面 #686077、晶体 #55DED5、辅助矿脉 #AC8DDA、地面 #555363。暗色必须有明度层次；青色矿脉负责导航重点，不把每块地面都画成发光网格。

### 结构

- 墙体：倾斜柱状玄武岩与不规则矿物断面；边缘存在晶体断口，与海底圆滑侵蚀岩形成清楚差异。
- 陨坑：外缘抬升、内缘断裂，核心为深紫岩与少量晶簇。围岩方向服务盆地形态，不能把所有坑都画成相同圆环。
- 门洞：断裂岩拱中嵌不对称矿物构件，不使用中式宫门或西式地牢门。
- 地表：低对比冷灰岩板、细颗粒火山尘和局部裂纹；稀疏矿脉形成局部连续组块，普通砖提供无矿脉版本。
- 外围：冷灰玄武岩荒原，色调与可行走地面相邻但明度较低，靠侧壁和碰撞边界区分。
- 装饰：簇状晶体、半透明异星菌伞、孢子囊、柱状岩、失活矿物遗构；地标为非对称晶核。沿陨坑边缘与裂隙聚集，禁止全地图等距散点。

首批样板：双向柱状岩壁、内外角、陨坑内缘与外缘、断裂岩拱、荒原及尘土地砖、三类晶簇、菌伞与晶核地标。

## 制作与验收顺序

1. 每主题一张材质、结构与比例概念板，固定主色、视角、光向。先审天宫，明确去除黑墙基底。
2. 每主题制作小型可拼接样板。墙体拐角、直线和门洞实际拼接，地砖 8×8 平铺，检查裂缝、透明黑边、阴影叠加和重复纹理。
3. 在原生尺寸下制作两个斜向的完整墙族和装饰，记录图像偏移与完整物件边界。
4. 使用独立编号打包图库，在编辑器中注册对应墙族、地砖与装饰；同时准备客户端安装清单。
5. 三主题各生成 256、320、512 尺寸场景，检查总览、原尺寸近景、前后遮挡、碰撞可达、保存重载和资源缺失行为。

## 概念板提示词

这些提示词用于独立生成概念板，不能直接作为运行时图集。内置图像工具当前不可用，尚未调用图像生成 API。

共同约束：Create a professional production concept sheet for a classic 2D orthographic diagonal-view RPG environment kit. Consistent upper-left light, consistent scale, readable silhouettes at native game resolution. Show a small assembled room and bridge or corridor, plus separated wall segments, corners, floor materials and landmark props. No typography, no watermark, no characters, no UI. Distinguish structure from ground shadow. This is an art direction sheet, not a ready-to-slice sprite atlas.

天宫：Celestial Chinese palace suspended above a soft luminous cloud sea. Warm ivory white jade walls, pale celadon pillars, restrained matte gold trim, blue-grey ambient shadows, cloud-relief railings and bridge posts, understated jade floor inlays, lantern pillars, incense burner and a small elegant turquoise-roof shrine. Majestic, light and serene, detailed material craftsmanship. Wall foundations must be pale carved jade with visible thickness, never black dungeon stone or a black skirt.

海底：Submerged coral sanctuary. Rounded eroded blue-grey reef walls, pearl limestone shell arches, fine cool sand transitioning into submerged stone paving, coral fans and brain coral, seagrass colonies, a giant clam altar. Deep teal surrounding water, restrained cyan bioluminescence, subtle caustic texture. Clear material separation and open walking space. No terrestrial brick dungeon walls, no heavy black border, no entire-scene blue filter.

外星球：Alien impact-basin sanctuary. Oblique columnar purple-grey basalt walls, angular fractured crater rims, sparse cyan crystalline mineral veins, cold volcanic dust, asymmetric mineral arch, translucent alien fungal canopies and a large asymmetric crystal nucleus. Bold geological silhouettes, readable dark midtones and restrained luminous accents. No medieval castle components, no repetitive glowing tile grid, no conventional terrestrial vegetation.
