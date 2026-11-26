# BatteryHub 吸收设计稿：任务栏内嵌显示 + 设备扩充

> 日期：2026-11-27
> 输入：`2026-11-27-batteryhub-ui-research.md`（任务栏/功能）、`2026-11-27-batteryhub-device-research.md`（设备/协议）
> 目标：把任务栏胶囊替换为 BatteryHub 式「内嵌图标+百分比」；吸收其最有价值的产品能力；把设备覆盖从 3 类扩到通用 Windows 电池设备 + 后续厂商协议。
> 许可边界：上游 GPL-3.0，本设计与后续实现均为 clean-room（只吸收事实与行为，不复制代码/文案）。

## 1. 目标与非目标

### 目标

1. **显示形式替换**：任务栏不再显示左下角圆角胶囊；改为在通知区域左侧内嵌 `[图标] 93%  [图标] 30%` 样式的透明条，跟随主题/DPI/任务栏自动隐藏，位置由真实托盘锚定。
2. **信息与状态**：图标 + 百分比；低电红、充电蓝闪电、充满绿勾、休眠变暗、未知 `?`；逐设备 tooltip；空态占位/隐藏策略。
3. **交互**：左键打开主窗口；右键原生菜单（打开、刷新、任务栏显示设置、退出）；不使用 WinUI Flyout/ContentDialog。
4. **健壮性**：`TaskbarCreated` 重建、Explorer 重启恢复、全屏隐藏、多屏（P1）镜像；任何定位异常回退固定位置或隐藏，不覆盖托盘/时钟。
5. **设备扩充阶段 1**：打通「设备数据已有、UI 只显示三类」的收敛点——通用 Windows 电池设备（键盘/手柄/音箱/第二只鼠标等）+ Generic HID Battery Strength + Xbox WGI 手柄；总览与任务栏同步展示。
6. **功能吸收路线**：把 BatteryHub 的历史/阈值/重命名/托盘兜底/多显示器/诊断 CLI 等列为 P1/P2（见 §6），本迭代只做 P0 + 设备阶段 1。

### 非目标（本迭代）

- 不实现 100 个厂商私有协议（罗技扩展/Razer/HyperX 等放 P2，见设备调研 §9.2）；
- 不实现自绘 Win11 弹层菜单、多语言、自更新、Win10 ReBar 缩窄、竖向任务栏列布局、RTL；
- 不复制上游 GPL 代码、注释、英文文案或 Material Icons 字体；不引入 17 品牌色表（与 Bench 设计令牌冲突）；
- 不改动现有三个控制模块（耳机 SPP/鼠标 HID++/遥控器 sidecar）的协议行为。

## 2. 显示方案设计

### 2.1 形态（对齐用户偏好）

上游截图形态：**无底板、透明背景、图标+百分比直接坐在任务栏上**（默认 13px 文字 / 16px 图标 / 5px 图标-文字间距 / 14px 设备间距 / 左右 8px，DIP）。用户明确不喜欢现有胶囊底板，故：

- 默认：**透明条**，只画图标与文字；
- 可选：`任务栏背景板` 开关（默认关）给亮色壁纸/透明任务栏主题兜底（低 alpha 底板，内缩 4、圆角 6）；
- 位置：通知区域（`TrayNotifyWnd`）左边缘 −条宽−2px，垂直居中；真实托盘不存在时回退旧固定位置（235 DIP）或隐藏；
- 不再显示竖线分隔与圆角外框（胶囊元素移除）；设备之间用间距区分，可选分隔线开关（默认关）。

### 2.2 架构（在现有 `PreviewTaskbarPill` 基础上演进）

