# UI 视觉系统 —— 台面 / Bench（2026-11）

> 这是当前生效的视觉与交互规范。改任何页面/控件前先读本文件与 `stability/crash-history.md`。
> 历史：Mint Paper（2026-10，纸感卡片）与 Ink Bench（2026-11 初，细线表单）都已废弃；
> 原因分别见 `history/implementation-log.md` 对应条目。

## 1. 设计判断

外设管理软件的本质是**设备仪表盘**，不是参数表单。三条原则高于一切：

1. **设备是主体**：实物图、连接状态、电量是画面主角；参数围绕设备组织。
2. **一个焦点、一条主路径**：每页有明确视觉中心与唯一主操作；次要控制收进互斥工作区。
3. **质感来自比例、层次与细节**：细线分区、墨色选中、真实产品图；不用渐变/发光/玻璃。

## 2. 外壳

- 固定窗口 1280×840；左侧**设备栏 224**（品牌、总览、三台设备实时状态/电量、设置、引擎状态）。
- 设备栏同时是导航：不需要单独的“当前设备”页头；切页只换右侧工作区。
- 内容区顶部 40px 为拖拽区；页面标题写在内容里（30px Display SemiBold）。
- 两套主题（浅色默认 / 深色），键集合完全一致。

## 3. 颜色令牌（`Styles/Tokens.Color.xaml`）

| 用途 | 浅色 | 深色 |
|---|---|---|
| 画布 | `#F5F6F7` | `#0F1114` |
| 侧栏/底板 | `#FFFFFF` | `#0B0C0F` |
| 卡片/输入 | `#FFFFFF` | `#16181C` |
| 内嵌面板 | `#F3F4F6` | `#14171B` |
| 细线 | `#E6E8EB` | `#23272C` |
| 主文字/次文字/弱文字 | `#15171A` / `#5A6068` / `#8A9099` | `#F2F4F7` / `#A6ADB6` / `#6F7783` |
| 墨色（选中/主按钮） | `#15171A`（字 `#FFFFFF`） | `#F2F4F7`（字 `#111317`） |
| 品牌强调（在线/焦点/链接） | `#0F6B58` | `#4FD1B0` |
| 耳机 / 鼠标 / 遥控器 | `#5B4BC4` / `#A66B22` / `#1F6F8C` | `#A08CFF` / `#DFA45C` / `#5FB6D6` |
| 成功 / 警告 / 危险 | `#1D8A5B` / `#B97A12` / `#C0453C` | `#3FBE8B` / `#E0A84E` / `#F07A70` |

规则：
- **交互与选中一律墨色**；强调色只用于：在线状态点、链接、焦点、电量/进度。
- 状态永远「颜色 + 文字」双编码。
- 设备色只出现在设备栏图标、总览电量条、页面 overline；不铺满界面。

## 4. 字阶（`Styles/Tokens.Type.xaml` + `Styles/Bench.xaml`）

| 样式 | 用途 |
|---|---|
| `BenchPageTitleStyle` 30/36 SemiBold | 页面/设备标题 |
| `BenchDeviceTitleStyle` 22/28 SemiBold | 总览机架里的设备名 |
| `BenchSectionTitleStyle` 13/18 SemiBold | 分区标题 |
| `BenchGutterNumberStyle` 22 Light | 左侧编号 01/02/03 |
| `BenchNumberStyle` 30/34 SemiBold | 大号读数（DPI、电量） |
| `BenchLabelStyle` 10.5 SemiBold + 字距 100 | 字段标签（over/指标名） |
| `BenchValueStyle` 13 SemiBold | 指标值/行标题 |
| `BenchMetaStyle` 11.5 | 说明文字 |
| `BenchFootnoteStyle` 11 | 脚注/状态文字 |

- 显示字体 `Segoe UI Variable Display`，正文 `Segoe UI`，图标 `Segoe Fluent Icons`；不引入第三方字体。

## 5. 布局与组件

