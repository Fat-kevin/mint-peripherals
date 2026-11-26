# BatteryHub 设备与协议调研：能力矩阵、差距与扩充路线

> 日期：2026-11-27
> 状态：调研完成；可作为设备扩充设计稿（`2026-11-27-batteryhub-design.md`）与实施计划的输入
> 关联任务：共享任务板 task-2；同批姊妹文档 `2026-11-27-batteryhub-ui-research.md`（任务栏显示侧，本文不展开）
> 调查对象：`.artifacts/upstream-wireless-battery-monitor`（BatteryHub，fork 自 krisgfx9/Wireless-Device-Battery-Monitor，commit `0c1adbe`，2026-11-26，GPL-3.0）
> 对照对象：`device-center-preview`（外设中枢 · 薄荷外设，WinUI 3 / .NET 8 / C#，MIT）

## 引用约定

本文所有事实尽量带 `file:line` 出处：

- `U:` = `.artifacts/upstream-wireless-battery-monitor/`（上游只读克隆），例：`U:src/devices.cpp:41`
- `D:` = `device-center-preview/`（我们的应用），例：`D:src/DeviceCenterPreview.App/Services/RealPeripheralService.cs:625`
- 引用上游 = 只把它当**事实来源**；正文不复制其代码或文档措辞（见 §11）。

---

## 1. 摘要

### 1.1 上游规模（可直接引用的数字）

| 指标 | 数值 | 出处/口径 |
|---|---|---|
| README 设备支持行 | **113 行** | `U:README.md:101-212` 的表格行（每行一个厂商/子系列） |
| 厂商/平台分组标签 | **111 个**（含 Bluetooth、Generic HID、HeadsetControl、Phone Link 等非厂商行） | 对设备表首列加粗的行标题去重 |
| 明确写出的 VID:PID 对 | **280 个**；其中 **96 个不同 USB vendor id** | `grep '[0-9A-F]\{4\}:[0-9A-F]\{4\}' README.md` |
| README 标注过真机测试的行 | 71 行 | `awk` 统计含 tested/hardware-tested 的表格行 |
| 驱动/协议源文件 | **113 个 `src/drivers/*.cpp`**（108 个驱动实现 + 5 个共享协议/传输辅助文件） | `ls src/drivers/*.cpp` |
| 轮询入口 | `drivers.h` 声明 **111 个 `Poll*`**；Windows 主轮询按固定顺序调用 **110 个**（`PollZebronics` 仅 Linux 构建） | `U:src/drivers/drivers.h:70-186`、`U:src/devices.cpp:51-160` |
| 代码量（纯 C++ 源码） | **31,197 行**（`src/*.cpp,*.h` + `src/drivers/*.cpp,*.h`） | `wc -l` |
| 回归测试 | **83 个测试文件**（大量是"模拟 HID 传输"的驱动级测试） | `ls tests` |
| 协议笔记 | **23 篇** `docs/*.md` + 6 个 Keychron 固件补丁文件 | `ls docs` |
| 品牌色 | 17 个品牌 + 无品牌项 | `U:src/brands.cpp:10-29` |
| HeadsetControl | 捆绑 4.3.0（GPL-3.0，独立 exe） | `U:NOTICE.md`、`U:README.md:30` |

> **型号数量**无法从 README 精确计数（一行常列 5–20 个型号，部分只按系列描述）；口径上用"113 支持行 / 111 分组 / 280 个明确 VID:PID"更可信，本文不编造型号总数。

### 1.2 能力概况（一句话版）

BatteryHub 是一个 Win32 单进程、约 350 KB 的托盘/任务栏电量工具：**每个厂商一个只读驱动**，先枚举 HID 顶层集合，再按 VID:PID + collection + report 长度识别，发**只读**电量查询并"认领"该 VID:PID，最后用通用兜底（HeadsetControl → Bluetooth → Generic HID → XInput）覆盖未被认领的设备；所有设备统一进入带 `percent/charging/asleep/connected/graceSec/millivolts` 语义的 `DeviceStatus` 模型，由任务栏/托盘/历史/告警共用（`U:src/devices.h:5-23`、`U:src/devices.cpp:41-162`）。

### 1.3 我们与它的差距（三句话版）

1. **数据已在、UI 未露**：我们的 `VigilWatt.DeviceManager` 已经能看到 BLE / 经典蓝牙 / 5 个厂商插件的设备，但 `RealPeripheralService.Classify` 只保留耳机/鼠标/遥控器三类，键盘、手柄、音箱、第二个同类鼠标等全部被丢掉（`D:src/DeviceCenterPreview.App/Services/RealPeripheralService.cs:1201-1221`、`:641-648`）；任务栏胶囊与总览页也是固定三类（`D:src/.../Taskbar/PreviewTaskbarPill.cs:228-260`、`D:src/.../Views/DeviceCenterPage.xaml:41-52`）。
2. **通用 Windows 路径不完整**：BLE/经典蓝牙电量我们已有（且 BLE 多一条 GATT 直读兜底），但缺 **Generic HID Battery Strength**、**Xbox（Windows.Gaming.Input/XInput）**、**Fast Pair 左右/盒分量**；这些不需要写厂商协议，属于低风险高收益。
3. **厂商协议覆盖量级差距大**：上游约 100 个品牌/平台的专用驱动，我们只有 5 个 VigilWatt 插件（其中 Logitech/MCHOSE 默认关闭）+ 应用层罗技 HID++（受控鼠标）+ 1 个耳机 SPP + 遥控器 sidecar；这部分是阶段 2/3 的工作，且必须 clean-room（上游 GPL 代码不可复制）。

### 1.4 本文给出的路线（先看结论）

| 阶段 | 内容 | 性质 | 预估 |
|---|---|---|---|
| **阶段 1** | 放开设备类型 + 通用 Windows 电池（Generic HID、Xbox WGI/XInput）+ 任务栏/总览/设置展示 | 低风险、高收益；**不写任何厂商协议** | 3–5 人日 |
| **阶段 2** | 罗技 HID++ 扩展到键盘/耳机/更多型号（复用已有会话）+ **Razer** + **HyperX** 两个有公开协议笔记的厂商 | 需要新协议驱动；clean-room | 罗技 2–3 人日；每厂商 2–4 人日 |
| **阶段 3** | 其余厂商长尾、HeadsetControl 桥、蓝牙专有（Fast Pair/AirPods/TWS RFCOMM）、手柄专有协议、键盘固件补丁类 | 长期 backlog，按用户设备优先级排 | 单厂商 1–5 人日 |
| 不做/慎做 | 自更新器、Phone Link 私有文件读取、Linux 构建、翻译体系移植 | 与"设备扩充"无关或触碰隐私/发布红线 | — |

---

## 2. 调查对象、方法与边界

### 2.1 对象与许可

- 目录为 **GPL-3.0** 项目的**修改版 fork**：`U:README.md:1-8`、`U:NOTICE.md:1-40`、`U:README.md:543-547`。上游 README 明确：分发基于它的作品必须同样以 GPL 开放源码。
- 我们的应用是 **MIT**（`D:LICENSE:1-3`），因此**任何上游 C++ 代码、注释、文档措辞都不能复制、逐行翻译或改名移植**。允许吸收的是**协议事实**（VID/PID、report 结构、命令/字段偏移、状态语义）——这些事实同时来自更上游的公开项目（OpenRazer、Solaar、HeadsetControl、rivalcfg、HaloBattery、SDL 等，上游 README 的 Credits 章节已逐项列出，`U:README.md:464-541`）。
- 本文只做只读调查：没有运行上游程序、没有连接任何设备、没有修改上游克隆。

### 2.2 方法

1. 通读 `U:README.md`（支持设备表、显示语义、限制、How it works、Credits、License）。
2. 逐个读取 `U:docs/*.md` 协议笔记与 `U:src/drivers/*.cpp` 的文件头注释，建立"厂商 → 传输 → 协议 → 字段"矩阵。
3. 读 `U:src/devices.cpp`（驱动顺序）、`U:src/drivers/drivers.h`（`PollContext`/认领）、`U:src/devices.h`（状态模型）、`U:src/main.cpp`（轮询/grace/离线状态机）、`U:src/refresh_policy.h`、`U:src/runtime_estimate.h`、`U:src/brands.cpp`、通用兜底四件套（`generic.cpp`/`xinput.cpp`/`headsetcontrol.cpp`/`ble_adverts.cpp`+`fastpair.cpp`）。
4. 对照我们自己的 `D:docs/features/*.md`、`D:docs/01-architecture.md`、`D:src/DeviceCenterPreview.App/Services/`、`D:external/VigilWatt.Core/`（`Devices/`、`Ble/`、`Hid/`、`Plugins/`）。
5. 差距与路线按"我们现有架构能否复用"逐项判定。

### 2.3 不在本文范围

- 任务栏显示实现原理/视觉规格（姊妹文档 task-1 负责）。
- 具体设计稿与任务拆分（task-3 / task-4）。
- 厂商固件逆向、写入类能力（上游本身也只读电量，绝不写配置/灯效/DPI）。

---

## 3. BatteryHub 架构与判定规则（决定能力边界）

### 3.1 轮询管线：厂商驱动优先，通用兜底最后

```text
PollAllDevices()                                  U:src/devices.cpp:41-162
├─ EnumHid() 枚举全部 HID 顶层集合（一次）        U:src/hid.cpp:13-60
├─ 过滤 HKCU\Software\WirelessBattery\IgnoreDevices（用户忽略的 VID:PID）
├─ 110 个厂商 Poll* 按固定顺序执行：
│   每个驱动按 VID/PID + collection + report 长度识别 → 发只读查询 → ctx.Add() 并 Claim(VID,PID)
├─ 通用兜底（也按顺序）：
│   HeadsetControl(exe) → Bluetooth(Windows 属性+FastPair) → Generic HID(Battery Strength) → XInput
└─ 返回 vector<DeviceStatus>
```

关键规则：

- **认领（claim）**：`PollContext.claimed` 以 `(VID,PID)` 为键；驱动成功后 `Claim`，后续驱动与兜底用 `IsClaimed` 跳过，保证同一设备不被重复显示（`U:src/drivers/drivers.h:12-30`）。
- **只读**：每个驱动只发电量/充电/在线状态查询；上游 README 明确"only ever sends read-only battery queries. Unknown devices are never written to"（`U:README.md:442-446`）。
- **顺序敏感**：多个品牌共用 VID 时靠"更专用的驱动排前面"解决。实例：
  - `PollAula` 在 `PollKysona`、`PollSonix` 之前（`U:src/devices.cpp:53-54`）
  - `PollAtkSerial`/`PollAtkMercury`/`PollPulsar`/`PollAtkBitmouse`/`PollChimera` 都在 `PollKysona` 前（`:62-69`）
  - `PollArbiter` 在 `PollLamzu` 前（同为 373E，`:76-77`）
  - `PollMachenike` 在 `PollMiscMice`（Ajazz/Delux 共用 248A）前（`:108-109`）
  - `PollRazerHeadsets`/`PollBarracudaPro` 在通用 `PollRazer` 前（`:118-119`）
  - `PollLogitechG435`/`PollAstro` 在 HID++ 的 `PollLogitech` 前（`:121-123`）
  - `PollVgnClient` 放到已认领 VGN 共用 id 的驱动之后（`:89`）
- **用户忽略**：`HKCU\Software\WirelessBattery\IgnoreDevices` 存 `VID:PID` 列表，命中则所有驱动都不打开该设备，用于"某设备被轮询会出问题"的逃生门（`U:src/devices.cpp:16-37`、`U:README.md:288`）。
- **共享句柄容错**：HID 接口被厂商应用独占（`ERROR_SHARING_VIOLATION`）时仍从路径解析 VID/PID，供识别/展示占位（`U:src/hid.cpp:36-50`）。

### 3.2 设备状态语义（这是我们最该吸收的部分）

上游 `DeviceStatus`（`U:src/devices.h:5-23`）：

| 字段 | 语义 | 对我们的价值 |
|---|---|---|
| `key` | 跨轮询稳定身份（含接收器换口的场景）；托盘图标 id | 重命名/阈值/历史/隐藏都按 key 持久化 |
| `name` / `via` | 展示名 / "2.4 GHz receiver"、"Bluetooth"、"USB cable" | `via` 是**传输**，不是协议；展示要用 |
| `kind` | Other/Mouse/Headset/Keyboard/Gamepad/Phone/Microphone/Speaker | 图标与分组 |
| `percent` | 0..100，**-1 = 当前未知**（不是 0） | "不伪造数据"的模型基础 |
| `detail` | 额外分量，如 `L 80%, R 75%, case 40%` | 多电池设备 |
| `levelText` | 只有粗略档位时的文字（Low/Full…） | XInput 兜底 |
| `note` | 无读数时的建议文案（被 UI 翻译） | 空态提示 |
| `charging` / `full` | 充电中 / 充满且在座 | 状态图标 |
| `asleep` | 接收器有应答但设备在休眠 → 显示最后值（默认变暗） | 休眠语义 |
| `connected` | 驱动**明确**报告离线无线链路；默认隐藏 | 离线语义（不靠"没回包"推断） |
| `millivolts` | 电压（0=未知） | 高端展示/诊断 |
| `graceSec` | 设备停止应答后保留最后值多久（内部还会夹到至少 2 个轮询周期） | 防闪烁 |
| `vid` | USB/蓝牙 HID vendor id | 品牌色 |

**状态机**（`U:src/main.cpp:850-925`）：

1. 每轮把 `results` 按 `key` 并入 `g_entries`；记录 `lastPct`、`lastSeen`。
2. 上一轮在线、本轮没出现、且 `graceSec` 未过期、且同名设备没有从别的链路应答 → 标 `asleep`，percent 置 -1、显示 dim 的最后值（`:894-905`）。
3. 超过 grace 仍没出现 → 标 `connected=false`、`asleep=false`、percent=-1（`:906-916`）；默认隐藏，开启 "Show disconnected devices" 才显示暗淡的 ×（`U:README.md:39`）。
4. 显示层：`IsShown` 默认只显示 `connected`；`asleep` 时 dim 显示最后值（可关）；低电量红色（默认 15%，可配 5–40%）；充电蓝色⚡，充满且在电上绿色✓（`U:README.md:33-54`、`U:src/main.cpp:270-280,719-734`）。

**告警与历史**：低电提醒只在"真实、新鲜、0–100、已连接、非休眠、非充电"时触发，一次触发后电量回升 >10 个百分点或开始充电再武装（`U:README.md:65-84`）；历史仅本地记录已连接、醒着、有数字读数的设备，30 分钟采样一次、保留 30 天、约 2 MB 上限，`estimate` 只在"≥4 点、≥2 小时、下降 ≥3 个百分点、读数新鲜、连续放电"时给出，且明示"不是设备报告的续航"（`U:README.md:86-93`、`U:src/runtime_estimate.h:1-60`）。设备身份用 `--json` 的 `key`，重命名存储不改变驱动匹配（`U:README.md:55-61`、`U:src/known_devices.cpp:71-107`）。

### 3.3 刷新与失败策略

| 机制 | 事实 | 出处 |
|---|---|---|
| 默认轮询 | **60 秒**，菜单可选（"Check battery every"） | `U:src/main.cpp:56-63,124,1148` |
| 登录/唤醒/设备到达后的追跑 | 登录后固定 3s/7s/15s 再轮询；唤醒/到达后 3s/5s/10s/15s，直到设备有读数；设备事件可提前唤醒（每步最多 4 次） | `U:src/refresh_policy.h:14-80` |
| 单设备超时 | 各驱动自己设（例：HyperX Haste2 reply ≤1s、Dart 1s、VAXEE 100ms+最多 3 次读、Areson 1.8s×2、RAWM ~1s、Crociris 1s 预算、Wobkey 4s、Rapoo 每接口最多等 4s） | 各 `U:src/drivers/*.cpp` |
| 慢查询降频 | 例：Razer BlackShark V3 系列 **5 分钟**一次；ATK/Evision 键盘接收器**最多 10 分钟**一次；GameSir Cyclone Pro BLE **2 分钟** | `U:README.md:109,135,437` |
| 失败策略 | 读失败**什么都不报**（不伪造 0%）；由 grace/离线状态机兜底；`asleep` 只在"接收器回了但设备睡"时置位，不靠丢包推断 | `U:src/devices.h:13-22`、`U:src/main.cpp:894-916` |
| 轮询线程 | `PollAllDevices()` 阻塞 1–2 秒，跑在 worker 线程；轮询完 `memory_trim` 归还页 | `U:README.md:442-446`、`U:src/main.cpp:925` |
| 单实例/命令 | `--quit` 单实例；`--list/--json/--dump/--demo/--report` 诊断 | `U:README.md:324-334` |

### 3.4 身份、去重与优先级（同一设备多条路径）

- **物理设备一个稳定 key**：驱动按 VID/PID/设备自身 id 构造；接收器换 USB 口不影响 key（`U:src/drivers/drivers.h:31-44` 的 `StableKey` 注释；各驱动 key 形式如 `kysona:3554:F527`、`areson:25A7:FA07`，`U:README.md:339-343`）。
- **同一设备同时有线和无线**：多数驱动**共享同一个条目/同一个 key**，并**线缆读数优先**：
  - SteelSeries Aerox/Prime、Rival 650 "preferring the cable"（`U:README.md:110`、`U:src/drivers/steelseries.cpp:293`）
  - DAREU "Prefers a valid USB reading when both are connected"（`U:README.md:158`、`U:src/drivers/dareu.cpp:172`）
  - Machenike "A live cable reading takes precedence over the dongle's cached state"（`U:src/drivers/machenike.cpp:48`）
  - Razer 同 key 时 "a live reading wins over an asleep one"（`U:src/drivers/razer.cpp:192`）
- **接收器多设备**：Razer HyperSpeed 一个接收器暴露鼠标+键盘两个槽，驱动按槽位分别查询、按**设备自己报告的产品 ID** 建 key；键盘睡着时鼠标仍更新（`U:docs/razer_hyperspeed.md`、`U:README.md:429`）。同一接收器多型号的（如 MCHOSE 5253）用型号探测/产品名区分。
- **跨驱动去重**：不同链路（如接收器 + 蓝牙）可能各报一条；上游用"同名设备另一条链路在线时，跳过 grace 中的旧条目"（`U:src/main.cpp:889-905`），通用名（含 "wireless device"）不参与同名匹配。
- **品牌色判定顺序**：驱动 key 前缀 → VID → 名字规则（`U:src/brands.cpp:157-169`），特殊规则如 Sony 音频（WH-/WF-/LinkBuds）不算 PlayStation（`:137`）。

---

## 4. 设备与协议能力矩阵

### 4.1 传输方式总表