```
IPeripheralService（GetBatteryDevices 新契约）
        │ Changed（任意线程）
        ▼
TaskbarStripState（不可变快照：设备项 + 主题 + 尺寸参数）
        │ DispatcherQueue.TryEnqueue
        ▼
TaskbarBatteryStrip（新，替代 PreviewTaskbarPill）
  ├─ NativeWindowHost：顶层隐藏窗口 → SetParent(Shell_TrayWnd)、WS_CHILD|CLIPSIBLINGS、
  │   WS_EX_LAYERED|NOACTIVATE，UpdateLayeredWindow（沿用现有“三道锁”）
  ├─ GeometryTracker：TrayNotifyWnd 左缘回退 ClockButton → 固定 235 DIP；异常隐藏
  ├─ StripLayout：DIP 计算（MulDiv），宽度=2*8+Σ(icon+5+text+14)
  ├─ StripRenderer：GDI+ 32bpp Bitmap → DIB；字体 Segoe Fluent Icons→Segoe MDL2 回退
  ├─ ThemeWatcher：SystemUsesLightTheme + WM_SETTINGCHANGE(ImmersiveColorSet)
  ├─ TaskbarRestartWatcher：隐藏顶层窗口（非 message-only）接收 TaskbarCreated → 重建
  └─ NativeTooltip：TOOLTIPS_CLASSW 多行；文本不变不更新
```

原则：任务栏条**不依赖 XAML/主窗口存活**，只消费不可变快照；主窗口关闭到托盘后条继续工作（与现状一致）。

### 2.3 定位算法（简化版）

1. `Shell_TrayWnd` 可见且 `GetWindowRect` 正常，否则隐藏；
2. 任务栏为竖向（高>宽）→ 隐藏（v1 不支持竖向，兜底托盘图标）；
3. `edge = TrayNotifyWnd.left`（可见且在任务栏内）；否则 `ClockButton.left`；否则回退 `bar.left + 235*scale`；
4. `x = edge - width - 2*scale`，clamp 到 `[bar.left+4, edge-width-2]`；若宽度放不下 → 只放可用数量的设备（最少 1 个，其余 `+N`）或隐藏；
5. `y = bar.top + (bar.height - height)/2`，height = bar.height（整条透明）；
6. DPI 取 `GetDpiForWindow(bar)`；所有尺寸 `MulDiv`；
7. **避让**：v1 不做 Widgets/UIA/第三方挂件避让（收益/风险比低），但保证 x 不小于任务栏左缘且不覆盖托盘；若未来加入，放 P2。

### 2.4 视觉规格（Bench 化）

| 项 | 值（DIP） |
|---|---|
| 文字字号 / 字重 | 13 / 400（可设置 小12 / 中13 / 大15） |
| 图标尺寸 | 16 |
| 图标↔文字 | 5 |
| 设备间距 | 14 |
| 左右边距 | 8 |
| 行高 | 任务栏客户区高，内容垂直居中 |
| 背景板（可选） | 内缩 4、圆角 6、alpha 240（浅 #F3F3F3 / 深 #202020） |

颜色：前景取任务栏主题（浅：`#181818`；深：`#F5F5F5`）；设备图标用 Bench 设备令牌（深色任务栏用提亮变体）：耳机 `#8F82E8`（浅色 `#5B4BC4`）、鼠标 `#D9A05B`（浅 `#A66B22`）、遥控器 `#5FB3D1`（浅 `#1F6F8C`）、通用设备 `#9AA3AD`。状态：低电 ≤15% 红（浅 `#C41E28` / 深 `#FF5A5A`）；充电闪电蓝（浅 `#0078D4` / 深 `#60CDFF`）；充满绿勾（浅 `#107C10` / 深 `#6CCB5F`）；休眠 alpha 120；未知 `?`。所有状态**颜色+符号双编码**，文字不单独依赖颜色。

图标字形：`Segoe Fluent Icons`（Win11）→ `Segoe MDL2 Assets`（Win10 回退），按设备类型（耳机/鼠标/遥控器/键盘/手柄/音箱/通用电池）选码点；创建字体后用 `GetTextFaceW` 校验实际字体，防方框。不嵌入第三方字体。

### 2.5 主题、重启、全屏、多屏

