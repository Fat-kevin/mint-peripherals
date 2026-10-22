# AGENTS.md — device-center-preview

> 这是「薄荷外设」（Device Center Preview）的局部规则。`vigil-watt/AGENTS.md` 是同工作区的规则入口，其 git/远程/构建红线仍然有效；
> 本文件补充本目录的阅读顺序与稳定性约束。根目录 `README.md` 是对外宣传页，agent 总览看 `docs/00-agent-readme.md`。

## 开工前先读

1. 本文件；
2. [`docs/README.md`](docs/README.md)（渐进式文档入口；技术版总览 [`docs/00-agent-readme.md`](docs/00-agent-readme.md)）；
3. 改 UI/窗口 → [`docs/ui/design-system.md`](docs/ui/design-system.md) + [`docs/stability/crash-history.md`](docs/stability/crash-history.md)；
4. 改设备协议 → 对应 `docs/internals/*.md`；
5. 改构建/运行 → [`docs/03-build-run-debug.md`](docs/03-build-run-debug.md)。

## 项目速览

- 名称：外设中枢（Device Center Preview）
- 技术栈：WinUI 3 / .NET 8 / x64 / Windows App SDK 1.7
- 结构：`src/DeviceCenterPreview.Core`（模型/契约）+ `src/DeviceCenterPreview.App`（UI/真实服务）+ `external/`（自包含依赖源码）+ `sidecars/sayall-engine`（Rust 遥控器引擎）+ `tests` + `tools/DeviceProbe`
- 自包含：本目录可以独立编译/运行，不再引用 `vigil-watt`、`NICEHCK_OriG-in`、`remote-mic-app-windows`；新增引用只能指向 `device-center-preview/` 内部。
- 数据入口：页面只通过 `App.PeripheralService`（`IPeripheralService`）取数据/写入。

## 硬约束（违反会导致黑屏/闪退）

1. **窗口固定 1280×840**：尺寸只能在 `InitializeComponent` 之前设置一次，之后不要加 resize/最大化/最小尺寸重排逻辑；构造后再 `AppWindow.Resize` 会延迟黑屏/原生崩溃。
2. 不要使用 `Flyout`、`ContentDialog`、`StartBringIntoView()` 做主交互/映射编辑。
3. 15s 刷新只更新文本/数值；不要每次重建 `BitmapImage`、`ItemsSource` 或大量控件。
4. WinRT/后台事件回 UI 前必须 `DispatcherQueue.TryEnqueue`。
5. 页面 `Unloaded` 必须退订 `Changed`/`RemoteButtonReceived`、停止计时器、恢复 `mappings.suspend=false`。
6. 硬件写入必须“先读 → 写 → 回读校验”；失败要提示，不伪造成功。
7. 不显示伪造电量/设备；真实服务失败时用 `UnavailablePeripheralService`。

## 构建

```powershell
dotnet build src/DeviceCenterPreview.App/DeviceCenterPreview.App.csproj -c Debug -p:Platform=x64
dotnet test tests/DeviceCenterPreview.Core.Tests/DeviceCenterPreview.Core.Tests.csproj
# sidecar（需要 cargo）
$env:PATH = "$env:USERPROFILE\.cargo\bin;$env:PATH"
cargo build --manifest-path sidecars/sayall-engine/Cargo.toml
```

- 构建前先退出正在运行的 app，否则 `MSB3027`。
- 必须 x64；AnyCPU 会触发 `WindowsAppSDKSelfContained` 错误。

## 不要碰

- `bin/`、`obj/`、`sidecars/**/target/`；
- `%APPDATA%\app.getsayall.remote-mic.windows\` 中的用户映射（除非用户明确要求）；
- `vigil-watt/` 仓库的凭据/发布文件；
- 未经用户确认的 git 写操作/远程操作。

## 文档维护

- 新增功能/修复稳定性问题后，同步更新：
  - `docs/features/*.md` 或 `docs/internals/*.md`；
  - `docs/history/implementation-log.md`；
  - 如改变架构/窗口/弹层策略，更新 `docs/decisions.md` 与 `docs/stability/crash-history.md`。
- 文档按“L0 README → L1 架构/目录/构建 → L2 功能 → L3 协议 → L4 稳定性/决策/历史”渐进组织，
  不要把全部细节堆回 README。