| 传输 | 发现方式 | 覆盖设备 | 典型可读字段 | 备注 |
|---|---|---|---|---|
| **USB 2.4G 接收器（dongle）** | HID 接口枚举，VID/PID + collection | 绝大多数无线鼠标/键盘/耳机/手柄 | %、充电、少量 mV；部分多设备槽、休眠/离线位 | 上游主战场，约 90% 驱动 |
| **USB 线缆** | HID 接口枚举（同一/相邻 PID） | 无线设备的"有线模式"、有线耳机、部分键盘 | 同左；线缆读数通常最新（优先） | 与接收器条目合并 |
| **USB 专用 base station / 发射器** | HID 接口 | ASTRO A50/A20、SteelSeries Nova Elite、Turtle Beach、JBL、Audeze 等耳机 | %、充电、docked/座充状态 | 常有多接口/串口混合 |
| **蓝牙经典（BR/EDR）** | Windows 设备属性 / HFP 节点 / RFCOMM | 任意 Windows 显示电量的蓝牙音频/键鼠 | Windows 电池属性；专有 RFCOMM 可拿 L/R/盒 | 需配对+连接+驱动上报 |
| **蓝牙 LE（BLE）** | Windows 属性 / GATT 0x180F / 广播 | BLE 键鼠、TWS、Fast Pair 耳机 | 属性 %；GATT 直读；广播可拿 L/R/盒 | 未配对也可读 GATT 的特例见 Marshall |
| **专用无线（Xbox Wireless / PlayStation Link / NearLink）** | Windows.Gaming.Input / HID / 适配器 | Xbox 手柄、PULSE Explore、ATK NK 等 | WGI 真实 mWh→%；或厂商帧 | XInput 只有 4 级 |
| **虚拟串口（COM）** | 设备节点 | ATK N9 Pro/Ultra 一代 | % | 需要厂商 app 关闭 |
| **Windows 通用属性（Settings 里能看到电量的任意设备）** | SetupAPI/DeviceWatcher | 任意蓝牙/BLE 外设 | 单个 %（Windows 只存一个） | 上游兜底，见 §6 |

### 4.2 协议族总表（先看"有几种读法"）

| 协议族 | 代表设备 | 传输 | 读取方式（事实） | 字段 | 上游实现/出处 |
|---|---|---|---|---|---|
| **HID++ 1.0/2.0** | 罗技 Unifying/Bolt/Lightspeed/Nano 接收器、线缆、蓝牙设备、G 系列耳机 | 全部 | 特征发现 0x1004 unified battery / 0x1000 battery level / 0x1001 voltage / 0x1F20 ADC；1.0 寄存器 0x07/0x0D；G435 走 CRC-16 vendor report | %、充电、mV、耳机按电压曲线换算 | `U:src/drivers/logitech.cpp:1-14`、`U:README.md:108` |
| **Razer 90 字节 feature report** | Razer 无线鼠标/键盘/接收器 | receiver/cable | `[0]status [1]tid [2..3]剩余包 [4]协议 [5]长度 [6]class [7]cmd [8..87]args [88]crc(2..87 xor)`；电量 class 07 cmd 80（args[1] 0..255），充电 class 07 cmd 84（args[1]=1） | %、充电 | `U:src/drivers/razer.cpp:1-18` |
| **Razer HyperSpeed 多设备槽** | HyperSpeed 双模接收器 | receiver | 91 字节 feature report：槽查询 class 00 cmd BF transaction 1F/9F；回复 `02` + 两个 3 字节槽（active + 大端 PID） | 分别读鼠标/键盘 | `U:docs/razer_hyperspeed.md:7-15` |
| **Razer 耳机专有** | BlackShark V2/V3 系、Barracuda X/Pro | receiver/cable | BlackShark 自家 vendor（V3 系 5 分钟一次）；Barracuda X Airoha；Barracuda Pro "PA" 协议 | %、充电（部分） | `U:src/drivers/razer_headset.cpp`、`barracuda_pro.cpp`、`U:README.md:109` |
| **SteelSeries vendor HID** | Arctis 系、Nova 系、Aerox/Prime/Rival | receiver/cable/base station | 耳机协议源自 HeadsetControl；鼠标源自 rivalcfg；Nova Elite 独立 base station 交换 | %、充电（部分） | `U:src/drivers/steelseries.cpp:1-6`、`steelseries_elite.cpp:1-3` |
| **Corsair 多协议** | Void 系、Virtuoso、HS80 MAX、Slipstream 键鼠、Sabre V2 Pro | receiver/cable | Void 系输出 `C9 64` → `[2]%`、`[4]` 状态；Bragi GET；Compx 17 字节 | %、充电（部分）、mic-up | `U:src/drivers/corsair.cpp:1-12` |
| **HyperX 多协议** | Cloud 系耳机、Pulsefire Haste2/Haste/Dart | receiver/cable | Haste2：output `50 02` → input `51 02 percent charge .. mV`；Haste 原版：output `0x51` → input byte4 %、byte5 charging/full；Dart：heartbeat `51` → `[4] % [5] charging` | %、充电、Haste2 有 mV | `U:docs/hyperx_haste_2.md:24-39`、`docs/hyperx_pulsefire_haste.md`、`docs/hyperx_pulsefire_dart.md` |
| **Glorious 族 64 字节 feature report** | Glorious、Hitscan、G-Wolves（部分）、WLMouse YING | receiver/cable | 64 字节 feature report、读命令 | %、充电（部分） | `U:src/drivers/glorious.cpp:1-3`、`U:README.md:116-117,144` |
| **Kysona 17 字节帧** | Kysona、Pulsar、ATK/VXE、G-Wolves、HECATE、Taidu、Chimera、Zaopin | receiver/cable | report id `08`，cmd `03` 在线 / `04` 电量；byte16 = `0x55 - sum(0..15)`；reply byte6 %、byte7 充电、byte8-9 mV 大端 | %、充电、mV | `U:docs/zaopin_8k_receiver.md:26-41`、`U:README.md:118,150-152` |
| **Pulsar/Sonix 64 字节 feature** | Pulsar X2 V3/eS、Feinmann、PRO Dongle、ZYWOO | receiver/cable | feature report `08 81 01`（只读）；部分用 B4 状态查询 | %、充电（部分） | `U:README.md:119-123`、`U:src/drivers/pulsar.cpp:1-5` |
| **EVision 04/1A 类** | ATK V75X、VGN/VXE 键盘、Redragon M999、AJAZZ AK992、AULA 部分鼠标 | receiver/cable | output report `04`，cmd `1A`，`FF1C:0092`；键盘另有 report `0x13` cmd `4A` | %、充电（部分） | `U:src/drivers/atk_keyboard.cpp:1-3`、`redragon_evision.cpp`、`aula*.cpp` |
| **SONiX 键盘（20 01）** | AJAZZ AK820 MAX/AK980 V2/AK820 Pro、AULA F108 Pro、EPOMAKER 等 | receiver | vendor report `20 01` on usage page FF60，sum checksum，33 字节 | %（无充电） | `U:src/drivers/sonix.cpp:1-3`、`U:README.md:159-160` |
| **Compx 17 字节（GetPower）** | Corsair Sabre、Cherry XTRFY、DAREU A950、Zenith、Sharkoon、Fantech XD5、Areson | receiver/cable | 17 字节 output/feature，`08 03` 连接 / `08 04` 电量；checksum 使总和=0x55 | %（1% 或 10% 步）、充电、mV | `U:docs/areson.md:18-60`、`U:src/drivers/fantech.cpp`、`zenith.cpp`、`dareu.cpp` |
| **Xenta 被动帧** | Inphic IN10 系、Delux M900 Pro/M800 Pro、Motospeed X6、Attack Shark X6/R1 等 | receiver | 被动 5 字节状态帧（model/event/battery state/percent），无需请求 | %、充电/满、休眠 | `U:docs/inphic.md`、`U:src/drivers/misc_mice.cpp:1-6` |
| **MCHOSE 多代** | L7/M7/A7/K7、A5 Gen1、G3 V2、K7 V2 Ultra+、V9 系耳机、K87 V3、G75 V2 键盘 | receiver/cable | V1 反转 payload；M HUB `0x0900`/report 4D；A5 0x83；G3 `FF01:0010`；耳机 MC/BLK；键盘 GLW | %、充电（部分） | `U:src/drivers/mchose*.cpp`、`U:README.md:170-178` |
| **AULA 多协议** | F75/F87/F99 键盘、X9 Ultra、SC580、HERO 68 Air、A7Pro 耳机 | receiver/cable | report `0x13` cmd `4A`；鼠标 report `04` cmd `1A`；SC580 cmd `0x30`；HERO 20 字节 FF60 cmd 0x87；A7Pro `55 65 01` | %、充电（部分） | `U:src/drivers/aula*.cpp`、`U:README.md:156,179-181` |
| **Keychron Launcher** | M 系鼠标（1K/4K/8K/Ultra-Link）、K5 Max/Q6 Ultra 8K 键盘、Nape Pro | receiver/cable | Launcher feature report `51 06`；Ultra-Link `B5 03`/`B3 06`；Nape `A7 31` | %、电压、充电（键盘需固件补丁） | `U:src/drivers/keychron.cpp:1-3`、`U:README.md:130-131` |
| **Royal Kludge** | MG6 Pro（QiWang）、M3（BeiYing） | receiver/cable | QiWang 64 字节 `GET_BATTER_VOL`；BeiYing `4F 81` | %、电压、充电 | `U:src/drivers/royal_kludge.cpp:1-3`、`U:README.md:166-167` |
| **Rongyuan 3151** | Attack Shark、Ajazz 键盘、MonsGeek M5W | receiver | `F7` dongle status on `FFFF:0002`；byte2 电量 | %（该固件充电不可用） | `U:src/drivers/rongyuan.cpp:1-4`、`U:docs/royuan_2.4g_keyboard.md` |
| **其它键盘直读** | Semico 1A2C、36B0/PMO/VIA、Wobkey、NuPhy、CHERRY、DAREU、AJAZZ | 各自 | `AA 55 CC 33`/`0x0E`、PMO `AA 30`/VIA custom-get、0xBB 被动、NuPhyIO `55 05`、CHERRY `1A`、Togran、SONiX | %、充电（部分） | 见 §12.1 |
| **Airoha RACE 0CD6** | Audeze Maxwell、ENDORFY Viro、AKG N9、Skullcandy 类 dongle | dongle | vendor report `06` / input `07`，属性 `0CD6` | %、充电（部分） | `U:src/drivers/audeze.cpp:1-4`、`endorfy.cpp`、`akg.cpp` |
| **RFCOMM/SPP 专有** | Galaxy Buds、Nothing/CMF、Sony WH/WF、Soundcore | 蓝牙 | 各自服务 UUID + 帧（连上后查询/推送） | 左/右/盒、充电 | `U:README.md:195-199` |
| **BLE 广播** | AirPods/Beats、Fast Pair 耳机、Marshall | BLE | Apple proximity `0x07`；Fast Pair service data `FE2C`；Marshall 标准 `180F/2A19` 免配对读 | 单值或 L/R/盒、充电 | `U:src/drivers/airpods.cpp:1-3`、`ble_adverts.cpp`、`fastpair.cpp:6,61`、`U:docs/marshall.md` |
| **PlayStation 输入报告** | DualShock 4 / DualSense / Edge | USB/蓝牙/适配器 | 输入报告内嵌电量/充电位 | %、充电 | `U:src/drivers/playstation.cpp:1-4` |
| **Xbox Windows.Gaming.Input** | Xbox 无线适配器/USB/蓝牙手柄；GameSir GIP 模式 | 全部 | `IGameControllerBatteryInfo` mWh 剩余/满 → % | 真实 %、充电 | `U:src/drivers/xinput.cpp:54-115` |
| **Switch Pro 输入报告** | 057E:2009 兼容手柄 | 2.4G/蓝牙 | report `0x30` 电量字节（需主机先开启完整报告） | 5 档 | `U:src/drivers/switch_pro.cpp:1-2` |
| **SDL/厂商手柄帧** | 8BitDo、GameSir、Flydigi、BEITONG | 各自 | vendor FF7A/FFA0/00FF/FFF0；SDL `01`/`04` 报告 | %、充电 | `U:src/drivers/eightbitdo.cpp`、`gamesir*.cpp`、`flydigi.cpp`、`beitong.cpp` |
| **Windows 通用蓝牙属性** | "Settings → 蓝牙和设备"里能显示电量的任意设备 | 经典/BLE | `DEVPKEY_Device_BatteryLevel`（GUID `104EA319-6EE2-4701-BD47-8DDBF425BBE5`, pid 2）+ 连接状态 | 单个 % | `U:src/drivers/generic.cpp:78,263+` |
| **HID Battery Strength** | 任意暴露标准电池 usage 的 HID 设备 | HID | page 0x06 usage 0x20（Generic Device Controls）/ page 0x85 usage 0x65（Battery System），feature 或 input report | % | `U:src/drivers/generic.cpp:74-80,203-260` |
| **HeadsetControl（外部 exe）** | HeadsetControl 支持的全部耳机 | 各自 | `headsetcontrol -b -o json --timeout 1500` 解析 JSON | %、充电 | `U:src/drivers/headsetcontrol.cpp:1-4,208-246` |

### 4.3 按厂商设备矩阵

> 说明：下表把 README 的 113 行压缩为可读分组；每行覆盖该厂商/子系列的全部传输与型号。最后一列为"硬件验证程度/注意事项"（完整逐行清单见 `U:README.md:101-212`）。"可读字段"默认含电量与充电，特别项单独标。

#### A. 一线外设与音频厂商（独立协议栈）

| 厂商/系列 | 代表型号 | 传输 | 协议/读取方式 | 可读字段 | 验证/注意 |
|---|---|---|---|---|---|
| **Logitech（含 G 系列）** | 鼠标/键盘：MX、G304/G502/G PRO、K380/K915、Lightspeed/Unifying/Bolt/Nano；耳机 G533/G535/G633/G635/G733/G933/G935/G PRO X；G435 用 0ADF dongle | receiver/cable/蓝牙 | HID++ 1.0/2.0 特征发现：0x1004/0x1000/0x1001/0x1F20；1.0 寄存器 0x07/0x0D；G435 CRC-16 vendor `GET_BATTERY`(mV) | %、充电、mV；耳机电压曲线换算；G435 仅 mV | 硬件测试充分；C542 接收器不讲话 HID++（`U:README.md:431`）；G533 等老耳机 1.0 |
| **ASTRO（罗技旗下）** | A50 X、A50 Gen5/4/3、A20 Gen1/2 | USB base station | 厂商 vendor 报告（A50 X/Gen5 与 Gen3/4 协议不同） | %、充电、docked | 部分型号硬件测试（`U:README.md:107`） |
| **Razer** | 无线鼠标/键盘/接收器；BlackShark V2 HyperSpeed、V2 Pro(2020/2023)、V3/V3 Pro/V3 X、V3 Pro Xbox；Barracuda X(2022)、Barracuda Pro；HyperSpeed 多设备接收器 | receiver/cable/蓝牙（Windows 属性） | 见 §4.2：90 字节 feature report；HyperSpeed 双槽；BlackShark/Barracuda 专有 | %、充电；HyperSpeed 鼠标/键盘分别显示 | 大量真机；Synapse 共存时要处理共享 feature 邮箱 |
| **SteelSeries** | Arctis 1/7/7+/9/Pro/Pro Wireless、Nova 3P/5/7/7X/7P/Pro Wireless、Nova Elite（base station）、GameBuds；Aerox 3/5/9、Prime、Rival 3/650/Pro | receiver/cable/base station | vendor HID；耳机协议源自 HeadsetControl；鼠标源自 rivalcfg；线缆优先 | %、充电（部分） | 真机多；GG 只显示 3 格时数值仍对得上 |
| **Corsair** | Void/Void Pro/Void Elite/HS70/HS80；Virtuoso、Void V2、HS80 MAX、Slipstream 键鼠（新接收器 2B00）、Dark Core RGB Pro SE；Sabre V2 Pro | receiver/cable | 输出 `C9 64`；Bragi GET（只读）；Compx 17 字节 | %、充电（部分）、mic-up | 新老接收器差异大 |
| **HyperX** | Cloud III/III S/II/II Core/Alpha/Alpha 2/Flight/Flight S/Stinger Core/Core DTS；Pulsefire Haste 2/Haste 2 Core/Mini、Dart、Haste 原版 | dongle/cable | 3 套鼠标协议 + 耳机 vendor 报告（见 §4.2） | %、充电、Haste2 mV | 三个鼠标协议都有独立协议笔记，真机验证充分 |
| **Glorious / Hitscan** | Model O/O-/D/D- Wireless、Model O2/D2 PRO；Hitscan Hyperlight X/N | dongle/cable | 64 字节 vendor feature report（Hitscan 同协议） | %、充电（部分） | Glorious Model O 真机 |
| **Cooler Master** | MM712、MM731 | dongle/cable | vendor interface 3，usage page FF0A，32 字节无 report id | %（只读） | MM712 真机 |
| **ASUS ROG** | Strix Scope II 96 RX、Azoth、Falchion 键盘；Keris/Harpe/Gladius/Chakram/Spatha/Pugio/Strix Impact 鼠标；Pelta 耳机；Cetra Open Wireless 耳塞（L/R/盒）；Omni Receiver | receiver/cable | ROG HID battery query `[id][12][sub]`（sub 01 键盘 / 07 鼠标） | %、充电；Cetra 分量 | 来源 G-Helper/peripheral-battery-tray |
| **Sony（游戏+音频）** | DualShock 4、DualSense/Edge；INZONE H9 II/H5/Buds、H9/H7 一代；PULSE Explore（PlayStation Link）；WH/WF 蓝牙耳机 | USB/蓝牙/适配器/dongle | PS 输入报告电池位；INZONE HCI；PULSE `82/A0/B0`；WH/WF RFCOMM | %、充电；PULSE 左右；WH/WF 左右/盒 | 各子协议独立，部分真机 |
| **Samsung Galaxy Buds** | Buds/+/Live/Pro、Buds2/2 Pro/FE/Core、Buds3/3 Pro/3 FE | 蓝牙 RFCOMM | 三星 RFCOMM 状态帧（只读，推送） | 左/右/盒、充电 | 需关 Galaxy Buds app；托盘显示较低耳 |
| **Apple AirPods / Beats** | AirPods Max（200A/201F）、AirPods Pro 1/2/3、AirPods 1–5、Beats | 蓝牙 BLE 广播 | Apple proximity pairing `0x07`；单值或 L/R/盒，10% 步 | %、充电、L/R/盒 | 需配对型号匹配 |
| **Jabra** | Evolve2/Evolve3 等 GNP 耳机（USB 或 Jabra Link dongle） | USB/dongle | GNP class 12 / op 02，FF00 usage 05/01，地址 08（USB）/04（dongle） | %、充电 | 无产品 id 列表，按协议识别；DECT base 不做 |
| **JBL** | Quantum 650/520/650X/850/950/950X/350/610/360/810 Wireless | dongle/cable | HIDV3（FF12）/ HIDV1（GET 49 电量、GET 68 链路） | %、充电 | 真机 650/350/810 |
| **Audeze** | Maxwell、Maxwell 2、Maxwell Xbox 版（dongle 或 USB-C） | dongle/cable | vendor report 06 / input 07，属性 0CD6（只读） | %、充电；dongle 卡死有专门状态 | 协议笔记 `U:docs/audeze_maxwell.md` |
| **Turtle Beach** | Stealth 700 Gen 3（USB 发射器/耳机线） | USB | Airoha RACE 0x9942 + CoAP GET /GSI（Swarm II 同款） | %（无充电标志） | 真机（HaloBattery） |
| **Poly / Plantronics** | Voyager 8200 UC 等，BT600 适配器（047F:02EE/02F7） | USB 适配器 | report 07 get/set，电池项 `0A 1A`（只读） | % | 移植自 HaloBattery |
| **AKG / ENDORFY** | AKG N9 Hybrid（0ECB:20D9）；ENDORFY Viro Wireless（3738:0072） | dongle | Airoha RACE `0CD6` | %（ENDORFY 无充电） | AKG 真机 |
| **NZXT** | Lift Elite Wireless（1E71:2101/2129） | dongle/cable | vendor 读 `4E 02 81 .. B0` | % | 来源 NZXT CAM 抓包 |
| **Fractal Design** | Scape 耳机 | dongle/USB-C | vendor `GET_DEVICE_STATE`（FF00） | %、充电 | 来源 Adjust Pro web app |
| **Alienware** | AW920H（413C:A515）；蓝牙走 Windows | dongle | vendor `06 C0 0A` on FF13:0001，reply 07 XOR 校验 | %、充电、critical-level | 来自 AWCC 插件解码 |
| **Marshall** | EMBERTON II 音箱 | 蓝牙（经典配对 + LE 直读） | 标准 BLE `180F/2A19` 免配对读；LE 地址来自 Fast Pair Message Stream | %（10% 步；无充电） | 需 FastPair 地址；`U:docs/marshall.md` |
| **Anker Soundcore** | Life/Space/Liberty 等 | 蓝牙 RFCOMM | Soundcore vendor RFCOMM 或 Fast Pair stream | 左右/盒等 | 来源公开抓包 |
| **Nothing / CMF** | Nothing Ear (3a)/(a)/(2)/Ear、CMF Buds | 蓝牙 RFCOMM | NT Link service `aeac4a03-...`，`0xC007` 查询 | 左/右/盒 | 只读写查询 |
| **MOONDROP / MAONO** | GM01 Pro（35D8:0133）；MAONO PD200W（352F:040E） | dongle/cable | WalkPlay report 4B；MAONO 实验性查询 | %、充电（MOONDROP 线缆） | 真机 |
| **GameSir / 8BitDo / Flydigi / BEITONG（手柄类）** | GameSir Nova Lite/Nova2/G7 Pro/Cyclone 2/Cyclone Pro/Super Nova/Tarantula Pro；8BitDo Ultimate 2/2C/Pro 系；Flydigi Vader 5 Pro/Apex 5；BEITONG KP20/KP40D | 2.4G/USB/蓝牙 | vendor FF7A/FFA0/FFF0/00FF、BLE 8650、SDL 报告、Xbox GIP 模式 | %、充电；部分只有粗粒度 | 详见 §4.3.C |

