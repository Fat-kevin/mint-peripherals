# 功能：任务栏内嵌电量条 / 系统托盘

## 任务栏内嵌电量条

文件：`Services/Taskbar/TaskbarBatteryStrip.cs`（替代旧 `PreviewTaskbarPill.cs`，旧胶囊已删除）

形态：通知区域（`TrayNotifyWnd`）左侧 2px 的**透明内嵌条**，只画图标与百分比，
不再有胶囊底板、圆角外框和竖线分隔；可选低透明度背景板。

- **窗口**：沿用已跑通的三道锁——隐藏顶层宿主窗口 + `Shell_TrayWnd` 子窗口
  （`SetParent` → `WS_CHILD|WS_CLIPSIBLINGS` → `WS_EX_LAYERED|WS_EX_NOACTIVATE` → `UpdateLayeredWindow`）。
  全条用 alpha=1 近透明底填充，保证整条可点；GDI+ 32bpp 绘制到 DIB。
- **定位**（v1.2.0 起）：优先放在**任务栏左侧空位**——`Start`/`ReBarWindow32` 左缘（Windows 11
  居中任务栏时任务按钮区的起点）再往左，预留小组件（天气/Widgets）约 170 DIP 后右对齐；
  这样不会覆盖固定/运行中的任务按钮。左侧放不下时回退 `TrayNotifyWnd.left − 条宽 − 2px`，
  再回退 `ClockButton.left` / 任务栏左缘 + 235 DIP。`x` clamp 在任务栏左缘内；
  竖向任务栏、几何异常、全屏前台、任务栏不可见时隐藏。`y` = 任务栏顶（整条透明、内容垂直居中）。
- **位置设置**：设置 → 外观 → 任务栏显示 →「位置」：自动（推荐，优先任务栏左侧空位）/ **左下角**（固定在原胶囊位置，距任务栏左缘 235 DIP）/ 通知区域左侧 / 任务栏左侧。
  自动模式在任务栏较空时仍优先左侧空位，避免覆盖任务按钮；拥挤任务栏也不会压图标。左下角模式右侧到任务按钮/托盘之间放不下时会回退左侧空位，仍放不下则隐藏（不覆盖任务按钮）。
- **DPI**：`GetDpiForWindow(Shell_TrayWnd)`；所有 DIP 尺寸 `MulDiv/Round` 缩放。
- **内容**：消费 `IPeripheralService.GetBatteryDevices()`；耳机若带双耳独立电量（SPP `BatteryInfo` 的
  `LeftEarPercent`/`RightEarPercent`），显示为 `左 70% 右 70%`，只有系统聚合电量时仍显示单值；顺序
  耳机 → 鼠标 → 遥控器 → 键盘/手柄/音箱/其他；最多 6 项，放不下或超出显示 `+N`；
  过滤离线设备；休眠设备由设置决定是否显示；空列表隐藏条。
- **视觉**（DIP）：
  - 文字 13（设置小 12 / 中 13 / 大 15），字体 `Segoe UI Variable Text` → `Segoe UI`；
  - 图标 16，图标↔文字 5，设备间距 14，左右边距 8；
  - 图标用系统 `Segoe Fluent Icons`（Win11）→ `Segoe MDL2 Assets`，创建字体后用
    `GetTextFaceW` 校验实际字体（GDI 对缺失字体会静默替换出方框）；
  - 设备色取 Bench 令牌的深/浅任务栏变体：耳机 `#8F82E8/#5B4BC4`、鼠标 `#D9A05B/#A66B22`、
    遥控器 `#5FB3D1/#1F6F8C`、其他 `#9AA3AD/#6B7380`；
  - 文字/背景走任务栏主题：浅色前景 `#181818`、深色 `#F5F5F5`；
  - 背景板（默认关）：内缩 4、圆角 6、alpha 240（浅 `#F3F3F3` / 深 `#202020`）。
- **状态**（颜色 + 符号双编码）：
  - 低电 ≤15%（且未充电）→ 红（浅 `#C41E28` / 深 `#FF5A5A`）；
  - 充电 → 蓝色闪电 `E945`（浅 `#0078D4` / 深 `#60CDFF`）；
  - 充满（`Full` 或 100% 且插电）→ 绿色对勾 `E73E`（浅 `#107C10` / 深 `#6CCB5F`）；
  - 休眠旧值 → 整条设备项 alpha 120（图标/文字/符号一起变暗）；
  - 未知电量 → `?`；仅图标样式下未知也画 `?`，避免状态完全丢失。
- **主题**：读 `HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize\SystemUsesLightTheme`
  （任务栏主题，不是 `AppsUseLightTheme`）；隐藏宿主窗口收 `WM_SETTINGCHANGE(ImmersiveColorSet)`
  与 `WM_DISPLAYCHANGE`/`WM_DPICHANGED` 后清签名重绘，并同步 tooltip 的
  `SetWindowTheme("Explorer" / "DarkMode_Explorer")`。
