# BatteryHub 任务栏显示与产品特性调研（clean-room）

> 日期：2026-11-27
> 状态：调研完成；结论、视觉规格、复刻步骤与优先级已给出，可直接支撑设计文档与实施计划
> 上游仓库：`lo0kie/Wireless-Device-Battery-Monitor`（项目名 BatteryHub，fork 自 `krisgfx9/Wireless-Device-Battery-Monitor`）
> 本地只读克隆：`.artifacts/upstream-wireless-battery-monitor`，commit `0c1adbe5`（2026-11-26），GPL-3.0
> 相关项目文档：[任务栏胶囊/托盘](../features/taskbar-tray.md)、[视觉系统](../ui/design-system.md)、[崩溃史](../stability/crash-history.md)

## 0. 摘要（先读这里）

### 0.1 十条关键事实

1. **任务栏显示 = 任务栏的一个 layered 子窗口**。它把 `Shell_TrayWnd` 当父窗口（`WS_CHILD | WS_CLIPSIBLINGS` + `WS_EX_LAYERED | WS_EX_NOACTIVATE`），自己用 GDI 把整条像素画进 32bpp DIB，再用 `UpdateLayeredWindow` 一次提交；不用 `WM_PAINT`，不参与 XAML/WinUI 布局。多显示器上每个 `Shell_SecondaryTrayWnd` 各有一个“镜像”子窗口。
2. **定位基准是 `TrayNotifyWnd` 左边缘**，不是固定像素：`x = 托盘左边缘 - 条宽 - 2px`（RTL 时镜像），再依次避让 Win10 的资讯/天气子窗口、Win11 的 Widgets 按钮矩形、任务栏上其它应用的小挂件；任何一步“算不出来/会盖住东西”就**隐藏而不是猜**。
3. **Win10 与 Win11 是两套实现路径**：Win10 任务按钮在 `ReBarWindow32`（可 `MoveWindow` 缩窄让位）；Win11 任务栏是 XAML island（`Windows.UI.Composition.DesktopWindowContentBridge`），只能靠 UI Automation 读时钟/Widgets/任务按钮矩形，必要时把 bridge 高度缩短来“预留底部空间”。
4. **DPI 逐任务栏计算**：`GetDpiForWindow(任务栏)` 取 DPI，所有尺寸 `MulDiv` 缩放；小数间距先乘后 round；每个镜像用自己的任务栏 DPI。
5. **Explorer 重启靠 `TaskbarCreated` 广播恢复**：隐藏的顶层窗口（刻意不用 message-only）收到广播后销毁并重建胶囊、重挂托盘图标；同时 `Alive()` 用 `IsWindow` 兜底。
6. **默认视觉**（文本行）：`[图标] 30%  [图标] 93%`，字号 13、图标 16、图标↔文字 5、设备间距 14、左右边距 8、分隔线宽 2+两侧留白 6（全部是 96 DPI 逻辑像素，可调范围见 §3.1）。低电 ≤15% 红、充电蓝闪电、充满绿勾、休眠整条 alpha 120/255 变暗、断开 ×、未知 `?`、粗电量文字（Low/Full）替代数字。
7. **品牌色是可选层**：17 个品牌各有“深色任务栏亮色版 / 浅色任务栏加深版”两组 RGB；命中顺序为驱动 key 前缀 → USB VID → 名称规则 → 罗技型号启发式；命中后图标着色 + 柔光晕 + 文字下方 2px 电量条。
8. **菜单分两套**：左键=设备列表（显示/隐藏、排序、重命名、每设备低电阈值、历史、刷新、退出）；右键=设置（显示样式、位置、监视器、休眠/断开策略、语言、自启、更新）。菜单是自绘 layered 弹窗（32px 行、圆角、行列勾选、RTL、避让任务栏），失败时回退系统 `TrackPopupMenu`。
9. **状态/刷新是事件驱动的**：默认 60s 轮询（可 30s–24h），但登录后 3/7/15s、唤醒/设备到达后 3/5/10/15s 会补轮询；HID 设备变更、蓝牙 ACL 连接、电源恢复、显示器开关都会提前触发；任务栏 Geometry 约 1s 复核一次，只有“尺寸/位置/DPI/内容/主题”签名变化时才重绘。
10. **许可边界**：上游是 GPL-3.0。本报告只做行为级记录与原创描述，**未复制任何代码、注释、字符串表或自绘菜单实现**；品牌色/注册表键名/窗口类名属于事实与接口信息，可以独立重写。实现阶段必须按本文档从零编写，不得逐行移植。

### 0.2 优先级一览

| 级别 | 内容 | 理由 |
|---|---|---|
| **必做** | 真实托盘锚定（`TrayNotifyWnd` 左侧 + 回退固定 235 DIP）、`SystemUsesLightTheme` 主题、`TaskbarCreated` 重建、状态语义（低电/充电/充满/休眠/未知）、tooltip、重绘门控与 GDI 资源管理 | 正确性/稳定性收益最大，改动集中在现有 [`PreviewTaskbarPill.cs`](../../src/DeviceCenterPreview.App/Services/Taskbar/PreviewTaskbarPill.cs)，不动 WinUI 页面 |
| **可选** | 左键设备菜单（Win32）、无设备占位、托盘模式兜底、多显示器镜像、唤醒/插拔补轮询、每设备低电阈值、历史与运行时估算、`--dump`/`--list` 诊断 CLI | 产品力提升，但工作量或视觉冲突需权衡 |
| **不做** | 复制 GPL 代码/字符串、自绘 Win11 弹出菜单、嵌入 Material Icons 私有字体、Win10 ReBar 缩窄、竖向任务栏、RTL、签名更新系统、支持请求窗口、品牌色全表（与我们 Bench 设备色令牌冲突） | 收益低/风险高/与现有技术栈或视觉系统冲突 |

---

## 1. 上游项目与调研方法

### 1.1 项目画像

| 项 | 事实 | 证据 |
|---|---|---|
| 形态 | 单 exe、纯 C++/Win32、无运行时/无后台服务；约 350 KB、常驻约 2 MB | `README.md:29` |
| 授权 | GPL-3.0（fork 自 krisgfx9 项目，改名为 BatteryHub） | `LICENSE:1-3`、`README.md:3-8` |
| 代码量 | `widget.cpp` 1760 行、`main.cpp` 2208 行、`popup_menu.cpp` 1792 行、`battery_history.cpp` 609 行、`report.cpp` 770 行；另有 ~20 个驱动 | `wc -l` |
| 结构 | UI 线程（隐藏消息窗口 + 胶囊 + 托盘 + 菜单）＋ 1 个轮询 worker 线程；数据通过 `PostMessage(WM_RESULTS)` 回 UI | `src/main.cpp:1842-1862`、`src/main.cpp:1877-1883` |
| 数据来源 | 每个厂商驱动按 VID/PID 只发只读查询；未知设备不写；通用回退：HID battery usage、Bluetooth、XInput、HeadsetControl | `README.md:442-446` |
| DPI 声明 | 清单里 `PerMonitorV2` | `res/app.manifest` |
| 主题基准 | 读 **`SystemUsesLightTheme`**（任务栏主题），不是 `AppsUseLightTheme` | `src/main.cpp:179-185` |

### 1.2 它和我们当前胶囊的定位差异

| 维度 | BatteryHub（上游） | 外设中枢现状 |
|---|---|---|
| 位置 | 通知区域**左侧**，跟随托盘几何 | 任务栏左下角，固定“左缘 + 235 DIP”（[taskbar-tray.md](../features/taskbar-tray.md)） |
| 外观 | 默认透明文本条；可选背景板/分隔线/品牌色/圆环等 | 固定“胶囊底板 + 边框 + 圆角” |
| 内容 | 任意数量无线设备，按用户排序 | 耳机（含 L/R）/鼠标/遥控器，仅已连接 |
| 状态 | 低电/充电/充满/休眠/断开/未知/粗电量/连接方式/同类角标 | 低电颜色、充电闪电 |
| 多设备 | 横向排布、竖向任务栏变成列、可堆叠 | 圆点分隔 |
| 多显示器 | 每块任务栏一个镜像 | 仅主任务栏 |
| 主题 | 跟随系统任务栏明暗 | 读 `AppsUseLightTheme`（见 §6.3 风险 3） |
| 交互 | tooltip + 左键设备菜单 + 右键设置菜单 | 点击打开主窗口 |
| Explorer 重启 | `TaskbarCreated` 重建 | 1s 定时器检测父窗口/任务栏后重挂 |

### 1.3 调研方法与证据约定

- **只读**：所有上游文件未修改；本报告与任何产品代码无关。
- 证据引用格式：`src/widget.cpp:1336` 指上游仓库根目录下文件与行号；`README.md:33` 同理。“U/” 前缀仅在本报告内部用于指代 `.artifacts/upstream-wireless-battery-monitor`。
- 视觉数据除源码常量外，另用 Pillow 对 `docs/screenshot.png`（500×96）做了像素级测量（§3.7），验证默认字号/间距。
- 未覆盖：各厂商协议驱动的具体报文（与本次 UI 目标无关）；Linux 入口。

### 1.4 许可证边界（实现前必须遵守）

1. **禁止**复制/翻译上游任何源码、注释、i18n 字符串表、自绘菜单代码、README 文案进本项目。
2. 本报告中的数字、颜色值、注册表键名、Win32 类名/消息名属于**事实与互操作接口**，可以独立实现；颜色请以品牌官方色或本报告表格重新取值。
3. 自绘弹窗、文字遮罩渲染、圆环/光晕算法**不要照搬**；如需要，按 §2.14 的行为步骤用 C#/GDI+ 或 Direct2D 重写。
4. 未来实现必须以本文档为唯一输入（clean-room 记录）；若需要再看上游源码，应记录原因并换人复核，避免“边看边抄”。

---

## 2. 任务栏实时显示原理（Win32 全流程）

### 2.1 进程与窗口拓扑

```
BatteryHub.exe
├─ 隐藏顶层窗口 "WirelessBatteryWnd"（WS_POPUP | WS_EX_TOOLWINDOW）
│    唯一职责：收广播/通知/轮询结果、持有 tray 图标、弹菜单（owner）
│    ← 刻意不是 message-only：message-only 窗口收不到 TaskbarCreated 广播（src/main.cpp:2128-2130）
├─ 胶囊主窗口（class "WirelessBatteryWidget"）: Shell_TrayWnd 的 layered 子窗口
├─ N 个镜像窗口（同 class）: 每个 Shell_SecondaryTrayWnd 一个子窗口
├─ Tooltip 窗口（TOOLTIPS_CLASSW，owner=隐藏窗口）
└─ 轮询 worker 线程（只做设备查询 → PostMessage WM_RESULTS）
```

关键不变量：**胶囊是任务栏的子窗口**，所以任务栏自动隐藏滑动、整体隐藏、被全屏覆盖时，胶囊天然跟随/被裁剪，不需要自己算任务栏动画。

### 2.2 窗口类、样式与消息

| 项 | 做法 | 证据 |
|---|---|---|
| 类名 | `WirelessBatteryWidget`；`lpfnWndProc`、`hCursor=IDC_ARROW`、无背景刷、无图标 | `src/widget.cpp:58`、`1353-1361` |
| 创建 | `CreateWindowExW(WS_EX_LAYERED | WS_EX_NOACTIVATE, class, "", WS_CHILD | WS_CLIPSIBLINGS, 0,0,1,1, parent=任务栏, …)` | `src/widget.cpp:1362-1363` |
| 初始状态 | 1×1、隐藏；先 `Paint()` 再 `Reposition()`，两者成功后才显示 | `src/widget.cpp:1382-1386` |
| 绘制 | 无 `WM_PAINT`；整条渲染到 32bpp DIB → `UpdateLayeredWindow(ULW_ALPHA)` | `src/widget.cpp:957-985` |
| 命中区域 | 整张位图清成 alpha=1 的近透明色（`0x01000000`），让整条都可接收鼠标，同时视觉透明 | `src/widget.cpp:894-900` |
| 鼠标消息 | `WM_MOUSEMOVE`+`TrackMouseEvent(TME_LEAVE)` → hover 高亮；`WM_MOUSELEAVE` 取消；`WM_LBUTTONUP`→`PostMessage(owner, clickMsg, 0)`；`WM_RBUTTONUP`→`clickMsg=1`；`WM_MOUSEACTIVATE`→`MA_NOACTIVATE`（永不抢焦点） | `src/widget.cpp:1307-1328` |
| 键盘 | 不处理（无焦点） | 同上 |
| 层级 | `SetWindowPos(HWND_TOP, …, SWP_NOACTIVATE | SWP_SHOWWINDOW)`，每次定位重抬到同级最上；用 `GetWindow(GW_HWNDPREV)` 检查是否被同级覆盖 | `src/widget.cpp:1746-1757` |