#### B. 国产共享协议族与长尾品牌

| 厂商/系列 | 代表型号 | 传输 | 协议/读取方式 | 可读字段 | 验证/注意 |
|---|---|---|---|---|---|
| **Kysona 族**（Kysona / Pulsar 部分 / ATK-VXE / G-Wolves / Hitscan 早期 / Redragon Compx / Taidu / HECATE / Chimera / Zaopin） | Kysona M600/Mercury、ATK VXE X3 Pro/Dragonfly R1/R1 Pro Max/R1 SE+、ATK F1 Extreme、HECATE G5M PRO、Chimera V2、Taidu TSG808、Redragon M916 Pro、Zaopin 8K 接收器 | dongle/cable | report `08` cmd `03/04`，byte16 `0x55-sum`，reply byte6 %/byte7 充电/byte8-9 mV | %、充电、mV（部分型号无充电位） | 真机多；5% 步与 1% 都有 |
| **Pulsar Sonix 系** | X2 V3 / X2 V3 eS（1K/8K dongle 或线）、Feinmann FO1/8K、PRO Dongle、ZYWOO The Chosen X | dongle/cable | 64 字节 feature `08 81 01`；PRO/ZYWOO 用 B4 状态查询 | %、充电（Feinmann 无） | 部分真机 |
| **ATK 系列** | ATK/VXE NK（NearLink）、ATK Zero（线+8K L0/L1）、ATK 8K Nano、ATK Z87、Mercury I/M1、N9 Pro/Ultra 一代 | dongle/cable/COM | BITMOUSE report 08 on FF05；Holtek GetConfigData(82) FF10；FF22 三条 64B 查询；Z87 EVision FF1C；COM 9600 8N1 | %、充电；Mercury 无充电 | N9 Ultra 真机；Mercury 协议来自 Joule PR |
| **EVision 族** | ATK V75X、VGN/VXE S99/V98 Pro/N75 Pro/VXE75/V87、Redragon K1NG M999、AJAZZ AK992、Feker Alice80、Wobkey Rainy75（Evision 控制器） | dongle/cable | output `04` cmd `1A` on `FF1C:0092`；键盘 report `0x13` cmd `4A` | %、充电（部分） | 真机；Redragon M999 磁吸座充电可读 |
| **SONiX 键盘** | AJAZZ AK820 MAX/AK980 V2/AK820 Pro、AULA F108 Pro、EPOMAKER EA75/P75/EA75、Galaxy100 等 20 个 receiver | dongle | vendor `20 01` on FF60，sum checksum，33 字节 | %（无充电） | 部分真机；精确 VID:PID/collection 要求 |
| **36B0 / PMO / WOBKEY / URX / EVO80** | PMO Wave 75 RGB/Aurora 80/P75/80、URX Core75、EVO80 | dongle/cable | PMO Hub `START_COMM/GET_BATTERY(AA 30)/STOP_COMM`；URX VIA custom-get | %（无充电） | 部分真机 |
| **Wobkey** | Rainy75 V2 | dongle/cable | 被动 `0xBB` 遥测约 5 秒一次（0x93 enable） | %、充电 | 真机 |
| **NuPhy** | WH80（USB/蓝牙）、Halo96 V2（自定义固件 2.4G） | USB/蓝牙/2.4G | NuPhyIO `55 05 00 21 01 20`，byte8 低 7 位 %、高位充电；Halo96 被动 usage 序列 | %、充电 | 真机 WH80；Halo96 需刷固件 |
| **Inphic** | IN10R MAX/IN10 MAX（KP 8K 接收器 1D57:FA65） | dongle | 被动 `000A:0000` 5 字节状态帧（Xenta 解析） | %、充电/满 | 真机 model 73；model 74 未测 |
| **EWEADN（前行者）** | S7 Max/Pro/Ultra、S9 Max/Pro/Ultra/Ultra+（372E:103D/1027） | dongle/cable | feature `03` on FF00:0001：link `47`、device basics `4F 81` | %、充电 | 来源官方 web driver |
| **DAREU** | EM901X、A84、A980 Pro、A950 Wing、EK75 Pro | dongle/cable | 4 套协议：Compx 17B / SONiX / Togran / Compx(08 04) | %、充电、mV（A950） | 多型号真机 |
| **MCHOSE** | L7/M7/A7/K7 系、A5 Gen1、G3 V2、K7 V2 Ultra+、K87 V3、G75 V2 键盘、V9/V9 Pro/Pro 2 耳机 | dongle/cable | 多代：V1 反转 payload、M HUB `0x0900`/4D、A5 0x83、G3 FF01:0010、MC/BLK 耳机、GLW 键盘 | %、充电（部分） | 真机 G3 V2/K7/V9 Pro 2 |
| **AULA** | F75/F87 Pro/F99、X9 Ultra、SC580/SC580X、HERO 68 Air、A7Pro 耳机 | dongle/cable | report `0x13` cmd 4A；鼠标 report 04 cmd 1A；SC580 cmd 30；HERO FF60 0x87；A7Pro `55 65 01` | %、充电（部分） | SC580/HERO/A7Pro 真机 |
| **Keychron** | M4 等 M 系鼠标（1K/4K/8K/Ultra-Link）、K5 Max、Q6 Ultra 8K、Nape Pro 轨迹球 | dongle/cable/蓝牙 | Launcher `51 06`；Ultra-Link `B5 03/B3 06`；Nape `A7 31` | %、电压、充电 | 键盘 2.4G 需固件补丁；Stock 只能蓝牙 |
| **Royal Kludge** | MG6 Pro（QIWANG） | dongle/cable | QiWang 64 字节 `GET_BATTER_VOL` | %、电压、充电 | 真机 |
| **Royal Kludge M3** | M3 | dongle/cable | BeiYing `4F 81`（与 EWEADN 同族） | %、充电 | M3 真机 |
| **Rongyuan 3151** | Attack Shark R3/AJAZZ AJ179 Pro/K86/X82 PRO HE、MonsGeek 2.4G | dongle | `F7` status on FFFF:0002 | %（该固件充电不可靠） | MonsGeek 真机 |
| **Semico** | Attack Shark X98、AJAZZ AK820 Max Plus | dongle/cable | `AA 55 CC 33` / `0x0E` on FF02:0002 | %（充电标志省略） | 来源 openajazz/Kraber |
| **RAWM / SADES** | Leviathan V4、ES21/ER21/ES11/G11、SADES Dagger | dongle/cable | RAWM HUB JSON 事件流（query+ping，ESB 通道） | %、充电 | Leviathan V4 真机 |
| **VGN** | OMNISOUND（ZHOUTING）耳机；VGN/VXE 鼠键（VID 多） | dongle | V HUB "ZHOU" `55 65 01`；鼠键按 VGN HUB 协议 | %、充电（部分） | 真机 |
| **VGN 协议实现（vgn_protocol）** | 同上 | dongle | VGN 客户端只读查询集合 | % | 来源 VHUB 4.0.11 |
| **VGN Client 生态** | VGN/VXE 键盘鼠标、部分 3151/320F/3554 设备 | dongle | 读 VGN HUB 客户端协议 | % | 在其它共用 VID 驱动之后运行 |
| **Motospeed / Delux / Ajazz** | Motospeed X6、Delux M900 Pro/M800 Pro、Ajazz AJ179/AJ159/AJ139 V2 | dongle/cable | Xenta 被动帧、0xF7 轮询 + feature 05、AJ139 BLE | %、充电（部分） | 各自抓包 |
| **Incott / Wraith** | G23 Pro/G23 V2/G24；Wraith W1 | dongle/cable | 推流 input report 09 / 按钮读 86 | %、充电（部分） | 首次真机 G23 Pro |
| **Attack Shark 等** | R3、K86、X82、X6/R1、X11、X3 Max、V3 Pro、V6、X98 等 | dongle/蓝牙 | Rongyuan / Xenta / Semico 共享协议 | %、充电（部分） | 以协议族归类 |
| **Fantech** | Helios Go XD5、Raigor III/II WG10、1EA7:0064/0066 通用 2.4G Mouse | dongle/cable | Compx 17B `08 04`；FFB5:0001 `B5` 状态页 | %、充电（部分） | XD5/Raigor III 真机 |
| **Areson** | Redragon M915RGB-WL、M913 原版（25A7:FA07/FA08） | dongle | 17 字节 GetPower，10% 步 + 充电 | 10% 步 %、充电 | 真机 |
| **Redragon（Compx/Evision/Sinowealth）** | M916 Pro 1K、M913 Impact Elite、G49、M999 | dongle/cable | Kysona / Evision / Sinowealth 各自协议 | % | 部分真机 |
| **CHERRY** | MX 3.0S Wireless（046A:00EA/00E3）、Cherry XTRFY 鼠标（046A:0330） | dongle/cable/蓝牙 | `1A` on FF1C:0092（键盘）、Compx 17B（鼠标） | %、USB 供电状态 | 真机 |
| **Akko** | Air 01（3151:5006）、V9 Dash Pro/Ultra（38EE:000C/000F） | dongle | Rongyuan `F7` / `1A` on FF1C | %、充电 | 真机 |
| **Darmoshark** | M3/M3S（Telink 接收器）、M3S Pro（1915:0723） | dongle | 厂商 identity/battery report；FF0A 状态查询 | % | 无真机记录（README 未列真机） |
| **Machenike** | L7 Arctic/L7 Mix（248A:FF4C/FF4E） | dongle/cable | feature `51 06`（21 字节），byte11 %、byte12 充电 | %、充电 | 真机，协议笔记最完整之一 |
| **Niceboy** | MW400（248A:FA02） | dongle | report `05` 查询 `24 FA 48`，电压表映射 10% 步 | 10% 步 %、充电 | 真机 |
| **MSI** | VERSA 300 ELITE Wireless、CLUTCH GM31/GM41 | dongle/cable | `0F 20` on FF0D:00B0 / FF00:0001；GM41 被动 `A0 07` | %、充电 | VERSA/GM31 真机 |
| **Arbiter Studio** | Akitsu/Akitsu 3950/Akitsu Medium | dongle/cable | feature `0x83` on FFFF:0000 | %、充电 | 部分真机 |
| **CROCIRIS（西伯利亚）** | X7/X7 Ultra Max（36F9:AA20） | dongle | WebHID 协议 `AA/55` framed `0x12` status on FF0A:0001 | %、充电 | 真机 |
| **Press Play** | ICARUS V2 Nearlink（7768:6434） | dongle/蓝牙 | output `06 05` on FFB2:0001；蓝牙走 Windows | % | 真机 |
| **LAMZU** | Maya X/Maya/Maya M/Tachi/Inca/Paro/Thorn/Atlantis Mini/Orcus | dongle/cable | vendor feature report（Aurora） | %、充电 | 无真机记录 |
| **G-Wolves** | Fenrir/Fenrir Pro/HTX/HTS/HSK/HTR/HT-S2/Lycan/WARG/VUK | dongle | Glorious 或 Kysona 协议 | % | 协议二选一 |
| **WLMouse** | YING Magnesium、Beast X 8K/Mini/Pro/MAX、Sword X 8K、Beast X V2、Beast G | dongle/cable | vendor feature report，status `0x83` | %、充电 | YING 真机 |
| **Finalmouse** | UltralightX、Starlight X | dongle | xpanel vendor GETs on FF00（只读），wake 命令，电压曲线 | %、充电 | 两型号真机 |
| **Endgame Gear** | OP1w 4k v1/v2、XM2w 4k v1/v2（3367:1970）、XM2we（3367:1961） | dongle | feature A1 / 电量 B4 | %、充电 | 4K 真机 |
| **VAXEE / ZYGEN** | VAXEE 4K（3057:1002）、8K（3057:2002）、NP-01S V2 线缆（3057:1011） | dongle/cable | 64B feature `0E A5 0B/10 01 01`，读回 byte5 0–20 ×5 | 5% 步 %、充电 | 真机；`U:docs/vaxee.md` |
| **ZOWIE** | EC1/EC2/EC3-CW、DW 系、U2 | dongle | 社区逆向的只读 command | %、充电 | 无上游真机记录 |
| **Ninjutso / Sprime** | Sora/Sora V2/Ten/Ten Air；Sprime PM1 | dongle/cable | NinjaForce feature 05/06（Sora V2 0x15，Ten 0x13/12/11）；Sprime 同族 0x05/0x15 | %、充电（部分） | 真机 Sora V2/Ten Air/PM1 |
| **Scyrox** | V12M（395A:0006） | dongle | S-Center profile 读（output 08 cmd 01 on FF00:0002） | %（无充电） | 无真机记录 |
| **IPI PIAO** | IPI PIAO / PIAO-2.4G 无线鼠标（372E:1014 接收器；另有同族有线 PID） | dongle/cable | BeiYing 协议（`U:src/drivers/piao.cpp:1-4`） | % | README 无独立表行，驱动存在 |

#### C. 游戏手柄/控制器

| 系列 | 代表型号 | 传输 | 协议/读取方式 | 可读字段 | 备注 |
|---|---|---|---|---|---|
| **Xbox** | Xbox One/Series 手柄（无线适配器/USB/蓝牙）；手柄上耳机 | 适配器/USB/蓝牙 | `Windows.Gaming.Input` BatteryReport（mWh→%）；XInput 四级兜底 | 真实 %、充电；XInput 仅 4 级 | 只对 MS（045E）与 GameSir GIP（3537）走 WGI 真实 %（`U:src/drivers/xinput.cpp:54-56`） |
| **PlayStation** | DualShock 4、DualSense、DualSense Edge | USB/蓝牙/无线适配器 | 输入报告电量位 | %、充电 | 布局参考 Linux hid-playstation |
| **PlayStation Link** | PULSE Explore 耳塞 | PS Link 适配器（054C:0ECC） | feature `82/A0/B0` on FF01:0020；B0 byte55 低/高半字节 = 左/右 0–15 | 左/右（0–15→%）；无充电/盒 | PULSE Elite 不做 |
| **Switch Pro 兼容** | GameSir Tarantula Pro（Switch 模式）等 057E:2009 | 2.4G/蓝牙 | HID report `0x30` 电量字节 | 5 档 | 需要 Steam Input 等先开启完整报告 |
| **Steam Controller** | Valve Steam Controller | USB dongle | Linux hid-steam 输入报告布局 | 电量/充电 | 无 README 表行；有驱动，`U:src/drivers/steam_controller.cpp:1-2` |
| **GameSir** | Nova Lite/Nova 2/Nova 2 Lite、G7 Pro、Cyclone 2、Cyclone Pro、Super Nova/Tarantula Pro | 2.4G/USB/蓝牙 | FF7A vendor；FFF0（Cyclone 2）；BLE 8650/865F（Cyclone Pro）；SAPJoy FFA0 | %、充电；Cyclone Pro 充电中无 % | 各自协议文件 |
| **8BitDo** | Ultimate 2/2C/3-mode/2.4G/Bluetooth、Pro 3/Pro 2、SN30 Pro、SF30 Pro | 2.4G/USB/BLE | vendor status query（FF7A/FFA0）+ SDL input report 01/04 | %、充电 | SDL 协议参考 |
| **Flydigi** | Vader 5 Pro、Apex 5 | 2.4G/USB | vendor FFA0:0001 info query，0–5 档映射 | 0/20/…/100%、充电 | 粗粒度 |
| **BEITONG（北通）** | KP20/BTP-KP20、KP40D | 2.4G 接收器（XInput 模式） | vendor "get base info" 00FF，report 02，nibble 混淆 payload | %、充电 | 无需 iControl |

#### D. 操作系统通用路径与桥接

| 来源 | 覆盖 | 条件 | 可读字段 | 限制 |
|---|---|---|---|---|
| **Bluetooth（Windows 属性）** | Settings → 蓝牙和设备里显示电量的任意设备（耳机、Ble 键鼠、TWS） | 已配对 + 已连接 + Windows/驱动上报；经典蓝牙经 HFP，BLE 经 GATT 缓存 | 单个 % | 音箱无 HFP 时没有；Windows 只存一个值 |
| **Fast Pair 广播** | 支持 Fast Pair 的 TWS（Jabra Elite、Pixel Buds、部分 Sony/JBL/Bose） | BLE 广播服务数据 `FE2C` + 与已配对设备按地址匹配 | 左/右/盒、充电 | 需要 BLE 广播 watcher |
| **Generic HID** | 暴露标准 HID *Battery Strength*（page 0x06 usage 0x20）或 Battery System（0x85 usage 0x65）的设备 | feature 或 input report；排除 UPS/电源、绘图板、已由蓝牙报告的同一容器 | % | 厂商私有 2.4G 接收器通常不暴露该 usage |
| **Xbox WGI/XInput** | Xbox 手柄与 GIP 模式第三方手柄 | WGI 报告存在且非 NotPresent；XInput 需手柄在槽位 | 真实 % 或 4 级 | 第三方 pad 多报 "Full" |
| **HeadsetControl 外部 exe** | HeadsetControl 支持的全部耳机 | 可执行文件在 exe 旁或 PATH；每轮异步、结果缓存 | %、充电 | 外部进程；GPL-3.0 边界 |
| **Phone Link** | 已连接 Android 手机 | 读 Phone Link 写盘的 StartMenuCompanion.json | %、充电 | 未公开 API，可能随更新失效（`U:README.md:428`） |

