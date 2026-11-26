<div align="center">

<img src="docs/screenshots/icon.png" width="96" alt="薄荷外设图标"/>

# 薄荷外设 · Mint Peripherals

**把散落各处的蓝牙外设，收进同一扇窗。**

耳机调音、鼠标调档、遥控映射、任务栏电量值守——一台 Windows 设备上的外设控制台，
原生 WinUI 3，不塞浏览器内核，不画假数据。

[![Version](https://img.shields.io/badge/version-1.2.2-0E9A76?style=flat-square)](https://github.com/Fat-kevin/mint-peripherals/releases)
[![License: MIT](https://img.shields.io/badge/License-MIT-0E9A76?style=flat-square)](LICENSE)
[![Platform](https://img.shields.io/badge/Platform-Windows%2010%2F11-0078D4?style=flat-square)](https://github.com/Fat-kevin/mint-peripherals/releases)
[![.NET 8](https://img.shields.io/badge/.NET-8-512BD4?style=flat-square)](src)
[![WinUI 3](https://img.shields.io/badge/UI-WinUI%203-3CCFA5?style=flat-square)](src/DeviceCenterPreview.App)

[下载安装](#-安装) · [亮点](#-亮点) · [界面](#-界面) · [任务栏](#-任务栏显示) · [支持的设备](#-支持的设备) · [从源码构建](#-从源码构建) · [文档](#-文档) · [开源协议](#-开源协议)

</div>

---

## ✨ 亮点

**🌿 真·设备真·协议** —— 不模拟、不缓存造假：耳机走 SPP 串口协议、鼠标走罗技 HID++、遥控器走自研 Rust 引擎，每一颗电量数字都来自真实回包。写入必须"先读 → 写 → 回读校验"，失败就说失败。

**📊 任务栏一眼可见** —— 任务栏内嵌 `🎧 左70% 右60%` 式的实时电量：透明无胶囊、跟随明暗主题与 DPI；位置可选（自动 / 左下角 / 通知区域左侧 / 任务栏左侧），拥挤任务栏也不覆盖任务按钮；低电红、充电蓝闪、充满绿勾、休眠变暗。

**🎨 Mint Studio 设计语言** —— 雾白薄荷画布、呼吸状态点、无边胶囊开关、零外框布局。深浅双主题，外加**壁纸染色**：像 Mica 一样让界面透出你桌面的颜色，但没有实时合成的开销与崩溃风险。

**🔔 常驻不打扰** —— 最小化到托盘继续值守，连接提醒、低电量提醒、写入失败提醒一个不少。想关哪个，设置页一键关。

**🧱 稳定是纪律，不是口号** —— 固定窗口杜绝 resize 崩溃、页面内编辑取代弹层、15 秒刷新只改文本不重建控件。每一类黑屏触发点都有[档案可查](docs/stability/crash-history.md)，踩过的坑不再踩第二次。

**🚀 装完即用** —— 安装包自带 .NET 8 运行时与全部依赖，按用户安装不需要管理员；升级直接双击覆盖，设置原地保留。

## 📸 界面

| 设备中心 · 总览 | 鼠标 · HID++ |
|:---:|:---:|
| ![设备中心](docs/screenshots/hub.png) | ![鼠标页](docs/screenshots/mouse.png) |

| 耳机 · SPP（双耳独立电量） | 遥控器 · rc003 |
|:---:|:---:|
| ![耳机页](docs/screenshots/headset.png) | ![遥控器页](docs/screenshots/remote.png) |

| 设置 · 外观与授权 | 深色 + 壁纸染色 |
|:---:|:---:|
| ![设置页](docs/screenshots/settings.png) | ![深色主题](docs/screenshots/dark.png) |

## 📊 任务栏显示

把电量直接放上任务栏，不必常开主窗口：

![任务栏内嵌电量条](docs/screenshots/taskbar.png)

- **形态**：透明内嵌 `图标 + 百分比`，无胶囊底板；跟随任务栏明暗主题、DPI 缩放，Win10 图标自动回退 Segoe MDL2；
- **内容**：耳机（可分左右耳）、鼠标、遥控器，以及可选的键盘 / 手柄 / 音箱等通用设备；最多 6 项，溢出显示 `+N`；
- **状态**：低电 ≤15% 红、充电蓝闪电、充满绿勾、休眠变暗、未知 `?`；悬停查看逐设备明细（含充电盒电量）；
- **位置**：自动（优先任务栏左侧空位）/ 左下角 / 通知区域左侧 / 任务栏左侧，设置里随时切换；
- **交互**：左键打开主窗口，右键原生菜单（打开 / 立即刷新 / 任务栏显示设置 / 退出）；Explorer 重启后自动重建，全屏游戏自动隐藏。

| 设置 · 任务栏显示 |
|:---:|
| ![任务栏设置](docs/screenshots/taskbar-settings.png) |

> 任务栏显示形态参考了开源项目 BatteryHub；本项目为 clean-room 实现，只吸收行为与协议事实，未复制其 GPL-3.0 代码。调研记录见 [BatteryHub 调研与设计](docs/investigations/2026-11-27-batteryhub-design.md)。

## 🎯 支持的设备

| 模块 | 已验证设备 | 能做什么 |
|---|---|---|
| 🎧 耳机 | YUANDAO OriG in | 双耳独立电量、ANC 模式、EQ 预设、查找耳机、静默写入开关；任务栏可分左右耳显示 |
| 🖱️ 鼠标 | 罗技 G304 / G305 | DPI 五档位、板载配置读写（含轮询率/按键映射）、主机/板载模式切换 |
| 🎮 遥控器 | 小米遥控器（MI RC） | 语音通道、按键映射、音量键/返回键系统级注入 |
| ⌨️ 通用设备 | Windows 已知电池的键盘 / 手柄 / 音箱 / 第二只鼠标等 | 只读聚合显示（VigilWatt DeviceManager + Generic HID Battery Strength + Xbox Gamepad），任务栏与总览「其他设备」可见 |
| 🔋 电量 | 以上全部 | 常驻监控、任务栏内嵌条、连接/低电提醒、托盘速览 |

> 未验证型号会**明确标注只读**，绝不假装可写。当前写入仅对罗技 HID++ 已验证型号开放。
> 通用设备为只读聚合，不写入、不缓存伪造数值；可在设置中关闭「显示其他设备」。

## 📦 安装

1. 到 [Releases](https://github.com/Fat-kevin/mint-peripherals/releases) 下载最新的 `DeviceCenterPreview-Setup-x.x.x.exe`（约 83 MB，自包含运行时）
2. 双击安装——按用户安装，不需要管理员；安装中会弹一次 UAC，用于注册遥控器音量键/返回键的辅助任务
3. 装完直接启动；**旧版本升级直接双击覆盖**，主题、映射、开关、任务栏位置全部原地保留

<details>
<summary>安装时可能遇到的提示</summary>

- **SmartScreen「未知发布者」**：本地构建未签名，点「更多信息 → 仍要运行」
- 首次启动若提示未发现鼠标：动一动鼠标再点「刷新鼠标」（罗技接收器休眠唤醒）
- 任务栏条默认开启；不想常驻可在 设置 → 外观 → 任务栏显示 关闭（托盘图标仍在）
</details>

## 🔧 从源码构建

```powershell
# 需要 .NET 8 SDK + Windows App SDK 1.7；必须 x64
dotnet build src/DeviceCenterPreview.App/DeviceCenterPreview.App.csproj -c Debug -p:Platform=x64

# 跑测试
dotnet test tests/DeviceCenterPreview.Core.Tests/DeviceCenterPreview.Core.Tests.csproj

# 打安装包（Inno Setup 6 + 自包含发布）
dotnet publish src/DeviceCenterPreview.App/DeviceCenterPreview.App.csproj `
  -c Release -p:Platform=x64 -r win-x64 --self-contained true -o publish/app
& "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" installer/DeviceCenterPreview.iss
```

遥控器功能需要 Rust 工具链构建 sidecar：`cargo build --manifest-path sidecars/sayall-engine/Cargo.toml`

## 🗂️ 项目结构

```
src/DeviceCenterPreview.Core   # 模型 / 设置存储 / 协议契约（无 UI 依赖）
src/DeviceCenterPreview.App    # WinUI 3 界面 / 真实设备服务 / 任务栏内嵌条
external/                      # 自包含依赖源码（耳机控制栈、VigilWatt 电量核心）
sidecars/sayall-engine/        # Rust 遥控器引擎
vendor/rc003-helper/           # GPL-3.0 音量键辅助（独立进程随包分发）
installer/                     # Inno Setup 脚本
docs/                          # 渐进式文档：功能 / 协议 / 稳定性档案 / 决策记录
```

## 📚 文档

- [文档索引](docs/README.md) —— 按"改什么"快速找到对应文档
- [任务栏内嵌条](docs/features/taskbar-tray.md) —— 定位、主题、状态与重建策略
- [稳定性档案](docs/stability/crash-history.md) —— 每个黑屏触发点与规避方式
- [实现日志](docs/history/implementation-log.md) —— 1.2.x 任务栏/设备扩展记录
- [BatteryHub 调研与设计](docs/investigations/2026-11-27-batteryhub-design.md) —— 任务栏形态与设备覆盖的取舍

## 🤝 开源协议

本项目代码以 [MIT](LICENSE) 协议开源。

随安装包分发的 `rc003-helper`（音量键/返回键辅助）是独立进程，以
[GPL-3.0](vendor/rc003-helper/LICENSE) 协议随包分发，源码与许可声明保留在
`vendor/rc003-helper/`。本应用与其通过命令行参数交互，不产生代码级链接。

---

<div align="center">

**薄荷外设** · WinUI 3 / .NET 8 / Rust · 为爱发电的个人工具

⭐ 如果它管住了你的外设，欢迎点个 Star

</div>