### 2.3 创建与附着流程（上游顺序）

1. `FindWindowW("Shell_TrayWnd")`；若不存在或没有 `TrayNotifyWnd` 子窗口 → 创建失败（回退托盘模式）。`src/widget.cpp:1338-1339`
2. 读任务栏窗口矩形，`高>宽` 判定竖向任务栏，记录 `g_vertical`。`1340-1342`
3. 有 `Windows.UI.Composition.DesktopWindowContentBridge` 子窗口 → Win11 XAML 任务栏，无 ReBar；否则找 `ReBarWindow32`（Win10）。`1343-1347`
4. 注册类、创建胶囊子窗口、创建 tooltip、启动 Widgets 按钮 UIA 查询线程、`Paint()`、`Reposition()`。`1353-1386`
5. 主程序每秒调 `Reposition()`；`SetItems()`（每次轮询后）→ 更新 tooltip 文本 → 重绘全部 → 重新定位。`src/widget.cpp:1415-1431`
6. 删除时：给 Win10 ReBar 恢复宽度的 `WM_SIZE`、`ReserveBottom(0)` 还任务栏高度、销毁镜像/tooltip/主窗口。`src/widget.cpp:1389-1411`

### 2.4 水平任务栏定位算法（重点，可直接重写）

主任务栏（LTR）：

1. 取 `Shell_TrayWnd` 的屏幕矩形 `bar` 和客户区 `client`；取 `TrayNotifyWnd` 屏幕矩形 `tr`，要求可见且落在 bar 内，否则本轮放弃。`src/widget.cpp:1594-1617`
2. 起始边 `edge = tr.left`（RTL 用 `tr.right`）。`1685-1686`
3. **避让 Win10 资讯/天气**：依次找子窗口 `ShellFeedsTaskbarViewWindow`、`DynamicContent2`（不同 build 用不同类），可见且在其左侧则把 `edge` 再左移。`1692-1699`
4. **避让 Win11 Widgets 按钮**：读取 UIA 线程缓存的按钮矩形，若会与本条重叠则 `edge = 按钮 left`。`1700-1704`
5. **避让其它应用挂件**：`StepPastOtherWidgets(edge, bar, horizontal, rtl)`：枚举任务栏子窗口 + 顶层窗口，排除 explorer/自身/不可见，要求“完全在任务栏内且 topmost；尺寸 ≤ 任务栏长度 1/3”，然后最多 8 轮把 `edge` 左移到不再重叠（±16px 容差）；两个挂件会在一秒内自动稳定。`1552-1592`、`1705`
6. 屏幕坐标 → 任务栏客户区坐标；`left = clientX(edge) - 条宽 - 2px`。`1706-1708`
7. 减用户自定义偏移 `TaskbarOffset`（DIP，允许负数）。`1711-1716`
8. **校正** `RecoverHorizontalWidgetX`：位置在 `[0, barW - 条宽]` 内直接用；否则优先上一次有效位置；否则按“托盘贴任务栏外缘”估算 `barW - trayW - 条宽 - 2`；最后才 clamp。这样即使被第三方任务栏美化工具给出 `-61` 之类异常托盘坐标，也不会把胶囊甩出屏幕或盖住开始按钮。`70-88`、`1718-1726`
9. 可选“最左端/任务栏应用按钮左侧”（Win11 居中任务栏留白处），命中条件不满足就回退到托盘左侧。`1728-1734`、`424-458`
10. Win10 再缩窄 `ReBarWindow32` 避免任务按钮滑到胶囊下面；Explorer 每次布局会把它撑回去，所以每次定位都重做。`1737-1744`
11. **去抖**：`托盘矩形 / 任务栏矩形 / Widgets 矩形 / 条宽 / 位置` 都没变且未被同级覆盖 → 直接返回，不做 `SetWindowPos`。`1746-1757`

镜像窗口（副屏任务栏）：同样的“贴托盘/时钟左侧 + 避让”，但时钟来源分三种：有可见 `TrayNotifyWnd` 用其左缘；Win10 无 XAML 时用 `ClockButton` 子窗口左缘（时钟可被关掉）；Win11 XAML 时用 UIA 线程测得的 `clockLeft` / Widgets 矩形 / 任务按钮右缘，并额外减 `SecondaryTaskbarOffset`。任何一项未知或空间不足 → `HideMirror`。`1018-1147`

竖向任务栏见 §2.6。

### 2.5 多显示器（镜像窗口）

- 每块可见的 `Shell_SecondaryTrayWnd` 创建一个同款子窗口（`SyncMirrors`，约 1s 一次），消失的设备栏销毁镜像。`src/widget.cpp:1178-1185`、`1247-1283`
- 镜像与主条共用绘制/点击/tooltip 代码；悬停状态、DPI、尺寸各自维护。`42-50`、`1001-1008`、`1290-1305`
- **存在的意义**：主屏全屏游戏时主任务栏被盖住，副屏任务栏上的镜像仍然可见。`src/widget.cpp:39-41`（行为注释，已改写）
- 监视器身份用 `DISPLAY_DEVICE.DeviceID`（跨重启/Explorer 重启/重新编号稳定）；笔记本 MUX/Advanced Optimus 切换会让整 ID 变化，于是再用 `DISPLAY#<型号>#…` 里的型号做唯一匹配，匹配到唯一一台才认。`1191-1236`、`1525-1537`
- 用户可选“全部 / 仅主屏 / 某一块屏”；选中的屏不在线时主屏顶上，保证信息不消失。`1232-1261`、`1474-1514`（菜单）
- 每块屏的 DPI 单独取（`g_dpiBar` 临时切换继承来源）。`56`、`61-68`

### 2.6 竖向任务栏

- 判定：任务栏窗口矩形 `高>宽`；设备变为**一列**：每个设备一块（图标居中、百分比在图标下、可带闪电），块间距按通知区域图标节奏，列宽=任务栏宽、居中。`src/widget.cpp:792-833`
- 位置：默认在通知区域上方 2px，居中于任务栏宽度；可选项：自动（避开 XAML 任务按钮列）/顶部 / 托盘上方 / 底部。`1618-1659`
- Win11“底部”方案很巧：把任务栏的 XAML 内容容器（`DesktopWindowContentBridge`）高度缩短 `条高`，Explorer 会把任务按钮和托盘整体上移，底部空出条的位置；再用屏幕取色（取条上方 4px 处三点一致的颜色，避免取到文字）填充条的背景，消除色带。Explorer 每次布局会恢复高度，所以每次 `Reposition` 重做。`90-130`、`1635-1650`、`104-118`
- Win10：直接 `MoveWindow` 缩窄 `ReBarWindow32` 高度。`1661-1669`
- 不支持竖向 RTL 任务栏（直接隐藏镜像）。`1027`

### 2.7 DPI 缩放

- DPI 来源：当前正在绘制/定位的那块任务栏的 `GetDpiForWindow`（镜像临时切换），失败回 96。`61-68`
- 整数尺寸：`MulDiv(v, dpi, 96)`；小数间距：先 `v*dpi/96.0` 再 `lround`（否则 0.5 与 1 会得到同样结果）。`66-68`
- 字号/图标尺寸本身是“用户 DIP 设置 × 系统字号基准”：`textPx = MulDiv(px, size, 13)`、`iconPx = MulDiv(px, iconSize, 16)`；设备间距按布局类型再折算（普通 14 基准，堆叠/竖向用更小基准）。`493-510`
- 重绘条件包含 `dpi != 上次`；镜像各自记录 `m.dpi`。`1604-1610`、`1028-1034`
- 清单 `PerMonitorV2` 是前提（`res/app.manifest`）。

### 2.8 明暗主题与背景板

| 项 | 做法 | 证据 |
|---|---|---|
| 主题源 | `HKCU\…\Themes\Personalize\SystemUsesLightTheme`（系统任务栏主题） | `src/main.cpp:179-185` |
| 变更通知 | `WM_SETTINGCHANGE` 且 `lp=="ImmersiveColorSet"` → 重读主题、重绘、重设 tooltip 主题、重设菜单主题 | `src/main.cpp:1962-1967` |
| 前景色 | 浅色主题纯黑 / 深色主题纯白 | `src/widget.cpp:686` |
| 低电红 | 浅 (196,30,40) / 深 (255,90,90) | `687` |
| 充电蓝 | 浅 (0,120,212) / 深 (96,205,255) | `688` |
| 充满绿 | 浅 (16,124,16) / 深 (108,203,95)（Windows success 绿） | `689` |
| 可选背景板 | 浅 #F3F3F3 / 深 (32,32,32)，alpha 240，内缩 4px、圆角 6px | `909-912` |
| hover 高亮 | 浅：黑 alpha 20 / 深：白 alpha 26，内缩 4px、圆角 4px（模仿托盘图标 hover） | `913` |
| tooltip 主题 | `SetWindowTheme("Explorer"/"DarkMode_Explorer")` + `WM_THEMECHANGED` | `460-469` |
| 原生菜单/对话框主题 | `uxtheme` 序号 135 `SetPreferredAppMode(2/3)` + 136 Flush，再 `WM_THEMECHANGED`；自绘菜单用同一明暗标志 | `src/main.cpp:187-196`、`1615-1617` |

### 2.9 点击、hover 与 tooltip

- **点击语义**：左键→设备菜单，右键→设置菜单；胶囊只 `PostMessage` 给 owner，不自己弹菜单、不激活。`src/widget.cpp:1318-1323`、`src/main.cpp:1891-1893`
- **hover**：整条画一层圆角高亮，并且重绘后重新定位（因为宽高可能没变，但保证层级）。`1289-1305`
- **tooltip**：`TOOLTIPS_CLASSW`，`WS_EX_TOPMOST|WS_POPUP|TTS_ALWAYSTIP|TTS_NOPREFIX`，`owner=隐藏窗口`；`TTF_IDISHWND|TTF_SUBCLASS`，`uId = 胶囊 HWND`；`TTM_SETMAXTIPWIDTH=600`；可用 `TTM_ACTIVATE` 全局开关；文本变化才 `TTM_UPDATETIPTEXTW`。`src/widget.cpp:1149-1157`、`1366-1380`、`1415-1431`
- tooltip 内容 = 每台显示中的设备一行：`显示名: 状态`（状态含 `L 80%, R 75%, case 40%` 这类分量详情、`charging`、`fully charged`、`asleep`）。`src/main.cpp:791-822`、`614-628`

### 2.10 重绘时机与性能

