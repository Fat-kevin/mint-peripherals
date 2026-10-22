# 构建、运行、调试

## 1. 环境

- Windows 10/11 x64
- .NET 8 SDK（本项目目标 `net8.0-windows10.0.19041.0`）
- Windows App SDK 1.7（由 NuGet 还原）
- Rust/cargo（仅遥控器 sidecar）
- 必须 **x64**；AnyCPU 会触发 `WindowsAppSDKSelfContained` 错误

## 2. 构建

项目已自包含：C# 依赖在 `external/`，Rust 依赖在 `external/rust/`，不需要
`vigil-watt`、`NICEHCK_OriG-in`、`remote-mic-app-windows` 目录。

```powershell
cd E:\Project\Bluetooth\device-center-preview

# 主应用（关闭正在运行的实例，否则 DLL 被锁定）
dotnet build src/DeviceCenterPreview.App/DeviceCenterPreview.App.csproj -c Debug -p:Platform=x64

# 或者从解决方案构建（包含 Core、external 和测试）
dotnet build DeviceCenterPreview.slnx -c Debug -p:Platform=x64
```

sidecar：

```powershell
$env:PATH = "$env:USERPROFILE\.cargo\bin;$env:PATH"
cargo build --manifest-path sidecars/sayall-engine/Cargo.toml
```

主应用构建目标 `CopySayallEngine` 会把 `target/{debug,release}/sayall-engine.exe`
复制到输出目录的 `sidecars/`。

## 3. 安装包

安装包使用 Inno Setup 6 编译，安装目录按当前用户放在
`%LOCALAPPDATA%\Programs\DeviceCenterPreview`，不需要管理员权限。

```powershell
# 自包含运行目录：包含 .NET 8 Desktop Runtime、Windows App SDK、sidecar
dotnet publish src/DeviceCenterPreview.App/DeviceCenterPreview.App.csproj `
  -c Release -p:Platform=x64 -r win-x64 --self-contained true `
  -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false `
  -o publish/app

# 编译安装程序（Inno Setup 6）
& "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" installer\DeviceCenterPreview.iss
```

输出：`dist\DeviceCenterPreview-Setup-1.0.2.exe`。安装包未签名，Windows 可能提示
“未知发布者”；这是本地构建的正常现象。安装包会额外弹一次 UAC，用于把随包的
`helper\sayall-helper.exe` 注册为 `SayAll RC003 Helper` 计划任务（音量键/返回键需要）。
安装/卸载脚本见 [`installer/README.md`](../installer/README.md)。

## 4. 运行

```text
src\DeviceCenterPreview.App\bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\DeviceCenterPreview.App.exe
```

或：

```powershell
dotnet run --project src/DeviceCenterPreview.App/DeviceCenterPreview.App.csproj -c Debug -p:Platform=x64
```

## 5. 测试
```powershell
dotnet test tests/DeviceCenterPreview.Core.Tests/DeviceCenterPreview.Core.Tests.csproj
```

当前 10 个纯逻辑测试（含 `AppSettingsStore` 的默认值/往返/阈值夹紧/损坏隔离）；硬件协议不做单测，必须真机验证。

## 6. 日志与诊断

```text
%LOCALAPPDATA%\DeviceCenterPreview\logs\app-YYYYMMDD.log
%LOCALAPPDATA%\DeviceCenterPreview\logs\crash.log
%LOCALAPPDATA%\DeviceCenterPreview\settings.json
%LOCALAPPDATA%\DeviceCenterPreview\backups\*.zip
%LOCALAPPDATA%\DeviceCenterPreview\diagnostics\*.zip
%LOCALAPPDATA%\CrashDumps\DeviceCenterPreview.App.exe.*.dmp
```

- 设置页“导出诊断包”会生成脱敏日志 + `summary.json` + `settings.json`；不直接复制原始日志。
- 日志只记录设备名/状态/特征响应/错误，不记录语音内容；诊断包里的 MAC/用户名/HID 路径会打码。
- WER 报告：`C:\ProgramData\Microsoft\Windows\WER\ReportArchive\AppCrash_DeviceCenterPrev_*`。
- 原生崩溃可用 WinDbg 的 `cdb.exe`：

```powershell
$cdb = "C:\Program Files\WindowsApps\Microsoft.WinDbg_1.2610.1001.0_x64__8wekyb3d8bbwe\amd64\cdb.exe"
& $cdb -z <dump> -y . -c ".ecxr; kb; q"
```

## 7. 常见问题

| 现象 | 原因 / 处理 |
|---|---|
| 构建 MSB3027/MSB3021 | 应用还在运行，先退出/结束进程再构建 |
| 双击没反应 | 单实例：已有实例被激活；若上一个实例正在退出，稍等重试 |
| 黑屏、内容消失 | 先看 [`stability/crash-history.md`](stability/crash-history.md)；不要加窗口 resize/弹层/StartBringIntoView |
| 设置→耳机→设置后只剩标题栏 | 缓存页面二次进入 + `NumberBox` 的已知崩溃；设置页改用 `ComboBox`，不要改回 |
| 多页面轮转后只剩标题栏 | 缓存页中的 `NumberBox`/`RadioButtons` 会触发 XAML 内容树消失；鼠标 DPI 用 `TextBox`，耳机 ANC/EQ 用 `ComboBox` |
| 耳机 SPP `0x80072740` | 原版 `YuandaoTws.Desktop` 占用同一 SPP 通道，先退出原版 |
| 鼠标全部特征为 0 | 鼠标休眠；移动鼠标唤醒后点“刷新鼠标” |
| 遥控器映射保存失败 | 见 [`internals/mapping-storage.md`](internals/mapping-storage.md)；新按键必须有 3 个手势槽位 |
| sidecar 未启动 | 确认输出目录 `sidecars/sayall-engine.exe` 存在；原版 SayAll 不要同时运行 |

## 8. 修改前的硬规则

- 不要改窗口尺寸/启用 resize；初始尺寸只能放在 `InitializeComponent` 之前设置一次（本机构造后再 resize 会在数分钟后原生崩溃）。
- 不要使用 Flyout/ContentDialog/StartBringIntoView 做映射编辑。
- 不要在 15s 刷新里重新创建 BitmapImage、ItemsSource 或大量控件。
- 任何设备事件回 UI 前必须 `DispatcherQueue.TryEnqueue`。
- 写入硬件前先读后写、回读校验；失败要回滚/提示，不伪造成功。
