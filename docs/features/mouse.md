# 功能：鼠标

## 已实现（当前 G304 真机验证）

- 罗技 HID++ 2.0 发现：接收器 `VID_046D`，Usage Page `0xFF00`，设备槽位 1–6/0xFF。
- 读：设备名、电量/充电、当前 DPI、轮询率、固件（BOT/RQM）。
- 写：当前 DPI、轮询率、5 个 DPI 档位、当前档位、配置文件名称、板载模式、设备 profile 返回的按键映射（G304 当前 6 个，协议上限 16）。
- 写入后回读校验；板载配置写完后 CRC 校验（CCITT，初值 0xFFFF，多项式 0x1021）。
- 图片：按型号名映射 20 个罗技产品图；未知型号回退统一鼠标图标。
- 页面：固定窗口下两列（性能 / 设备信息），窄内容不再重排。
- 写入能力闸门：只有“型号名含 G304/G305 + 板载 profile 有效 + profile 可写”才置
  `MouseControls.WriteSupported=true`；其他型号保持只读，页面显示原因并禁用应用按钮。
- 模块开关：关闭后 `SetEnabled(false)`，停止轮询/HID++ 请求；重新开启再刷新。

## 代码路径

| 内容 | 文件 |
|---|---|
| HID++ 读写/特征/板载 profile | `Services/Mouse/LogitechMouseService.cs` |
| 型号 → 图片 | `Services/DeviceArtwork.cs` |
| 页面 | `Views/MousePage.xaml(.cs)` |
| HID 底层 | `external/VigilWatt.Core/Plugins/Logitech/HidppIO.cs` / `HidppCodec.cs` |

## 页面结构

1. 顶部：产品图/回退图标、名称、状态、设备选择、电量。
2. 三枚摘要：当前 DPI / 轮询率 / 板载开关。
3. “性能”卡：当前 DPI 滑杆、轮询率、5 档 DPI 编辑器（静态 `TextBox`）、当前档位、板载模式、配置名称。
4. “设备信息”卡：名称、连接、电量、固件、DPI 范围、轮询率列表、板载可写、控制通道。
5. “按键映射”卡：按 `MouseControls.Buttons` 的实际数量和顺序显示 1–16 个功能下拉（禁用/鼠标键/键盘/媒体/DPI）；不预设 6 个按键，也不假设左/右键锁定。0 个按键显示空状态，超出可视行数在卡内滚动。未知动作显示“未识别（保留原值）”，应用时保留原动作。
6. 底部：应用设置 / 刷新鼠标 / 保存状态。

## 实现要点

- `_requestsAllowed` 闸门：所有 HID++ 请求在读取/写入期间才允许；Probe 结束后必须重新打开。
- `HidppCodec.FeatureFunc(feature, func)` 只接受 0–15 的功能号；`0x8100` 的
  `0x10/0x20/0x50…0x80` 实际是“功能号 << 4”，必须用 `RequestFull` 并让 `HidppIO` 填 SwId。
- 板载 profile 格式（G304，255 字节）：
  - `[0]` 轮询率（ms）
  - `[1]` 默认 DPI 档索引
  - `[2]` Shift 档索引
  - `[3..12]` 5 个 LE16 DPI
  - `[32+4i]` 按键映射（behavior SEND/FUNCTION 等）
  - `[160..207]` UTF-16LE 配置名
  - 最后 2 字节 CRC
- 写扇区：`0x60` 开始 → `0x70` 16 字节块（payload 不含 sector/offset）→ `0x80` 完成；回读比对。
- 图片缓存：只在型号变化时 `new BitmapImage`，否则会因反复解码触发 XAML 崩溃。
- 按键映射最多 16 个静态单元（协议上限），`ComboBox.ItemsSource` 只在构造时设置一次；刷新只改 `Visibility`、按钮名和 `SelectedValue`，不重建列表。左/右/中键使用通用物理名；其他型号的非标准键显示“按键 N”槽位，只有已确认布局的 G304/G305 显示侧键/DPI 名称。
- 5 档 DPI 与 16 个按键单元必须是 XAML 静态控件；不要在缓存页里使用 `NumberBox`。
  `NumberBox` 在多次进入后会让 XAML 内容树整体消失（见 `docs/stability/crash-history.md`）。
- DPI 档位 `TextBox` 在 `Apply` 时统一校验 0–12000；非法输入只提示、不写硬件。

## 当前支持边界

- 通用读取适用于多数罗技 HID++ 鼠标；**完整写入只保证 G304/G305 这一套 255 字节 profile 格式**。
- 非 G304/G305 或 profile 校验失败的设备：`WriteSupported=false`，页面显示“只读/未验证写入”，
  `ApplyAsync` 直接拒绝；不再只靠按钮置灰。
- OpenMouse 的 94 个驱动 / 158 个 supported 型号在外部 AGPL `@openmouse/protocol` 包，未移植。
- G304 无灯光、无宏；暂未做配置导入导出、Setup Code、固件更新、诊断包。
- 鼠标休眠时不会伪装在线；唤醒后点“刷新鼠标”。

## 真机验证记录

- G304：20%，DPI 1350，轮询 1000 Hz，固件 `BOT 69.02 / RQM 68.02`。
- DPI 档位写入：`400/800/1350/3200/6400`，CRC 回读一致。
- 按键映射：把第 3 键改成“后退”并回读成功，随后恢复为“音量-”。