- 主题读 `HKCU\...\Themes\Personalize\SystemUsesLightTheme`（任务栏主题，而非 App 主题），`WM_SETTINGCHANGE` 刷新；
- 监听 `TaskbarCreated`（注册隐藏顶层窗口）→ 销毁条并重建、重挂 tooltip；保留 1s `IsWindow`/父窗口检查兜底，2–3s 冷却防抖；
- 全屏游戏：沿用现有 `SHQueryUserNotificationState` + 前台矩形覆盖检测 → 隐藏，退出恢复；
- 多显示器：v1 只主屏；P1 为每块 `Shell_SecondaryTrayWnd` 建镜像（同绘制/点击；各自 DPI/位置）。

### 2.6 交互与设置

- 左键单击：打开主窗口（现状）；
- 右键：原生 `TrackPopupMenu`（TPM_RETURNCMD）——打开外设中枢 / 立即刷新 / 任务栏显示设置 / 退出；隐藏 owner 窗口先 `SetForegroundWindow`，菜单返回后 `PostMessage(WM_NULL)`；
- 设置页「外观」新增「任务栏显示」区：开关（启用任务栏条）、样式（图标+百分比 / 仅图标 / 仅百分比）、字号（小/中/大）、背景板（默认关）、显示休眠设备、显示「其他设备」；
- 设置持久化到现有 `AppSettings`（新增字段，默认值兼容旧配置；无需 schema 升级，JSON 反序列化缺省即用默认）。

## 3. 设备扩充设计（阶段 1）

### 3.1 数据模型

新增 Core 记录（不破坏现有 `PeripheralSnapshot`）：

```csharp
public enum BatteryDeviceKind { Headset, Mouse, Remote, Keyboard, Gamepad, Speaker, Other }

public sealed record BatteryDeviceSnapshot(
    BatteryDeviceKind Kind,
    string Id,
    string DisplayName,
    int? Percent,
    ChargingState Charging,
    bool IsConnected,
    bool IsAsleep,          // 最近一次读数过期/休眠
    bool IsOffline,         // 驱动明确报告离线（不是“本轮没读到”）
    string? ConnectionType, // Bluetooth / 2.4G / USB / 未知
    IReadOnlyList<DeviceComponentBattery> Components,
    string Source);         // "vigilwatt" | "hid-battery" | "xbox" | "vendor:xxx"
```

`IPeripheralService` 新增 `IReadOnlyList<BatteryDeviceSnapshot> GetBatteryDevices();`（Mock/Unavailable 同步；旧三类设备也映射进来，任务栏与总览统一消费）。

### 3.2 数据来源（阶段 1，不写厂商协议）

1. **VigilWatt.DeviceManager 全量放开**：`RealPeripheralService.Classify` 当前只保留三类；改为把 BLE/经典蓝牙/系统电池/5 个插件中未被三类认领的设备收进 `BatteryDeviceSnapshot`（键盘/手柄/音箱/第二只鼠标/通用 HID），去重与优先级按设备调研 §5.2；
2. **Generic HID Battery Strength**（新增 watcher）：枚举 HID 顶层集合，匹配 Generic Device Controls usage page 0x06 / usage 0x20 的输入报告，读 0–100 电量；只读、S 级风险；
3. **Xbox 手柄**（新增 watcher）：`Windows.Gaming.Input.Gamepad` + `TryGetBatteryReport` 取百分比与充电状态；XInput 仅作为粗粒度兜底（可选）；
4. 来源统一为 `Source` 字段，刷新失败保留 last-known + `IsAsleep` 语义（不伪造实时）。

### 3.3 UI 展示

- **任务栏条**：设备顺序 耳机→鼠标→遥控器→其他（按电量低优先? 固定类型顺序，最多 6 个；超出显示 `+N`；tooltip 全量）；
- **总览页**：新增「其他设备」区块（固定 6 行：键盘/手柄/音箱/其他），行显示图标/名称/电量/状态；数据从 `GetBatteryDevices()` 取，避免每 15s 重建控件（沿用缓存页约束）；
- **设置**：开关「其他设备电量」（默认开）；关闭后不显示其他设备（采集仍可暂停）。
- 不新增独立页面；不改变现有三设备详情页。

### 3.4 刷新与失败策略