- 间距 4/8/12/16/20/24/32；页面内边距 32,28,32,24；分区之间 18–28。
- 圆角：容器 6–8，按钮 6，胶囊 999（仅分段条/标签）；**不做大圆角卡片堆叠**。
- 组件（`Styles/Bench.xaml`）：
  - `BenchModeButtonStyle`：模式瓦片。未选中 = 白底 + 1px 边；选中 = 墨色填充 + 反白文字。
  - `BenchSegmentButtonStyle`：分段条（工作区切换/设置分类），选中用代码设墨色。
  - `BenchInkButtonStyle`：唯一主操作；`BenchOutlineButtonStyle` / `BenchGhostButtonStyle`：次级。
  - `BenchMappingCellStyle`：遥控器映射单元格（代码动态创建，统一外观）。
  - `BenchComboBoxStyle` / `BenchTextBoxStyle` / `BenchToggleSwitchStyle`：收紧后的原生控件。
- **Hover 规则（2026-11 修复）**：所有可能被代码改成墨底的按钮，hover **不得改背景色**，
  只允许改描边/透明度（`BenchModeButtonStyle` 的 PointerOver 只设 BorderBrush + Opacity 0.92）。
  否则会出现“白字落在浅色 hover 底上”的不可读状态。新加按钮样式必须遵守。
- 仪表条：页头下方一条 1px 描边的横条，5 格等宽 + 竖线分隔，label + value（连接/固件/降噪/EQ/写入）。

## 6. 页面结构

### 设备总览
- 页头 + 1px 描边的**三列机架**（每列一台设备，竖线分隔，不用卡片）：编号 + 状态点、实物图、名称、
  协议/固件、电量（耳机三分量进度条；鼠标/遥控器大数字 + 进度条）、一行事实说明、"打开X" 链接。
- 底部系统条：本地引擎 / 同步 / 数据 / 打开设置。
- 整列可点（每列是一个 Button，内部不再嵌套按钮）。

### 耳机
- 页头（查找耳机为唯一主操作）+ 仪表条（连接/固件/降噪/EQ/写入）。
- 左侧设备台：充电盒图 + 三分量电量（进度条 + 充电状态）。
- 右侧三个编号分区：降噪模式（6 个瓦片，点击即写入）、音效预设（7 个瓦片）、快捷功能（5 行开关）。
- 不可用时顶部提示条说明原因，控件禁用；所有写入显示真实结果。

### 鼠标
- 页头（刷新 / 应用设置）+ 仪表条（DPI/轮询率/板载/电量/写入）。
- 三个互斥工作区（分段条切换，同一屏只出现一组控件）：
  - 灵敏度：DPI 大读数 + 滑杆 + 轮询率；
  - 按键：16 键两列列表（动作下拉）；
  - 板载与设备：板载开关、当前档位、5 个 DPI 档位、配置名、设备信息。
- 左侧设备台保留设备选择与电量环；未保存修改有明确提示。

### 遥控器
- 页头（连接/断开）+ 连接工具栏（设备下拉、扫描、电量）。
- 三个互斥工作区：按键布局（设备图 + 热点，配右侧手势编辑器）、映射表（按键行 + 三手势单元格，
  点击单元格就地编辑）、语音与检查（音频路由、语音工具、增益、全按键、接收测试结果）。
- 映射保存、接收测试、语音应用都显示真实结果，不假装成功。

### 设置
- 分类切换（外观/启动/设备模块/写入授权/通知/备份与诊断）+ 单列分组行 + 右侧常驻「运行状态」。
- 每行 = 名称 + 一句说明 + 右侧控件；设备开关按设备成对排列（启用 / 允许写入）。
- 设置即时保存；恢复默认有二次确认；诊断包脱敏说明常驻。

## 7. 稳定性约束（不因改版放松）

