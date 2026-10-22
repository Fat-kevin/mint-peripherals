# 功能：耳机

## 已实现

- 真实设备发现：`YUANDAO OriG in`（经典蓝牙 + LE）。
- SPP 控制通道：`0000A100-1000-8000-4E48-434B4354524C`。
- 分量电量：左耳 / 右耳 / 充电盒；充电状态；固件版本。
- 六态 ANC：关闭 / 通透 / 普通 / 深度 / 实验性 / 风噪。
- 七档 EQ：悔恨之泪 / 均衡中正 / 欧美澎湃 / 真律还原 / 游戏优化 / 细腻佳音 / 温婉人声。
- 五个快捷开关：游戏模式、低延迟、双设备、入耳检测、抗风噪。
- 查找耳机：点击后 5 秒倒计时，按钮变“取消查找（n）”；倒计时结束才发送鸣响，可随时取消。
- 设置闸门：模块关闭后停止 SPP；写入关闭后页面只读，服务层拒绝写入并给出原因。
- 未知充电状态：耳机只返回电量、不返回充电标志时，分量/整机显示“未知”，不再把 null 当作“未充电”。
- 断开/未识别控制：`HeadsetControls.Default` 为 `IsKnown=false`，页面清空 ANC/EQ/开关选择，不显示伪造的当前值。

## 代码路径

| 内容 | 文件 |
|---|---|
| 连接/SPP/控制/查找 | `src/DeviceCenterPreview.App/Services/RealHeadsetController.cs` |
| 页面 | `src/DeviceCenterPreview.App/Views/HeadsetPage.xaml(.cs)` |
| 聚合与去重 | `src/DeviceCenterPreview.App/Services/RealPeripheralService.cs` |
| 协议常量 | `src/DeviceCenterPreview.Core/Models/PeripheralModels.cs` + `external/YuandaoTws.*` |

## 页面结构

1. 顶部：充电盒渲染图 + 设备名 + 状态徽章（已连接/未连接、固件）。
2. 三张分量卡：左耳/右耳/充电盒（图片、电量、充电状态）。
3. “降噪与音效”卡：
   - ANC 六态与 EQ 七档使用 `ComboBox`，`ItemsSource` 只在构造时设置一次；
   - 原设计使用原生 `RadioButtons`，但在缓存页二次进入后会让 XAML 内容树整体消失（见
     `docs/stability/crash-history.md`），因此回退为下拉选择，功能与写入不变。
4. 五个 `ToggleSwitch`。
5. 查找耳机 + 状态文本。

## 实现要点

- `UpdateHeadsetControlsAsync` 返回 `WriteResult`；页面 `Save()` 只在 `_loading == false && controlsAvailable && writeAllowed` 时发送，避免刷新回环。
- 查找流程没有模态框：`_findCts` 非空时再次点击按钮即取消；`Unloaded` 时取消。
- 耳机断开或控制未知时禁用所有控件并清空查找状态；连接中不保留上一次的 ANC/EQ 选择。
- 去重合并时保留 SPP 侧 `ControlHint`，否则写入关闭的说明会在 `DeviceManager` 条目合并后丢失。

## 真机注意

- 原版 `YuandaoTws.Desktop` 会独占 SPP；同时运行会出现 `BluetoothConnectionException` / `0x80072740`。
- 若 Windows 蓝牙未连接耳机，页面只显示配对信息，不能控制。
- 停止鸣响命令是 best-effort（协议文档承认 `0x03A2=0` 的停机语义未完全坐实）。

## 待办 / 限制

- 开机启动已由应用级设置实现（`SettingsPage` + `StartupService`）；原版的分量历史、
  托盘 ANC 快捷菜单、手动重连/断开仍不接入（见 `integration-parity.md` 的取舍）。
- 五个快捷开关的 V2 固件分流尚未完成，当前仍发 V1；本机固件 4.8 的实际效果需真机验证，
  未抓官方 App 报文前不盲发 V2。
- 提示音音量、触控/手势映射：协议载荷未坐实，未接线；不猜测字节。
- 耳机写入项需要逐项真机回归（尤其是实验性降噪/风噪）。
- 查找耳机也受写入开关控制；关闭写入后不会再鸣响。