| 时机 | 动作 | 证据 |
|---|---|---|
| 每次轮询结果 | `SetItems` → 仅当 tooltip 文本变化才更新 tooltip → `PaintAll()` → `Reposition()` | `src/widget.cpp:1415-1431` |
| 每秒 | 主程序 `TIMER_WIDGET` → `Reconcile()`（模式/空态）→ `widget::Reposition()`（几何复核）＋更新检查 tick | `src/main.cpp:1948-1961`、`1956` |
| 悬停/移出 | 重绘当前条（及镜像）并重定位 | `1289-1305` |
| 主题/DPI/尺寸/方向变化 | `Reposition` 内比对 `dpi/vertical/任务栏客户区尺寸` → 重绘；`SetLightTheme` 直接重绘 | `1433-1437`、`1604-1610` |
| 数据未变 | `SetItems` 每次都重绘（上游简单做法）；几何/签名未变时不做 `SetWindowPos` | `1415-1431`、`1746-1757` |
| 内存 | 轮询/菜单结束后 `SetProcessWorkingSetSize(-1,-1)` 归还工作集（延迟 2s 合并触发） | `src/memory_trim.h:9-22`、`src/main.cpp:925`、`1625` |

要点：条面积很小（任务栏高 × 几百 px），CPU 像素合成完全可接受；真正的性能风险在**每秒重复分配**和**无谓 `SetWindowPos`**，上游都用签名/缓存躲掉了。

### 2.11 任务栏不可用 / 自动隐藏 / 全屏游戏兜底

- **自动隐藏**：不用处理——胶囊是任务栏子窗口，任务栏滑出屏幕时它一起滑出。
- **全屏游戏**：主屏任务栏被游戏窗口覆盖，胶囊自然看不见；副屏镜像仍显示（§2.5）。上游没有额外“检测全屏后隐藏”的代码（这点与我们现状不同，见 §6）。
- **任务栏不存在/被替换**（Explorer 未就绪、第三方任务栏、RDP 异常）：创建失败 → 主程序 `Reconcile()` 自动切到**托盘图标模式**（每设备一个数字图标），并每秒重试创建。`src/main.cpp:934-966`
- **任务栏几何不可信**（美化工具报告异常托盘坐标）：`RecoverHorizontalWidgetX` 校正 + “未知就隐藏”策略（镜像）/“保留上次有效位置”（主条）。`src/widget.cpp:70-88`、`1136`、`1232-1245`
- **空设备**：不隐藏窗口，画一个“电池未知”占位图标（alpha 110）+ tooltip “无设备”；可选“无设备时切托盘占位图标”。`src/widget.cpp:711`、`src/main.cpp:700-715`、`938-966`

### 2.12 Explorer 重启后的重建

1. 主程序启动时 `RegisterWindowMessageW("TaskbarCreated")`（`src/main.cpp:2121`）。
2. Explorer 崩溃重启后向所有顶层窗口广播 `TaskbarCreated`；隐藏的顶层窗口（非 message-only）能收到。
3. 处理：`widget::Destroy()` → `g_useWidget=false` → 清通知图标状态 → `ReAddAll()` 重挂托盘图标 → `Reconcile()` 重建胶囊。`src/main.cpp:1868-1876`
4. 兜底：`Alive()` 每次检查 `胶囊窗口 && 任务栏窗口` 的 `IsWindow`；`Reposition` 失败返回 false。`src/widget.cpp:1413`、`1594-1595`
5. 注意：Explorer 重启期间 `CreateWindowEx` 可能失败，要允许下一轮重试，不能死循环紧成立刻重建。

### 2.13 Windows 10 与 Windows 11 差异总表

| 维度 | Windows 10 | Windows 11 |
|---|---|---|
| 任务按钮容器 | `ReBarWindow32`（真实子窗口，可 `MoveWindow` 缩窄让位） | `Windows.UI.Composition.DesktopWindowContentBridge`（XAML island；里面的内容不可单独缩窄） |
| 时钟 | `ClockButton` 真实子窗口（可被用户关闭 → 不存在） | 无子窗口；UIA `AutomationId=SystemTrayIcon` |
| 资讯/天气 vs Widgets | `ShellFeedsTaskbarViewWindow` 或 `DynamicContent2`（随 build 变化） | Widgets 按钮为 XAML；UIA `AutomationId=WidgetsButton`；天气文字宽度变化 → 矩形需周期性重读 |
| 任务按钮矩形 | 无需 UIA | UIA `ClassName=Taskbar.TaskListButtonAutomationPeer`；开始按钮 `AutomationId=StartButton` |
| 让位手段 | 缩窄 ReBar（每次布局被 Explorer 恢复 → 每次重做） | 左下/左端避让；竖排“底部”时缩短 bridge 高度预留空间 |
| XAML 判定 | 无 `DesktopWindowContentBridge` 子窗口 | 有 |
| 竖向任务栏 | ReBar 高度缩窄 | bridge 高度预留 + 底部背景取色填充 |
| 出错风险 | feeds 类名/存在的 build 差异；ReBar 被恢复 | XAML 内部结构随版本变化；Widgets 按钮位置/宽度变化；UIA 跨进程调用可能阻塞 |
| RTL | 水平可镜像，竖向不支持 | 同 |

> 上游对 Win11 的定位**优先用真实子窗口矩形**（`TrayNotifyWnd`），只在拿不到时才用 UIA——这是给我们的关键启示：**不要一开始就依赖 UIA**。

### 2.14 Clean-room 复刻步骤（C#/Win32 视角，可直接排期）

> 目标形态：在现有 `PreviewTaskbarPill` 上做“托盘锚定 + 状态完善 + 主题/重建”，不引入 XAML。以下为原创步骤，不依赖上游代码。

**A. 定位（P0）**
1. 每次 `Update()`：`FindWindowW("Shell_TrayWnd")`；确认存活；取 `GetWindowRect`。
2. `FindWindowExW(taskbar, 0, "TrayNotifyWnd", 0)` 取托盘矩形；校验可见、尺寸>0、仍在任务栏矩形内（容差 2px）。
3. 计算 `edgeX = trayRect.Left`；`pillLeftScreen = edgeX - pillWidthPx - 2*scale`。
4. 校验 `pillLeftScreen >= taskbar.Left` 且 `+ width <= taskbar.Right`；否则回退到当前 235 DIP 方案（保留 NICEHCK 兼容位置），再否则 clamp。
5. `ScreenToClient(taskbar, ref pt)` 后调用 `UpdateLayeredWindow`/`ShowWindow`；`y = (任务栏高 - 胶囊高)/2`。
6. 几何签名 = `taskbar rect + tray rect + width + height + scale + theme + visible`；不变则直接 return（不重绘、不 SetWindowPos）。
7. 托盘不可用/异常：隐藏胶囊并记录一次日志（防刷屏），下一轮再试。
8. 任务栏方向为竖向：v1 直接隐藏胶囊（或回退托盘模式），不要尝试列布局（§7 不做）。

**B. 主题（P0）**
9. 读 `HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize\SystemUsesLightTheme`（不是 `AppsUseLightTheme`，后者是应用主题，任务栏跟随前者）。
10. 在 `WndProc` 里处理 `WM_SETTINGCHANGE(lParam="ImmersiveColorSet")` 与 `WM_DISPLAYCHANGE`、`WM_DPICHANGED`：清签名 → 立刻 `Update()`。
11. hover 高亮/背景/边框颜色按 §3.3 明暗表切换；tooltip 用 `SetWindowTheme(hwnd, light?"Explorer":"DarkMode_Explorer", null)`。

**C. Explorer 重启（P0）**
12. 在 App 启动时创建**隐藏顶层窗口**（`WS_POPUP|WS_EX_TOOLWINDOW`，非 message-only）或子类化主窗口 `HWND`；`RegisterWindowMessage("TaskbarCreated")`。
13. 收到广播：销毁胶囊 HWND（等待 `IsWindow` 为 false 或直接 `DestroyWindow` 自己的窗口）、清缓存签名、重建窗口类实例、重新 `AttachToTaskbar`、重新加 tooltip；失败则 1s 后由定时器重试。
14. 保留现有“1s 检查父窗口/任务栏”作为兜底，但不要在 Explorer 正在重启时反复 `DestroyWindow/CreateWindow` 抖动（加 2–3s 冷却）。

**D. 视觉与状态（P0/P1）**
15. 字号 13、图标 16、图标↔文字 5、设备间距 14（均为 DIP，乘 `GetDpiForWindow(taskbar)/96`；取整用 `Math.Round`）。
16. 状态：`≤15%` 且非充电 → 红；`charging` → 蓝闪电；`full` → 绿勾；`sleeping/未知值` → 整体 alpha 120/255（或按设置）；`unknown` → `?`；耳机保留 L/R（我们的数据模型已有分量）。
17. tooltip：原生 `TOOLTIPS_CLASSW`（`TTF_IDISHWND|TTF_SUBCLASS`，`TTM_SETMAXTIPWIDTH=600`），每个设备一行 `名称: 状态`；文本不变时不更新。
18. 无设备：画占位图标（电池未知）+ tooltip 说明，而不是隐藏（隐藏会让用户以为坏了）。

**E. 交互与兜底（P1）**
19. 点击保持“打开主窗口”（现有行为）；需要设备菜单时用 Win32 `CreatePopupMenu/TrackPopupMenu`（owner=隐藏窗口），**不要用 WinUI MenuFlyout**（见 §6.3 风险 5）。
20. 创建失败/竖向/用户选择 → 托盘模式：复用现有 `TrayService`，每设备一个图标（数字 + 电量条），tooltip=设备状态；任务栏恢复后自动切回（1s 复核）。
21. 唤醒/插拔回调 → 延迟 3/5/10/15s 补轮询（§5 刷新策略），避免休眠/插入瞬间读到旧值。

---

## 3. 视觉规格（可直接用于设计文档）

### 3.1 几何与排版（96 DPI 逻辑像素；`Px(v)=MulDiv(v,dpi,96)`）

| 参数 | 默认 | 可调范围 | 说明 / 证据 |
|---|---|---|---|
| 文字高度 | 13 px | 9–24 | 用户“字号”；`widget.h:34`、`main.cpp:424`、`text_style_dialog.h:8` |
| 字重 | 400 | 100–900 | 默认常规；`widget.h:35` |
| 图标高度 | 16 px | 10–28 | 与文字独立；`widget.h:36` |
| 图标↔文字间距 | 5 px | 0–24（可 0.5 步进） | “文字间距”；`widget.h:37` |
| 设备间距 | 14 px | 2–60 | 行布局；堆叠/竖向按 10/14、12/14 折算；`widget.h:38`、`498-500` |
| 分隔线两侧留白 | 6 px | 0–30 | 开启分隔线后设备间距=留白×2+线宽；`widget.h:39`、`506-510` |
| 字距 | 0 px | −2–12 | 小数按逐字符 sub-pixel 排布；`widget.h:40`、`518-553` |
| 左右边距 | 8 px | 固定 | 内容从 `Px(8)` 起；`widget.cpp:867-868` |
| 图标与文字基线 | 垂直居中于条高 | — | 每 piece `y=(H−h)/2`；`widget.cpp:923` |
| 条高 | = 任务栏客户区高 | — | 不额外加高 |
| 条宽 | `2*8 + Σ(图标+间距+文字+设备间距)` | — | 由内容决定，无最大宽度；`widget.cpp:867-880` |

派生尺寸：

| 元素 | 尺寸 / 规则 | 证据 |
|---|---|---|
| 充电池闪电 | 图标字体 11 px 基准（随 iconSize 缩放），文字右侧 `iconGap` | `widget.cpp:692-693`、`742-761` |
| 连接方式小标记 | 同上 11 px 基准，alpha 160/255 | `762-768` |
| 同类设备角标 | 字体 `max(8, 字号×7/13)`（默认 7→最小 8px）；贴在图标右下 +3px，1px 任务栏色描边 | `701`、`771-777`、`937-943` |
| 品牌电量条 | 高 `max(2, Px(2))`，距文字 `Px(3)`，宽度=文字宽，按百分比从左填充，轨道 alpha 45/255 | `925-935` |
| 圆环样式 | 直径 `Px(28)` 取偶；线宽 `max(2, d/13)`；图标 11px 基准内嵌居中；轨道 alpha 45(浅)/60(深)，弧=12 点起顺时针百分比 | `652-676`、`715-733` |
| 堆叠样式 | 图标在上、文字在下；仅当任务栏高 ≥ 图标高+间距+文字高+5+6 才启用 | `785-793`、`835-865` |
| 竖向列 | 左右 padding 10；每设备：图标→图标↔文字间距→文字（闪电在右）→块间距（12 基准）；分隔线宽=任务栏宽−20 | `794-833` |
| hover 高亮 | 内缩 4、圆角 4，浅色黑 20 / 深色白 26 | `913` |
| 可选背景板 | 内缩 4、圆角 6，浅 #F3F3F3 / 深 (32,32,32)，alpha 240 | `909-912` |
| 分隔线 | 线宽 `max(2, Px(2))`；行布局高 12px 居中；alpha 浅 55 / 深 64 | `948-955`、`871-875` |