---

## 5. 发现、识别与优先级规则（可被我们照抄的"策略事实"）

### 5.1 驱动匹配顺序（上游经验清单）

1. **先枚举一次 HID，所有驱动共用**，不在驱动内部反复枚举（`U:src/devices.cpp:44`）。
2. **专用驱动先于通用驱动**：同一个 VID 有多个品牌/多代协议时，靠顺序 + claim 解决（§3.1 列表）。
3. **通用兜底一定最后**，且 `PollBluetooth` 在 `PollGenericHid` 之前——注释理由是"Windows 自己的电量对同一设备更有权威"（`U:src/devices.cpp:157-159`）。
4. **兜底只处理未被认领的 VID:PID**（`ctx.IsClaimed`），避免同一设备出现两条。
5. **未知设备绝不写**；驱动只发只读查询（`U:README.md:442-446`）。
6. **识别不只靠 VID:PID**：还要 collection usage page/usage、report 长度、产品字符串、接口号；部分型号共用 PID，用型号探测/产品名区分（如 MCHOSE 5253、NuPhy Halo96 要求产品名 `NuPhy Halo96 V2 Dongle`、Keychron 键盘、RAWM 型号）。

### 5.2 同一设备多路径优先级（按可靠性排序）

| 场景 | 规则 | 出处 |
|---|---|---|
| 线缆 + 接收器同时在线 | 线缆读数优先（视为最新/最真实） | SteelSeries/DAREU/Machenike 实现 |
| 接收器缓存 vs 实时 | 有实时回包用实时；接收器缓存只在无回包时被 grace 兜住 | `U:src/main.cpp:894-905` |
| 同一 key 有 asleep 与 live 两条 | live 优先 | `U:src/drivers/razer.cpp:192` |
| 蓝牙 / 接收器双链路 | 同名且另一条链路在线时，跳过 grace 中的旧条目 | `U:src/main.cpp:889-905` |
| 一个接收器多个设备 | 按槽位/设备自身 PID 分别建 key，分别显示 | `U:docs/razer_hyperspeed.md` |
| 通用名设备 | 含 "wireless device" 等通用名不参与跨链路同名去重（避免误合并） | `U:src/main.cpp:889` |
| 绘图板/数位屏 | 一律跳过，不轮询（曾把 XP-Pen Y 轴翻转，issue #232） | `U:src/drivers/generic.cpp:203-211` |
| 用户明确忽略 | `IgnoreDevices` 注册表，按 VID:PID 全链路禁止打开 | `U:src/devices.cpp:16-37` |

### 5.3 失败/超时/退避

- 驱动级：查询有明确 deadline（几百 ms 到几秒）；超时返回"无结果"，**不发 0%**；下一次轮询重试。
- 接收器无应答但设备物理在位：条目保留、`asleep`/grace 语义；超过 grace 标离线。
- 打开失败（被占用）：不缓存"永久失败"，按退避重试（如 Logitech 类驱动 10 分钟退避）。
- 慢/写帧设备：降低查询频率（BlackShark 5 分钟、ATK 键盘 10 分钟、GameSir BLE 2 分钟）。

### 5.4 展示与身份

- 图标按 `kind`（鼠标/键盘/耳机/手柄/手机/麦克风/音箱/其它），蓝牙设备图标带蓝牙标记（`U:src/widget.cpp:471`、`U:README.md:33-54`）。
- 品牌色三级判定：驱动 key 前缀 → VID → 名字规则（`U:src/brands.cpp:157-169`）。
- 同名同类多设备：图标上加 ≤3 字母角标/序号（`U:README.md:40`）；我们的 `Deduplicate` 按 `(Kind, Name)` 合并会导致同类多设备"只剩一个"，这是要改的点（`D:src/.../RealPeripheralService.cs:1056-1066`）。
- 重命名/低电阈值/隐藏/忽略都按稳定 key 存注册表（`U:README.md:55-88`、`U:src/known_devices.cpp:71-107`）。

---

## 6. Windows 通用路径（BatteryHub 如何"覆盖任意设备"）

> 结论先行：BatteryHub 的"任意设备"其实由 **4 条互补的通用路径**组成，任何一条都不需要厂商协议；但对"私有 2.4G 接收器"完全无效（那只能靠 §4.2 的厂商驱动）。

### 6.1 Generic HID Battery Strength（最重要的一条）

**算法**（`U:src/drivers/generic.cpp:203-260`）：

1. 对所有未被厂商驱动认领的 HID collection，排除 page 0x84/0x85（UPS/电源）与 page 0x0D（Digitizers/绘图板）。
2. 若该设备已被 Bluetooth 路径按容器 ID 报告过，跳过（避免 Windows 属性与 HID 字段重复，且蓝牙侧 HID 字段常读 0）。
3. 打开句柄 → `HidD_GetPreparsedData` → 对 Feature 再 Input 报表遍历 `HidP_GetValueCaps`。
4. 命中 `UsagePage 0x06 & Usage 0x20`（Generic Device Controls / Battery Strength）或 `0x85 & 0x65`（Battery System / Absolute State Of Charge）。
5. 用 `HidD_GetFeature` / `HidD_GetInputReport` 读整份报表，`HidP_GetUsageValue` 取值。
6. 默认按 `LogicalMin..LogicalMax` 线性映射到 0–100；Apple 设备（VID 0x05AC）另有"直接当百分比"的 quirk（`val<=100`）。
7. 名称取 HID product string；type 由 collection/名字推断。

**可用条件**：设备固件确实按标准 HID 暴露电池 usage；句柄可打开（不要被厂商应用独占）。**不适用**：绝大多数 2.4G 私有协议鼠标键盘、无标准 usage 的耳机。

### 6.2 蓝牙属性 + Fast Pair（覆盖"任意蓝牙设备"）

**算法**（`U:src/drivers/generic.cpp:263-390`）：

