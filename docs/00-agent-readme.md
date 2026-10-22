# 外设中枢（Device Center Preview）

VigilWatt 外设整合的原生 WinUI 3 预览应用：聚合 **耳机 / 鼠标 / 遥控器** 的电量、状态与二级控制，
并常驻任务栏胶囊与系统托盘。

> 文档采用渐进式结构：**先读本文件**，再按需进入
> [`docs/README.md`](docs/README.md)。给后续 agent 的硬规则见 [`AGENTS.md`](AGENTS.md)。
>
> **自包含**：本目录已包含所有 C# 依赖（`external/`）和 Rust 遥控器依赖（`external/rust/`）。
> 编译和运行不再需要外部的 `vigil-watt`、`NICEHCK_OriG-in`、`remote-mic-app-windows` 文件夹。

## 当前状态（2026-10-20）

- **名称/图标**：外设中枢；`Assets/PeripheralHub.ico`（EXE/窗口/托盘/标题栏统一）。
- **窗口**：固定 1280×840，不可缩放/最大化；页面内部纵向滚动。
- **耳机**：真实 SPP；L/R/盒电量、固件、六态 ANC、七档 EQ（原生 ComboBox）、
  五个快捷开关、查找耳机（5 秒倒计时可取消）。
- **鼠标**：罗技 HID++ 通用读取；G304 的 5 档 DPI、当前档位、配置名、板载模式、
  设备返回的 1–16 个按键映射、固件读写（CRC 回读）；20 个罗技型号图片，未知型号回退图标。
- **遥控器**：Rust sidecar 常驻；MI RC 自动连接；13 键 × 3 手势映射表，点表格直接分配并
  自动保存；语音工具切换显示快捷键提示；“接收测试”只观察不注入。
- **任务栏胶囊**：左下角 235 DIP；只显示已连接设备；35px 高；全屏自动隐藏，退出恢复。
- **托盘**：运行期间常驻；双击打开；右键打开/退出。
- **关闭按钮**：点 X / Alt+F4 只隐藏主窗口，任务栏胶囊和托盘图标继续运行；要真正退出请用托盘右键“退出”。
- **开机自启**：设置页顶部开关；写入当前用户 `Run` 启动项，不需要管理员权限；程序移动后下次启动自动修复路径。
- **稳定性**：已移除多类 WinUI 原生崩溃触发点（resize、弹层、StartBringIntoView、图片/控件重建）。
- 不显示伪造数据；真实服务失败时显示错误原因，不退回模拟数据。

## 快速构建 / 运行

```powershell
cd device-center-preview

# 主应用（必须 x64；构建前退出正在运行的实例）
dotnet build src/DeviceCenterPreview.App/DeviceCenterPreview.App.csproj -c Debug -p:Platform=x64

# 运行
src\DeviceCenterPreview.App\bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\DeviceCenterPreview.App.exe

# 测试
dotnet test tests/DeviceCenterPreview.Core.Tests/DeviceCenterPreview.Core.Tests.csproj
```

> 以上命令只依赖 `device-center-preview/` 内的文件。C# 依赖在 `external/`，
> Rust 依赖在 `external/rust/`，sidecar 源码在 `sidecars/`。

安装包（自包含，不需要目标机另装 .NET）：

```powershell
dotnet publish src/DeviceCenterPreview.App/DeviceCenterPreview.App.csproj `
  -c Release -p:Platform=x64 -r win-x64 --self-contained true `
  -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false `
  -o publish/app

& "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" installer\DeviceCenterPreview.iss
```

输出：`dist\DeviceCenterPreview-Setup-1.0.2.exe`

遥控器 sidecar：

```powershell
$env:PATH = "$env:USERPROFILE\.cargo\bin;$env:PATH"
cargo build --manifest-path sidecars/sayall-engine/Cargo.toml
```

> 构建目标会把 `sayall-engine.exe` 复制到输出目录 `sidecars/`。
> 原版 `sayall-windows-app` 与本应用不能同时运行；原版 `YuandaoTws.Desktop` 会占用耳机 SPP。

## 目录

```text
device-center-preview/
├─ README.md / AGENTS.md / docs/     # 渐进式文档
├─ src/DeviceCenterPreview.Core/     # 模型 + 服务契约
├─ src/DeviceCenterPreview.App/      # WinUI 3 页面、真实设备服务、任务栏/托盘
├─ external/                        # 自包含依赖：VigilWatt.Core + 耳机分层 + Rust crates
├─ sidecars/sayall-engine/           # Rust 遥控器引擎（引用 external/rust）
├─ tests/DeviceCenterPreview.Core.Tests/
└─ tools/DeviceProbe/                # 只读设备探测工具
```

详见 [`docs/02-folder-map.md`](docs/02-folder-map.md)。

## 已知限制

- 鼠标完整写入仅保证 G304/G305 的 HID++ 单 profile 格式；OpenMouse 全品牌驱动未移植
  （94 驱动/158 supported 型号在外部 AGPL 包中）。
- 遥控器映射导入导出、首次向导、Helper 健康检查未接入；Helper 载荷已随安装包分发并由安装器注册计划任务。
- 设置持久化、开机启动、连接通知未接入。
- 本机 WinUI 3 运行时对窗口 resize/弹层敏感，当前固定窗口是无崩溃的取舍；
  详见 [`docs/stability/crash-history.md`](docs/stability/crash-history.md)。

## 诊断

- 日志：`%LOCALAPPDATA%\DeviceCenterPreview\logs\app-YYYYMMDD.log`
- 崩溃日志：同目录 `crash.log`
- 崩溃转储：`%LOCALAPPDATA%\CrashDumps`
- 遥控器配置：`%APPDATA%\app.getsayall.remote-mic.windows\`

## 文档索引

- 总入口：[`docs/README.md`](docs/README.md)
- 架构/目录/构建：[`docs/01-architecture.md`](docs/01-architecture.md)、[`docs/02-folder-map.md`](docs/02-folder-map.md)、[`docs/03-build-run-debug.md`](docs/03-build-run-debug.md)
- 功能：[`docs/features/`](docs/features/)
- 协议：[`docs/internals/`](docs/internals/)
- UI：[`docs/ui/design-system.md`](docs/ui/design-system.md)
- 稳定性/决策/历史：[`docs/stability/crash-history.md`](docs/stability/crash-history.md)、[`docs/decisions.md`](docs/decisions.md)、[`docs/history/implementation-log.md`](docs/history/implementation-log.md)