### 3.2 图标来源与字形

- 上游默认嵌入 **Material Icons Outlined** 私有字体（`res/MaterialIconsOutlined-Regular.otf`，4.5 KB，Apache-2.0），通过 `AddFontMemResourceEx` 运行时注册；注册失败则回退 **Segoe Fluent Icons → Segoe MDL2 Assets**。`src/icon_font.cpp:31-54`、`icon_font.h:5-7`
- 用 `CreateFontW(-px, …, L"Material Icons Outlined")`；图标尺寸独立于文字尺寸。`src/icon_font.cpp:48-52`
- 结构体字段顺序即码点顺序（`icon_font.h:19-24`）：
  - 设备：headset、mouse、keyboard、gamepad、phone、microphone、speaker、bluetooth、devices
  - 状态：bolt、check、wifi、batteryUnknown、batteryFull、battery、batteryCharging、batteryAlert、offline、usb
  - 菜单：rename、alerts、history、monitor、globe、stopwatch、refresh、help、support、update、exit、chevronRight
  - 托盘电量：`battery_0_bar` … `battery_6_bar`（7 档）
- 码点表（Material / Segoe 回退）见 `src/icon_font.cpp:9-24`；实现时**不要抄表**，直接选用系统 `Segoe Fluent Icons` 的等价码点（Win11 自带），Win10 回退 `Segoe MDL2 Assets`，并像上游一样用 `GetTextFaceW` 验证字体是否真的存在（GDI 会静默替换，导致方框）。`src/widget.cpp:475-491`
- 设备类型→图标：耳机/鼠标/键盘/手柄/手机/麦克风/音箱/未知（未知优先蓝牙图标）；`src/icon_font.cpp:56-68`
- 字号/字族：文字默认 **Segoe UI Variable Text → Segoe UI**（后者比前者小 1px 以保持视觉一致）；用户可换任意已安装字体。`src/widget.cpp:694-701`

### 3.3 状态视觉

| 状态 | 图标 | 文字/条 | 颜色 | 证据 |
|---|---|---|---|---|
| 正常 | 设备图标（品牌色或前景色） | `93%` | 前景：浅黑/深白 | `widget.cpp:686`、`735-760` |
| 低电（≤15%，非充电） | 设备图标 | 数字变红 | 浅 (196,30,40) / 深 (255,90,90) | `687`、`735` |
| 充电 | 设备图标 + 蓝闪电 | 数字 + 闪电 | 浅 (0,120,212) / 深 (96,205,255) | `688`、`736`、`761` |
| 充满（插电且 100%） | 绿色对勾代替闪电 | 数字 | 浅 (16,124,16) / 深 (108,203,95) | `689`、`736`、`761` |
| 休眠/旧值 | 设备图标变暗 | 数字变暗（可关） | 整条 alpha 120/255 | `714`、`main.cpp:276-282` |
| 断开（可选显示） | 设备图标 + 断链图标 | 显示 `--%`，整条变暗 | 前景 alpha 120 | `main.cpp:803-813`、`widget.cpp:763` |
| 未知 | 设备图标 | `?` | 前景 | `widget.cpp:753` |
| 粗电量（Low/Full） | 设备图标 | 文字替代数字 | 前景 | `widget.h:17`、`main.cpp:806` |
| 连接方式标记（可选） | 设备图标 + wifi/蓝牙/USB/断链 | 数字后小标记 | 前景 alpha 160 | `widget.cpp:740-768`、`main.cpp:779-789` |
| 品牌设备（可选） | 品牌色图标 + 柔光晕 | 品牌色数字 + 电量条 | 品牌色（§3.4） | `widget.cpp:743-760`、`747-751` |
| 无设备 | 电池未知图标 | — | 前景 alpha 110 | `widget.cpp:711` |

### 3.4 品牌色（深色任务栏版 / 浅色任务栏加深版）

| 品牌 | 深色 RGB | Hex | 浅色 RGB | Hex |
|---|---|---|---|---|
| Razer | (68,214,44) | #44D62C | (22,140,20) | #168C14 |
| Logitech | (0,184,252) | #00B8FC | (0,120,180) | #0078B4 |
| SteelSeries | (255,82,0) | #FF5200 | (204,65,0) | #CC4100 |
| Corsair | (245,228,26) | #F5E41A | (150,122,0) | #967A00 |
| HyperX | (232,35,63) | #E8233F | (200,16,46) | #C8102E |
| ROG | (255,46,77) | #FF2E4D | (208,0,42) | #D0002A |
| ZOWIE | (240,60,50) | #F03C32 | (192,32,26) | #C0201A |
| PlayStation | (31,139,255) | #1F8BFF | (0,98,196) | #0062C4 |
| Xbox | (34,168,34) | #22A822 | (16,124,16) | #107C10 |
| Samsung | (91,123,255) | #5B7BFF | (20,40,160) | #1428A0 |
| Apple | (199,199,204) | #C7C7CC | (110,110,115) | #6E6E73 |
| Jabra | (255,200,0) | #FFC800 | (140,106,0) | #8C6A00 |
| JBL | (255,61,0) | #FF3D00 | (204,52,0) | #CC3400 |
| Cooler Master | (157,107,255) | #9D6BFF | (106,43,217) | #6A2BD9 |
| Steam | (26,159,255) | #1A9FFF | (11,111,191) | #0B6FBF |
| Redragon | (230,0,18) | #E60012 | (192,0,15) | #C0000F |
| Wobkey | (45,96,255) | #2D60FF | (24,63,199) | #183FC7 |

- 取值原则（已改写）：深色版即官方色；Xbox/PlayStation/Samsung 的蓝绿在深色任务栏偏暗，做了同色相提亮；浅色版统一加深，保证在 #F3F3F3 上对比度 ≥3:1（黄色与银色加深最多）。`src/brands.cpp:7-29`
- 命中优先级：① 驱动 key 前缀（`razer:`、`logi:`、`steelseries:`…）→ ② USB/蓝牙 VID → ③ 名称规则（contains / whole word / prefix / exact）→ ④ 罗技型号正则（G/K/M + 3 位数字）→ 否则无品牌色。`src/brands.cpp:141-169`
- 例外：Sony 音频设备（WH-*/WF-*/LinkBuds）虽用 Sony VID 但不给 PlayStation 色；Microsoft VID 只有 gamepad 才判 Xbox；共享 VID（HP/HyperX、ASUS/ROG、Microsoft/Surface）留给名称判断。`src/brands.cpp:70-87`、`136-137`、`163-165`
- 注意：**品牌色表是事实数据**，但实现时建议改用我们 Bench 的设备色令牌（耳机 `#5B4BC4`、鼠标 `#A66B22`、遥控器 `#1F6F8C`，见 [design-system.md](../ui/design-system.md:34)）以免视觉系统冲突。

### 3.5 可选样式开关（上游）

`Icons above percentages`（堆叠省宽）、`Ring style`（圆环充能）、`Mark connection type`（2.4G/蓝牙/USB 小标记）、`Separator between devices`、`Background behind taskbar text`、`Show percent sign`、`Brand colors`、`Show tooltip`、`Make room for other taskbar widgets`、`At the left end of the taskbar`、`Just before the taskbar apps`、竖向位置四选一、`Show the widget on`（监视器）。

### 3.6 Tooltip 内容

- 每个显示中的设备一行：`<显示名>: <状态>`；无设备时 `BatteryHub: 未找到带电池信息的设备`。`src/main.cpp:791-822`
- 状态串规则：`off / not connected` / 粗电量文本 / `N%` / `N% (last known)` / `unknown`；若百分比可用且有分量详情则追加 `(L 80%, R 75%, case 40%)`；再追加 `fully charged` / `charging` / `asleep`；无电量时追加驱动建议。`src/main.cpp:614-628`
- 显示名 = 用户重命名优先，否则驱动名；tooltip 同时用于托盘图标。`src/main.cpp:150-152`
- 最大宽度 600px（防止长设备名把 tooltip 拉成一条线）。`src/widget.cpp:1378`

### 3.7 截图实测（`docs/screenshot.png`，500×96，200% 缩放）

| 测量 | 像素 | 折算 DIP | 对应默认值 |
|---|---|---|---|
| 任务栏高（整图） | 96 px | 48 | Win11 200% 任务栏 |
| 耳机图标墨迹 | 28×28 px | 14×14 | 16px 图标字体的墨迹高度（字形留白） |
| 鼠标图标墨迹 | 22×32 px | 11×16 | 同上 |
| “30%”文字墨迹高 | 18 px | 9 | 13px 字号数字墨迹（≈字号的 0.7） |
| 图标→文字间距 | 10 px | 5 | `iconGap=5` ✅ |
| 两设备间距（“30%”右侧到鼠标图标） | 28 px | 14 | `itemGap=14` ✅ |
| 托盘/系统图标 | 32×32 px | 16 | 系统托盘小图标 |
| 背景色 | (66,66,67) | — | 实测任务栏底色（上游背景板默认关闭，所以看到的是任务栏本身） |
| 蓝色状态点 | (0,120,215) 附近 | — | 系统“有更新”指示，非胶囊内容 |

结论：截图使用**默认文本样式**（13/16/5/14）、未开品牌色/分隔线，条本身无底板，位于通知区域（⌃）左侧。可直接作为“若采用上游式透明条”的视觉基线；我们的胶囊保留底板，则需重新截基线（见 §6.7）。

### 3.8 托盘图标模式视觉（任务栏不可用时的兜底）

- 每设备一个 16×16（SM_CXSMICON×DPI）图标：Material 电量条字形（`battery_0_bar`…`battery_6_bar`，按百分比取整档）+ 覆盖数字，或“?”/“×”。
- 电量条着色：充电 (0,170,255)、≤15% (232,32,48)、≤30% (255,150,0)、品牌色、否则绿 (30,190,90)；数字低电红 (240,50,60)；休眠 alpha 140/255。`src/main.cpp:582-605`
- tooltip = 显示名 + 状态；无设备时一个“电池未知”占位图标。`src/main.cpp:700-741`

---

## 4. 设置与菜单

### 4.1 菜单结构（左键设备 / 右键设置；均为中文转述）

**左键菜单**（`ShowMenu(false)`，`src/main.cpp:1224-1624`）：

```
无设备占位行（禁用）
────────
[设备 A]            <右侧：状态文本>
   ├ 在任务栏/托盘中显示（勾选=显示）
   ├ 上移 / 下移（托盘模式下）
   ├ 重命名…
   ├ 恢复原名称
   ├ 电池提醒 ▸  默认(15%) / 5% / 10% / 15% / 20% / 25% / 30% / 35% / 40% / 禁用通知
   └ 电池历史…
[设备 B] …
────────
立即刷新
（无设备时候选占位）
────────
退出
```

**右键菜单**（`ShowMenu(true)`）分组顺序：