- 复用现有 15s 全量刷新 + 服务事件；Generic HID/Xbox watcher 独立 30–60s 轮询，失败退避（2s/10s/60s）；
- 休眠/离线：只在驱动明确报告时置位；过期读数保留并标 `IsAsleep`，tooltip 标注"上次读数"；
- 所有新 watcher 只读，不上报、不写设备。

## 4. 功能吸收优先级

| 优先级 | 能力 | 决策 |
|---|---|---|
| **P0 本迭代** | 任务栏内嵌显示替换胶囊、托盘锚定、任务栏主题、TaskbarCreated 重建、状态语义、tooltip、右键原生菜单、显示设置（开关/样式/字号/背景板） | 必做 |
| **P0 本迭代** | 设备阶段 1：通用 Windows 电池聚合 + Generic HID + Xbox + 总览「其他设备」 | 必做 |
| P1 下一迭代 | 多显示器镜像；每设备重命名；每设备低电阈值（替换全局阈值）；唤醒/插拔补轮询；托盘图标兜底模式；`--list/--dump` 诊断 CLI | 高价值，放后续 |
| P2 | 电池历史 + 运行时估算（复用 HistoryStore）；厂商协议（罗技扩展、Razer、HyperX、Kysona 族）；品牌色可选；堆叠/圆环样式；自定义位置偏移；Win10 支持 | 长期 |
| 不做 | GPL 代码/文案移植、自绘弹层、Material Icons 私有字体、17 品牌色全表、多语言、自更新、Phone Link、写入类能力 | — |

## 5. 风险与缓解

| 风险 | 缓解 |
|---|---|
| 任务栏条遮挡托盘/时钟/其它挂件 | 托盘左缘锚定 + 宽度自适应 + clamp；放不下则少显示/隐藏；异常回退 235 DIP |
| 任务栏 XAML/layered 子窗口在 Win11 上绘制层级异常 | 沿用现有已跑通的 child-of-Shell_TrayWnd 方案；失败则隐藏并记日志（托盘图标仍在）；真机截图验收 |
| Explorer 重启/任务栏重建 | `TaskbarCreated` 监听 + 1s 兜底 + 冷却；验收脚本杀 explorer |
| 主题判断错误 | 改读 `SystemUsesLightTheme`；明暗两套色；浅色对比度验收 |
| DPI 模糊/错位 | `GetDpiForWindow(taskbar)` + MulDiv；100/150/200% 截图验收 |
| 新增 watcher 影响稳定性（WinRT/COM） | 独立线程/定时器，事件回 UI；只读；失败静默退避；可开关关闭 |
| 设备分类放开导致总览/胶囊数据错乱 | 三类设备仍走原 snapshot；新增设备只进「其他设备」列表；去重与类型优先级明确 |
| GPL 传染 | clean-room：设计只依据调研文档的事实描述；实现不打开上游源码；评审相似度 |
| 旧胶囊设置/代码残留 | 删除 `PreviewTaskbarPill` 调用与文件（保留必要原生辅助方法迁入新类）；文档同步 |

## 6. 验收标准

1. 任务栏：通知区域左侧 2px 显示 `[耳机] xx%  [鼠标] xx%  [遥控] xx%`；100/150/200% DPI 不模糊不错位；不压托盘/时钟；
2. 主题：系统浅/深色切换立即变化；浅色对比度达标；
3. 状态：低电红、充电闪电、充满绿勾、休眠变暗、未知 `?`、tooltip 逐设备一行；
4. 健壮：`taskkill /f /im explorer.exe` 后 ≤2s 恢复；全屏游戏隐藏、退出恢复；关窗到托盘后条仍在；
5. 交互：左键打开主窗口；右键菜单四项可用；设置页开关/样式/字号即时生效并持久化；
6. 设备：键盘/手柄/音箱/第二只鼠标出现在任务栏与总览「其他设备」；无设备时条隐藏；关闭「其他设备电量」后消失；
7. 回归：现有耳机/鼠标/遥控器页功能、任务栏胶囊移除、托盘退出、单实例不变；构建 0 警告、核心测试通过；
8. 打包：版本 1.2.0，覆盖安装，设置/映射/备份保留。