- 固定窗口 1280×840，尺寸只在 `InitializeComponent` 前设置一次。
- 不用 `Flyout` / `ContentDialog` / `StartBringIntoView` 做主交互；映射编辑就地展开。
- 15s 刷新只改文本/数值/进度；不重建 `BitmapImage`、`ItemsSource`、大控件树。
- 后台事件回 UI 必须 `DispatcherQueue.TryEnqueue`；页面 `Unloaded` 退订。
- 不伪造电量、连接、能力；未接入就禁用并写明原因。
- 缓存页不使用 `NumberBox` / `RadioButton`；工作区切换用 `Button` + 代码管理选中态。

## 8. 截图与验证

- `design/studio/shots/` 保存当前实现的实际截图（s1-hub / s2-headset / s3-mouse / s4-remote* / s5-settings）。
- 截图脚本：`tools/capture-window.ps1`（PrintWindow 优先，带 UIA 点击参数）；hover 状态用
  `tools/capture-hover.ps1` 验证。
- 设计阶段不反复启动应用；改完统一构建一次、跑一遍五页 + hover + 空/错误状态。


## 9. 2026-11-27 实现：等权设备总览（Balanced）

总览从「左侧栏 + 三列机架」改为「84px 图标栏 + 三张等权设备卡」，解决左栏大面积空白与耳机主场过大两个比例问题。

### 布局比例

- 窗口 1280×840；图标栏宽 **84**（原 248）；主区内边距 32。
- 三列等宽，间距 14；每卡约 370 宽，铺满内容区高度。
- 卡片内：设备名 18 / 元数据 11.5 / 产品图 172–220 / 电量 46 / 底部事实与动作。
- 图标栏：品牌 38、导航按钮 48×44、状态点 5–7；不再放设备列表与文字。

### 视觉令牌

- 背景沿用 `AppCanvasBrush`；图标栏 `AppChromeBrush`；卡片使用新增的
  `AppCardGlassBrush`（浅色半透白 / 深色半透白），边框 `AppCardBorderBrush`，圆角 16。
- 设备身份只用卡片顶部 2px 色线与状态点：耳机 `AppHeadsetBrush`、鼠标 `AppMouseBrush`、遥控 `AppRemoteBrush`。
- 强调色仍是薄荷绿；卡片主体文字保持中性，不把设备色用于正文。
- 背景加两枚低透明度径向环境光（Content 区），仅作氛围，不承载信息。

### 动效

- 入场：三卡 110ms 错峰，620ms CubicEase(EaseOut) 从 opacity 0 / translateY 20 落位；数字 760ms 计数。
- hover：卡片上浮 4px，260ms EaseOut；pointer exit 回 0。
- 低电/状态：数字与状态点颜色用现有 `AppDangerBrush`，不新增循环动画。
- 所有动画只改 `Opacity` / `TranslateTransform.Y`；遵循 `UISettings.AnimationsEnabled`；页面 `Unloaded` 停止数字计时器。

### 验收截图

- `design/balanced-app-final-light.png`、`design/balanced-app-dark.png`
- `design/balanced-app-headset-dark.png`（其他页导航仍可用）


### 图标栏状态点（修正）

- 状态点不再放在图标自身的测量范围内；每个设备按钮内用 30×30 容器承载 18px 图标，6px 状态点贴图标右下角。
- 状态点颜色只表示在线/离线（`AppSuccessBrush` / `AppOfflineBrush`）；tooltip 同时给出设备名、状态和电量，避免只靠颜色。
- 引擎状态点独立在最底部，与设备状态点分开。

### 内部页统一（2026-11-27 同日）

- 所有详情/设置页统一到同一套页面层：30px 页面标题、无 overline、32px 页面内边距。
- `BenchPanelStyle` 改为 `AppCardGlassBrush` + `AppCardBorderBrush` + 16 圆角，与总览卡片同一表面语言。
- 选中 tab / 主按钮从近黑改为品牌薄荷绿（`AppInkBrush` 改为强调色，`AppOnInkBrush` 随主题），不再出现黑色胶囊。
- 输入/按钮圆角统一到 8；设置、鼠标、遥控器的工作区切换保持同一选中逻辑。
- 保留所有既有功能与写入校验，仅改表面与选中态 token。
