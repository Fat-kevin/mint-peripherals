# BatteryHub 吸收实施计划（迭代 1.2.0）

> 依据：`2026-11-27-batteryhub-design.md`；调研：`2026-11-27-batteryhub-ui-research.md`、`2026-11-27-batteryhub-device-research.md`
> 本迭代范围：P0 任务栏内嵌显示（替换胶囊）+ P0 设备阶段 1（通用 Windows 电池聚合）；P1/P2 留 roadmap。

## 1. 交付物

1. 任务栏：胶囊 → BatteryHub 式透明内嵌条（图标+百分比、托盘锚定、主题、状态、tooltip、右键原生菜单、TaskbarCreated 重建、设置页显示选项）；
2. 设备：新增 `BatteryDeviceSnapshot` 契约；任务栏与总览「其他设备」显示通用 Windows 电池设备（键盘/手柄/音箱/第二只鼠标等）+ Generic HID Battery Strength + Xbox WGI；
3. 版本 1.2.0：应用 + 安装包 + 发布说明 + 文档同步；
4. 测试：现有 Core 测试通过；新增 watcher 的纯逻辑单测（如有）；实机截图验收（任务栏 100/200% DPI、明暗主题、空态/多设备、其他设备出现）。

## 2. 工作分解

### WP0（lead，前置契约；其他任务依赖它）

| 项 | 文件 | 内容 |
|---|---|---|
| 数据模型 | `src/DeviceCenterPreview.Core/Models/PeripheralModels.cs` | 新增 `BatteryDeviceKind`、`BatteryDeviceSnapshot`（Kind/Id/Name/Percent/Charging/IsConnected/IsAsleep/IsOffline/ConnectionType/Components/Source） |
| 服务契约 | `src/DeviceCenterPreview.Core/Services/IPeripheralService.cs` | 新增 `IReadOnlyList<BatteryDeviceSnapshot> GetBatteryDevices();` |
| 桩实现 | `MockPeripheralService.cs`、`UnavailablePeripheralService.cs` | Mock 映射现有三类；Unavailable 返回空 |
| 真实服务 | `Services/RealPeripheralService.cs` | 先实现“现有三类 → BatteryDeviceSnapshot”的映射（`Source="vigilwatt"`）；通用设备聚合由 WP2 扩展 |
| 设置字段 | `src/DeviceCenterPreview.Core/Settings/AppSettings.cs` | `TaskbarStripEnabled/ TaskbarStripStyle(icon+percent/percent/icon)/ TaskbarStripFontSize(small/medium/large)/ TaskbarStripBackground/ TaskbarStripShowSleeping/ ShowOtherBatteryDevices`；Normalize 收敛 |
| 构建 | — | `dotnet build` 通过后，WP1/WP2 才可并行开始 |

### WP1（子代理 A：任务栏内嵌显示；写作用域）

- `src/DeviceCenterPreview.App/Services/Taskbar/`（新增 `TaskbarBatteryStrip.cs`，删除/替换 `PreviewTaskbarPill.cs`）
- `src/DeviceCenterPreview.App/App.xaml.cs`（创建/销毁、传入 settings 与打开主窗口回调）
- `src/DeviceCenterPreview.App/Views/SettingsPage.xaml/.cs`（外观页新增「任务栏显示」区：开关/样式/字号/背景板/显示休眠/其他设备开关）
- `docs/features/taskbar-tray.md`（文档同步）

实现要点（详见设计 §2）：
1. 沿用现有 native 窗口“三道锁”（顶层隐藏窗口 → SetParent(Shell_TrayWnd) → WS_CHILD/CLIPSIBLINGS + WS_EX_LAYERED|NOACTIVATE → UpdateLayeredWindow）；
2. 定位：`TrayNotifyWnd.left - 宽 - 2px`，回退 `ClockButton.left`，再回退 235 DIP；竖向任务栏/计算失败隐藏；
3. 渲染：GDI+ 32bpp；Segoe Fluent Icons→Segoe MDL2 回退（GetTextFace 校验）；13/16/5/14/8 DIP 规格；设备图标用 Bench 设备色（深色变体）；低电红/充电蓝闪/充满绿勾/休眠 alpha/未知 `?`；默认透明无底板，背景板可开关；
4. 主题：`SystemUsesLightTheme` + `WM_SETTINGCHANGE`；
5. 交互：左键打开主窗；右键 `TrackPopupMenu`（打开/刷新/显示设置/退出）；原生 tooltip 逐设备一行；
6. 重建：隐藏顶层窗口接收 `TaskbarCreated` → 销毁重建 + 重挂 tooltip；保留 1s `IsWindow` 兜底与 2–3s 冷却；
7. 数据：消费 `GetBatteryDevices()`；按 类型顺序耳机→鼠标→遥控器→其他，最多 6 项，溢出 `+N`；空列表隐藏；
8. 设置变更即时生效（`AppSettings` → strip 更新）；不依赖 XAML/主窗口存活。

