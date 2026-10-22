# 外设中枢（Device Center Preview）文档

> 目标：任何新接手的 agent / 开发者，从本文件开始，按层级阅读即可知道：
> **每个目录是什么、已经做了什么、为什么这样做、怎么改、哪里容易踩坑。**

## 阅读顺序（渐进式）

| 层级 | 文档 | 什么时候读 |
|---|---|---|
| L0 | [`../README.md`](../README.md) | 只想快速知道项目是什么、怎么跑 |
| L1 | [`01-architecture.md`](01-architecture.md) | 第一次接触代码，理解进程/分层/数据流 |
| L1 | [`02-folder-map.md`](02-folder-map.md) | 找文件、判断某个目录的职责 |
| L1 | [`03-build-run-debug.md`](03-build-run-debug.md) | 构建、运行、看日志、排错 |
| L2 | [`features/headset.md`](features/headset.md) | 改耳机页/SPP/查找耳机 |
| L2 | [`features/mouse.md`](features/mouse.md) | 改鼠标页/HID++/DPI/按键 |
| L2 | [`features/remote.md`](features/remote.md) | 改遥控器页/映射/接收测试 |
| L2 | [`features/settings.md`](features/settings.md) | 改设置持久化/模块开关/通知/备份/诊断 |
| L2 | [`features/integration-parity.md`](features/integration-parity.md) | 对三个原项目做功能完整性审计/取舍 |
| L2 | [`features/taskbar-tray.md`](features/taskbar-tray.md) | 改任务栏胶囊/托盘/全屏隐藏 |
| L2 | [`ui/design-system.md`](ui/design-system.md) | 改 UI、配色、排版、窗口 |
| L2 | [`ui/redesign-research.md`](ui/redesign-research.md) | 做视觉改版、选 AI 设计工具/资源、看当前诊断与方向 |
| L3 | [`internals/headset-spp.md`](internals/headset-spp.md) | 调耳机协议帧、连接失败 |
| L3 | [`internals/logitech-hidpp.md`](internals/logitech-hidpp.md) | 调 HID++ 特征、板载配置 |
| L3 | [`internals/remote-sidecar.md`](internals/remote-sidecar.md) | 改 sidecar 命令/事件/生命周期 |
| L3 | [`internals/mapping-storage.md`](internals/mapping-storage.md) | 改映射 JSON、配置持久化 |
| L4 | [`stability/crash-history.md`](stability/crash-history.md) | 出现黑屏/闪退/卡死，或要改窗口/弹层 |
| L4 | [`decisions.md`](decisions.md) | 想知道“为什么固定窗口/为什么不用 WebView2” |
| L4 | [`history/implementation-log.md`](history/implementation-log.md) | 想知道到目前为止做过什么、下一步 |

## 一句话现状（2026-10-21）

- WinUI 3 / .NET 8 / x64，**固定窗口 1280×840**，应用名「外设中枢」。
- 外观：设置页可切换深色/浅色/跟随系统；两套 token 均可用，状态/设备图标为自绘矢量。
- 耳机：真实 SPP 控制（ANC/EQ/开关/查找），分量电量与固件。
- 鼠标：罗技 HID++ 通用读取 + G304 的 DPI 档位/按键/板载模式/固件写入；20 个罗技型号图片。
- 遥控器：Rust sidecar 常驻；13 键 × 3 手势在表格内直接分配并自动保存；接收测试只观察不注入。
- 设置：`settings.json` 持久化；四个模块开关真正停启；三类写入授权在服务层强制；连接/低电量/写入失败通知。
- 备份/诊断：固定目录导出/导入（清单校验 + 白名单）、诊断包日志脱敏。
- 任务栏胶囊：左下角、只显示已连接设备；全屏自动隐藏；35px 高。
- 稳定性：已定位并移除多类 WinUI 原生崩溃触发点，当前窗口不可缩放、映射不使用弹层。

## 重要边界

- 本项目是**个人自用预览**，未接入完整发布流程；不要擅自发布/推送（见 `vigil-watt/AGENTS.md`）。
- 只复刻原项目已实现的能力，不伪造数据；未接入的能力必须标注/禁用。
- 修改 UI 前先读 [`ui/design-system.md`](ui/design-system.md) 与 [`stability/crash-history.md`](stability/crash-history.md)。
