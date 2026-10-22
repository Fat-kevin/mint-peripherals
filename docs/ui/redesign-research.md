# UI 改版调研与方向（2026-10-20，历史记录）

> **已归档：** 本文是改版前的调研与方法记录，其中“系统 Accent / Fluent 设置页”等建议
> 已被 2026-10-21 的 **Graphite Signal Desk / 石墨信号台** 取代。
> 当前落地规范见 [`design-system.md`](design-system.md)，完整提案与线框见
> [`redesign-proposal-2026-10-21.md`](redesign-proposal-2026-10-21.md)。

> 目标：把现在“能用但平”的界面改成有明确气质、信息层级清楚、少滚动就能完成操作的桌面控制台。
> 约束：先做静态设计和资源比较，**不要为了看效果反复启动应用**；最终只做一次构建 + 一次启动验收。

## 1. 结论先说

- 最值得引入的 skill 是 **Anthropic `frontend-design`**：专门解决“看起来像模板默认值”的问题，和当前界面的毛病正好对应。
- 最适合本项目的“设计知识库”是 **UI UX Pro Max**：它明确支持 WinUI 3，带 192 条产品类型规则、79 种风格、192 套配色、119 条 UX 准则，可当检查表用，不要盲信生成结果。
- 想快速探索多个视觉方向，用 **Superdesign**（AI 产品设计 agent / skill）出静态稿；不要把它的 Web 代码直接搬进 WinUI。
- 原生桌面最该抄的不是 Dribbble，而是 **Fluent 2 + WinUI Gallery + Windows 设计指南**；AI 工具只负责找方向和做静态稿。
- 当前界面不是“换个颜色”能救的：主要问题是信息架构空、层级弱、控件密度不合理、强调色不统一。

## 2. 可用的 AI 设计 skills