验收：设计 §6 第 1–5、7 条；100%/200% DPI、明暗主题、空态、多设备、关窗到托盘、explorer 重启、全屏隐藏/恢复；截图。

### WP2（子代理 B：设备阶段 1；写作用域）

- `src/DeviceCenterPreview.App/Services/RealPeripheralService.cs`（分类放开：三类以外的 BLE/经典蓝牙/系统电池/插件设备 → `BatteryDeviceSnapshot`；去重/优先级；`ShowOtherBatteryDevices` 开关过滤）
- 新增 `src/DeviceCenterPreview.App/Services/Battery/GenericHidBatteryWatcher.cs`（HID Usage Page 0x06/Usage 0x20 Battery Strength，只读，轮询+失败退避）
- 新增 `src/DeviceCenterPreview.App/Services/Battery/GamepadBatteryWatcher.cs`（`Windows.Gaming.Input.Gamepad.TryGetBatteryReport`，WinRT 事件/定时）
- `src/DeviceCenterPreview.App/Services/RealPeripheralService.cs` 组合两个 watcher（生命周期/Dispose/线程→UI 事件）
- `src/DeviceCenterPreview.App/Views/DeviceCenterPage.xaml/.cs`（总览新增「其他设备」区块：固定 6 行，图标/名称/电量/状态；15s 刷新只更新文本，不重建控件）

实现要点：
1. 不改现有三类快照/页面行为；`GetBatteryDevices()` 返回统一列表（三类 + 其他）；
2. VigilWatt 侧新增 `_otherDevices` 列表，来源于 `DeviceManager` 的 `DevicesChanged`/快照过滤（排除已由耳机/鼠标/遥控器认领的 id）；不启用默认关闭的 VigilWatt 插件（Logitech/MCHOSE）；
3. Generic HID：SetupAPI 枚举 HID 顶层集合 → `HidD_GetPreparsedData`/`HidP_GetCaps` 找 usage page 0x06 usage 0x20 → `HidD_GetFeature`/输入报告读取；只读、失败退避；线程安全；
4. Xbox：WinRT `Gamepad.GamepadAdded/Removed` + `TryGetBatteryReport`（`BatteryReport.RemainingCapacityInMilliwattHours`/`FullChargeCapacityInMilliwattHours` 换百分比），USB 有线手柄无电池时忽略；
5. `IsAsleep/IsOffline` 只在明确信号时置位；未读数保留 last-known 并标注；
6. 设置 `ShowOtherBatteryDevices=false` 时列表不返回其他设备（采集可停）。

验收：插入/移除键盘/手柄/音箱时总览与任务栏同步变化；关闭开关后消失；无这类设备时总览区块隐藏；不回归三类设备页面；只读（无写设备）。

### WP3（lead：集成/验收/打包）

1. 合并 WP1/WP2，处理冲突（App 初始化、Settings 页、共享契约）；
2. 构建 x64 Debug/Release 0 警告；`dotnet test`；Rust/sidecar 不涉及；
3. 实机验收（设计 §6 全清单 + 截图基线）；
4. 版本 1.2.0（csproj + installer.iss）；`dist/release-notes-v1.2.0.md`；publish + ISCC + sha256；覆盖安装验证；
5. 文档：`docs/README.md` 索引、`features/taskbar-tray.md`（WP1 更新）、`features/integration-parity.md`（设备覆盖现状）、`history/implementation-log.md`；
6. 回滚：保留 1.1.2 安装包；任务栏条设置默认开启但可关；删除新增 watcher 不影响旧功能。