```
（设备行不出现）
立即刷新
────────
记录电池历史（勾选）
充到该电量时提醒 ▸ 80% / 85% / 90% / 关闭
每隔多久检查电量 ▸ 30s / 60s / 2min / 5min / 10min / 15min / 30min / 60min
────────
显示设置 ▸
   ├ 显示样式 ▸
   │    在任务栏显示 / 以托盘图标显示 / 无设备时移到托盘
   │    图标在上、百分比在下 / 任务栏文字背景板 / 圆环样式 / 显示百分号 / 品牌颜色
   │    标记连接方式 / 设备间分隔线 / 显示 tooltip / 文字样式…
   ├ 任务栏位置 ▸
   │    通知区域旁 / 40 / 80 / 120 / 160 / 200 px 偏移 / 自定义… / 为其它任务栏小组件让位 /
   │    任务栏最左端 / 任务栏应用按钮前
   ├ 竖向任务栏位置 ▸（仅竖向时）自动 / 顶部 / 通知区域上方 / 底部（Win11 会把任务栏图标上移）
   ├ 在哪些屏幕上显示 ▸ 全部 / 仅主屏 / 某一个显示器（未连接时显示在主屏）
   ├ 隐藏最近/休眠设备 / 鼠标保留最后电量 / 显示已断开设备 / 休眠设备变暗
语言 ▸ 自动（Windows 语言）/ 内置语言列表 / 添加自己的语言…
开机启动（勾选）
────────
更新可用: vX.Y.Z… / 检查更新…
自动检查更新（勾选）
请求支持新设备…
支持项目…
────────
退出
```

- 两级菜单由**同一棵 item 树**生成，再按规则过滤：设备行 + 刷新进左键，其余进右键；分隔线/退出两边都有。`src/main.cpp:1584-1604`
- 打开菜单前 `SetForegroundWindow`；从任务栏弹出时把任务栏矩形作为“排除区”，避免最后一项落到任务栏下面。`src/main.cpp:1606-1623`
- 自绘菜单规格（若参考其体验、不抄实现）：圆角、半透明面板、32px 行高、圆角 hover、右侧勾选列、RTL 从右排、过高时滚动、上限 400 DIP；创建失败回退系统菜单。`src/popup_menu.h:1-38`

### 4.2 注册表设置项全表

根键：`HKCU\Software\WirelessBattery`（`src/main.cpp:85`）。除注明外均为 `REG_DWORD`（0/1 布尔或整数）。

| 值名 | 类型 | 默认 | 含义 / 备注 |
|---|---|---|---|
| `TaskbarWidget` | DWORD | 1 | 1=任务栏胶囊，0=托盘图标模式 |
| `HideLastKnownDevices` | DWORD | 0 | 隐藏“最后已知/休眠”设备 |
| `KeepMouseLastKnown` | DWORD | 0 | `HideLastKnownDevices` 打开时，鼠标仍保留最后电量 |
| `MoveToTrayWhenEmpty` | DWORD | 0 | 无设备时胶囊换成托盘占位 |
| `ShowDisconnectedDevices` | DWORD | 0 | 显示明确报告离线链路的设备（暗 ×） |
| `DimSleepingDevices` | DWORD | 1 | 休眠设备变暗 |
| `RecordBatteryHistory` | DWORD | 1 | 记录电池历史 |
| `MakeRoomForOtherWidgets` | DWORD | 1 | 避让其它任务栏挂件 |
| `TaskbarOffset` | DWORD | 无 | 相对托盘的横向偏移（DIP，可负）；写 0 时删除 |
| `SecondaryTaskbarOffset` | DWORD | 无 | 副屏镜像的额外偏移 |
| `WidgetBackground` | DWORD | 0 | 文字背景板 |
| `WidgetMonitor` | SZ | `all` | `all` / `main` / 显示器 DeviceID |
| `OtherTaskbars` | DWORD | 1 | 旧版总开关，仅在新值缺失时映射为 all/main |
| `StackedLayout` | DWORD | 0 | 图标在上、百分比在下 |
| `ShowPercentSign` | DWORD | 1 | 数字后显示 `%` |
| `RingStyle` | DWORD | 0 | 圆环充能样式 |
| `ConnectionIcons` | DWORD | 0 | 数字后加连接方式小标记 |
| `DeviceDivider` | DWORD | 0 | 设备间细分隔线 |
| `ShowTooltip` | DWORD | 1 | hover tooltip |
| `VerticalPlace` | DWORD | 0 | 竖向位置：0 自动 / 1 顶部 / 2 托盘上方 / 3 底部 |
| `WidgetFarLeft` | DWORD | 0 | 任务栏最左端 |
| `WidgetBeforeApps` | DWORD | 0 | 任务栏应用按钮左侧 |
| `BrandColors` | DWORD | 0 | 品牌色 |
| `PollIntervalSeconds` | DWORD | 60 | 轮询间隔，读取时 clamp 30–86400 |
| `TextFont` | SZ | 空 | 字体名；空=Windows UI 字体 |
| `TextSizePx` | DWORD | 13 | 字号 9–24 |
| `TextWeight` | DWORD | 400 | 字重 100–900 |
| `TextIconSizePx` | DWORD | 16 | 图标高 10–28 |
| `TextIconGapPx10` | DWORD | 50 | 图标↔文字间距 ×10（5.0），兼容旧 `TextIconGapPx` |
| `TextItemGapPx10` | DWORD | 140 | 设备间距 ×10（14.0），兼容旧键 |
| `TextDividerGapPx10` | DWORD | 60 | 分隔线留白 ×10（6.0），兼容旧键 |
| `TextLetterSpacingPx10` | DWORD | 0 | 字距 ×10（0），兼容旧键 |
| `DeviceAliases` | MULTI_SZ | 空 | 每设备重命名（按稳定 device key） |
| `HiddenDevices` | MULTI_SZ | 空 | 隐藏设备 key 列表 |
| `DeviceOrder` | MULTI_SZ | 空 | 用户排序（设备 key） |
| `KnownDevices` | MULTI_SZ | 空 | 上次会话见过的设备归档（重启后离线占位） |
| `AlertThresholds` | MULTI_SZ | 空 | 每设备低电阈值；`device-key<TAB>阈值`，−1=禁用 |
| `ChargeLimitAlert` | DWORD | 0 | 充电到 80/85/90 时提醒，0=关 |
| `Language` | SZ | 空 | 语言 code，空=跟随 Windows |
| `IgnoreDevices` | SZ | 无 | 不打开的设备 `VID:PID` 列表（逗号分隔，README 说明） |

开机启动：`HKCU\Software\Microsoft\Windows\CurrentVersion\Run`，值名 `WirelessBattery`，内容为带引号的 exe 全路径；开启时写入、关闭时删除，路径不一致视为未开启（移动后由启动逻辑修复）。`src/main.cpp:83`、`467-484`

### 4.3 文字样式编辑器（`text_style_dialog.cpp`）

- 纯 Win32 对话框：系统标题栏/消息字体/标准控件，随用户主题、DPI、字体设置；客户区 468×282（96 DPI），全部经 `MulDiv(px,dpi,96)` 缩放。`src/text_style_dialog.cpp:10-25`
- 字段：字体（下拉，枚举已安装字体并去重、跳过 `@` 竖排变体，最多 128 字符）、字号、字重（下拉存数值）、图标尺寸、图标↔文字间距、设备间距、字距、分隔线留白；数值校验失败在对话框内显示可读错误并聚焦该框。`src/text_style_dialog.cpp:17-21`、`43-180`
- 小数框允许 1 位小数（`5.5`），显示时不带多余的 `.0`。`src/text_style_dialog.cpp:81-103`
- 我们建议：**不开放字体选择**（视觉系统固定字体），只开放“文字大小/图标大小/间距”三档已足够。

### 4.4 其它设置项

| 项 | 行为 | 证据 |
|---|---|---|
| 每设备重命名 | 最多 64 字符，仅影响显示（tooltip/菜单/通知），不改驱动匹配；可恢复原名 | `src/device_aliases.h`、`main.cpp:1162-1191` |
| 每设备低电阈值 | 5–40%（步进 5）+ 禁用；默认 15%；提醒只从“新鲜、已连接、非充电”读数触发，一次充电只提醒一次，电量回升超过阈值+10 或开始充电后重新武装；休眠/未知/断开的读数不会重置闩锁 | `src/alert_rules.h:10-45` |
| 充电上限提醒 | 80/85/90%，只在“跨越”时提醒一次，充满 100% 不提醒；一次充电只提醒一次 | `src/alert_rules.h:47-63` |
| 历史 | 查看器 1020×710，24h/7d/30d 三档，展示最新/最低/最高/变化量/样本数，可清除单设备历史 | `src/battery_history.cpp:577-607`、`src/history_model.h:105-130` |
| 启动项 | Run 键写入/删除，路径修复 | `src/main.cpp:467-484` |
| 刷新间隔 | 菜单 30s/60s/2m/5m/10m/15m/30m/60m；存储 clamp 30s–24h | `src/main.cpp:58`、`458-465` |
| 多语言 | 内置 + 用户目录 `%APPDATA%\WirelessBattery\lang\*.txt`；格式 `@code/@name/英文=译文`，命名占位符 `{name}`，缺失回退英文；`@rtl=1` 支持从右到左；`--list/--dump` 固定英文 | `src/i18n.h:1-51`、`src/i18n.cpp:203-219` |
| 单实例 | 互斥量 `Local\WirelessBatteryTray`；第二个实例直接退出（不激活已运行实例） | `src/main.cpp:2115-2116` |

---

## 5. 其它可吸收的产品特性评估

### 5.1 逐项表

> 价值 = 对「外设中枢」的产品价值；复杂度 S/M/L（按我们技术栈估算）；建议 = 必做 / 可选 / 不做。

| # | 特性 | 上游做了什么 | 价值 | 复杂度 | 建议 |
|---|---|---|---|---|---|
| 1 | 电池历史 + 查看器 | 本地 TSV，30 天、30 分钟采样（状态变化 60s 最小）、≤16000 样本、≤2MB 压缩；查看器 24h/7d/30d + 统计 + 清除 | 高 | M | 可选（高优先） |
| 2 | 低电提醒规则 | 每设备阈值/禁用 + 闩锁规则（充电或回升 >阈值+10 再武装） | 高 | S | **必做** |
| 3 | 充电上限提醒 | 80/85/90 跨越提醒一次 | 中 | S | 可选 |
| 4 | 运行时估算 | 只用真实放电样本，≥4 点/≥2h/≥3pp/读数 <3h，0.02–12 %/h，上限 90 天；状态 Ready/Charging/Stale/Insufficient/Flat；UI 明确“趋势不是测量” | 中 | S | 可选 |
| 5 | 刷新策略 | 登录后 3/7/15s；唤醒/到达后 3/5/10/15s；设备事件可提前唤醒（每步最多 4 次）；默认 60s | 高 | S–M | **必做**（至少唤醒/插拔补轮询） |
| 6 | 托盘图标模式 | 每设备一个数字电量图标；任务栏不可用/竖向/无设备时兜底；无设备占位 | 中高 | M | 可选（失败兜底为必做） |
| 7 | 单实例 | 互斥量；第二实例静默退出 | 中 | — | 已有（我们还会激活旧实例，更好） |
| 8 | CLI | `--list`/`--json`/`--dump`/`--demo`/`--quit`/`--report`/`--help`；`--dump` 另存 `%TEMP%\BatteryHub-dump.txt` | 中（排查/自动化） | S | 可选（`--dump`/`--list`） |
| 9 | 更新检查 | 启动 30s 后 + 每 24h 查 GitHub API；SHA256SUMS + 内置公钥签名校验；只在菜单提示，不弹通知；便携版只给页面 | 低（个人自用） | L | 不做 |
| 10 | 多语言 | 文件驱动、占位符安全、RTL、用户可覆盖单个词 | 低–中 | M | 不做（现为中文自用） |
| 11 | 内存整理 | 轮询/菜单后归还工作集 | 低 | S | 可选 |
| 12 | 设备重命名 | 按稳定 key，最多 64 字符，可恢复 | 中 | S | 可选（我们已有设备 ID/名称体系，可接） |
| 13 | 设备归档 | 重启后恢复离线设备占位，避免“消失” | 中 | S | 可选 |
| 14 | 平滑充电读数 | 充电时若读数比充电前低，沿用充电前值（最长 12h） | 中 | S | 可选 |
| 15 | 无设备占位 | 电池未知图标 + tooltip | 中 | S | **必做**（很便宜） |
| 16 | 自绘 Win11 弹出菜单 | 1792 行自绘 layered 菜单 + 系统菜单回退 | 中 | L | 不做 |
| 17 | 双菜单拆分 | 左键设备、右键设置 | 高 | S | 可选（点击开窗之外再加菜单） |
| 18 | 多显示器镜像 | 每块副屏任务栏一个胶囊 | 中 | L | 可选（后置） |
| 19 | 竖向任务栏 | 列布局 + 三种位置 + Win11 预留底部 | 低 | L | 不做 |
| 20 | 兼容其它小组件 make room | 扫描任务栏上第三方小挂件并左移让位 | 低–中 | M | 可选 |
| 21 | 品牌色 | 17 品牌 × 明暗两套 + 匹配规则 | 中（视觉愉悦） | S–M | 不做整表（改用我们 Bench 设备色） |
| 22 | 请求支持设备窗口 | 用户勾选/预览后上报 + 打开 GitHub issue | 低 | M | 不做 |
| 23 | 徽标/角标/堆叠/圆环/分隔线 | 多种视觉开关 | 低–中 | S | 不做（与胶囊风格冲突） |
| 24 | 悬停/全屏/自动隐藏 | child-of-taskbar 天然处理自动隐藏；镜像覆盖全屏游戏 | 高 | S | 部分必做（隐藏策略见 §6） |

