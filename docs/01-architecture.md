# 架构与数据流

## 1. 进程模型

```text
┌─────────────────────────────┐
│ DeviceCenterPreview.App.exe │  WinUI 3 / .NET 8 / x64
│  ├─ DeviceManager           │  VigilWatt：BLE/HID/经典蓝牙电量
│  ├─ RealHeadsetController   │  耳机 SPP（YuandaoTws）
│  ├─ LogitechMouseService    │  HID++（VigilWatt HidppIO）
│  ├─ RemoteEngineClient      │  TCP JSONL 客户端
│  ├─ PreviewTaskbarPill      │  Shell_TrayWnd 子窗口（GDI+ 分层）
│  └─ TrayService             │  Shell_NotifyIcon
└──────────────┬──────────────┘
               │ 127.0.0.1 随机端口 + 一次性 token + UTF-8 JSONL
┌──────────────▼──────────────┐
│ sidecars/sayall-engine.exe  │  Rust；复用 sayall-windows / sayall-core
│  ├─ BLE GATT/ATVV 语音      │
│  ├─ Raw Input（遥控器按键） │
│  ├─ key host 子进程（注入门）│
│  └─ 1 Hz snapshot + button  │
└─────────────────────────────┘
```

- 主应用单实例（Mutex + 激活事件）；重复启动会激活已有窗口。
- sidecar 由主应用启动、退出时 `system.shutdown`；3 秒未退则强杀进程树。
- 原版 `sayall-windows-app` 不能与本应用同时运行（BLE/钩子冲突）。

## 2. 主应用分层

| 层 | 位置 | 职责 |
|---|---|---|
| 模型 | `Core/Models` | `PeripheralSnapshot`、`HeadsetControls`、`MouseControls`、`RemoteControls`、`KeyMapping`、`RemoteButtonEvent` |
| 契约 | `Core/Services/IPeripheralService.cs` | 页面唯一数据入口；读取、切换设备、写入、遥控器映射/接收事件 |
| 聚合 | `App/Services/RealPeripheralService.cs` | 把 DeviceManager、PnP、耳机、鼠标、遥控器合并为设备列表；15s 刷新；字段级 Merge；模块启停、写入闸门、通知 |
| 设置 | `Core/Settings/AppSettings.cs` | `AppSettings` 记录 + `AppSettingsStore`（原子保存/损坏隔离/阈值夹紧） |
| 控制器 | `RealHeadsetController` / `LogitechMouseService` / `RemoteEngineClient` | 各自设备协议与状态；均支持 Stop/SetEnabled |
| 页面 | `Views/*` | 只调用 `App.PeripheralService`；不直接碰协议 |
| 桌面集成 | `PreviewTaskbarPill` / `TrayService` | 任务栏电量、托盘 |

### 数据流

```text
设备事件/定时器
   → 各控制器更新自身状态
   → RealPeripheralService.RefreshAsync()
   → 合并、去重、选择设备
   → Changed 事件
   → 页面 DispatcherQueue.TryEnqueue(Refresh)
   → 页面只更新文本/数值，不重建控件
```

- 所有 WinRT/后台事件进入 UI 前必须 `DispatcherQueue.TryEnqueue`。
- 页面刷新遵循“**签名变化才更新 ItemsSource / 图片 / 控件结构**”，否则只改文本。
- 断开设备不保留上一次电量；不显示伪造数据。
- 写入授权在 **服务层** 强制执行：`IPeripheralService` 的写方法先检查模块/写入开关，
  再调用控制器；页面禁用按钮只是第二道提示。

## 3. 设备发现与选择

`RealPeripheralService` 合并四类来源：

1. `VigilWatt.Core.DeviceManager`：BLE、HID、经典蓝牙电量；
2. Windows PnP（`System.Management`）：接收器/已配对但未连接的设备；
3. `RealHeadsetController`：SPP 连接后的耳机（优先分量/固件）；
4. `LogitechMouseService` / `RemoteEngineClient`：受控设备。

`EnsureSelections` 优先“已连接 + 有控制 + 有分量 + 有电量”的设备，并记住用户手动选择。
`Deduplicate` 做字段级合并（SPP/SayAll 优先），避免 DeviceManager 条目覆盖真实控制信息。

## 4. 窗口与桌面

- 窗口固定 **1280×840**，不可缩放/最大化；尺寸只在 `InitializeComponent` 之前设置一次，
  之后不再 resize。原因见 [`decisions.md`](decisions.md) 与 [`stability/crash-history.md`](stability/crash-history.md)。
- 自定义标题栏 48px，标题「外设中枢」。
- 任务栏胶囊：`Shell_TrayWnd` 的 layered child，左下角 235 DIP 锚点；只显示已连接设备；35px 高；全屏自动隐藏。
- 托盘：运行期间常驻；双击恢复；右键打开/退出；`X` / Alt+F4 隐藏主窗口，托盘和任务栏胶囊继续运行，真正退出走托盘右键“退出”。

## 5. 线程与生命周期

- `RealPeripheralService` 构造函数先创建所有控制器，再启动会触发 Changed 的组件（历史 NRE 修复）。
- `RemoteEngineClient` 用后台读线程 + `TaskCompletionSource` 请求；写操作串行化。
- 页面 `Unloaded` 必须退订 `Changed` / `RemoteButtonReceived`，停止计时器；接收测试要解除映射暂停。
- sidecar 退出会 `shutdown_ble_for_exit` 并关闭 key host。

## 6. 存储位置

| 内容 | 路径 |
|---|---|
| 应用日志 | `%LOCALAPPDATA%\DeviceCenterPreview\logs\app-YYYYMMDD.log` |
| 崩溃日志 | `%LOCALAPPDATA%\DeviceCenterPreview\logs\crash.log` |
| 应用设置 | `%LOCALAPPDATA%\DeviceCenterPreview\settings.json` |
| 配置备份 | `%LOCALAPPDATA%\DeviceCenterPreview\backups\*.zip` |
| 诊断包 | `%LOCALAPPDATA%\DeviceCenterPreview\diagnostics\*.zip` |
| 电量历史 | `%LOCALAPPDATA%\DeviceCenterPreview\history.db` |
| 遥控器设置 | `%APPDATA%\app.getsayall.remote-mic.windows\settings.json` |
| 遥控器映射 | 同目录 `button-mappings.json` |
| 语音热键 | 同目录 `voice-hold-hotkey.json` |