- **重建**：宿主是隐藏**顶层**窗口（不是 message-only），`RegisterWindowMessage("TaskbarCreated")`
  广播到达后销毁条 + tooltip 再重建（2.5s 冷却防抖）；保留 1s `IsWindow`/父窗口检查兜底。
- **tooltip**：原生 `TOOLTIPS_CLASSW`（`TTF_IDISHWND|TTF_SUBCLASS`，max tip width 600），
  逐设备一行：`名称 电量% · 充电中/已充满/休眠/未连接（左 xx% / 右 xx%）`；文本不变不更新。
- **交互**：左键/双击打开主窗口；右键原生 `CreatePopupMenu/TrackPopupMenu`（`TPM_RETURNCMD`，
  先 `SetForegroundWindow`，菜单后 `PostMessage(WM_NULL)`）四项：
  打开外设中枢 / 立即刷新 / 任务栏显示设置 / 退出。
- **性能**：1s tick 先算便宜签名（任务栏矩形、锚点、可见性、主题、DPI、设置、设备摘要），
  不变时零重绘、不 `SetWindowPos`、不建 GDI+ 对象；字体/画笔/位图全部 `using`。
- **生命周期**：不依赖 XAML/主窗口存活；关闭到托盘后条继续工作；退出时 `Dispose`。

### 设置（设置 → 外观 → 任务栏显示）

持久化在 `AppSettings`（缺省即默认值，兼容旧配置）：

| 设置 | 字段 | 默认 |
|---|---|---|
| 任务栏电量条开关 | `TaskbarStripEnabled` | 开 |
| 显示样式 | `TaskbarStripStyle`：`icon+percent` / `percent` / `icon` | `icon+percent` |
| 字号 | `TaskbarStripFontSize`：`small` / `medium` / `large`（12/13/15 DIP） | `medium` |
| 背景板 | `TaskbarStripBackground` | 关 |
| 显示休眠设备 | `TaskbarStripShowSleeping` | 开 |
| 显示其他设备 | `ShowOtherBatteryDevices` | 开 |

设置页改动即时落盘并经 `App.UpdateSettings` → `TaskbarBatteryStrip.ApplySettings()` 立即重绘。

### 全屏隐藏

`IsFullscreenForeground()`：

1. 取 `GetForegroundWindow()`；排除自身进程、最小化窗口、任务栏/桌面类
   （`Shell_TrayWnd` / `Progman` / `WorkerW`）。
2. `SHQueryUserNotificationState` 返回 `QUNS_BUSY(2)` / `QUNS_RUNNING_D3D_FULL_SCREEN(3)` /
   `QUNS_PRESENTATION_MODE(4)` / `QUNS_APP(7)` → 全屏。
3. 兜底：前台窗口矩形覆盖其显示器 `rcMonitor`（±2px）。
4. 全屏时 `ShowWindow(SW_HIDE)` 并清签名；退出全屏后下一 tick 重绘恢复。

> 普通最大化窗口不会隐藏（任务栏仍可见，窗口矩形不覆盖整个显示器）。

## 系统托盘

文件：`Services/TrayService.cs`

- `Shell_NotifyIcon` 托盘图标；使用 `Assets/PeripheralHub.ico`。
- 单击/双击打开主窗口；右键菜单“打开外设中枢 / 退出外设中枢”。
- 运行期间常驻。
- **关闭主窗口（X / Alt+F4）只隐藏窗口**，进程、托盘图标、任务栏条继续运行；
  只有托盘右键“退出”才真正结束进程。
- 关闭后首次会弹一次托盘气泡，提示仍在后台运行、可双击重新打开。
- 托盘图标加载失败时回退系统默认图标。

## 相关约束

- 任务栏条是独立原生窗口，不参与 XAML 布局；不要把它和页面 resize 绑定。
- 全屏判断每秒执行一次，开销可忽略；任务栏条自身 1s tick 只做签名比较。
- Explorer 重启：`TaskbarCreated` 广播触发重建，1s `IsWindow` 检查兜底。
- 隐藏主窗口时不要销毁 `MainWindow`，否则托盘/任务栏条回调会失去目标；实际退出由
  `App.ExitApplication()` 设置 `IsExiting` 后关闭窗口。
- 不要用 WinUI `Flyout`/`ContentDialog` 做任务栏条菜单（无 XAML 根、NOACTIVATE 原生窗口上不稳）；
  一律 Win32 `TrackPopupMenu`。

## 验证记录

- （旧胶囊）无边框全屏黑窗口覆盖显示器：条 `visible=False`；关闭后 `visible=True`。
- （旧胶囊）关闭主窗口后：进程仍存活、主窗口 `visible=False`、条仍 `visible=True`；
  再次启动同一 exe 会激活已有实例并重新显示主窗口。
- 新版内嵌条的真机截图（100/150/200% DPI、浅/深主题、Explorer 重启、关窗到托盘）
  由集成验证统一补录；本迭代只做 x64 Debug 构建验收。