1. 用 SetupAPI 枚举 `BTHENUM\`、`BTHLE\`、`BTHLEDEVICE\` 设备节点。
2. 读 `DEVPKEY_Device_BatteryLevel`（GUID `104EA319-6EE2-4701-BD47-8DDBF425BBE5`，PID 2）——**与 Settings → 蓝牙和设备里看到的是同一个值**。
3. 按 `DEVPKEY_Device_ContainerId` 归并主设备节点，取连接状态（`DEVPKEY_Device_IsConnected`，classic+LE 共享容器时任一在线即在线，`U:src/drivers/generic.cpp:290-306`）。
4. 类型来自 `DEVPKEY_Device_ClassOfDevice` / `DEVPKEY_DeviceContainer_Category`；BLE 设备没有 CoD 时用 HID 集合特征/名字猜。
5. 已由厂商驱动报告的容器跳过。
6. 耳机类再挂 BLE Fast Pair 监听：`ble_adverts.cpp` 被动收广播，`fastpair.cpp` 解析 `FE2C` service data，按蓝牙地址匹配，得到 L/R/盒与各自充电（`U:src/drivers/ble_adverts.cpp:197`、`fastpair.cpp:6,61`）。
7. 关闭状态节点（`DN_HAS_PROBLEM`）跳过；优先取"活着"的节点（`U:src/drivers/generic.cpp:303-318`）。

**可用条件**：设备已配对；Windows 侧读到电量（经典蓝牙通常靠 HFP；BLE 靠 GATT Battery Service 缓存）；不一定需要连接（配对后设备出现即可，但状态节点要健康）。**失败场景**：无 HFP 的音箱（Marshall EMBERTON II 即此情况，`U:docs/marshall.md`）、厂商私有协议设备、只在厂商 app 内显示电量的设备。

### 6.3 Xbox：Windows.Gaming.Input（真实 %）+ XInput（粗粒度兜底）

- `Windows.Gaming.Input.RawGameController` → `IGameControllerBatteryInfo.TryGetBatteryReport()` → `RemainingCapacityInMilliwattHours / FullChargeCapacityInMilliwattHours` 换算 %，`BatteryStatus` 给充电/NotPresent（`U:src/drivers/xinput.cpp:60-125`）。
- 只对 VID `0x045E`（Microsoft）与 `0x3537`（GameSir GIP 模式）采用 WGI 数值，因为其他第三方 pad 常报假值（`xinput.cpp:54-56`）。
- 已由厂商驱动认领的 pad 跳过；XInput 兜底给 4 级（Empty 5%、Low 25%、Medium 60%、Full 100%）并带 `levelText`（`U:src/drivers/xinput.cpp:150-209`）。
- 耳机插在手柄上的电量：XInput `BATTERY_DEVTYPE_HEADSET`；过滤"没有耳机却回音"的假耳麦（`U:src/drivers/xinput.cpp:150-209`）。

**可用条件**：Windows 能枚举到 Xbox 手柄（适配器或蓝牙）；WGI 需要 `RawGameController` 可用（Win10 1903+ 一般可用）；第三方 pad 伪 Xbox 会走 XInput 粗粒度。

### 6.4 HeadsetControl 桥（可选、外部进程）

- 若 `headsetcontrol.exe` 在程序旁边或 PATH，则执行 `headsetcontrol -b -o json --timeout 1500`（只查电量），解析 JSON：`id_vendor/id_product/battery.status/level`；映射成 `hsc:VID:PID` key、`charging = BATTERY_CHARGING`、`connected = AVAILABLE/CHARGING`（`U:src/drivers/headsetcontrol.cpp:1-4,150-246`）。
- 首次轮询同步等待（保证首屏完整）；之后每 **>50s** 在独立线程刷新一次，结果缓存，不阻塞主轮询（`:230-260`）。
- 已被内置驱动认领的 VID/PID 跳过（外置 bridge 只补漏）。
- **许可**：HeadsetControl 4.3.0 为 GPL-3.0，上游以"独立可执行文件 + 对应源码附件"方式随安装包分发（`U:NOTICE.md`、`U:README.md:279,375`）。这是**进程边界**，不是代码链接——我们若采用，必须沿用同样边界与源码提供义务。

### 6.5 Phone Link（不建议吸收）

- 读取 Phone Link 为开始菜单伴侣卡片写出的 `StartMenuCompanion.json`；官方未公开，可能随更新失效；卡片超 3 小时显示为暗淡（`U:README.md:205,428`）。**隐私/稳定性红线**：属于读第三方 App 私有数据，与我们的隐私规则冲突（工作区 AGENTS.md §6），不建议做。

### 6.6 与我们的 `VigilWatt.DeviceManager` 逐项差距

| 能力 | BatteryHub | 我们（VigilWatt.Core + App） | 差额/结论 |
|---|---|---|---|
| BLE 电量 | Windows 属性 + Fast Pair 广播 | `BleBatteryWatcher`：DeviceWatcher 属性 + **GATT 0x180F/0x2A19 直读兜底**（`D:external/VigilWatt.Core/Ble/BleBatteryWatcher.cs:35,129,159`） | **基本持平**；缺 Fast Pair L/R/盒 |
| 经典蓝牙电量 | Windows 属性（HFP 节点） | `BluetoothClassicWatcher`：AEP 属性 + `DEVPKEY_Device_BatteryLevel` 设备节点兜底（`.../BluetoothClassicWatcher.cs:100`、`ClassicDevNodeReader.cs:56`） | **基本持平** |
| 通用 HID Battery Strength | ✅ 有 | ❌ **没有**（`HidDeviceScanner` 只看 top-level caps，不读 value caps 电池 usage；`NativeHid` 已暴露 `HidP_GetValueCaps` 但未用于电池） | **真差距，阶段 1** |
| Xbox WGI / XInput | ✅ 有 | ❌ 没有 | **真差距，阶段 1** |
| 设备类型/状态语义 | `connected/asleep/grace/full/mV/levelText/detail` | `DeviceStatus` 只有 `IsOnline/BatteryPercent/Charging/Protocol/Kind`，**无 asleep/grace/mV/detail** | 阶段 1 先按在线/离线+最后值兜底；阶段 2 再扩展模型 |
| 厂商 HID 插件 | 约 110 个驱动入口 | **5 个**：Rapoo（被动）、Inphic（被动）、Betop（手柄）、Logitech HID++（默认关）、MCHOSE（默认关）——`D:external/VigilWatt.Core/Plugins/`、`Settings/AppSettings.cs:62-72` | 阶段 2/3 主战场 |
| 耳机专有 | ~30 个耳机驱动 + HeadsetControl | 只有 YUANDAO SPP（控制 + 分量） | 阶段 2/3 |
| 历史/估算 | TSV 历史 + 运行时间估算 + 查看器 | `HistoryStore`（SQLite）已写库（`D:external/VigilWatt.Core/Storage/HistoryStore.cs:20`），**无 UI**；`RemainingTimeEstimator` 存在未接 UI（`:46`） | 我们其实"已具备数据"，缺展示；可与总览一起做（P1） |
| 低电告警 | 每设备阈值、防重复、只认真实读数 | 有全局阈值 20%（`D:src/.../RealPeripheralService.cs:276-292`），但**对未被 Classify 保留的设备也会触发**（数据在但 UI 不显示，见 §7.2） | 阶段 1 顺手统一 |
| 告警/重命名/忽略 | 按设备 key 持久化 | `DeviceCustomizations` 模型存在未接（`D:external/VigilWatt.Core/Settings/DeviceCustomization.cs`），`IgnoreDevices` 无对应 | 阶段 2/3 |

---

## 7. 我们现状审计（决定"能复用多少"）

### 7.1 现有真实能力（已实现、可复用）

| 能力 | 实现 | 备注 |
|---|---|---|
| 设备电量聚合 | `D:external/VigilWatt.Core/Devices/DeviceManager.cs:57-137`：BLE + 经典蓝牙 + 系统电池 + 插件（反射发现 `IBatteryPlugin`） | 60s 主轮询 + 15s 快速发现（`:241,275-300,390-408`） |
| 厂商插件框架 | `IBatteryPlugin` + `BatteryPluginBase` + `DeviceClaimRegistry` + `OfflinePolicy` + `WriteBackoff`（`D:external/VigilWatt.Core/Plugins/`） | 新增品牌 = 加一个实现类；非常适合阶段 2/3 |
| HID 底层 | `HidDeviceScanner`（枚举 + caps + report ID）、`HidDeviceInfo`/`HidDeviceHandle`（GetFeature/SetFeature/WriteOutput/ReadInputAsync，`.../Hid/HidDeviceInfo.cs:77-190`）、`NativeHid`（含 `HidP_GetValueCaps` P/Invoke） | 足以实现 §4.2 的全部 feature/output+input 协议 |
| 罗技 HID++（应用层） | `D:src/.../Services/Mouse/LogitechMouseService.cs`：自有 HID++ 会话，读/写/板载；`_featureUnifiedBattery/BatteryStatus`（`:620-621`） | 目前只服务"选中的那只鼠标"；写仅 G304/G305 |
| 耳机 SPP | `D:src/.../Services/RealHeadsetController.cs`（YuandaoTws）+ `BuildHeadsetComponents` | 仅 YUANDAO 系列；分量/固件/控制 |
| 遥控器 | `D:src/.../Services/Remote/RemoteEngineClient.cs` + Rust sidecar | MI RC（RC003） |
| 历史存储 | `HistoryStore`（SQLite，`%LOCALAPPDATA%\DeviceCenterPreview\history.db`，由 `RealPeripheralService` 注入） | 已写、未展示 |
| 告警/通知 | `OnLowBatteryDetected` + `App.Notify`（`D:src/.../RealPeripheralService.cs:276-292`） | 全局阈值 |
| 设备选择/去重/合并 | `EnsureSelections`（连接/控件/电量/分量排序）、`Deduplicate`（Kind+Name）、`Merge`（字段级，SPP 优先） | 见 §7.2 局限 |
| 任务栏胶囊 | `PreviewTaskbarPill.BuildItems()` 固定三类（`D:src/.../Taskbar/PreviewTaskbarPill.cs:228-260`），`Render` 动态宽度，左锚 235 DIP（`:304-305`） | 见姊妹 UI 文档 |
| 设备中心总览 | 固定三列机架（`D:src/.../Views/DeviceCenterPage.xaml:41-52`），15s 刷新只改文本 | 稳定性红线见 §10.4 |
| 设置 | `AppSettings`（schema 3）4 个模块开关 + 3 个写入开关 + 通知/阈值/主题/备份（`D:src/DeviceCenterPreview.Core/Settings/AppSettings.cs:38-61`） | 暂无"其他设备/厂商插件"开关 |
| 自包含依赖 | `D:external/README.md`：VigilWatt.Core 是 MIT 快照，允许在本地修 | 修改规则：先改 external 快照，保持单测 |

### 7.2 当前收敛点（我们为什么会"看不到设备"）

1. **`Classify` 只认三类**：`RealPeripheralService.RefreshAsync` 遍历 `_manager.Devices` 时 `kind is null → continue`（`D:src/.../RealPeripheralService.cs:638-648,1201-1221`）。键盘、手柄、音箱、第二个同类鼠标、手机全部被丢弃——**数据已在，只是没显示**。
2. **每类只展示一台**：`EnsureSelections` 每类只选一台；`GetSnapshot(kind)` 也只返回选中的那台。任务栏胶囊同样只取三类各一台（`:228-260`）。
3. **模型过薄**：`PeripheralSnapshot` 没有 asleep/离线/最后已知/电压/分量之外的细节（`D:src/DeviceCenterPreview.Core/Models/PeripheralModels.cs:28-40`）；`RealDevice` 只有 `IsConnected`（`:1260-1274`）。
4. **VigilWatt 插件开关是"双份配置"**：`DeviceManager` 用构造传入的 `HostSettings` 的 `PluginEnabled` 决定是否运行插件（`D:src/.../RealPeripheralService.cs:265-273`、`DeviceManager.cs:323-330`），但插件内部 `BatteryPluginBase.WritesEnabled`/`LogitechPlugin.WritesEnabled` 读的是 **静态 `AppSettings.Load()`**（`D:external/VigilWatt.Core/Plugins/BatteryPluginBase.cs:77-82`、`Plugins/Logitech/LogitechPlugin.cs:55-58`），而那个静态 Load 读的是 **`%LOCALAPPDATA%\VigilWatt\settings.json`**（`AppPaths.cs:10`）——不是我们的设置文件。结果是"开关看起来在 App 里，实际由另一个文件决定"；阶段 1 必须统一。
5. **默认开关**：Logitech 与 Mchose 默认关闭（`D:external/VigilWatt.Core/Settings/AppSettings.cs:62-72`），所以即使 UI 放开设备类型，罗技 HID++ 键盘/耳机也不会通过 DeviceManager 出现。
6. **通知与展示不一致**：`LowBatteryDetected` 对全部 DeviceManager 设备生效（含未展示的键盘/手柄），可能出现"弹了低电提醒却看不到设备"。
7. **去重按名字**：`Deduplicate` 以 `(Kind, Name)` 合并（`.../RealPeripheralService.cs:1056-1066`），同名同类多设备会被合并成一条；跨来源（DeviceManager vs LogitechMouseService）同一鼠标名字常常不同（"G304" vs "Logitech G304"），又可能重复。
8. **刷新周期固定 15s**：`_timer`（`:103`）仅重新读取快照；数据源的 60s 主轮询/15s 发现是 DeviceManager 自己的节奏。上游可配 60s 基准——我们阶段 1 不需要改节奏，但要把"最后更新时间/陈旧"语义显示出来。
9. **历史无界面**：`HistoryStore` 只在服务里 `Observe`，无读取展示入口。
10. **没有品牌色/图标体系**：`brands.cpp` 的 17 色表、type glyph 映射都没有对应物。

### 7.3 我们的架构优点（相对上游）

- **托管语言 + WinUI 3**：实现 feature/output HID 协议、BLE GATT、RFCOMM、WGI 都比 C++ Win32 省事；`HidDeviceHandle` 已封装 feature/report 读写。
- **插件化 DeviceManager**：新增品牌协议只需一个插件类 + codec，不动主流程；比上游"改 PolAllDevices 顺序 + 新 cpp"更模块化（但要注意插件顺序/认领问题，见 §9.1）。
- **持久化/设置/备份/诊断**已有基础设施，比上游的注册表方案更完整。
- **稳定性纪律**（固定窗口、15s 只改文本、不重建 ItemsSource）已经踩过坑，设备扩充必须沿用（§10.4）。

### 7.4 需要警惕的既有风险

- WinRT/BLE watcher 与 `RealHeadsetController`、LogitechMouseService、WGI 都是 COM/WinRT 使用者；**新增 GamepadBatteryWatcher 必须复用主线程/后台线程边界**，不要新增 UI 线程上的等待。
- `Windows.Gaming.Input.RawGameController` 首次枚举是异步填充（上游 `Sleep(300)`），不能在 15s 刷新里同步阻塞 UI 线程。
- HID 打开失败/被独占：我们的 `HidDeviceInfo.Open()` 会抛 `UnauthorizedAccessException`（`.../HidDeviceInfo.cs:38-56`），新代码必须捕获并退避，不能让异常冒到 `RefreshAsync`（那里没有 try/catch 包住每个来源，一个异常会跳过整轮合并）。
- 日志隐私：HID 路径含设备实例路径，`AppLog` 不得写完整路径/MAC（工作区 AGENTS.md §6；上游 `report.cpp:6` 也是同样原则）。

---

## 8. 差距分析：上游有、我们没有（按"影响面"分类）

### 8.1 影响面分类

- **【仅显示】** 数据已由现有 DeviceManager 提供，缺的是"过滤 + UI + 状态语义"。
- **【新数据源/OS API】** 需要新增读取器，但**不写厂商私有协议**（Generic HID / XInput / Fast Pair）。
- **【新协议驱动】** 必须实现厂商协议（只读电量查询）。
- **【UI/交互】** 菜单、设置、历史、重命名、阈值、品牌色、多设备布局等。
- **【不做】** 触碰隐私/发布/合规红线。

### 8.2 差距表

| # | 上游能力 | BatteryHub | 我们现状 | 影响面 | 需要什么 | 阶段 |
|---|---|---|---|---|---|---|
| 1 | 任意蓝牙设备电量显示 | ✅ 属性 + FastPair | DeviceManager 有、`Classify` 丢弃 | 仅显示 | 放开分类 + 展示 | **1** |
| 2 | 键盘设备（任意） | ✅ | DeviceManager kind=Keyboard 被丢 | 仅显示 | 同上 | **1** |
| 3 | 手柄（非 Xbox） | ✅ 专用协议 | Betop 插件有数据但被丢 | 仅显示/新数据源 | 分类 + XInput | **1** |
| 4 | 音箱/麦克风/手机 | ✅ | DeviceManager 有、被丢 | 仅显示 | 分类 | **1** |
| 5 | 同类多设备（两只鼠标/两个手柄） | ✅ 角标区分 | `EnsureSelections` 只选一个 | 仅显示 | 列表模型 + 胶囊多设备 | **1** |
| 6 | 通用 HID Battery Strength | ✅ | ❌ | 新数据源 | `HidP_GetValueCaps`+读 usage | **1** |
| 7 | Xbox WGI/XInput 电量 | ✅ | ❌ | 新数据源 | WinRT + P/Invoke | **1** |
| 8 | 休眠/最后已知/变暗 | ✅ asleep+grace | ❌（只在线/离线） | 模型 + UI | `IsAsleep`/`LastSeen` | 1（app 层）/2（模型） |
| 9 | 明确离线 × 显示（可选） | ✅ | 离线即隐藏（保留条目） | 模型 + UI | 同上 | 1/2 |
| 10 | 每设备低电阈值/一次性触发 | ✅ | 全局 20% | UI + 设置 | 设置扩展 | 2 |
| 11 | 电池历史查看器 | ✅ TSV + 图表 | `HistoryStore` 已写、无 UI | UI | 历史查询/展示页 | 2 |
| 12 | 续航估算 | ✅ 保守投影 | `RemainingTimeEstimator` 已有、无 UI | UI | 接 UI | 2 |
| 13 | 设备重命名/隐藏/忽略 | ✅ 注册表 | `DeviceCustomizations` 存在未接；无忽略 | 模型 + UI | 设置/菜单 | 2 |
| 14 | 品牌色/类型图标 | ✅ 17 品牌 | ❌ | UI | glyph/色表 | 2 |
| 15 | 任务栏多设备/多显示器/纵向 | ✅ | 固定三类、单显示器主任务栏 | UI | 姊妹文档 task-1/3 | 2 |
| 16 | 罗技 HID++ 键盘/耳机/更多型号 | ✅（任意 HID++ 设备） | 仅受控鼠标 | 新数据源（已有协议代码） | 扩展现有 HID++ 会话 | **2** |
| 17 | Razer 鼠标/键盘 | ✅ | ❌ | 新协议驱动 | 90B feature + HyperSpeed | **2** |
| 18 | HyperX 耳机/鼠标 | ✅ | ❌ | 新协议驱动 | Haste2/Haste/Dart + 耳机 | **2** |
| 19 | SteelSeries | ✅ | ❌ | 新协议驱动 | HeadsetControl/rivalcfg 事实 | 2/3 |
| 20 | Corsair | ✅ | ❌ | 新协议驱动 | Void/Bragi/Compx | 3 |
| 21 | ASUS ROG / ZOWIE / Cooler Master / Glorious / VAXEE / 等独立品牌 | ✅ | ❌ | 新协议驱动 | 逐厂商 | 2（VAXEE 等低成本候选）/3 |
| 22 | 国产共享协议（Kysona/EVision/SONiX/Compx/Xenta/MCHOSE/AULA/Keychron/RK/Rongyuan/Semico/RAWM/VGN…） | ✅ | ❌（我们只有 Rapoo/Inphic/Betop/MCHOSE 部分） | 新协议驱动 | 逐族 | 3 |
| 23 | Fast Pair 左右/盒分量 | ✅ | ❌（BLE watcher 只拿单值） | 新数据源 | BLE 广播 watcher | 3 |
| 24 | AirPods/Beats | ✅ | ❌ | 新协议（BLE 广播） | proximity `0x07` | 3 |
| 25 | Galaxy Buds / Nothing / Sony WH / Soundcore（RFCOMM） | ✅ | ❌ | 新协议（RFCOMM） | 各厂商服务 | 3 |
| 26 | Marshall（免配对 GATT 读） | ✅ | ❌（我们 GATT 直读需要已连接 BLE 设备；免配对特例无） | 新数据源 | 地址来源 + 直读 | 3 |
| 27 | HeadsetControl 桥 | ✅ 外部 exe | ❌ | 新桥 + 许可 | 进程边界 + GPL 义务 | 3 |
| 28 | 手柄专有（PlayStation/Switch/8BitDo/GameSir/Flydigi/BEITONG） | ✅ | ❌（仅 Xbox 将随阶段 1 有） | 新协议 | 逐厂商 | 3 |
| 29 | 键盘固件补丁类（Keychron/NuPhy Halo96） | ✅ 需刷固件 | ❌ | 新协议 + 用户门槛 | 文档化"需固件" | 3/不做 |
| 30 | `--list/--json/--dump` 诊断 | ✅ | 有日志/诊断包；无设备 dump | UI/工具 | 只读诊断页 | 2 |
| 31 | 轮询间隔可配/唤醒即时刷新 | ✅ | 固定 15s 快照 + 60s 源轮询 | UI + 服务 | 设置项 | 2 |
| 32 | Phone Link 手机电量 | ✅ | ❌ | **不做** | 读第三方私有 JSON，违反隐私红线 | 不做 |
| 33 | 自更新器/多语言/Linux | ✅ | ❌ | **不做** | 与设备扩充无关；发布/翻译不在本迭代 | 不做 |

### 8.3 "只影响任务栏显示" vs "需要新协议驱动"速查

- **只影响任务栏/总览显示（阶段 1 就能看到效果）**：#1–#5、#8–#9、#11–#15、#31。
- **需要新数据源但无厂商协议（低风险）**：#6、#7、#23（广播）、#26（特例 GATT）。
- **需要新厂商协议驱动（阶段 2/3）**：#16–#22、#24–#25、#28–#29。
- **外部工具/许可**：#27。
- **不做**：#32、#33。

---

## 9. 扩充路线（重点）

### 9.0 总体原则与架构选择

| 原则 | 决策 |
|---|---|
| 单一数据源 | 继续以 `VigilWatt.Core.DeviceManager` 为设备台账；**不在 App 层另起一套 HID 轮询** |
| 不重复造设备发现 | BLE/经典蓝牙/系统电池直接用现有 watcher；厂商协议用 `IBatteryPlugin` 扩展点；通用 OS 路径新增 watcher，排在插件之后 |
| 受控页面不动 | 耳机/鼠标/遥控器三页的数据契约（`PeripheralSnapshot`/`Controls`/写入闸门）保持原样；新设备只进"电池总览" |
| 数据模型分层 | `DeviceKind`（3 类受控设备）不变；新增 `BatteryCategory` + `BatteryDeviceSnapshot` 承载所有设备 |
| 只读 | 阶段 1 全部只读；阶段 2 厂商插件只发**电量/充电查询**，不写配置/DPI/灯效（与上游原则一致，`U:README.md:442-446`） |
| 分阶段可回退 | 每阶段一个开关（`OtherDevicesEnabled` / 每厂商 `PluginEnabled`），出问题关掉即可回到今天的行为 |
| clean-room | 只吸收"协议事实"，实现与测试全部自写；引用公开来源（§11） |

### 9.1 阶段 1：Windows 通用设备聚合（低风险、高收益，不写厂商协议）

**目标**：把"数据已经在 DeviceManager 里但被丢弃"的设备 + 两条通用 OS 路径（Generic HID、Xbox）显示到总览与任务栏；不引入任何厂商协议代码。

#### 9.1.1 涉及文件/模块

| 层 | 文件 | 改动 |
|---|---|---|
| Core 模型 | `D:src/DeviceCenterPreview.Core/Models/BatteryOverviewModels.cs`（**新增**） | `BatteryCategory`、`BatteryTransport`、`BatteryDeviceSnapshot` |
| Core 契约 | `D:src/DeviceCenterPreview.Core/Services/IPeripheralService.cs`（改） | 新增 `IReadOnlyList<BatteryDeviceSnapshot> GetBatteryDevices();` |
| Core 实现 | `D:src/DeviceCenterPreview.Core/Services/MockPeripheralService.cs`、`UnavailablePeripheralService.cs`（改） | 返回空列表/模拟少量设备 |
| 设置 | `D:src/DeviceCenterPreview.Core/Settings/AppSettings.cs`（改） | `OtherDevicesEnabled`（默认 true）、`ShowAsleepInTaskbar`（默认 true）、`LastKnownMinutes`（默认 30，夹紧 0–120） |
| 聚合服务 | `D:src/DeviceCenterPreview.App/Services/RealPeripheralService.cs`（改） | 放开 `Classify` 过滤为"全量映射"；新增 `_batteryDevices`、`GetBatteryDevices()`、`LastKnown` 字典与过期；签名比较扩展；**三受控设备路径保持原 `Deduplicate`/`EnsureSelections` 逻辑不变** |
| 通用 HID 基础 | `D:external/VigilWatt.Core/Hid/NativeHid.cs`（改） | 新增 `HidP_GetUsageValue` P/Invoke、`HasBatteryValueCaps(preparsed)` 辅助 |
| 通用 HID 枚举 | `D:external/VigilWatt.Core/Hid/HidDeviceScanner.cs`、`HidDeviceInfo.cs`（改） | 扫描时计算 `HasBatteryUsage`；`Open(readOnly:true)` 变体（`GENERIC_READ`，避免对受保护 collection 请求写权限） |
| 通用 HID 读取 | `D:external/VigilWatt.Core/Hid/BatteryUsageReader.cs`（**新增**） | 纯函数：给定 feature/input 报表与 value caps，返回 0–100（含 logical min/max 缩放、Apple quirk） |
| 通用 HID 监听 | `D:external/VigilWatt.Core/Devices/GenericHidBatteryWatcher.cs`（**新增**） | 在插件轮询之后跑；跳过已认领接口、UPS/电源、Digitizer；失败退避 10 分钟；产出 `DeviceStatus` |
| Xbox 电量 | `D:external/VigilWatt.Core/Devices/GamepadBatteryWatcher.cs`（**新增**） | WGI `RawGameController` + `IGameControllerBatteryInfo`（mWh→%），XInput 4 级兜底；30–60s 轮询；后台线程 |
| 模型扩展 | `D:external/VigilWatt.Core/Models/DeviceStatus.cs`、`DeviceSource`（改） | 新增 `DeviceSource.GenericHid` / `DeviceSource.XInput`；`DeviceSourceInfo.ShortLabel` 补"蓝牙/HID/Xbox"标签 |
| 主流程 | `D:external/VigilWatt.Core/Devices/DeviceManager.cs`（改） | `Start()` 启动新 watcher；`PollOnceAsync` 末尾跑 Generic HID；`Dispose` 释放；`RefreshPresentation` 覆盖新来源 |
| 设置单源修复 | `D:external/VigilWatt.Core/Settings/AppSettings.cs`、`Plugins/BatteryPluginBase.cs`、`Plugins/Logitech/LogitechPlugin.cs`（改） | 新增 `AppSettings.Active`（内部静态 hook，由 DeviceManager 构造时赋值为注入实例）；`Load()` 优先返回 Active。消除"`%LOCALAPPDATA%\VigilWatt\settings.json` 与 App 设置打架" |
| UI 总览 | `D:src/DeviceCenterPreview.App/Views/DeviceCenterPage.xaml(.cs)`（改） | 三列机架下方增加"其他设备（N）"区：固定 6 行预置控件，刷新只改文本/可见性 |
| UI 任务栏 | `D:src/DeviceCenterPreview.App/Services/Taskbar/PreviewTaskbarPill.cs`（改） | `BuildItems()` 追加上限 5 台（+ "+N"），按类别选 glyph；充电/低电/休眠颜色沿用现有 `BatteryColor` |
| 设置页 | `D:src/DeviceCenterPreview.App/Views/SettingsPage.xaml(.cs)`（改） | "设备模块"增加"其他设备电量"开关与"最后已知值保留分钟"（用下拉，不用 NumberBox，见 §10.4） |
| 通知 | `D:src/DeviceCenterPreview.App/Services/RealPeripheralService.cs`（改） | 低电提醒只对"当前会展示的设备"触发，避免"弹提醒但界面看不到" |

#### 9.1.2 数据模型建议（阶段 1 版）

```csharp
// Firmware/OS 事实 → App 展示模型。Percent=null 表示"现在没有可信读数"，不允许用历史值冒充。
public enum BatteryCategory { Mouse, Keyboard, Headset, Speaker, Gamepad, Phone, Remote, Other }
public enum BatteryTransport { UsbReceiver, UsbCable, BluetoothClassic, BluetoothLe, BaseStation, System, Unknown }

public sealed record BatteryDeviceSnapshot(
    string Id,                          // 稳定 key：沿用 DeviceManager.Id（ble:... / btc:... / <plugin>:VID:PID / hid:VID:PID / xbox:...）
    BatteryCategory Category,
    string DisplayName,
    BatteryTransport Transport,
    string Protocol,                    // "Windows 蓝牙电池属性" / "HID Battery Strength" / "Xbox Wireless" ...
    bool IsOnline,
    bool IsAsleep,                      // 阶段1：仅"最后值保留期内"视为睡眠（app 层推导），阶段2起改用驱动显式 asleep
    int? Percent,                       // 新鲜读数；null = 未知
    int? LastKnownPercent,              // 仅用于变暗显示
    ChargingState Charging,             // 复用现有 Core 枚举
    string? Detail,                     // "L 80%, R 75%, case 40%"（阶段1多为空）
    string? LevelText,                  // "Empty/Low/Medium/Full"（XInput 兜底）
    DateTimeOffset LastUpdated,
    bool HasControlPage);               // 耳机/鼠标/遥控器 = true（显示"进入工作区"）