## 3. 依赖与并行

```
WP0（lead，前置）
 ├─ WP1（子代理 A，任务栏） ──┐
 └─ WP2（子代理 B，设备）   ──┴─ WP3（lead 集成/验收/打包）
```

- WP1 与 WP2 文件写作用域无重叠（WP1：Taskbar/App.xaml.cs/SettingsPage/taskbar 文档；WP2：Core 契约之外的 Services/RealPeripheralService/Battery watcher/DeviceCenterPage）；WP0 已加契约，避免两边改同一文件。
- 冲突兜底：若两边都需要 `RealPeripheralService`（WP1 只读调用，WP2 修改），由 WP2 独占该文件；WP1 不改。

## 4. P1/P2 路线（roadmap，不进本迭代）

| 优先级 | 项 | 说明 |
|---|---|---|
| P1 | 多显示器镜像 | 每块 `Shell_SecondaryTrayWnd` 一个条；主屏全屏时副屏仍可见 |
| P1 | 每设备重命名 | `AppSettings.DeviceAliases`；任务栏 tooltip/总览生效 |
| P1 | 每设备低电阈值 | 替换全局阈值；通知闩锁（充电/回升 10% 重置） |
| P1 | 唤醒/插拔补轮询 | 现有服务事件 + 3/5/10/15s 补读 |
| P1 | 托盘兜底模式 | 任务栏不可用/竖向时每设备数字托盘图标 |
| P1 | `--list/--dump` | 诊断 CLI，便于支持新设备 |
| P2 | 电池历史 + 运行时估算 | 复用 `HistoryStore`；简单图表 |
| P2 | 厂商协议阶段 2 | 罗技 HID++ 扩展、Razer、HyperX（按设备调研 §9.2，clean-room） |
| P2 | 阶段 3 | 长尾厂商、Fast Pair/TWS、手柄专有、HeadsetControl 桥 |
| P2 | 视觉增强 | 堆叠/圆环/连接方式标记/品牌色（评估与 Bench 冲突） |

## 5. 验收与发布

- 必测：设计 §6 的 1–8 条；截图基线 `design/verification/1.2.0-taskbar-{100,200}-{light,dark}.png`；
- 构建：`dotnet build ... -c Debug/Release -p:Platform=x64` 0 警告；`dotnet test` 通过；
- 打包：版本 1.2.0，`dist/DeviceCenterPreview-Setup-1.2.0.exe` + `.sha256` + `release-notes-v1.2.0.md`；
- 覆盖安装验证：设置/映射/备份保留；旧胶囊不再出现；卸载回滚 1.1.2 可用。

## 6. 实施结果（1.2.0，2026-11-27）

- 任务栏：`Services/Taskbar/TaskbarBatteryStrip.cs` 替换旧胶囊；透明内嵌条、任务栏主题/DPI、
  低电/充电/充满/休眠/未知状态、逐设备 tooltip、原生右键菜单、TaskbarCreated 重建；
  位置「自动」优先任务栏左侧空位（`Start`/`ReBar` 左边、预留 Widgets 170 DIP 并右对齐），
  放不下回退通知区域左侧；设置新增 位置/样式/字号/背景板/休眠显示。
- 设备：`BatteryDeviceSnapshot` 契约 + `RealPeripheralService.GetBatteryDevices()` 聚合
  VigilWatt 通用设备 + `GenericHidBatteryWatcher` + `GamepadBatteryWatcher`；
  总览新增「其他设备」6 槽位区块；设置可关闭。
- 文档：两份调研 + 设计 + 本计划；`docs/features/taskbar-tray.md`、`docs/01-architecture.md`、
  `docs/02-folder-map.md`、`docs/00-agent-readme.md`、`docs/README.md`、`README.md`、
  `features/integration-parity.md`、历史日志同步；截图 `docs/screenshots/taskbar*.png`。
- 实机验证：任务栏条位于天气小组件与开始之间（x≈300，不覆盖任务按钮），显示
  `耳机 70% / 鼠标 90% / 遥控器 92%`；设置页位置/样式/字号可配置。
