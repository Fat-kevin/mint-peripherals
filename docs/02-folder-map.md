# 目录地图（每个文件夹是什么）

根目录：`device-center-preview/`

```text
device-center-preview/
├─ DeviceCenterPreview.slnx          # 预览应用 + Core + 测试的解决方案
├─ README.md                          # L0：项目概览、构建、运行、已知限制
├─ AGENTS.md                          # 给后续 agent 的硬规则与阅读顺序
├─ docs/                              # 本渐进式文档（入口 docs/README.md）
├─ src/
│  ├─ DeviceCenterPreview.Core/       # 纯模型 + 服务契约；无 WinUI 依赖
│  │  ├─ Models/PeripheralModels.cs   # Snapshot / Controls / KeyMapping / 事件模型
│  │  └─ Services/                    # IPeripheralService、Mock、Unavailable、WriteResult
│  └─ DeviceCenterPreview.App/        # WinUI 3 宿主、真实设备服务、页面
│     ├─ App.xaml(.cs)                # 生命周期、单实例、托盘、任务栏胶囊
│     ├─ MainWindow.xaml(.cs)         # 固定窗口尺寸（仅 XAML 初始化前设置一次）、导航、图标
│     ├─ Views/                       # 5 个页面（设备中心/耳机/鼠标/遥控器/设置）
│     ├─ Services/
│     │  ├─ RealPeripheralService.cs  # 聚合 DeviceManager + 三个控制器 → Changed
│     │  ├─ RealHeadsetController.cs  # 耳机 SPP 连接/控制/查找
│     │  ├─ DeviceArtwork.cs          # 型号名 → 罗技产品图
│     │  ├─ AppLog.cs                 # 每日滚动日志
│     │  ├─ TrayService.cs            # Shell_NotifyIcon 托盘
│     │  ├─ Mouse/LogitechMouseService.cs # HID++ 读/写/板载配置
│     │  ├─ Remote/RemoteEngineClient.cs  # sidecar 启动/TCP/JSONL/状态
│     │  └─ Taskbar/PreviewTaskbarPill.cs # 任务栏分层贴片 + 全屏隐藏
│     └─ Assets/
│        ├─ PeripheralHub.ico/.png    # 当前应用图标「外设中枢」
│        ├─ VigilWatt.ico/.png        # 旧图标（保留未用）
│        └─ Devices/                  # 耳机/遥控器/鼠标图片；Logitech/ 为型号图
├─ external/                          # 自包含依赖（不再引用外部仓库）
│  ├─ VigilWatt.Core/                 # HID++/罗技底层
│  ├─ YuandaoTws.Domain/              # 耳机领域模型
│  ├─ YuandaoTws.Application/         # 耳机应用层
│  ├─ YuandaoTws.Infrastructure/      # 耳机 SPP/WinRT 实现
│  └─ rust/                           # sayall-core + sayall-windows Cargo workspace
├─ sidecars/sayall-engine/            # Rust 遥控器无头引擎（独立进程）
│  ├─ src/main.rs                     # 协议/命令/1Hz 快照/按键边沿事件
│  ├─ Cargo.toml                      # 引用 external/rust 本地 crates
│  ├─ README.md                       # sidecar 构建/协议/命令（本目录权威）
│  └─ target/                         # cargo 产物（不要提交/不要手改）
├─ tests/DeviceCenterPreview.Core.Tests/ # 5 个纯逻辑单元测试
└─ tools/DeviceProbe/                 # 只读设备探测工具（独立 console）
```

## 不应该提交/手改的目录

- `bin/`、`obj/`、`sidecars/**/target/`：构建产物。
- `%LOCALAPPDATA%\DeviceCenterPreview\`：日志、history.db、crash.log。
- `%APPDATA%\app.getsayall.remote-mic.windows\`：遥控器映射/设置（原版共享）。
- `external/` 是上游项目的源码快照；升级依赖时只改这里，不要重新指向目录外。

## 依赖方向

```text
Core.Models  ←  Core.Services  ←  App
App  →  external/VigilWatt.Core（DeviceManager/HID++ 底层）
App  →  external/YuandaoTws.*（耳机 SPP/协议）
App  →  sidecars/sayall-engine.exe（TCP + token + JSONL）
sidecars/sayall-engine  →  external/rust/crates（sayall-core/sayall-windows）
```

Core 不得引用 WinUI / Windows.Devices；页面只通过 `IPeripheralService` 拿数据。
所有箭头都落在 `device-center-preview/` 内，不依赖兄弟目录。