```

- **不扩展 `DeviceKind`**：避免影响 `GetSnapshot(DeviceKind)`、`EnsureSelections`、三个页面 switch。
- **`LastKnownPercent` 只在以下条件显示**：设备曾在线 + 当前失联 + 未超过 `LastKnownMinutes` + 用户未关闭"显示休眠/最后值"。
- 后续阶段 2/3 需要 `Millivolts`、显式 `Asleep`、`ViaNote` 时，优先给 `VigilWatt.Core.Models.DeviceStatus` 加**可选字段**（默认值不破坏现有插件），再映射进本记录。

#### 9.1.3 数据来源与映射规则

| 来源 | DeviceManager 数据 | 映射到 BatteryCategory | 运输 | 备注 |
|---|---|---|---|---|
| BLE watcher | `Source=Ble` | 用 `VigilKind` + 名字猜测 | `BluetoothLe` | 已是 Windows 权威值 |
| 经典蓝牙 watcher | `Source=BluetoothClassic` | 同上 | `BluetoothClassic` | 需已连接/设备节点健康 |
| 厂商插件 | `Source=Hid24G`，Id 前缀=插件名 | 插件 `Kind`；Unknown 用名字猜 | `UsbReceiver` | Rapoo/Inphic/Betop 默认开；Logitech/Mchose 默认关 |
| 系统电池 | `Source=System` | `Other`（或单列"本机"） | `System` | 默认关，设置页已有开关 |
| **Generic HID（新）** | `Source=GenericHid`，Id=`hid:VID:PID` | 名字/usage 猜 | `UsbCable`/`UsbReceiver`（无法区分则 Unknown） | 必须跳过已认领、UPS、Digitizer |
| **Xbox（新）** | `Source=XInput`，Id=`xbox:PID:index` | `Gamepad` | `BaseStation`/`BluetoothLe`（按连接方式，取不到则 Unknown） | 只对 045E/3537 用 WGI 精确值 |

**去重优先级**（同一物理设备多来源）：`LogitechMouseService（受控鼠标）> SPP 耳机 > Windows 蓝牙属性 > Generic HID > PnP 占位`。实现上：受控三类仍走原合并；总览列表对"同名同类"按此优先级取一条，并保留 `Id` 稳定；**不要**把 PnP 的"未确认连接"占位放进总览（只进三类原有逻辑）。

**过滤规则**（必须照上游抄的事实）：
- 跳过 Drawing Tablet / Digitizer（page 0x0D），不轮询（`U:src/drivers/generic.cpp:203-211`）。
- 跳过 UPS/电源（page 0x84/0x85 的 top-level 不是 Battery System 电池字段时——我们只认 `0x85:0x65` 的 value cap）。
- `IgnoreDevices` 等价物：设置页提供"忽略此设备"（阶段 2）。
- 通用名（"Wireless device"、"HID-compliant mouse"、"接收器"）不参与跨来源同名合并（`U:src/main.cpp:889`）。

#### 9.1.4 刷新与失败策略

| 项 | 决策 |
|---|---|
| 刷新节奏 | **不改** DeviceManager 的 60s 主轮询 + 15s 快速发现；App 侧 15s 只读快照。Generic HID 读放在主轮询末尾（每 60s 一次），Xbox 30–60s 一次 |
| 不阻塞 UI | 新 watcher 全在后台线程；`Changed` 后 UI 用 `DispatcherQueue.TryEnqueue`（现有约定） |
| HID 打开失败 | 每路径 10 分钟退避（对齐上游 Logitech 的 `OpenRetryBackoff` 思路）；不视为设备离线 |
| 读取失败 | 不发 0%、不伪造；本轮跳过，下轮重试；日志 Debug |
| 休眠 | 阶段 1 用"最后值保留期"近似：设备从列表消失 → `IsAsleep=true`（变暗）→ 超时 → `IsOnline=false`。**明确语义写进 UI 文案**，不宣称"设备报告睡眠" |
| 离线 | 默认隐藏；设置开启后显示暗淡"离线"；离线条目 30 分钟无更新后移除（对齐上游"不猜离线"原则的反面：我们只标"未发现"） |
| 通知 | 只在新鲜读数、非充电、跨过阈值时提示；阈值沿用现有 `LowBatteryThreshold` |

#### 9.1.5 UI 展示位置

1. **设备中心总览页**：三列机架保持不动；其下新增"其他设备"横向/纵向列表（固定 6 行，超过显示"还有 N 台"）。
   - 行内容：类别 glyph + 名称 + 传输标签（2.4G/蓝牙/线缆）+ 电量 + 充电/休眠/离线状态。
   - 硬约束：**XAML 里预置 6 个行控件**，刷新只改 `Text`/`Visibility`/`Foreground`；不重建 `ItemsSource`（→ §10.4）。
2. **任务栏胶囊**：受控三类排前，其他设备跟随；最多 5 项 + `+N`；低电红色、充电⚡、休眠变暗，全部沿用现有 `BatteryColor`/`Format`。
3. **设置页**："设备模块"卡片加"其他设备电量"开关；打开才采集/显示 Generic HID 与 Xbox；关闭时撤掉列表（对齐 VigilWatt `DropDevicesOf` 行为）。
4. **托盘 tooltip/菜单**：阶段 1 可只保持现状；把"每设备菜单/阈值"留给阶段 2（避免动到 UI 稳定性热点）。

#### 9.1.6 验收标准（阶段 1）

1. 一台**没有实现厂商协议**但带标准 HID Battery Strength 的设备，出现在总览与任务栏，读数与 Windows 设置页一致（±1%）。
2. 已配对并连接的蓝牙键盘/音箱/手柄（Windows 能显示电量的）出现在总览，字符类别正确（不误标成鼠标/耳机）。
3. 两只同类型鼠标/两个手柄同时在线时**都显示**，可区分（名称/角标），不再被 `(Kind,Name)` 合并。
4. Xbox 手柄：WGI 可用的型号显示真实 %，否则显示 4 级粗粒度且文案说明；不出现 "Full" 假 100%。
5. 设备断开/休眠：不显示 0%；休眠变暗且最多保留 30 分钟；离线默认隐藏，开关打开后显示"离线"。
6. 三个受控页面（耳机/鼠标/遥控器）功能与写入行为**零回归**（手动回归清单：连接、读值、写入回读、失败提示、模块开关）。
7. 关闭"其他设备电量"后：总览/胶囊不显示、新 watcher 停止；重新开启即恢复（无需重启）。
8. 代码审查确认：新增路径**没有任何 `SetFeature`/`WriteOutput`/厂商命令**；HID 只读（`HidD_GetFeature`/`Report`）。
9. `dotnet build ... -p:Platform=x64` 通过；`dotnet test` 通过；新增纯逻辑单测覆盖分类、缩放、去重、过期。
10. 连续运行 30 分钟：无 XAML 重建告警、无未处理异常、内存/句柄无明显增长（HID 句柄必须 using 释放）。

#### 9.1.7 风险与缓解（阶段 1）

| 风险 | 缓解 |
|---|---|
| Generic HID 与 DeviceManager 插件/蓝牙重复显示 | 插件优先：只处理 `_claims` 未认领接口；再按同名优先级去重；宁可少显示，不重复 |
| 打开受保护 HID collection 抛异常 | `Open(readOnly)` + 白名单 catch + 每路径退避；异常不得冒到 `RefreshAsync` |
| WGI 初始化 COM 与现有 BLE/WinRT 争用 | 在后台线程初始化一次并缓存；失败即降级到 XInput；绝不 UI 线程等待 |
| 放开过滤后条目暴增（邻居蓝牙设备） | 只接收"已配对/已知"来源（DeviceManager 已按连接/电量过滤）；总览/胶囊有上限 |
| 设置单源修复影响现有插件行为 | 默认值不变（Logitech/Mchose 仍关）；只把"读静态文件"改为"读注入实例"，补单测 |
| 胶囊变宽/挤压任务栏 | 上限 5 项 + "+N"；姊妹文档会给出布局公式 |
| 15s 刷新打乱现有 UI | 新列表独立于三列机架；只改文本；不动 `ItemsSource` |

**预估**：3–5 人日（1 名 C# 开发），含单测与手动回归；不依赖真机（可用 Windows 自带蓝牙设备验证）。

### 9.2 阶段 2：罗技 HID++ 扩展 + Razer + HyperX

> 前置条件：阶段 1 的数据模型、设置单源、总览/胶囊容器已就绪。

#### 9.2.1 2A：罗技 HID++ 扩展到键盘/耳机/更多型号

**现状**：`LogitechMouseService` 是应用层自有 HID++ 会话，只服务"选中的那只鼠标"；`VigilWatt.LogitechPlugin` 已实现完整的"接收器多槽 + 特征级联"，但默认关闭，且与鼠标服务**不能同时开两个会话**（同一接收器/接口会互抢 HID++ 报文与 SwId）。

**决策**：**以现有 `LogitechMouseService` 的 HID++ 会话为唯一入口**，把它从"单鼠标"扩展为"多设备枚举"；**不启用** `VigilWatt.LogitechPlugin`（避免双会话）。若未来想复用插件，必须先把 HID++ 会话统一到一处，二选一。

**实现要点**：

| 内容 | 说明 |
|---|---|
| 枚举范围 | 接收器槽位 1–6（+ 0xFF passthrough dongle 的 index 1），以及 USB 直连 / 蓝牙直连的 HID++ 设备；现有 `ProbeDeviceIndex`（`D:src/.../Services/Mouse/LogitechMouseService.cs:564`）已做 ping 选择 |
| 身份 | `logi:<wpid 或 pid>:<slot>`；同一设备换接收器/线缆保持同一 key（参考上游 `StableKey` 与 `logi:` 品牌 key） |
| 名称/类型 | 复用 `ReadDeviceName`（`D:external/VigilWatt.Core/Plugins/Logitech/LogitechPlugin.cs:688`）与 pairing info 的 kind 映射（HID++ "device type"：鼠标/键盘/耳机/手柄，`U:src/drivers/logitech.cpp:126-127` 同思路） |
| 电量级联 | 0x1004 → 0x1000 → 0x1001（电压曲线）→ 0x1F20（耳机 ADC）；1.0 设备走寄存器 0x07/0x0D。**我们已有 0x1004/0x1000 实现**（`LogitechMouseService.cs:620-621`、`HidppCatalog.CurveFor`） |
| 充电 | 0x1004/0x1000 的 status 字段；沿用现有 `HidppCodec` |
| 休眠/离线 | ping/查询超时 → `asleep`；接收器消失 → 离线（对齐上游 `graceSec=...` 语义；罗技接收器会在睡眠时把设备从链路丢掉，上游给 30 分钟） |
| 写能力 | **不扩展**：写仍只对已验证的 G304/G305 板载 profile 开放（`D:docs/features/mouse.md` 边界）；其余 HID++ 设备只读 |
| 与受控鼠标关系 | 选中的鼠标仍由同一会话提供 `MouseControls`；枚举结果里排除/复用其条目，避免重复 |

**新增/修改文件**：`D:src/DeviceCenterPreview.App/Services/Mouse/LogitechMouseService.cs`（扩展枚举并暴露 `IReadOnlyList<LogitechBatteryDevice>`）、`.../Services/Mouse/LogitechBatteryModels.cs`（新增，可选）、`.../Services/RealPeripheralService.cs`（映射进总览）、`D:external/VigilWatt.Core/Plugins/Logitech/HidppCatalog.cs`（如缺产品名/曲线可补数据，仅数据补充）。

**验收**：Bolt/Unifying 接收器上的罗技键盘、G 系列耳机与鼠标同时显示（键盘/耳机只读）；选中鼠标页面/写行为不变；接收器只被打开一个会话（日志验证 HID++ 请求序列无交错冲突）；休眠设备变暗不显示 0%；无厂商 app（G HUB）时也能读；重启后身份稳定。

**风险**：接收器被 G HUB/Options+ 占用（现有代码已处理打开失败）；耳机电压→% 曲线差异（保守：读不到特征就不显示）；多接收器/多设备时枚举耗时（放主轮询，别放 15s 刷新）。

**预估**：2–3 人日（复用现有 HID++ 代码，主要是枚举/映射/UI 接线与回归）。

#### 9.2.2 2B：厂商协议候选评估（从"有公开协议笔记、实现难度低、设备常见"三个维度）

| 候选 | 公开协议资料 | 协议复杂度 | 我们 HID 抽象支持 | 设备常见度 | 型号覆盖面 | 预估 | 结论 |
|---|---|---|---|---|---|---|---|
| **Razer** | OpenRazer 全量（90B feature 布局/CRC/命令表）；仓库内 HyperSpeed 笔记 | 中高（91B feature、transaction id、Synapse 共享邮箱、多包） | ✅ `SetFeature/GetFeature` | **很高**（鼠标/键盘/耳机） | 高 | 3–4 人日 | **选** |
| **HyperX** | 仓库内 3 篇完整鼠标笔记（Haste2/Haste/Dart）+ 耳机来自 HeadsetControl | 低中（output+input 一问一答，1s deadline） | ✅ `WriteOutput/ReadInput` | **高**（Cloud 耳机极常见） | 中高 | 2–3 人日 | **选** |
| Kysona/Pulsar 族 | `docs/zaopin_8k_receiver.md` + 公开 `hid-kysona` | **最低**（17B 帧、sum 校验） | ✅ | 中高（ATK/VXE/Pulsar 在国内流行） | **很高**（一协议覆盖 10+ 品牌） | 1.5–2 人日 | **阶段 2 低成本追加** |
| VAXEE | 仓库内 `docs/vaxee.md`（含抓包回放） | 低（64B feature、5% 步） | ✅ | 中 | 低中 | 1 人日 | 低成本候选 |
| Glorious/Hitscan | 仓库内无专文，但协议简单（64B feature） | 低 | ✅ | 中（偏老型号） | 中 | 1–2 人日 | 候选 |
| SteelSeries | HeadsetControl + rivalcfg；仓库内无专文 | 中高（多代耳机/鼠标） | ✅ | 高 | 高 | 3–5 人日 | 阶段 3 |
| Corsair | 仓库内无专文；Bragi/Compx/耳机三套 | 中高 | ✅ | 高 | 中高 | 3–5 人日 | 阶段 3 |
| Machenike | `docs/machenike.md` 最完整（VA/偏移/抓包） | **最低**（21B feature） | ✅ | 低 | 极低 | 0.5–1 人日 | 可当**模板试点**，不单列为阶段 2 |
| RAWM / VGN / Keychron | 仓库内有笔记（RAWM JSON 流 / VGN 客户端 / Keychron Launcher） | 中高 | ✅（RAWM 需要事件流） | 中 | 中 | 2–4 人日 | 阶段 3 |

**阶段 2 选定**：**Razer + HyperX**（按任务要求的两个厂商），并在阶段 2 收尾时判断是否顺手加 **Kysona 族**（成本最低、覆盖最广）。理由：Razer 是"高认知度 + 公开协议成熟"的代表；HyperX 耳机在国内保有量高且仓库内已有三篇独立协议笔记；两者都能只用只读查询、与我们 HID 抽象完全兼容。

#### 9.2.3 选定厂商的协议事实（clean-room 参考，不含代码）

**Razer（VID 0x1532）**
- 报文：**91 字节 feature report**（report id 0 + 90 字节）。布局：`[0] status [1] transaction id [2..3] remaining packets [4] protocol type [5] data size [6] command class [7] command id [8..87] arguments [88] crc = xor(bytes 2..87) [89] reserved`（`U:src/drivers/razer.cpp:1-5`）。
- 电量：`class 07 / cmd 80`，`args[1]` 为 0–255 电量 → `round(args[1]*100/255)`（`U:src/drivers/razer.cpp:161`）。
- 充电：`class 07 / cmd 84`，`args[1]==1` 为充电（`U:src/drivers/razer.cpp:163`）。
- 应答 status：`01` busy、`02` ok、`03` failed、`04` timeout、`05` not supported；busy 时要等待、timeout 可重试（`U:src/drivers/razer.cpp:6,59-62`）。
- 设备类型/命令：键盘与鼠标的 transaction id 不同（键盘已知表：无线 `0x9F`、有线 `0x1F`、BlackWidow V3 Pro 有线 `0x3F`；其他型号探测 `1F/3F/FF/08/9F`）（`U:src/drivers/razer.cpp:17-27,227-229`）。
- 多设备接收器（HyperSpeed）：槽查询 `class 00 / cmd BF`，`args[0]=0x10`，transaction `1F`（鼠标）/`9F`（键盘）；成功回复 `02` + 两个 3 字节槽（active + 大端 PID），槽 key 用设备自己报告的 PID（`U:docs/razer_hyperspeed.md:7-15`、`U:src/drivers/razer.cpp:207-224`）。
- 睡眠/离线：receiver timeout 无法区分"睡眠/关机"，上游保留最后值（`asleep`，grace 120s）并只在接收器物理移除时标离线（`U:src/drivers/razer.cpp:244-260`）。
- Synapse 共享 feature 邮箱：请求/回复可能被 Synapse 截胡，需按 tid+class+cmd+size+CRC 严格匹配，busy 时重试（`U:src/drivers/razer.cpp:44-70`）。
- 风险：不同型号命令/事务不同，需要型号表 + 探测；只做电池时可通过"电量查询成功即认领"降低型号表依赖，但键盘事务 id 仍需按表或探测。

**HyperX（VID 0x03F0 / 0x0951）**
- Pulsefire Haste 2 系（`03F0:0AB5`、`0F98`、`0DA0` 等）：vendor collection `FF00:0001`，64/65 字节；output `52 01` → input `53 01 <linked>`（0=鼠标关/离）；output `50 02` → input `51 02 <percent> <charge> .. .. <mV lo> <mV hi>`；charge 1=充电、2=充满；link=0 时不查电量（`U:docs/hyperx_haste_2.md:24-39`）。
- Pulsefire Haste（原版，`03F0:028E` 接收器 / `03F0:048E` 线缆）：64 字节 output `0x51` → 64 字节 input，byte4=电量（0/0xFF=未知）、byte5=0 放电/1 充电/2 满（`U:docs/hyperx_pulsefire_haste.md`）。
- Pulsefire Dart（`0951:16E1` 接收器）：output `51` heartbeat → `51 00 00 03 <percent> <charging> 0F ..`；无该头部的回复视为睡眠（无电量），`51 00 02 EC 05` 不可误读为 5%（`U:docs/hyperx_pulsefire_dart.md`）。
- Cloud III Wireless（`03F0:0C9D` 等）：report `66 <cmd>`；`82` → connected、`8A` → charging、`89` → `[2..3] mV`、`[4] %`（`U:src/drivers/hyperx.cpp:38-53`）。
- Cloud II Wireless：report `06 FF BB <cmd>`；`02` → `[7]%`、`03` → `[4]` 充电（`U:src/drivers/hyperx.cpp:55-60`）。
- 其余 Cloud 系（Cloud Flight S/Stinger/Core DTS 等）按 HeadsetControl 事实实现（`U:src/drivers/hyperx.cpp:1-13`）。
- 注意：Haste 2 刚上电几秒会报约 40%，链路刚建立 6 秒内的读数应忽略（`U:docs/hyperx_haste_2.md`）。

#### 9.2.4 阶段 2 涉及文件/模块（按插件架构）

| 层 | 文件 | 内容 |
|---|---|---|
| 插件 1 | `D:external/VigilWatt.Core/Plugins/Razer/RazerCodec.cs`（新增） | 纯函数：组帧/CRC/回复匹配/百分比换算/槽解析（可单测，无 I/O） |
| 插件 1 | `D:external/VigilWatt.Core/Plugins/Razer/RazerPlugin.cs`（新增） | `IBatteryPlugin`：认领 1532 的 91B feature 接口；只读查询；多设备槽；grace/asleep 语义；默认关闭 |
| 插件 1 | `D:external/VigilWatt.Core/Plugins/Razer/RazerModels.cs`（新增，可选） | 已知型号名/PID 表（只用于显示名，不影响匹配） |
| 插件 2 | `D:external/VigilWatt.Core/Plugins/HyperX/HyperXCodec.cs`（新增） | Haste2/Haste/Dart/Cloud3/Cloud2 的组帧与解码纯函数 |
| 插件 2 | `D:external/VigilWatt.Core/Plugins/HyperX/HyperXPlugin.cs`（新增） | 按 collection/VID/PID 分派到上述解码器；只读；grace；默认关闭 |
| 设置 | `D:external/VigilWatt.Core/Settings/AppSettings.cs`（改） | `PluginEnabled` 默认表加 `Razer=false`、`HyperX=false`（先默认关，用户显式开启） |
| 主流程 | `D:external/VigilWatt.Core/Devices/DeviceManager.cs` | 插件顺序：Razer/HyperX 与 Kysona 等共用 VID 的厂商需注意"先专用后通用"；`DiscoverPlugins` 目前按名字排序，需加**显式优先级**（见风险） |
| 模型 | `D:external/VigilWatt.Core/Models/DeviceStatus.cs`（可选改） | 增加 `bool IsAsleep`、`int? Millivolts`、`string? Detail`、`string? ViaNote`（默认可空，不改现有插件） |
| UI | 复用阶段 1 的 `BatteryDeviceSnapshot` 映射 | 无需新页面；设置页增加"厂商插件"开关组（Razer/HyperX，默认关，开启时提示只读） |
| 测试 | `tests/VigilWatt.Core.Tests/`（**新增测试项目**，net8.0-windows）或把 codec 放 Core 测试 | 组帧/解码/百分比换算/CRC/槽解析/超时/无回复/charging/full；fake 传输层 |
| 文档 | `D:docs/investigations/`（本文件已含协议事实）+ `D:docs/features/` 后续实施时补 | clean-room 来源与边界 |

**阶段 2 数据模型建议**（在阶段 1 的 `BatteryDeviceSnapshot` 上增量）：

- `VigilWatt.Core.Models.DeviceStatus` 增加**可选**字段：`bool IsAsleep`（驱动显式报告休眠，区别于"没回包"）、`int? Millivolts`（Haste2 等）、`string? Detail`（多分量/备注）、`string? ViaNote`（"2.4 GHz receiver"/"USB cable" 细化）；默认值保持现有插件行为不变。
- 映射层把 `DeviceStatus.Protocol` 写得更具体（"Razer feature report"/"HyperX NGENUITY"），供总览显示与诊断。
- 多设备接收器：`Id` 用"厂商前缀 + 设备自身 PID + 槽位"（Razer HyperSpeed 用设备报告的 PID，不用接收器 PID）；保证换接收器/换线缆后身份稳定。
- 充电/满：沿用 VigilWatt `ChargingState`（Unknown/Discharging/Charging/Full）；**不新增**"满电"字段。

**插件优先级问题**：`DeviceManager.DiscoverPlugins()` 按 `Name` 字母序排序（`D:external/VigilWatt.Core/Devices/DeviceManager.cs:71-92`），且各插件并行 `AttachAsync` 抢 `DeviceClaimRegistry`。Razer/HyperX 自己 VID 独占（1532/03F0/0951），没有共用 VID 冲突；但如果阶段 2 加 Kysona 族，会与现有 VGN/ATK/CHERRY 等共用 VID，需要给 `IBatteryPlugin` 增加 `Priority`/显式顺序（或用更具体的 `IsCandidate`）。**建议在阶段 1 顺手给插件系统加显式优先级字段**（默认 0，按名称兜底排序），成本很小。

#### 9.2.5 阶段 2 刷新与失败策略

| 项 | 决策 |
|---|---|
| 轮询 | 插件走 DeviceManager 60s 主轮询；不在 15s 快照里发 HID 帧 |
| 查询预算 | 每个接口最多 1 次电量 + 1 次充电查询；每次等待 ≤1s（Razer busy 可重试但总预算 ≤2s）；慢设备降频（可借鉴上游：BlackShark 5 分钟、ATK 键盘 10 分钟） |
| 写保护 | 只发只读命令；插件默认关闭；`WriteBackoff`/熔断沿用 `BatteryPluginBase` 现有机制 |
| 失败 | 无回复 → 本轮无结果；grace 内显示最后值（变暗）；超时 → 离线；**不伪造 0%** |
| 休眠 | Razer：receiver timeout 保留最后值（grace 120s），接收器移除才离线；HyperX：link=0 视为关/离，不查电量；Haste2 上电 6 秒内读数忽略 |
| 双路径 | 线缆 vs 接收器同 key 时线缆优先（Razer/Viper PID 变化案例：`00A5` 线缆/`00A6` 接收器同 key） |

#### 9.2.6 阶段 2 验收标准

1. Razer 无线鼠标（至少 1 个真机型号）与 Razer 无线键盘（HyperSpeed 双槽或单设备）显示电量/充电；Synapse 打开时读数仍正确（共享邮箱处理）。
2. HyperX Pulsefire Haste 2/Haste/Dart 中至少 2 类按文档验证读取正确；Cloud III Wireless 耳机读数与 NGENUITY/官方软件一致。
3. 设备睡眠/关电：显示最后值变暗，不显示 0%；接收器拔掉后转为离线。
4. 所有新查询**只读**：代码审查 + 抓包/日志确认无写配置/DPI/灯效命令。
5. 插件默认关闭；关闭状态下不打开任何相关 HID 接口（日志验证）。
6. 打开插件后，三受控页面、阶段 1 总览/胶囊无回归；DeviceManager 主轮询时长增加可接受（< +2s）。
7. Codec 纯函数单测覆盖：组帧字节、CRC、百分比换算（0/50/100/255 边界）、charging/full、超时/无回复、invalid reply 丢弃。
8. 真机验证记录写进 `D:docs/features/`（每厂商一节：型号、VID:PID、读数、对照官方软件、日期）。

#### 9.2.7 阶段 2 风险

| 风险 | 缓解 |
|---|---|
| Synapse/G HUB 抢占 feature 邮箱导致读数失败 | 严格匹配 + busy 重试 + grace；失败不报错只保留最后值 |
| Razer 型号/事务差异导致误判 | 电量查询成功才认领；未知型号用探测 tids；记录 `--dump` 等价日志便于用户反馈 |
| HyperX 多套协议按 PID 分派，共用 PID 或家族混淆 | 按 collection usage/长度 + PID 精确分派；先实现文档验证过的型号 |
| 双会话/双插件互抢（尤其未来 Kysona 族） | 阶段 1 加插件优先级；同 VID 时专用先于通用 |
| 插件写帧频率升高 | 默认关闭；每轮只读 2 帧；熔断/退避；慢设备降频 |
| 真机不足 | Codec 单测 + 社区验证；README/文档标注"硬件未验证"（上游同款做法） |
| clean-room 违约 | 只引用公开协议事实；实现/测试自写；review 时对照来源清单（§11） |

### 9.3 阶段 3：其余厂商 / 蓝牙专有 / HeadsetControl / 手柄生态（长期）

**排序原则**：用户设备保有量 × 协议资料完整度 / 实现成本。建议顺序：

1. **Kysona 族**（若阶段 2 没做）：一个协议覆盖 Kysona/Pulsar/ATK-VXE/G-Wolves/HECATE/Taidu/Chimera/Zaopin 等，性价比最高；复用阶段 2 的插件/Codec 模板。
2. **VAXEE / Glorious-Hitscan / Machenike / RAWM**：小协议、资料完整、成本 0.5–2 人日/家。
3. **SteelSeries / Corsair / ASUS ROG / Cooler Master / ZOWIE / LAMZU / WLMouse / Finalmouse / Endgame / Ninjutso / Sprime / Scyrox / MSI / NZXT / Alienware / Fractal / Arbiter / Niceboy / Darmoshark / Incott / Wraith / Crociris / Press Play 等独立品牌**：逐家插件。
4. **国产共享协议残余**：SONiX 键盘、Compx 17B、Xenta、EVision、MCHOSE 全系、AULA 全系、Keychron、Royal Kludge、Rongyuan、Semico、VGN、36B0/PMO/VIA、Wobkey、NuPhy、DAREU、Akko、CHERRY。
5. **蓝牙专有音频**：Fast Pair 左右/盒（先做，因为能复用 BLE watcher）→ AirPods/Beats（BLE 广播）→ Galaxy Buds / Nothing / Sony WH / Soundcore（RFCOMM）→ Marshall 免配对 GATT 特例。
6. **手柄生态**：PlayStation（输入报告，成本低）、Switch Pro、8BitDo、Flydigi、BEITONG、GameSir 专有模式（阶段 1 的 Xbox/XInput 已覆盖通用部分）。
7. **HeadsetControl 桥**（可选、独立进程、GPL 边界）：能一次覆盖大量耳机，但要处理许可/二进制分发/进程启动；建议在厂商插件达到 10+ 家后再评估，作为"长尾耳机"补充。
8. **键盘固件补丁类**（Keychron 2.4G、NuPhy Halo96）：**不做自动刷写**，只在文档/UI 说明"需厂商固件"。

**阶段 3 的工程约束（与阶段 1/2 同构）**：

| 项 | 内容 |
|---|---|
| 涉及文件/模块 | 每厂商一组 `D:external/VigilWatt.Core/Plugins/<Vendor>/`（Codec + Plugin）；RFCOMM/BLE 专有路径放 `D:external/VigilWatt.Core/Ble/` 或 `Devices/` 新 watcher；HeadsetControl 走独立进程桥（`D:src/.../Services/`）；UI 复用阶段 1 容器 |
| 数据模型 | 不新增模型；需要"多分量（L/R/盒）"时用阶段 1 已预留的 `Detail`/扩展 `BatteryDeviceSnapshot.Components`（新增 `IReadOnlyList<DeviceComponentBattery>?` 字段；沿用现有 `DeviceComponentBattery`） |
| 刷新与失败 | 按同厂商插件预算；RFCOMM/BLE 推送型只监听不轮询；外部 exe 首轮同步、之后 >50s 异步；失败不伪造、grace/离线语义同阶段 2 |
| 验收标准 | 每家：Codec 纯函数单测 + 至少 1 台真机（无真机写"未验证"）+ 只读审查 + 默认关闭 + 无回归；RFCOMM/BLE 专有额外验证"程序关闭后设备/系统无残留连接" |
| 风险 | 协议长尾维护成本高、WinRT RFCOMM 与 SPP 冲突（耳机 SPP 已占一个通道）、HeadsetControl 的 GPL 分发义务；缓解：插件化/默认关闭/可忽略设备/优先支持用户实际拥有的型号 |

### 9.4 阶段依赖与建议排期

```text
阶段1（通用聚合 + 模型 + UI 容器 + 设置单源 + 插件优先级）
  ├─> 阶段2A 罗技 HID++ 扩展（复用现有会话）
  ├─> 阶段2B Razer + HyperX（IBatteryPlugin + Codec + 测试）
  │      └─> 阶段2C（可选）Kysona 族 / VAXEE
  └─> 阶段3 长尾（按用户设备反馈排序）
