# 关键设计决策（ADR 摘要）

## D1：固定窗口 1280×840，不启用 resize

**背景**：本机 WinUI 3 在运行中改变窗口尺寸会触发 `Microsoft.UI.Xaml.dll` 原生崩溃
（combase `0x80070578`），已多次用 dump/WER 证实。

**决定**：固定尺寸、禁用拖拽 resize 与最大化；尺寸只在 `InitializeComponent` 之前设置一次，
之后绝不再调用 `AppWindow.Resize`；页面内部用 ScrollViewer 纵向滚动。

**代价**：不能自由调整窗口大小；好处是布局稳定、可预测、不黑屏。
**重新评估条件**：升级 Windows App SDK/WinUI 运行时，并做 resize + 页面切换压力测试后再考虑。

**2026-10-20 补记**：把 `Resize` 放在 `InitializeComponent` 之后，启动时看似正常，
但约 8–9 分钟后会黑屏并以 combase `0x80070578` 崩溃（WER 有记录、`crash.log` 无）。
前置到 XAML 树创建前、之后不再 resize，1280×840 连续 10+ 分钟及最小化/恢复均正常。

## D2：遥控器使用独立 Rust sidecar

**背景**：`remote-mic-app-windows` 是 Tauri + Vue + Rust；完整重写成本极高。

**决定**：把 Rust 引擎做成无头 sidecar，loopback TCP + 一次性 token + JSONL；
WinUI 只实现界面与 sidecar 生命周期。

**代价**：多一个进程；必须处理单实例/钩子冲突。
**收益**：复用已验证的 BLE/ATVV/映射/key host 逻辑，不用重写 4.6 万行平台代码。

## D3：不嵌入 WebView2/Tauri 页面

**决定**：所有页面用原生 WinUI 3 重写；不嵌入原版网页。
**原因**：用户要求原生 UI、统一视觉、避免 WebView2 额外崩溃/资源占用。

## D4：映射编辑不使用弹层

**背景**：Flyout/ContentDialog/StartBringIntoView 在本机多次引发 XAML 内容树崩溃。

**决定**：映射表内直接点击功能 → 页面内嵌 `ActionEditorCard`；保存后折叠。
**代价**：编辑面板占据页面空间；收益是稳定、可见、无弹层。

## D5：先做罗技 HID++，不一次性移植 OpenMouse 全部驱动

**背景**：OpenMouse 的协议在外部 AGPL `@openmouse/protocol`（94 驱动、158 supported 型号），
完整移植估计 0.75–1.5 人年；当前用户设备是 G304。

**决定**：先完成罗技 HID++ 通用读取 + G304 完整写入 + 20 个罗技型号图片；
其他品牌明确标注未支持，不伪造能力。

**后续路线**：若需要全品牌，优先做 Node sidecar 复用 `@openmouse/protocol`，
而不是逐个把驱动重写成 C#。

## D6：不显示伪造数据

**决定**：真实服务失败时使用 `UnavailablePeripheralService` 显示错误原因；
断开设备清空电量；未实现控件禁用并标注。

## D7：中文产品名「外设中枢」

**决定**：去掉 `VigilWatt` 英文前缀与“预览”后缀；窗口/托盘/标题栏统一使用「外设中枢」；
图标使用新的 hub 图形（`PeripheralHub.ico`）。

## D8：写入授权放在服务层

**背景**：只有页面禁用按钮时，刷新、快捷键、外部调用仍可能绕过；且用户要求“关闭后真的不写”。

**决定**：`IPeripheralService` 的每个写方法先检查模块开关和写入开关，拒绝时返回
`WriteResult.Fail(原因)` 并触发写入失败通知；页面再用 `WriteAllowed` 禁用控件。

**代价**：每个写入口都要重复闸门（已集中在 helper 中）；收益是行为可验证、不会“看起来禁用但仍写入”。

## D9：备份/诊断使用固定目录，不调系统文件选择器

**背景**：本应用无包身份；`FileSavePicker` 在设置页调用后日志停在
`backup picker opening`，不弹对话框也不返回，属于不可恢复的挂起。系统选择器还会引入额外 COM/窗口状态。

**决定**：备份导出到 `%LOCALAPPDATA%\DeviceCenterPreview\backups\`，诊断导出到 `diagnostics\`；
页面内 `ComboBox` 选择备份后导入，提供“打开备份/诊断/配置目录”按钮。

**代价**：不能任选保存路径；收益是稳定、可脚本化、导入路径可控（白名单之外不接受）。

## D10：UI 锁定原生 WinUI 3，不引入 Web UI 运行时

**背景**：视觉改版容易滑向“用 AI 生成一套 Web 页面再套壳”，但本项目要的是 Windows 原生桌面应用，
且现有 WinUI 3 已有设备服务、Sidecar 和稳定性约束。

**决定**：
- 技术栈固定为 **WinUI 3 + Windows App SDK 1.7 + .NET 8 + C# 12 + x64**，
  `WindowsPackageType=None`、`WindowsAppSDKSelfContained=true` 保持不变。
- 所有页面和组件用 **XAML 原生控件 / 自定义 `UserControl`** 实现；不引入 WebView2、Electron、Tauri、
  HTML/CSS 运行时，也不把 AI 生成的 React/Tailwind 代码直接搬进来。
- 不整体迁移到 WPF / Avalonia / MAUI：WPF 视觉与 Fluent 组合能力落后，Avalonia/MAUI 不是 Windows 原生，
  迁移成本高于收益。
- 新 UI 使用 `ResourceDictionary` 管理 token 和控件模板；先不做全量 MVVM 重写，
  保留现有 `IPeripheralService` 与页面 code-behind，新增组件用依赖属性/事件衔接。
- 设计工具（Figma、v0、Uizard、Superdesign）只用于**静态方向稿和设计 token**，不是运行时依赖。

**代价**：XAML 实现比 Web 慢，复杂图表/动效要自己做；收益是真正的 Windows 原生交互、
系统主题/DPI/无障碍一致，且不引入第二套渲染栈。

**验收**：`dotnet build` 为 x64 原生构建；运行时不出现 WebView2 进程/HTML 资源；
新增控件不触碰 `Flyout`/`ContentDialog`/`StartBringIntoView`，不改变固定窗口尺寸策略。