| Skill | 作用 | 适合本项目吗 | 入口 |
|---|---|---|---|
| `frontend-design`（Anthropic） | 给 agent 注入“有观点的视觉设计”方法，明确反对模板化默认值；覆盖字体、配色、布局、动效 | **最推荐**，用来定视觉方向 | [GitHub](https://github.com/anthropics/skills/tree/main/skills/frontend-design) |
| `ui-ux-pro-max-skill` | 192 类产品规则、79 种可搜索风格、192 套行业配色、74 组字体、119 条 UX 准则；支持 WinUI 3 / WPF / SwiftUI 等 22 种栈 | **最推荐**，当设计检查表和 token 来源 | [GitHub](https://github.com/nextlevelbuilder/ui-ux-pro-max-skill) |
| `superdesign-skill` | 让编码 agent 在画布上生成/迭代 UI mockup、组件、演示稿 | 适合先出 2–3 个方向，不适合直接产出 WinUI XAML | [GitHub](https://github.com/superdesigndev/superdesign-skill) |
| `web-interface-guidelines`（Vercel） | 交互、焦点、加载、空状态、可访问性清单；框架无关部分可复用 | 用来检查“好不好用”，不是视觉风格 | [GitHub](https://github.com/vercel-labs/web-interface-guidelines) |
| `canvas-design`（Anthropic） | 做海报/静态视觉，不是 UI 系统 | 只适合做启动图/宣传图 | [GitHub](https://github.com/anthropics/skills/tree/main/skills/canvas-design) |

> 这些是外部仓库，安装前先看 `SKILL.md` 和脚本；不要直接执行来源不明的安装脚本。

**项目内技能（已落盘）**：[`.dsh/skills/winui-device-console/SKILL.md`](../../.dsh/skills/winui-device-console/SKILL.md)
把本项目的原生 WinUI 3 技术栈、token、稳定性和“一次性启动验收”流程固化成 agent 可读的 skill；
外部通用 skill 只做参考，不替代这个项目约束。

## 3. AI 工具与免费额度（以官方页面为准）

| 工具 | 免费程度 | 适合做什么 | 注意 |
|---|---|---|---|
| [v0](https://v0.app/pricing) | Free：$0/月，7 条消息/天 | 快速生成 Web 布局、找信息层级灵感 | 产出 React/Tailwind，**不能直接用于 WinUI** |
| [Uizard](https://uizard.io/pricing/) | Free：3 次 AI 生成/月、2 个项目、10 个模板 | 低保真线框、快速试布局 | 免费额度很少，适合定方向 |
| [Visily](https://visily.ai/pricing/) | Starter：$0，300 AI credits/月 | 线框、流程图、团队草图 | 偏 Web/SaaS，桌面控件要改写 |
| [Figma](https://www.figma.com/pricing/) | Starter 免费；AI 功能按额度/付费 | 做高保真静态稿、设计 token、切图 | 不要用 Figma 生成代码直接替代 XAML |
| [Penpot](https://penpot.app/) | 开源免费 | 本地/自托管设计、线框 | 生态比 Figma 小 |
| [Excalidraw](https://excalidraw.com/) | 免费 | 信息架构、页面草图 | 不适合高保真 |

## 4. 免费资源网站

### 原生 WinUI / Fluent（优先级最高）

- [Fluent 2 Design System](https://fluent2.microsoft.design/)：颜色、字体、间距、控件状态、可访问性。
- [WinUI Gallery](https://github.com/microsoft/WinUI-Gallery)：所有 WinUI 控件的真实样子和交互，比看网页灵感更可靠。
- [Windows 应用设计指南](https://learn.microsoft.com/windows/apps/design/)：导航、布局、材质、动效、输入方式。

### 免费 UI 套件 / 图标

- [Untitled UI Free Figma UI Kit](https://www.untitledui.com/free-figma-ui-kit)：免费 Figma 设计系统，可拆卡片/表格/表单样式。
- [Untitled UI Free Icons](https://www.untitledui.com/free-icons)：1,100+ SVG 图标。
- [Figma Community](https://www.figma.com/community)：搜 “Windows 11 UI kit”“Desktop dashboard”“Device manager”。
- [Lucide](https://lucide.dev/)：开源线性图标，风格接近 Fluent。
- Phosphor / Fluent System Icons：做设备/状态图标的备选。
- [Mobbin](https://mobbin.com/)：真实 App 截图库，找桌面/移动端信息架构；完整库付费。
- Godly、Land-book、Dribbble：只用来找“气质”，不要照抄布局。

### 配色 / 字体

- [Coolors](https://coolors.co/)：快速调色板。
- [Realtime Colors](https://realtimecolors.com/)：在真实页面上试颜色。
- [Fontpair](https://www.fontpair.co/)：免费 Google Fonts 搭配。
- Google Fonts：本项目继续用 **Segoe UI Variable** 最稳，不要为了好看嵌入一堆字体。

## 5. 当前界面诊断

1. **设备中心太空**：三张卡只占上半屏，下面近一半是空的；没有概览、快捷动作、状态摘要。
2. **层级太平**：标题、卡片标题、设备名、指标、说明的字号和颜色差距不够；所有卡片同一重量。
3. **强调色不统一**：紫色竖条/开关、绿色状态、青色链接、白色文字混用；没有“主色只做一件事”的纪律。
4. **状态重复**：绿点 + “已连接” + 电量数字 + 进度条重复表达同一件事，但没有“最近同步/可执行动作”。
5. **耳机页产品图过大**：三张分量卡占了首屏，ANC/EQ 和快捷开关被推到下面，必须滚动。
6. **鼠标页表单感太重**：DPI 滑杆横跨整列，5 个档位像 5 个输入框；没有“预设/当前档位”的清晰操作路径。
7. **遥控器表格太密**：13 行 × 3 列全是“禁用”，文字 11–12px，行高小；扫描/连接行又挤在标题下面。
8. **导航 rail 空**：188px 宽，底部没有设备在线摘要、版本、退出/设置入口，像半成品。
9. **可点击感弱**：“进入控制 →”是带下划线的文字，不是明确按钮；表格单元格也看不出可编辑。
10. **字体偏小**：正文/标签 11–13px 在 1280×840 上显得局促，尤其遥控器表格。

## 6. 推荐方向：石墨仪表台（Graphite Console）

不是“再换一套深色卡片”，而是把界面当成一台**桌面仪器**：

- 深石墨底 + 蓝灰分层，不用纯黑；
- 三台设备各有识别色：耳机薰衣草、鼠标琥珀、遥控器青绿；
- 大数字只给电量和关键指标，其他文字降级；
- 设备图放在带色晕的“托盘”里，不铺满整卡；
- 卡片有清晰 anatomy，而不是每页各写各的；
- 少用渐变，靠层级、细边框、色块和字号建立质感。

### 6.1 设计令牌（建议）

| 用途 | 值 |
|---|---|
| 画布 | `#131418` |
| 导航 | `#0E0F12` |
| 卡片 | `#1B1D23` |
| 卡片悬浮/内层 | `#22252D` |
| 边框 | `#2D313B` |
| 主文字 | `#F5F7FB` |
| 次文字 | `#A3AAB8` |
| 弱文字 | `#6F7786` |
| 主强调 | `#8B5CF6` |
| 耳机色 | `#B69CFF` |
| 鼠标色 | `#F6B24A` |
| 遥控器色 | `#3DD6C4` |
| 成功 | `#45D6A0` |
| 警告 | `#F6B24A` |
| 危险 | `#FF6B6B` |

- 字体：`Segoe UI Variable Display`（标题）/ `Segoe UI Variable Text`（正文）；不引入第三方字体。
- 字号：页面标题 30、区块标题 18、正文 14、辅助 12；遥控器表格正文不小于 12。
- 间距：4/8/12/16/24/32 的 8px 网格；页面 padding 28–32；卡片 gap 16。
- 圆角：卡片 16、内层面板 12、按钮 10、状态胶囊 999。
- 阴影：基本不用；用 1px 边框和背景差分层。焦点态必须 2px 可见描边。

### 6.2 设备中心（Hub）

- 顶部：`设备中心` 30px + “3 台设备 · 刚刚同步” + 右侧刷新/设置图标按钮。
- 第一行：**不等宽 Bento**：耳机大卡（约 6 列）+ 鼠标/遥控器两张窄卡（各 3 列）；耳机卡放产品图、电量环、ANC 状态、主按钮。
- 第二行：左侧“快捷操作”3 个小 tile（查找耳机 / DPI 预设 / 接收测试），右侧“连接状态”列表；用完空白区。
- 卡片 anatomy：左上图标/产品图 + 名称/型号 + 状态胶囊；中部电量环 + 大数字；底部 2 个指标 + 主按钮；每卡一个识别色。
- 不伪造趋势图；没有历史数据就不放“最近 7 天”。

### 6.3 耳机页

- 顶部 hero：160px 产品图 + 名称/固件/状态 + 3 个电量环（左/右/盒）横排。
- 下面两列：左“声音”（ANC、EQ、空间音频），右“快捷开关”（游戏模式、低延迟、双设备、入耳检测、抗风噪）2×3 网格。
- “查找耳机”放在右侧设备操作区，不再占首屏大卡。
- 所有开关有“开/关”文字，不只靠紫色。

### 6.4 鼠标页

- 顶部 hero 压缩到 150px 高；右侧放“电量/连接/板载”三枚 chip。
- 左“性能”：DPI 大数字 + 滑杆 + 5 个档位 chip（当前档位高亮），轮询率下拉。
- 右“设备信息”做成 key-value 列表；底部“按键映射”表格，行高 40，列头小写。
- DPI 档位仍用静态 `TextBox`，不引入 `NumberBox`。

### 6.5 遥控器页

- 顶部：名称/状态 + 设备下拉 + 电量 + 扫描/连接/断开做成一条紧凑 toolbar。
- 中部：左 key map（360px，选中键高亮），右“当前键 × 手势”编辑面板；不要把所有 13×3 都塞进首屏。
- 下方：全量映射表，按“导航/媒体/系统”分组；未设置为弱文字，已设置为设备色 chip；行高 40，表头 11px、句式大小写，不用全大写小标签。
- 接收测试保留为次级按钮，不抢主操作。

## 7. 原生技术栈与组件方案（锁定）

**技术栈不换**，改版只换 XAML 组织方式和视觉 token：

| 层 | 选型 | 说明 |
|---|---|---|
| 语言/运行时 | C# 12 / .NET 8 | `net8.0-windows10.0.19041.0` |
| UI 框架 | **WinUI 3 / Windows App SDK 1.7** | 原生 XAML 控件、Fluent 资源、系统 DPI/无障碍 |
| 构建 | x64，`WindowsPackageType=None`，`WindowsAppSDKSelfContained=true` | 保持现有部署方式 |
| 设备层 | 现有 `IPeripheralService` + C# HID++/SPP 服务 + Rust sidecar | 不改协议、不改 sidecar 生命周期 |
| 视觉层 | `ResourceDictionary` token + 自定义 `UserControl` | 不引入 WebView2/HTML/React |
| 数据绑定 | 现有 code-behind + 轻量依赖属性/事件 | 暂不做全量 MVVM 重写，降低回归风险 |
| 验证 | xUnit + UIA 冒烟 + 一次截图 | 设计阶段不反复启动应用 |

**新增原生组件（建议）**：

- `Styles/Tokens.xaml`：颜色、字号、间距、圆角、状态色；所有页面只引用 token。
- `Controls/DeviceCard.xaml`：设备卡统一 anatomy（图、状态、电量环、指标、主按钮）。
- `Controls/BatteryRing.xaml`：用 `Path`/`Ellipse` 画静态电量环，不依赖第三方图表库。
- `Controls/StatusChip.xaml`：连接/离线/警告状态，色彩 + 文字双编码。
- `Controls/SectionCard.xaml`：区块标题 + 内容 + 可选操作，替代每页各写一套 `Border`。
- `Controls/KeyMappingTable`：保留现有静态行/单元格，改样式和交互，不改成动态重建。

**明确不做**：

- 不换 WPF/Avalonia/MAUI；不为视觉改版重写服务层。
- 不嵌入 WebView2、Electron、Tauri 页面，不用 HTML/CSS 做 UI 运行时。
- 不为了“好看”引入第三方 UI 库/图表库；原生控件 + 样式优先。
- 不在设计阶段反复启动应用；静态稿确认后只构建/启动一次验收。

## 8. 不改的红线

- 固定 1280×840；尺寸只在 `InitializeComponent` 前设置一次；运行中绝不 resize。
- 不用 `Flyout` / `ContentDialog` / `StartBringIntoView`。
- 15s 刷新只改文本/数值，不重建 `BitmapImage`、`ItemsSource`、大量控件。
- 缓存页面不用 `NumberBox` / `RadioButtons`；静态 `TextBox` + `ComboBox`。
- 所有设备事件回 UI 线程；页面 `Unloaded` 退订。
- 不伪造电量、趋势、设备状态。

## 9. 执行状态

1. 已出 **石墨仪表台（Graphite Console）** 方向稿，并扩展到 5 个页面：`design/mockups/hub.html`、`headset.html`、`mouse.html`、`remote.html`、`settings.html`。
2. 已渲染 1280×840 截图：`design/mockups/png/*.png`；设计说明见 `design/README.md`。
3. 静态稿阶段 **未启动应用**；下一步待确认视觉方向后一次性改 XAML/样式，构建一次，只启动一次看真实渲染。
4. 最后再做 10 分钟闲置、页面轮转、最小化/恢复回归。