```

- 阶段 1 必须先做：它提供 `BatteryDeviceSnapshot`、总览/胶囊容器、设置单源、插件优先级；阶段 2/3 的所有厂商都复用这一层。
- 阶段 2A 与 2B 可并行（不同文件/协议，改同一 `RealPeripheralService` 映射时注意写作用域合并）。
- 阶段 3 每完成一家即合入，不做大爆炸。

---

## 10. 数据模型与 UI 落点建议（跨阶段）

### 10.1 模型分层（最终形态）

```text
VigilWatt.Core（数据层，MIT 快照，可改但需保持单测）
  DeviceStatus { Id, Name, Source, Kind, BatteryPercent, Charging, IsOnline,
                 [阶段2+] IsAsleep, Millivolts, Detail, LevelText, ViaNote, Protocol }
       ▲ 现有 watcher（BLE/经典/系统）+ 插件（厂商）+ 新 watcher（GenericHid/XInput）

DeviceCenterPreview.Core（契约层，无 Windows 依赖）
  BatteryDeviceSnapshot（§9.1.2；纯数据，可单测）
  PeripheralSnapshot / Controls（受控三类，保持不变）

DeviceCenterPreview.App（聚合层）
  RealPeripheralService：DeviceManager.Devices + 控制器状态 → BatteryDeviceSnapshot 列表 + 三受控快照
       ▲ Changed 事件（UI 订阅，DispatcherQueue.TryEnqueue）
