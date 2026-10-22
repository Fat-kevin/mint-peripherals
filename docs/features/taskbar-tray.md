# 功能：任务栏胶囊 / 系统托盘

## 任务栏胶囊

文件：`Services/Taskbar/PreviewTaskbarPill.cs`

- 作为 `Shell_TrayWnd` 的 layered child window，用 `UpdateLayeredWindow` + GDI+ 绘制。
- 位置：任务栏左下角，左边缘向右 235 DIP（与 NICEHCK 耳机程序一致）；横向任务栏。
- 只显示**已连接**设备：
  - 耳机：`L xx%  R xx%`（有分量时）或总电量；
  - 鼠标：`xx%`；
  - 遥控器：`xx%`；
  - 用 `·` 分隔；充电时显示闪电。
- 可读性：高度 35px、字号 11px 粗体、图标 13px、内边距 8px。
- 点击胶囊打开主窗口。
- DPI/显示变化会重绘；无设备时隐藏。

### 全屏隐藏

`IsFullscreenForeground()`：

1. 取 `GetForegroundWindow()`；排除自身进程、最小化窗口、任务栏/桌面类
   （`Shell_TrayWnd` / `Progman` / `WorkerW`）。
2. `SHQueryUserNotificationState` 返回 `QUNS_BUSY(2)` / `QUNS_RUNNING_D3D_FULL_SCREEN(3)` /
   `QUNS_PRESENTATION_MODE(4)` / `QUNS_APP(7)` → 全屏。
3. 兜底：前台窗口矩形覆盖其显示器 `rcMonitor`（±2px）。
4. 全屏时 `ShowWindow(SW_HIDE)`；退出全屏后清空签名并重绘恢复。

> 普通最大化窗口不会隐藏（任务栏仍可见，窗口矩形不覆盖整个显示器）。

## 系统托盘

文件：`Services/TrayService.cs`

- `Shell_NotifyIcon` 托盘图标；使用 `Assets/PeripheralHub.ico`。
- 单击/双击打开主窗口；右键菜单“打开外设中枢 / 退出外设中枢”。
- 运行期间常驻。
- **关闭主窗口（X / Alt+F4）只隐藏窗口**，进程、托盘图标、任务栏胶囊继续运行；
  只有托盘右键“退出”才真正结束进程。
- 关闭后首次会弹一次托盘气泡，提示仍在后台运行、可双击重新打开。
- 托盘图标加载失败时回退系统默认图标。

## 相关约束

- 任务栏胶囊是独立原生窗口，不参与 XAML 布局；不要把它和页面 resize 绑定。
- 全屏判断每秒执行一次（主窗口计时器），开销可忽略。
- 若任务栏重启（Explorer 崩溃），`Update()` 会重新 `AttachToTaskbar`。
- 隐藏主窗口时不要销毁 `MainWindow`，否则托盘/胶囊回调会失去目标；实际退出由
  `App.ExitApplication()` 设置 `IsExiting` 后关闭窗口。

## 验证记录

- 无边框全屏黑窗口覆盖显示器：胶囊 `visible=False`；关闭后 `visible=True`。
- 关闭主窗口后：进程仍存活、主窗口 `visible=False`、胶囊仍 `visible=True`；
  再次启动同一 exe 会激活已有实例并重新显示主窗口。
- 1080p 下 35px/11px 版本作为可读性与体积的折中。
