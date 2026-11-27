# Product

<!-- impeccable:product-schema 1 -->

## Platform

web

> 工具口径值。实际产品是 **Windows 10/11 原生桌面应用（WinUI 3 / Windows App SDK 1.7）**，
> 不是网页；项目内的 `winui-device-console` 规则优先于任何 Web 平台建议。

## Stack

现有代码库：C# 12 / .NET 8 / WinUI 3 / x64，固定窗口 1280×840；设备服务层已经稳定
（耳机 SPP、罗技 HID++、遥控器 Rust sidecar、通用设备电量）。本次只改视觉与布局，
不改服务层与底层行为。

## Users

单一用户（本人）：Windows 11 桌面，同时使用三台无线外设——原道 YUANDAO OriG in 耳机、
罗技 G304 鼠标、小米 MI RC 遥控器。**几乎不主动打开主窗口**；主要依赖任务栏内嵌电量条 /
托盘常驻。打开主窗口的场景通常是：想看电量或低电告警、确认设备是否在线、偶尔调耳机 ANC/EQ、
鼠标 DPI/按键、遥控器映射，以及检查写入是否成功。

## Product Purpose

把三台外设的状态与可写能力收进一个原生控制台：平时在任务栏/托盘安静值守，需要时打开即可
确认“都正常 / 哪台出问题”，并完成必要的参数调整。成功 = 用户很少需要打开它；打开时 10 秒内
得到答案。

## Positioning

不是通用设备管理器，也不是厂商全家桶：只服务这三类设备的真实协议与真实回写校验，
并提供 BatteryHub 式的任务栏内嵌电量。数据不模拟、不缓存造假，写入必须“先读 → 写 → 回读”。

## Operating Context

- 绝大多数时间在后台：任务栏内嵌条 + 托盘。
- 打开主窗口通常是短时、任务导向：扫一眼状态、处理异常、调一次参数后关闭。
- 白天与夜间都用，需要浅色/深色双主题。
- 桌面固定 1280×840 窗口，不 resize、不最大化；无 Flyout/ContentDialog 主交互。
- 15 秒刷新只更新文本/数值/进度，不重建大控件树。

## Capabilities and Constraints

- 设备：耳机（SPP，双耳/充电盒电量、ANC/EQ/查找、写入）、鼠标（HID++，DPI/轮询率/板载配置写入）、
  遥控器（BLE 语音/按键映射/系统级注入）、通用设备（只读聚合）。
- 任务栏内嵌条与托盘常驻；主窗口只负责详细状态与控制。
- 固定窗口、无弹层、页面内就地编辑；背景事件必须 `DispatcherQueue.TryEnqueue`。
- 不伪造电量、连接、能力；失败要显示原因。
- 不重写服务层；视觉改版不改变功能路径与数据来源。

## Brand Commitments

- 名称：薄荷外设 / Mint Peripherals；现有品牌绿（薄荷色）是识别信号，但允许在新视觉世界中
  重新定义强调色的用法与比例。
- 现有产品语气：克制、技术、真实；不卖萌、不堆营销词、不用假数据。
- 现有设备实物图（耳机盒 / G304 / MI RC）是真实资产，应继续作为主体素材。

## Evidence on Hand

- 现有五页实机截图：`docs/screenshots/hub-1.2.2.png`、`mouse-1.2.2.png`、`headset-1.2.2.png`、
  `remote-1.2.2.png`、`settings-1.2.2.png`、`dark-1.2.2.png`。
- 设备实物图：`src/DeviceCenterPreview.App/Assets/Devices/`。
- 真实读数样例：耳机 左 70%/右 60%，鼠标 90%，遥控 92%。
- 历史设计规范：`docs/ui/design-system.md`（当前 Bench）、`docs/ui/redesign-research.md`（归档）。

## Product Principles

1. **Glance first**：主窗口第一眼回答“都正常吗？哪台有问题？”，而不是展示所有参数。
2. **Background by default**：主窗口是低频工具；任务栏/托盘才是高频界面，主窗口不应抢占注意力。
3. **Truth over chrome**：真实状态、真实失败原因；不为了“看起来丰富”加假图表/假指标。
4. **Precision, not dashboard**：这是控制台，不是 SaaS 仪表盘；少卡片、少按钮、少装饰。
5. **Ten-second job**：打开后 10 秒内能完成最常见任务并离开。

## Accessibility & Inclusion

- 正文不小于 12px，主信息不小于 14px；浅/深主题都按 WCAG AA 检查对比度。
- 键盘可达；状态不能只用颜色表达。
- 固定窗口下不允许横向滚动或内容裁切。