### 5.2 几个值得细看的实现事实

**电池历史（`battery_history.cpp` + `history_model.h` + `runtime_estimate.h`）**
- 文件：`%LOCALAPPDATA%\WirelessBattery\battery_history.tsv`；ASCII 行 `时间戳<TAB>十六进制设备key<TAB>百分比<TAB>是否充电`，设备 key 用 wchar_t 8 位十六进制编码，避免任意字符破坏行格式。`src/history_model.h:77-103`、`src/battery_history.cpp:33-40`
- 策略：保留 30 天、常规 30 分钟一条、状态变化最短 60 秒、上限 16000 条；文件 >1MB 就压缩重写、>4MB 拒绝加载；写临时文件 + `FlushFileBuffers` + `MoveFileEx(REPLACE|WRITE_THROUGH)` 原子替换；启动时清理过期/损坏行。`src/history_model.h:14-18`、`src/battery_history.cpp:53-109`
- 估算：保守，宁可显示“数据不足/数据陈旧”也不糊弄；结果是趋势不是测量（`src/runtime_estimate.h:23-74`）。
- 价值判断：我们的设备页只有“当前电量”，历史/趋势是很自然的增强；数据结构可放 Core 层，纯 C# 移植约 300–400 行 + 一处折线图（XAML `Polyline` 即可，避免引入 Win2D）。

**提醒规则（`alert_rules.h`）**
- 只从“新鲜 0–100%、已连接、非休眠、非充电”读数判定；`>阈值+10` 或充电时复位闩锁；休眠/未知/断开不重置闩锁（防止来回插拔刷提醒）。`src/alert_rules.h:35-45`
- 充电上限提示只在“看到跨越”的那一刻触发（启动时已 90% 不提示），充满 100% 交给“充满”通知。`47-63`
- 价值判断：我们的通知系统已有“低电量”，升级为“每设备阈值 + 闩锁”成本很低，属于必做级。

**刷新策略（`refresh_policy.h`）**
- 登录后固定 3/7/15s 各轮询一次（接收器可能枚举早于无线链路）；唤醒/设备到达后按 3/5/10/15s 逐步补，直到目标设备都报出电量；设备事件可提前唤醒，但每步最多 4 次，避免风暴。`src/refresh_policy.h:18-22`、`51-100`
- 触发源：HID 接口到达/移除（2.5s 去抖）、蓝牙 HCI ACL 连接事件、`PBT_APMRESUMEAUTOMATIC`（5s 后）、显示器开/关（3s 后，覆盖 Modern Standby 不报 suspend 的情况）。`src/main.cpp:1897-1947`
- 价值判断：能显著改善“唤醒后显示旧电量/插上接收器要等一分钟”的体验，建议移植策略（不需要照抄实现）。

**托盘图标兜底**
- 任务栏胶囊创建失败 → `Reconcile` 每 1s 重试并切托盘模式；每设备一个图标；无设备占位图标；托盘图标添加有 `NIM_ADD/NIM_MODIFY` 互相回退（shell 启动早期会失败）。`src/main.cpp:934-966`、`638-667`
- 价值判断：我们已有 `TrayService`（单图标），扩展为“任务栏不可用时显示设备图标”成本中等；至少应保证胶囊创建失败时有可见兜底。

**CLI 与诊断**
- `--list`：人类可读（名称/[连接方式]/{品牌}/状态/电量/mV/key）；`--json`：稳定 JSON 数组（key/name/via/kind/percent/level/detail/charging/asleep/connected/millivolts/graceSec）；`--dump`：所有 HID 集合 + 打不开的 HID + 所有 USB 设备 + 蓝牙设备，另存 `%TEMP%\BatteryHub-dump.txt`（无控制台时直接打开文件）；`--demo`：每个品牌样例设备（截图/配色用，不写历史/归档）；`--quit`：向运行实例发关闭并等待 30s。`src/main.cpp:1989-2062`、`2066-2110`
- 价值判断：我们已有 `tools/DeviceProbe`；如果要做“支持新设备/抓包排查”，`--dump` 值得加（约半天）。

**更新检查**
- 启动 30s 后与每 24h 查 GitHub 公开 API；只在菜单显示“有更新”，不弹窗；下载 `SHA256SUMS.txt` 与签名，用内置公钥验证后才下载安装器并校验 SHA-256；便携版只跳转发布页；开发版禁用。`README.md:290-322`
- 价值判断：个人自用预览不做（供应链/签名维护成本高）。

**多语言**
- 语言文件放在仓库 `lang/` 与 `%APPDATA%\WirelessBattery\lang`；同名 code 的用户文件覆盖内置；占位符命名式（`{name}`）使翻译错误不会崩溃；RTL 影响菜单布局；`--list/--dump` 固定英文。`src/i18n.h:1-51`
- 价值判断：现为中文自用，不做；但“占位符不拼接、缺失回退”是值得借鉴的工程习惯。

---

## 6. 在 WinUI3 / .NET8 / C# 胶囊中复刻：风险与注意事项

### 6.1 现状盘点（[`PreviewTaskbarPill.cs`](../../src/DeviceCenterPreview.App/Services/Taskbar/PreviewTaskbarPill.cs)，776 行）

| 项 | 现状 | 位置 |
|---|---|---|
| 窗口 | `CreateWindowExW(WS_EX_LAYERED|WS_EX_TOOLWINDOW|WS_EX_NOACTIVATE, …, WS_POPUP)` 后 `SetParent(Shell_TrayWnd)` 再改 `WS_CHILD`（“三道锁”） | `:101-157` |
| 绘制 | GDI+ 画到 32bpp Bitmap → DIB → `UpdateLayeredWindow`；胶囊底板+边框+圆角 10、设备间竖线分隔、闪电路径 | `:261-429` |
| 定位 | 横排：`x = taskbar.Left + 235*scale`，clamp；竖向：托盘上方居中 | `:293-313` |
| 尺寸 | 高 35 DIP（min 28，且 ≤ 任务栏高−6）；宽 ≤ 任务栏 55%；pad 8、gap 6、图标 14、textGap 4、bolt 7 | `:263-292` |
| 内容 | 耳机（L/R 或总电量）、鼠标、遥控器，仅 connected；`·` 分隔；低电颜色、充电闪电 | `:228-259`、`:474-479` |
| 主题 | 读 `AppsUseLightTheme`（**应用**主题，非任务栏主题） | `:481-495` |
| 刷新 | 1s `DispatcherQueueTimer` → 先比 signature（items+任务栏矩形+scale）再重绘 | `:60-64`、`:206-226` |
| DPI/显示变化 | `WM_DPICHANGED`/`WM_DISPLAYCHANGE`/`WM_SETTINGCHANGE` 清签名并 `Update` | `:547-567` |
| 全屏 | `SHQueryUserNotificationState` + 前台窗口覆盖其显示器矩形 → `SW_HIDE` | `:502-545` |
| Explorer 重启 | 1s 检查任务栏窗口/父窗口，丢失则 `EnsureWindow`/`AttachToTaskbar`；无 `TaskbarCreated` 监听 | `:166-181` |
| 交互 | 左键/双击 → 打开主窗口；无 tooltip、无设备菜单 | `:547-556` |
| 生命周期 | 创建于 `OnLaunched`（主窗口 Activate 之后），失败只记日志；关闭主窗口不销毁胶囊 | [`App.xaml.cs`](../../src/DeviceCenterPreview.App/App.xaml.cs:90-111) |

### 6.2 差距清单（相对上游）

| 能力 | 上游 | 我们 | 影响 |
|---|---|---|---|
| 位置 | 托盘左侧 2px，自适应 | 固定 235 DIP | 托盘宽度/DPI/多语言/窄屏会错位或压住托盘 |
| 主题 | `SystemUsesLightTheme` | `AppsUseLightTheme` | 系统“应用深色、任务栏浅色”时配色错误 |
| 重建 | `TaskbarCreated` 广播 | 1s 轮询重挂 | 重启后最多 1s 才恢复；无广播时可能重建抖动 |
| 状态 | 低电/充电/充满/休眠/断开/未知/粗电量/连接方式 | 低电/充电 | 信息不完整 |
| tooltip | 原生 tooltip 逐设备 | 无 | 无法看到分量/状态详情 |
| 交互 | 左键设备菜单 + 右键设置 | 仅打开主窗口 | 功能发现性差（但改动风险也高） |
| 多设备视觉 | 图标+间距+可选分隔/角标/品牌色 | 竖线分隔 | 可接受，按我们视觉系统调整 |
| 多显示器 | 镜像 | 仅主屏 | 主屏全屏游戏时信息消失 |
| 兜底 | 任务栏不可用→托盘 | 无（胶囊失败只记日志） | 胶囊失败时用户无感知 |
| 空态 | 占位图标 | 隐藏 | 用户以为坏了 |
| 精细 | 位置偏移、make room、竖向/监视器/字号等设置 | 无 | 个人自用可接受 |

### 6.3 风险清单（按严重度）