```

### 10.2 接口扩展（向后兼容）

- `IPeripheralService` 加 `GetBatteryDevices()`；Mock/Unavailable 实现返回空表。
- **不要**把 `BatteryDeviceSnapshot` 塞进 `GetSnapshot(DeviceKind)`（那会破坏三个页面）。
- 设置项加在 `AppSettings`（`OtherDevicesEnabled` 等），`Normalize()` 里夹紧；`MockPeripheralService` 同步补字段。

### 10.3 UI 落点（与稳定性红线一致）

| 位置 | 展示 | 约束 |
|---|---|---|
| 设备中心总览 | 三列机架 + "其他设备（N）"固定 6 行 | 预置控件、只改文本/可见性；不重建 ItemsSource；不弹层 |
| 任务栏胶囊 | 受控三类 + 其他设备（上限 5 + "+N"） | 沿用现有 `Render`/`signature` 机制；不新增窗口；全屏隐藏逻辑不动 |
| 设置页 | "其他设备电量"开关、"最后已知值保留"下拉、"厂商插件"分组（阶段 2） | 用 ToggleSwitch/ComboBox，不用 NumberBox；不弹 ContentDialog |
| 通知 | 低电/连接，仅对可见设备 | 沿用 `App.Notify` → InfoBar + 托盘兜底 |
| 历史/续航（P1） | 可在设备中心或独立区展示 `HistoryStore` 数据 | 只读查询；不阻塞刷新；图不要每 15s 重建 |

### 10.4 稳定性红线（来自本项目 AGENTS.md，必须遵守）

1. 固定窗口 1280×840：**不新增 resize/最大化**；新内容必须能在固定尺寸内滚动/收纳。
2. 15s 刷新只改文本/数值：**不重建 `ItemsSource`/`BitmapImage`/大量控件**；新列表用预置行或稳定集合 + 差异更新。
3. 不用 `Flyout`/`ContentDialog`/`StartBringIntoView` 做主交互；设置用页内控件。
4. WinRT/后台事件回 UI 必须 `DispatcherQueue.TryEnqueue`。
5. 页面 `Unloaded` 退订/停表；新增订阅要成对。
6. 硬件写入必须"先读→写→回读"；本项目设备扩充阶段全部只读，不引入写入。
7. 不显示伪造数据；失败用 `UnavailablePeripheralService`/空态。

---

## 11. 隐私 / 许可 / clean-room

### 11.1 许可事实

- BatteryHub：**GPL-3.0-or-later**（`U:README.md:543-547`）；它是 krisgfx9 项目的 fork（`U:NOTICE.md:1-40`）。其安装包捆绑的 HeadsetControl 4.3.0 同为 **GPL-3.0**，以独立可执行文件分发并附对应源码。
- 我们的应用：**MIT**（`D:LICENSE:1-3`）；已有一个 GPL-3.0 独立进程先例：`vendor/rc003-helper`（`D:README.md` 开源协议段）。
- `external/VigilWatt.Core`：**MIT** 快照（`vigil-watt/LICENSE`），允许本地修改；改 vendored 代码需遵守 `D:external/README.md` 的"先改快照、保持可独立构建"约定。

### 11.2 允许与禁止（clean-room 规则）

| 允许 | 禁止 |
|---|---|
| 读公开协议文档/开源实现，提取**事实**：VID:PID、feature/report 布局、命令字节、字段偏移、换算、超时、状态语义 | 复制/逐行翻译/改名移植上游 C++ 代码、注释、文档措辞 |
| 引用上游 `file:line` 作为**事实出处**（评审用） | 把上游源码文件放进本仓库或 vendor/ |
| 用公开来源（OpenRazer、Solaar、HeadsetControl、rivalcfg、SDL、HaloBattery、厂商 web 客户端 JS 等）的协议事实，自己做设计/编码/测试 | 反编译/打包/分发厂商二进制；读厂商 app 私有数据库/内存 |
| 引用公开来源链接与许可信息到 `NOTICE`/文档 | 把 GPL 代码与 MIT 代码链接成同一作品后闭源分发 |
| 外部进程桥接（如 HeadsetControl）按 GPL 义务提供源码/声明 | 静默捆绑 GPL 二进制而不提供对应源码 |

### 11.3 具体到本项目

- 阶段 1（OS API + 自有代码）**不涉及 GPL**：Generic HID 是 Windows 标准 HID 语义；Xbox 是 WinRT API。
- 阶段 2/3 的厂商协议实现：**必须**写"协议事实来源"注释（链接 + 事实摘要），但不抄代码；Codec/插件全部自写；单测自写。建议在实施文档/提交说明中记录 clean-room 声明。
- HeadsetControl（若阶段 3 采用）：保持**独立进程**（如 `vendor/rc003-helper` 模式），随包提供 GPL 文本与对应源码获取方式；不在主进程链接其代码。
- 日志/诊断（工作区 AGENTS.md §6）：不写真实蓝牙地址（脱敏）、不写完整 HID 路径、不写个人路径以外的敏感信息；新插件日志只写 VID:PID/型号/读数/错误码。
- 用户忽略/隐私：提供"忽略此设备"（阶段 2）避免对可疑设备轮询；不采集遥测。

---

## 12. 源码索引（附录）

### 12.1 上游 `src/drivers/` 全量文件 → 协议/设备（113 个 `.cpp`）

> 说明：`ble_adverts.cpp`、`fastpair.cpp`、`gamesir_sapjoy_protocol.cpp`、`vgn_protocol.cpp`、`xenta_protocol.cpp` 是共享协议/传输辅助文件（无 `Poll*` 入口）；其余为驱动实现。文件头事实见 `U:src/drivers/*.cpp:1-6`。

| 文件 | 协议/设备（一句话） |
|---|---|
| `airpods.cpp` | Apple AirPods/Beats BLE proximity 广播 |
| `ajazz_ak992.cpp` | AJAZZ AK992 Telink 接收器 → EVision 查询 |
| `akg.cpp` | AKG N9 Hybrid Airoha RACE（0CD6） |
| `akko.cpp` | Akko Air 01 Rongyuan / V9 Dash EVision |
| `alienware.cpp` | Alienware AW920H vendor `06 C0 0A` |
| `arbiter.cpp` | Arbiter Akitsu feature 0x83 |
| `areson.cpp` | Redragon M913/M915 Areson 25A7 GetPower |
| `astro.cpp` | ASTRO A50/A20 base station vendor |
| `atk_bitmouse.cpp` | ATK/VXE NearLink BITMOUSE |
| `atk_keyboard.cpp` | Evision 320F 键盘 / ATK Z87 |
| `atk_mercury.cpp` | ATK Mercury I/M1 FF22 三条查询 |
| `atk_serial.cpp` | ATK N9 Pro/Ultra 虚拟串口 |
| `audeze.cpp` | Audeze Maxwell vendor 06/07 + 0CD6 |
| `aula.cpp` | AULA/MCHOSE/Redragon 键盘 report 0x13/0x4A |
| `aula_headset.cpp` | AULA A7Pro 耳机 `55 65 01` |
| `aula_mouse.cpp` | AULA X9 Ultra FF1C 0x1A |
| `barracuda_pro.cpp` | Razer Barracuda Pro "PA" 协议 |
| `beitong.cpp` | BEITONG KP20/KP40D 00FF |
| `ble_adverts.cpp` | BLE 广播 watcher（FastPair/AirPods）【辅助】 |
| `cherry.cpp` | CHERRY MX 3.0S + XTRFY（Compx） |
| `chimera.cpp` | Chimera V2（Kysona 帧） |
| `coolermaster.cpp` | Cooler Master MM712/731 FF0A |
| `corsair.cpp` | Corsair Void/Bragi/Compx |
| `crociris.cpp` | CROCIRIS X7 WebHID 0x12 |
| `dareu.cpp` | DAREU EM901X/A84/A980 Pro/A950/EK75 |
| `darkrim.cpp` | DARKRIM SHADOW CRC-16 帧 |
| `darmoshark.cpp` | Darmoshark M3/M3S |
| `eightbitdo.cpp` | 8BitDo vendor + SDL 报告 |
| `endgame.cpp` | Endgame Gear 4K feature A1/B4 |
| `endgame_we.cpp` | Endgame Gear XM2we |
| `endorfy.cpp` | ENDORFY Viro Airoha RACE |
| `eweadn.cpp` | EWEADN S7/S9 `4F 81` |
| `fantech.cpp` | Fantech XD5 Compx |
| `fastpair.cpp` | Fast Pair 电池广播解析【辅助】 |
| `finalmouse.cpp` | Finalmouse xpanel GET |
| `flydigi.cpp` | Flydigi Vader 5 Pro/Apex 5 FFA0 |
| `fractal.cpp` | Fractal Scape GET_DEVICE_STATE |
| `galaxy_buds.cpp` | Samsung Galaxy Buds RFCOMM |
| `gamesir.cpp` | GameSir Nova 系 FF7A |
| `gamesir_ble.cpp` | GameSir Cyclone Pro BLE 8650 |
| `gamesir_sapjoy.cpp` | GameSir SAPJoy FFA0 GetDeviceInfo |
| `gamesir_sapjoy_protocol.cpp` | SAPJoy 帧编解码【辅助】 |
| `generic.cpp` | 通用兜底：HID Battery Strength + 蓝牙属性 + FastPair 合流 |
| `glorious.cpp` | Glorious/Hitscan/G-Wolves/WLMouse 共享 64B feature |
| `headsetcontrol.cpp` | HeadsetControl 外部 exe 桥 |
| `hyperx.cpp` | HyperX 耳机 + Pulsefire Haste2/Haste/Dart |
| `jabra.cpp` | Jabra GNP |
| `jbl.cpp` | JBL Quantum HIDV1/HIDV3 |
| `keychron.cpp` | Keychron Launcher（鼠/键盘/Nape） |
| `kysona.cpp` | Kysona 17B 帧（Pulsar/ATK/G-Wolves/HECATE/...） |
| `lamzu.cpp` | LAMZU vendor feature |
| `logitech.cpp` | Logitech HID++ 1.0/2.0（含耳机） |
| `logitech_g435_0adf.cpp` | Logitech G435 CRC-16 vendor |
| `machenike.cpp` | Machenike L7 `51 06` |
| `maono.cpp` | MAONO PD200W 实验性查询 |
| `marshall.cpp` | Marshall EMBERTON II BLE 180F 免配对读 |
| `mchose.cpp` | MCHOSE L7/M7/A7/K7 V1 反转 payload |
| `mchose_a5.cpp` | MCHOSE A5 Gen1 0x83 |
| `mchose_g3.cpp` | MCHOSE G3 V2 FF01:0010 |
| `mchose_headset.cpp` | MCHOSE V9 Pro 2 "MC" |
| `mchose_keyboard.cpp` | MCHOSE 3837:3033 "GLW" |
| `mchose_modern.cpp` | MCHOSE K7 V2 Ultra+ 0x0900/4D |
| `mchose_v9pro.cpp` | MCHOSE 一代 V9/S9/G9 "BLK" |
| `misc_mice.cpp` | Motospeed/Delux/Ajazz/Incott/Wraith 等小品牌 |
| `moondrop.cpp` | MOONDROP GM01 Pro WalkPlay |
| `msi.cpp` | MSI VERSA/GM31/GM41 `0F 20` |
| `niceboy.cpp` | Niceboy MW400 |
| `ninjutso.cpp` | Ninjutso NinjaForce feature 05/06 |
| `nothing_ear.cpp` | Nothing/CMF NT Link RFCOMM |
| `nuphy.cpp` | NuPhy WH80 NuPhyIO USB |
| `nuphy_halo96.cpp` | NuPhy Halo96 V2 自定义固件被动帧 |
| `nzxt.cpp` | NZXT Lift Elite `4E 02 81` |
| `phonelink.cpp` | Microsoft Phone Link 卡片（未公开 API） |
| `piao.cpp` | IPI PIAO BeiYing |
| `plantronics.cpp` | Poly BT600 report 07 |
| `playstation.cpp` | DualShock4/DualSense 输入报告 |
| `playstation_link.cpp` | PULSE Explore PS Link |
| `pressplay_icarus.cpp` | Press Play ICARUS V2 |
| `pulsar.cpp` | Pulsar X2 V3/Sonix |
| `raigor.cpp` | Fantech Raigor III FFB5 |
| `rapoo.cpp` | Rapoo VT 被动状态帧 |
| `rawm.cpp` | RAWM HUB 事件流 |
| `razer.cpp` | Razer 90B feature + HyperSpeed 槽 |
| `razer_headset.cpp` | BlackShark 系专有 |
| `redragon_evision.cpp` | Redragon M999 Evision |
| `rog.cpp` | ASUS ROG 键鼠/耳机 |
| `rongyuan.cpp` | Rongyuan 3151 F7 status |
| `royal_kludge.cpp` | RK MG6 QiWang |
| `scyrox.cpp` | Scyrox V12M profile 读 |
| `semico.cpp` | Semico `AA 55 CC 33` |
| `sinowealth_receiver.cpp` | Sinowealth/Redragon M602/M690 |
| `sonix.cpp` | SONiX 键盘 `20 01` |
| `sony_headphones.cpp` | Sony WH/WF RFCOMM |
| `sony_inzone.cpp` | Sony INZONE HCI |
| `soundcore.cpp` | Anker Soundcore RFCOMM |
| `sprime.cpp` | Sprime PM1 |
| `steam_controller.cpp` | Valve Steam Controller |
| `steelseries.cpp` | SteelSeries Arctis/Aerox/Prime/Rival |
| `steelseries_elite.cpp` | SteelSeries Nova Elite base station |
| `switch_pro.cpp` | Switch Pro 兼容 0x30 |
| `turtlebeach.cpp` | Turtle Beach Stealth 700 Gen3 |
| `vaxee.cpp` | VAXEE 4K/8K feature 0E |
| `vgn.cpp` | VGN OMNISOUND "ZHOU" |
| `vgn_client.cpp` | VGN/VXE 鼠键客户端协议 |
| `vgn_protocol.cpp` | VGN 协议编解码【辅助】 |
| `via_dongle.cpp` | 36B0/PMO/VIA 键盘 |
| `wlmouse.cpp` | WLMouse Beast 系 0x83 |
| `wobkey.cpp` | Wobkey Rainy75 V2 0xBB |
| `xenta_protocol.cpp` | Xenta 帧编解码【辅助】 |
| `xinput.cpp` | Xbox WGI + XInput |
| `zebronics.cpp` | Zebronics Compx 3554:FC0A |
| `zenith.cpp` | Zenith Pro Compx 17B |
| `zowie.cpp` | ZOWIE EC-CW/DW/U2 |

### 12.2 上游协议笔记 `docs/*.md`（23 篇）

| 文件 | 内容 |
|---|---|
| `ardor_fury_v2.md` | ARDOR Fury V2 被动 5 字节帧、tier nibble、两帧确认 |
| `areson.md` | Areson 25A7 GetPower 17B、×10 比例、FA07/FA08 |
| `audeze_maxwell.md` | Maxwell 06/07、0CD6、卡死 dongle 处理 |
| `darkrim.md` | DARKRIM CRC-16 帧、官方 hub 静态分析地址 |
| `gamesir_cyclone_pro.md` | Cyclone Pro BLE 8650/865F、充电中 FF |
| `gamesir_sapjoy.md` | SAPJoy FFA0 GetDeviceInfo 帧 |
| `hyperx_haste_2.md` | Haste2 50 02/51 02、link 查询、6 秒规则 |
| `hyperx_pulsefire_dart.md` | Dart heartbeat 51、无头部即睡眠 |
| `hyperx_pulsefire_haste.md` | Haste 原版 0x51 报告、byte4/5 |
| `inphic.md` | Inphic IN10 / Xenta 被动帧、model 映射 |
| `keychron_keyboard_firmware/`（目录） | K5 Max patch、Q6 Ultra 8K 补丁/刷写脚本 |
| `machenike.md` | L7 51 06、偏移表、线缆优先 |
| `marshall.md` | EMBERTON II BLE 180F 免配对读 + FastPair 地址 |
| `nuphy-halo96-v2.md` | Halo96 V2 自定义固件被动 usage 序列 |
| `nuphy.md` | WH80 NuPhyIO 55 05、checksum、byte8 |
| `rapoo.md` | Rapoo VT 07 状态帧、连接类型 nibble、充电位 |
| `rawm.md` | RAWM HUB 事件流/JSON、ESB 通道 |
| `razer_hyperspeed.md` | 91B feature、槽查询 BF、双设备 |
| `releasing.md` | 上游发布流程（与我们无关） |
| `royal_kludge_rk68.md` | RK68 无电量的负结论 + 帧布局 |
| `royuan_2.4g_keyboard.md` | Rongyuan 3151 F7 status、固件差异 |
| `vaxee.md` | VAXEE 64B feature 0E/A5/0B、×5 比例 |
| `vgn.md` | VGN HUB 协议、仅 2.4G、0xFF 睡眠 |
| `zaopin_8k_receiver.md` | Kysona 17B 帧实测（Compx 3554:F527） |

### 12.3 上游关键源码

| 文件 | 作用 |
|---|---|
| `U:src/devices.cpp:41-162` | `PollAllDevices()` 驱动/兜底顺序（110 个调用） |
| `U:src/devices.h:5-23` | `DeviceKind` + `DeviceStatus` 状态模型 |
| `U:src/drivers/drivers.h:12-64,70-186` | `PollContext`/认领/稳定 key/111 个轮询入口 |
| `U:src/hid.cpp:13-60` | Windows HID 枚举（含独占容错） |
| `U:src/drivers/generic.cpp:74-80,203-260,263-400+` | 通用 HID Battery Strength + 蓝牙属性 + FastPair |
| `U:src/drivers/xinput.cpp:54-125,150-215` | Xbox WGI + XInput |
| `U:src/drivers/headsetcontrol.cpp:1-4,150-246` | HeadsetControl 外部桥 |
| `U:src/drivers/ble_adverts.cpp:197`、`fastpair.cpp:6,61` | BLE 广播 watcher / FastPair 解析 |
| `U:src/main.cpp:56-63,99-102,124,894-916,1148` | 轮询周期、睡眠/离线/隐藏语义 |
| `U:src/refresh_policy.h:14-80` | 启动/唤醒/到达的追跑节奏 |
| `U:src/runtime_estimate.h:1-60` | 续航估算（保守投影） |
| `U:src/history_model.h`、`battery_history.cpp` | 历史记录模型/存储 |
| `U:src/known_devices.cpp:71-107` | 重命名/设备档案注册表 |
| `U:src/brands.cpp:10-29,49-87,157-169` | 17 品牌色 + 品牌判定 |
| `U:src/report.cpp:727-751`、`devices.cpp:164-176` | `--dump`（HID/USB/蓝牙全量清单） |
| `U:tests/`（83 个） | 模拟 HID 传输的驱动回归测试写法（我们应借鉴思路） |

### 12.4 我们侧相关文件（行号）

| 文件:行 | 内容 |
|---|---|
| `D:src/DeviceCenterPreview.App/Services/RealPeripheralService.cs:625-692` | `RefreshAsync`：DeviceManager 来源 + 过滤 + 合并 |
| `D:.../RealPeripheralService.cs:1201-1221` | `Classify`（只保留三类；阶段 1 要放开） |
| `D:.../RealPeripheralService.cs:1056-1107` | `Deduplicate`/`Merge`（按名字；同类多设备会被合并） |
| `D:.../RealPeripheralService.cs:1170-1200` | `EnsureSelections`（每类只选一台） |
| `D:.../RealPeripheralService.cs:265-273` | `CreateManager`（传入 HostSettings） |
| `D:.../RealPeripheralService.cs:276-292` | 低电通知（对所有 DeviceManager 设备触发） |
| `D:.../RealPeripheralService.cs:103` | 15s 刷新计时器 |
| `D:.../RealPeripheralService.cs:780-866` | PnP 枚举（阶段 1 总览不应收 PnP 占位） |
| `D:.../RealPeripheralService.cs:868-956` | 鼠标/耳机受控设备聚合 |
| `D:.../Services/Taskbar/PreviewTaskbarPill.cs:228-260` | `BuildItems`（固定三类） |
| `D:.../Services/Taskbar/PreviewTaskbarPill.cs:304-305` | 胶囊左锚 235 DIP |
| `D:src/DeviceCenterPreview.App/Views/DeviceCenterPage.xaml:41-52` | 固定三列机架 |
| `D:src/DeviceCenterPreview.App/Views/SettingsPage.xaml:105-165` | "设备模块"开关区 |
| `D:src/DeviceCenterPreview.Core/Models/PeripheralModels.cs:4-8,28-40` | `DeviceKind` / `PeripheralSnapshot` |
| `D:src/DeviceCenterPreview.Core/Services/IPeripheralService.cs:14-30` | 服务契约（阶段 1 加 `GetBatteryDevices`） |
| `D:src/DeviceCenterPreview.Core/Settings/AppSettings.cs:38-61,75-79` | 现有模块/写入/通知设置 |
| `D:src/DeviceCenterPreview.App/Services/Mouse/LogitechMouseService.cs:564,592,620-621` | HID++ 设备探测/电量特征 |
| `D:external/VigilWatt.Core/Devices/DeviceManager.cs:57-137,241,275-300,323-330,362-408` | 数据源聚合/轮询/插件开关/快速发现 |
| `D:external/VigilWatt.Core/Ble/BleBatteryWatcher.cs:35,129,159` | BLE watcher + GATT 180F 直读 |
| `D:external/VigilWatt.Core/Ble/BluetoothClassicWatcher.cs:32,100`、`ClassicDevNodeReader.cs:56` | 经典蓝牙属性 + 设备节点电量 |
| `D:external/VigilWatt.Core/Plugins/IBatteryPlugin.cs`、`BatteryPluginBase.cs:72-82`、`DeviceClaimRegistry.cs` | 厂商插件扩展点 |
| `D:external/VigilWatt.Core/Hid/HidDeviceScanner.cs:16,110-140`、`HidDeviceInfo.cs:11-190`、`NativeHid.cs:51+` | HID 枚举/句柄/值 caps |
| `D:external/VigilWatt.Core/Storage/HistoryStore.cs:20`、`Devices/RemainingTimeEstimator.cs:46` | 历史/续航已有实现 |
| `D:external/VigilWatt.Core/Settings/AppSettings.cs:62-72,120-124`、`Utils/AppPaths.cs:10` | 插件默认开关 + `%LOCALAPPDATA%\VigilWatt` 静态设置（阶段 1 修） |
| `D:docs/01-architecture.md`、`D:docs/features/taskbar-tray.md`、`D:docs/features/mouse.md` | 现有架构/UI/能力边界 |

### 12.5 上游测试写法（我们阶段 2 的测试模板）

- 83 个测试文件多数是"把驱动链接到模拟 HID 传输上"，断言发出的字节、超时、回复解析、边界值、已认领跳过（例：`U:tests/vaxee_driver_test.cpp`、`rapoo_driver_test.cpp`、`hyperx_haste_driver_test.cpp`、`razer_connection_test.cpp`、`receiver_frames_test.cpp`）。
- 我们对应做法：
  - 把组帧/解析写成**纯函数 Codec**（无 I/O，如 `RazerCodec`/`HyperXCodec`），在 `tests/` 直接单测。
  - 设备 I/O 薄层（插件）只做"打开/写/读/映射"，不承载协议逻辑；用接口 `IHidTransport`（阶段 2 视需要引入，最小化改动）做 fake 测试。
  - 协议事实回归：把抓到的真实字节以常量表写在测试里（公开协议文档中的示例帧），断言解码结果；不依赖真机。

---

## 13. 待验证/开放问题（实施前清单）

1. **Generic HID 实际覆盖率**：Windows 上真实暴露 `0x06:0x20` 的设备有多少（鼠标/键盘/手柄）？建议先在开发机用现有 `tools/DeviceProbe` 扩展一个只读 dump 验证（不写设备）。
2. **容器 ID 去重**：Generic HID 与蓝牙同设备如何可靠去重？上游用 `DEVPKEY_Device_ContainerId`；我们 `HidDeviceInfo` 目前没存容器 ID，需评估 SetupAPI 获取成本（阶段 1 可先用"同名优先 BT"降级）。
3. **WGI 初始化**：在现有 WinUI3 + 后台 watcher 进程模型下，`RawGameController` 首次枚举异步填充的等待策略（上游 `Sleep(300)`）；必须在后台线程验证。
4. **`Windows.Gaming.Input` 与 `Windows.Devices.Bluetooth` 的 COM 并发**：现有 BLE watcher 已在用 WinRT，新增 WGI 是否引入公寓冲突，需要 run 一次验证。
5. **插件开关单源修复**：`AppSettings.Active` 静态 hook 的最小改法是否会让 VigilWatt 上游同步困难（external 快照规则）；备选是给 `IBatteryPlugin` 加 `ApplySettings` 默认接口方法。
6. **蓝牙键盘/音箱在 Windows 的电量属性**：需实测本机几台设备（Settings 是否显示电量、`AepBattery_Level` 是否可用）；W820NB 类设备可能有"配对未连接仍显示旧电量"坑（VigilWatt 已有处理）。
7. **Razer 型号表/事务 id**：阶段 2 前需要确定"哪些型号用哪个 tid"的最小可用集；建议先做无线鼠标 + 无线键盘两类，不追求全型号。
8. **HyperX Cloud 耳机协议差异**：Cloud III/II/Core/Flight S 多套，先做文档/HeadsetControl 已覆盖且用户拥有的 1–2 个型号。
9. **真机可用性**：阶段 2 验收需要至少 1 台 Razer + 1 台 HyperX（或社区验证 + 标注未验证）。
10. **胶囊/总览容量**：固定窗口与任务栏宽度约束下，最多显示几台设备（与姊妹 UI 文档对齐）。

---

## 14. 结论

- **BatteryHub 的可复用价值 = 事实清单（113 支持行/280 个 VID:PID/110 个驱动入口/23 篇协议笔记）+ 状态语义（asleep/grace/offline/charged/低电）+ 只读纪律**，而不是 GPL 代码。
- 我们并不缺"设备数据入口"：`VigilWatt.DeviceManager` 已覆盖 BLE/经典蓝牙/系统电池 + 5 个厂商插件；缺的是**展示层放开 + 两条通用 OS 路径 + 厂商协议长尾**。
- **阶段 1 是性价比最高的一步**：不写厂商协议、只动我们自己的服务和 UI，就能把键盘/手柄/音箱/多设备/通用 HID 设备显示到总览与任务栏。
- **阶段 2 优先 Razer + HyperX**，同时把现有罗技 HID++ 会话扩展为多设备枚举；**阶段 3 按用户设备反馈做长尾**。
- 全程 clean-room、只读、默认关闭插件、可一键回退；不碰 Phone Link/厂商私有数据/写入能力。