| # | 风险 | 现象/原因 | 缓解 |
|---|---|---|---|
| 1 | **原生 HWND 与 WinUI 共存** | 胶囊是独立原生窗口，不参与 XAML 布局，本身是安全的；风险在于把 XAML 元素/`DispatcherQueue` 对象塞进原生渲染、或在窗口关闭后回调用已释放对象 | 胶囊保持“纯 Win32 + 自有状态快照”；渲染只读 `IPeripheralService` 的不可变快照；事件一律 `DispatcherQueue.TryEnqueue`；胶囊生命周期独立于主窗口（关闭到托盘不销毁），退出时统一 `Dispose` |
| 2 | **DPI** | 混合 DPI 下用错 DPI（用了主屏或 XAML DIP）；位图缩放导致糊；`SetParent` 之后的坐标换算 | 每次渲染取 `GetDpiForWindow(任务栏)`；所有尺寸 `MulDiv`/`Round`；确认应用清单 PerMonitorV2（WinUI3 默认）；`WM_DPICHANGED/WM_DISPLAYCHANGE` 清签名重绘；用截图在 100/150/200% 验收 |
| 3 | **主题用错注册表** | 我们读 `AppsUseLightTheme`，任务栏实际跟随 `SystemUsesLightTheme`；两者不同时胶囊与任务栏撞色 | 改读 `SystemUsesLightTheme`；监听 `WM_SETTINGCHANGE("ImmersiveColorSet")`；hover/边框/文字按明暗两套；tooltip 用 `SetWindowTheme("Explorer"/"DarkMode_Explorer")` |
| 4 | **Explorer 崩溃/重启** | 胶囊 HWND 随 `Shell_TrayWnd` 一起被销毁；旧窗口句柄失效；重挂期间可能反复创建 | 监听 `TaskbarCreated` 广播（**隐藏顶层窗口，不要 message-only**）；收到后 `DestroyWindow` 自己的胶囊、清签名、重建 + 重挂 tooltip；2–3s 冷却防抖动；保留 1s `IsWindow` 兜底；同时重挂 `TrayService` 图标 |
| 5 | **点击菜单：Win32 还是 WinUI** | WinUI `MenuFlyout/ContentDialog` 在“无 XAML 根/NOACTIVATE 原生窗口”上弹出不稳，容易抢焦点或落层错误；`crash-history` 也明确不鼓励弹层做交互 | v1 保持“点击开主窗口”；要设备菜单就用 **Win32 `CreatePopupMenu`+`TrackPopupMenu`**（先 `SetForegroundWindow` 到隐藏 owner，菜单关闭后 `PostMessage(WM_NULL)`，处理 `WM_ACTIVATEAPP`），或自己画一个原生弹出层；**不要**用 `Flyout`/`ContentDialog` |
| 6 | **字体缺失/被替换** | `Segoe Fluent Icons` 仅 Win11；GDI/GDI+ 对不存在字体会静默替换成别的字形（方框/错字） | 首选系统 `Segoe Fluent Icons`，Win10 回退 `Segoe MDL2 Assets`；创建字体后用 `GetTextFace` 校验实际字体名（上游做法）；不要依赖外部 OTF；如需跨版本一致可考虑 `Segoe Fluent Icons` + 内置最小图标字体（Apache-2.0），但会增加再分发合规成本 |
| 7 | **GPL-3.0 许可证** | 上游是 GPL；复制代码/注释/字符串会让我们的应用被 GPL 传染（尤其实在闭源分发时） | 本文档即 clean-room 记录：只依据行为与本文重新实现；不得打开上游源码“边看边写”；不复制菜单英文文案（用我们自己的中文）；品牌色/注册表键名/窗口类名属于事实可独立定义；实现评审时检查相似度 |
| 8 | **任务栏几何脆弱** | 第三方美化（StartAllBack、ExplorerPatcher、Windhawk）会改 `TrayNotifyWnd` 位置/可见性；Win11 版本迭代会改 XAML 结构；`TrayNotifyWnd` 可能不存在 | 优先使用真实子窗口矩形；任何一步异常 → 隐藏/回退固定位置（当前 235 DIP 作为 fallback）；不假设类名/顺序；记录一次日志；在新系统版本上做冒烟测试 |
| 9 | **Win10 支持** | 我们没有 ReBar 缩窄能力，Win10 上胶囊可能压住任务按钮；Win10 时钟子窗口/资讯类名又不同 | 若只支持 Win11：在 Win10 上直接回退托盘模式（用 RtlGetVersion 判断 build<22000）；若要支持 Win10，需要单独评审 ReBar 方案 |
| 10 | **自动隐藏 / 全屏游戏** | 我们是任务栏子窗口，自动隐藏天然跟随；但当前“检测全屏后 `SW_HIDE`”与任务栏自动行为叠加，可能出现“任务栏可见但胶囊不回来” | 保留全屏隐藏作为兜底，但恢复时强制清签名重绘；每轮先判断任务栏是否可见（`IsWindowVisible`）；主屏全屏时如需可见，只能做多显示器镜像（§5 #18） |
| 11 | **性能与句柄泄漏** | 1s 轮询每次都建对象/字符串（当前 `BuildItems`+signature 每秒分配）；GDI/GDI+ 资源若异常路径未释放会泄漏，长时间运行后 UI 卡顿 | 先比“任务栏矩形 + 可见性 + 内容摘要”再决定是否 `BuildItems`；字体/笔刷/位图全部 `using`/`Dispose`；压测 1 小时看 GDI 对象数与 CPU；内容不变时不重绘 |
| 12 | **多显示器** | 当前只挂主任务栏；主屏全屏时信息消失；副屏任务栏没有胶囊 | 可选后置：为每个 `Shell_SecondaryTrayWnd` 建镜像（同 §2.5）；每屏独立 DPI/位置；v1 不做不阻塞 |
| 13 | **线程模型** | 服务 `Changed` 可能来自后台线程；在后台线程碰 HWND 会崩或死锁 | 保持 `TryEnqueue` 回 UI；胶囊 `Update` 只在 UI 线程；不要在轮询线程里读/写窗口属性 |
| 14 | **测试/回归** | 任务栏相关行为很难自动化；每次改都可能引入“看不见/错位/不回来” | 建立手工验收清单（§6.6）：DPI 3 档、主题切换、explorer 重启、自动隐藏、全屏、关窗到托盘、退出重开；截图基线入库 |

### 6.4 推荐架构（对齐现有约束）

```
IPeripheralService（已有，事件 + 快照）
      │  Changed（任意线程）
      ▼
TaskbarStripState（纯数据：设备列表 + 状态 + 主题 + 布局参数，不可变）
      │  DispatcherQueue.TryEnqueue
      ▼
NativeTaskbarStrip（新，替代/演进 PreviewTaskbarPill）
  ├─ NativeWindowHost：注册类、child-of-taskbar、layered、UpdateLayeredWindow
  ├─ StripLayout：DIP 计算（复用现有 Render 逻辑）
  ├─ StripRenderer：GDI+（保留现有胶囊底板风格）
  ├─ GeometryTracker：托盘锚定 + 回退 + 签名缓存
  ├─ ThemeWatcher：SystemUsesLightTheme + WM_SETTINGCHANGE
  ├─ TaskbarRestartWatcher：隐藏顶层窗口 + TaskbarCreated（或子类化主窗口）
  └─ NativeTooltip（TOOLTIPS_CLASSW）
```

原则：**胶囊不依赖 XAML、不依赖主窗口存活、不参与应用主题切换**；它只消费一个不可变状态快照。这样即使主窗口隐藏/重开，胶囊仍稳定；也符合 [design-system.md](../ui/design-system.md:106-113) 的稳定性约束。

### 6.5 分阶段实施步骤（可直接排期）

| 阶段 | 内容 | 交付物 | 预估 |
|---|---|---|---|
| P0-1 | 托盘锚定 + 回退 + 签名缓存 + 先比后建 | 胶囊永远在通知区域左侧 2px；无托盘时回退 235 DIP；1s tick 不再分配 | 0.5–1 天 |
| P0-2 | 主题修正（`SystemUsesLightTheme` + 设置变更监听 + tooltip 主题） | 系统任务栏浅色/深色正确 | 0.5 天 |
| P0-3 | `TaskbarCreated` 重建 + 托盘图标重挂 + 冷却 | 杀 explorer 后 ≤2s 恢复、无重复窗口 | 1 天 |
| P0-4 | 状态语义 + 无设备占位 + tooltip | 低电红/充电蓝闪/充满绿勾/休眠变暗/未知 `?`/逐设备 tooltip | 1–1.5 天 |
| P1-1 | 唤醒/插拔补轮询（复用现有服务事件） | 唤醒后 3/5/10/15s 补读，插拔立即刷新 | 0.5–1 天 |
| P1-2 | 每设备低电阈值 + 闩锁规则 | 设置/存储/通知改造 | 1 天 |
| P1-3 | 点击设备菜单（Win32 TrackPopupMenu，先做“打开/隐藏设备”“刷新”“打开主窗口”） | 原生菜单，不抢焦点 | 1–2 天 |
| P1-4 | 托盘兜底模式（胶囊失败/竖向/用户选择） | 每设备数字图标 + tooltip | 1–2 天 |
| P2 | 历史 + 运行时估算、`--dump`/`--list`、多显示器镜像、make room | 按需 | 各 1–3 天 |

### 6.6 验收清单（每阶段跑一遍）

- [ ] 100% / 150% / 200% DPI：胶囊在通知区域左侧 2 DIP，垂直居中，无模糊、无错位。
- [ ] 系统主题浅色/深色切换：文字/底板/边框/hover/tooltip 立即变化，浅色下文字对比度 ≥ 4.5:1。
- [ ] 任务栏自动隐藏开/关：胶囊跟随滑动，不残留幽灵窗。
- [ ] `taskkill /f /im explorer.exe` 后自动恢复：≤2s 可见、无重复胶囊、托盘图标仍在、可再次点击。
- [ ] 主窗口关闭到托盘：胶囊与托盘均可用；再次启动 exe 激活旧实例，不产生第二个胶囊。
- [ ] 全屏游戏：不闪、不抢焦点；退出全屏后胶囊回来。
- [ ] 拔掉/插上接收器、蓝牙断开/重连：状态与 tooltip 正确，不显示过期电量为“实时”。
- [ ] 空设备/单设备/多设备：宽度正确、不压住时钟/托盘/其它小组件。
- [ ] 运行 1 小时：CPU 空闲 <1%、GDI 对象数稳定、无异常日志增长。
- [ ] 视觉截图基线（100%/200% 各一张）入库，后续改动用同脚本对比。

### 6.7 设计取舍建议（视觉）

1. **保留现有胶囊底板**（圆角 10、半透明底、1px 边框、竖线分隔）作为「外设中枢」的任务栏身份；上游的“透明文本条”是它的默认，不是唯一正确答案。
2. **但几何/字形向规格对齐**：字号 13、图标 16、图标↔文字 5、设备间距 14、左右 8（DIP）；当前我们的 35px 高胶囊内部偏挤，建议按“文字 13 + 图标 16 + 上下留白 8”重排。
3. **位置改为托盘锚定**（§6.5 P0-1），保留 235 DIP 仅作为托盘不可用时的回退；文档同步更新 [taskbar-tray.md](../features/taskbar-tray.md)。
4. **颜色使用我们的令牌**：前景/背景用 [design-system.md](../ui/design-system.md:22-35) 的画布/主文字；设备色用耳机 `#5B4BC4`、鼠标 `#A66B22`、遥控器 `#1F6F8C`（明暗两套已有），不要引入上游 17 品牌色表。
5. **状态双编码**（颜色 + 符号），遵循设计系统“状态永远颜色+文字双编码”：低电红 + 红色数字，充电蓝闪电，充满绿勾，休眠降透明度，未知 `?`。

---

## 7. 必做 / 可选 / 不做（最终建议）

### 7.1 必做（P0，先做这些）

1. **真实托盘锚定 + 回退**（`TrayNotifyWnd.Left − 宽 − 2px`；异常回退 235 DIP；竖向任务栏回退/隐藏）。
2. **主题正确性**：`SystemUsesLightTheme` + `WM_SETTINGCHANGE("ImmersiveColorSet")`；tooltip 主题；浅色对比度验收。
3. **Explorer 重启重建**：`TaskbarCreated` 广播（隐藏顶层窗口/子类化主窗口）→ 销毁重建 + 重挂 tooltip/托盘图标 + 冷却。
4. **状态语义**：低电 ≤15% 红、充电蓝闪电、充满绿勾、休眠 alpha 120/255、未知 `?`、无设备占位；断开 × 作为可选项（我们目前数据模型未必能区分“明确离线”与“本轮没读到”，需服务层提供信号）。
5. **tooltip**：原生 `TOOLTIPS_CLASSW`，逐设备一行 + 状态；文本不变不更新。
6. **重绘门控 + 资源管理**：签名含 items/任务栏矩形/托盘矩形/主题/DPI/可见性；先算便宜量；GDI+ 资源全部释放；1s tick 零分配。
7. **刷新策略**：唤醒/插拔后延迟补轮询 + 现有 15s 定时刷新基础上立即刷新。

### 7.2 可选（按收益排序）

1. 每设备低电阈值 + 提醒闩锁（S，高收益）。
2. 点击设备菜单（Win32 弹出菜单，S–M，中高收益；先做最小项）。
3. 托盘模式兜底（M，中高收益，提高健壮性）。
4. 电池历史 + 运行时估算（M，中高收益；触 Core 数据层）。
5. `--dump` / `--list` 诊断 CLI（S，中收益，方便支持新设备）。
6. 无设备占位（S，中收益）——也可并入必做 4。
7. 多显示器镜像（L，中收益，游戏用户）。
8. 平滑充电读数、设备重命名、设备归档、内存整理（S，小众但便宜）。
9. make room / 自定义位置偏移（M，低收益）。

### 7.3 不做

- 复制/移植 GPL 代码、注释、菜单英文文案；自绘 Win11 弹出菜单（1792 行，风险收益比差）。
- 嵌入 Material Icons 私有字体（系统 `Segoe Fluent Icons` 足够；Apache-2.0 字体也要承担再分发合规成本）。
- Win10 ReBar 缩窄、竖向任务栏列布局、RTL、更新签名系统、支持请求上报窗口、Linux、20 语言。
- 17 品牌色全表（与 Bench 设备色令牌冲突，视觉不统一）。
- 把 XAML 内容放进胶囊 / 用 WinUI Flyout 做胶囊菜单。

### 7.4 需要 lead 决策的 5 个问题

1. **视觉方向**：保留“胶囊底板”还是改上游式“透明条 + 可选背景板”？（决定设计文档与截图基线）
2. **位置策略**：真实托盘锚定（推荐）vs 保持固定 235 DIP（NICEHCK 兼容）；我建议“锚定 + 托盘缺失时回退 235”。
3. **交互**：仅“点击开主窗口” vs 增加 Win32 设备菜单/设置菜单（后者要新建原生弹层，风险中等）。
4. **多显示器**：v1 只主屏（推荐）vs 一开始就做镜像（工作量 L）。
5. **历史/阈值**：是否本迭代引入电池历史与每设备阈值（触及 `SettingsStore` 与通知服务）。

---

## 8. 证据索引（上游 `src/` 行号）

| 主题 | 位置 | 说明 |
|---|---|---|
| 胶囊职责/多显示器说明 | `widget.h:2-4` | 设计意图（改写引用） |
| WidgetItem 状态字段 | `widget.h:14-27` | percent/text/charging/full/dim/low/offline/bluetooth/link/label/accent |
| 文本样式默认值 | `widget.h:32-43` | 13/400/16/5/14/6/0，DIP |
| 窗口类/样式 | `widget.cpp:58`、`1353-1363` | class、`WS_EX_LAYERED|NOACTIVATE`、`WS_CHILD|CLIPSIBLINGS` |
| DPI 与缩放 | `widget.cpp:61-68` | `GetDpiForWindow`、`MulDiv`、小数 round |
| 异常托盘坐标校正 | `widget.cpp:70-88` | 回退算法 + 单测样例 |
| 背景取色/预留底部 | `widget.cpp:90-130` | `ReserveBottom`、`SampleBarColor` |
| UIA 线程与跨进程死锁 | `widget.cpp:132-146`、`340-399` | 独立线程 1.5s/5s；Widgets/时钟/任务按钮测量 |
| Span/Column 测量结构 | `widget.cpp:153-179`、`279-338` | UIA 矩形缓存 |
| Win11 XAML 判定 | `widget.cpp:275-277` | `DesktopWindowContentBridge` |
| 远程窗口/最左端定位 | `widget.cpp:424-458` | Widgets 按钮、应用按钮前 |
| tooltip 主题 | `widget.cpp:460-469` | `SetWindowTheme` |
| 字体回退校验 | `widget.cpp:475-491` | `GetTextFaceW` |
| 样式尺寸换算 | `widget.cpp:493-510` | TextPx/IconPx/GapPx/Divider |
| 文本遮罩/字距 | `widget.cpp:512-576` | 字形覆盖 → alpha；逐字符 sub-pixel |
| 合成/光晕/圆环 | `widget.cpp:578-676` | source-over、box blur、3×3 超采样圆环 |
| 三种布局 | `widget.cpp:678-881` | 行/堆叠/竖向；分隔线；品牌条 |
| 整条绘制/更新 | `widget.cpp:883-999` | alpha 1 命中、背景板、hover、`UpdateLayeredWindow` |
| 镜像定位 | `widget.cpp:1018-1147` | 托盘/时钟/UIA、RTL、隐藏兜底 |
| 镜像增删/监视器身份 | `widget.cpp:1178-1283` | `Shell_SecondaryTrayWnd`、DeviceID/型号匹配 |
| WndProc | `widget.cpp:1307-1328` | hover、左右键、`MA_NOACTIVATE` |
| 创建/销毁/更新 | `widget.cpp:1336-1431` | 创建流程、tooltip、`SetItems` |
| 设置 setter | `widget.cpp:1433-1550` | 主题/背景/样式/监视器/竖向 |
| 其它挂件让位 | `widget.cpp:1552-1592` | 8 轮、±16px、topmost、≤1/3 长度 |
| 主条定位全流程 | `widget.cpp:1594-1758` | 横/竖、偏移、Win10 ReBar、去抖 |
| 设置键名/默认值 | `main.cpp:81-118`、`336-465` | `kSettingsKey`、轮询、位置偏移 |
| 主题检测 | `main.cpp:179-185` | `SystemUsesLightTheme` |
| 设备归档/别名/隐藏/排序 | `main.cpp:204-265`、`354-381` | MULTI_SZ 存储 |
| 状态文本/变暗/隐藏 | `main.cpp:267-282`、`614-628` | StatusText/ShouldDim |
| 提醒规则接入 | `main.cpp:295-336`、`872-884` | 闩锁、历史、充电上限 |
| 托盘图标渲染 | `main.cpp:486-605` | 电量条字形、颜色、DPI |
| 托盘管理/兜底 | `main.cpp:607-741` | NIM 回退、占位、通知 |
| 排序/同类角标输入 | `main.cpp:743-775`、`labels.cpp:65-100` | 类型序、短标签算法 |
| LinkKind 判定 | `main.cpp:777-789` | via 字符串分类 |
| WidgetItem 组装 | `main.cpp:791-822` | 离线/充满/变暗/低电/tooltip |
| 平滑充电 | `main.cpp:826-850` | 充电时沿用充电前读数 |
| 轮询结果处理 | `main.cpp:852-926` | 归档/提醒/历史/grace/离线标记 |
| 模式切换/失败兜底 | `main.cpp:928-966` | 胶囊→托盘→每秒重试 |
| 菜单结构 | `main.cpp:1224-1624` | 设备子菜单、设置分组、两菜单拆分、弹窗调用 |
| 轮询 worker | `main.cpp:1842-1862` | 刷新策略 + 事件唤醒 |
| Explorer 重启处理 | `main.cpp:1868-1876` | `TaskbarCreated` |
| 设备/电源通知 | `main.cpp:1897-1947` | HID/BTH/HCI/电源/显示器 |
| 设置变更/主题 | `main.cpp:1962-1967` | `ImmersiveColorSet` |
| CLI/启动 | `main.cpp:1989-2207` | list/json/dump/demo/quit；mutex；隐藏顶层窗口 |
| 品牌色表/匹配 | `brands.cpp:7-169` | 17 品牌、优先级 |
| 图标字体/字形 | `icon_font.cpp:8-68`、`icon_font.h:18-24` | Material + Segoe 回退 |
| 文字样式编辑器 | `text_style_dialog.cpp:17-180`、`text_style_dialog.h:8-15` | 字段/范围/校验 |
| 历史模型 | `history_model.h:14-199` | 编码/保留/采样/统计 |
| 历史存储/查看器 | `battery_history.cpp:33-109`、`554-607` | 路径/原子写/采样/窗口 |
| 运行时估算 | `runtime_estimate.h:23-74` | 保守估算状态机 |
| 提醒规则 | `alert_rules.h:10-63` | 阈值/闩锁/充电上限 |
| 刷新策略 | `refresh_policy.h:18-100` | 登录/唤醒/到达补轮询 |
| 多语言 | `i18n.h:1-51`、`i18n.cpp:203-219` | 文件格式/存储/RTL |
| 内存整理 | `memory_trim.h:9-22` | 工作集归还 |
| 全局行为描述 | `README.md:33-63`、`86-93`、`290-322`、`324-354`、`425-462` | 显示/历史/更新/CLI/限制/多语言 |

## 9. 附录

### 9.1 上游源码地图

| 文件 | 行数 | 职责 |
|---|---|---|
| `src/main.cpp` | 2208 | 隐藏消息窗、轮询 worker、托盘、菜单、设置读写、CLI、生命周期 |
| `src/widget.cpp` | 1760 | 任务栏胶囊/镜像：定位、绘制、DPI、主题、UIA 测量、重定位 |
| `src/widget.h` | 100 | 胶囊对外接口与视觉参数 |
| `src/popup_menu.cpp` | 1792 | 自绘 Win11 风格弹出菜单（含系统菜单回退） |
| `src/battery_history.cpp` | 609 | 历史存储 + 查看器窗口 |
| `src/history_model.h` | 203 | 历史记录/序列化/保留上限 |
| `src/runtime_estimate.h` | 76 | 剩余时间保守估算 |
| `src/alert_rules.h` | 64 | 低电/充电上限提醒规则（纯函数） |
| `src/refresh_policy.h` | 102 | 补轮询状态机（纯 C++，可测） |
| `src/brands.cpp` | 169 | 17 品牌色 + 识别规则 |
| `src/icon_font.cpp/.h` | 70/30 | Material Icons 嵌入 + Segoe 回退 |
| `src/labels.cpp/.h` | 100/— | 同类设备短标签（≤3 字符） |
| `src/text_style_dialog.cpp/.h` | 321/— | 文字样式编辑对话框 |
| `src/devices.cpp/.h` | 176/30 | 设备枚举与 `DeviceStatus` |
| `src/report.cpp/.h` | 770/24 | “请求支持设备”窗口 + `--dump` 附加信息 |
| `src/i18n.cpp/.h` | —/51 | 语言文件加载与翻译 |
| `src/known_devices.*`、`device_aliases.h` | — | 设备归档、重命名存储 |
| `src/memory_trim.h` | 24 | 工作集归还 |
| `res/MaterialIconsOutlined-Regular.otf` | 4.5 KB | 私有图标字体（Apache-2.0） |
| `docs/screenshot.png` | 500×96 | 任务栏效果截图（本报告已实测） |

### 9.2 调研限制

- 未在真机运行上游二进制（避免与本机外设/驱动相互影响）；所有结论来自源码、文档与截图像素测量。
- 未覆盖各厂商 HID 协议；`docs/*.md` 中的设备协议文档（Razer/Logitech/Keychron 等）不在本次范围。
- Win11 具体 build 的 XAML 结构差异未逐一实测；§2.13 的差异表基于源码分支逻辑与注释，实施时需在目标机验证。
- README 有一处与代码不一致：README 称“竖向任务栏或任务栏找不到时自动切托盘图标”，但代码实际支持竖向列布局（`widget.cpp:1618-1680`），只有创建失败 `Reconcile` 才走托盘兜底。实现时以代码为准。
- 上游版本：仓库为改名后的 fork，commit `0c1adbe5`（2026-11-26）；README 提到的 1.1.0 版本号取自仓库文档，`build/version.h` 为构建期生成。

### 9.3 参考

- 上游仓库（只读来源）：`https://github.com/lo0kie/Wireless-Device-Battery-Monitor`（fork 自 `krisgfx9/Wireless-Device-Battery-Monitor`），GPL-3.0。
- Material Icons：Google，Apache-2.0（字形码点表可独立获取）。
- 本项目相关文档：[任务栏胶囊/托盘](../features/taskbar-tray.md)、[视觉系统](../ui/design-system.md)、[稳定性崩溃史](../stability/crash-history.md)、[决策记录](../decisions.md)。
