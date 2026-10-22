# 稳定性：黑屏 / 闪退 / 卡死排查记录

> 本文件的目的是防止后续 agent 再把已经踩过的坑重新引入。

## 1. 现象

- 窗口还在、标题栏按钮可点，但内容区全黑；
- 有的情况过一会自行退出；
- UIA 树只剩 5 个非客户区元素（标题栏/最小化/最大化/关闭），说明 **XAML 内容树整体消失**；
- 主窗口 `Responding=True`，但页面不可用。

## 2. 已确认的原生崩溃

WER / dump 关键信息：

- `Microsoft.UI.Xaml.dll 3.1.7.0`
- 异常：`c000027b`（fail fast）或 `c0000005`（访问冲突）
- 错误上下文：`combase 0x80070578`（`ERROR_INVALID_WINDOW_HANDLE`）
- 堆栈会经过 `uxtheme` / `user32!SendMessageW` / `Microsoft.UI.Xaml!OverrideXamlResourcePropertyBag`

含义：窗口句柄在 XAML 处理主题/窗口消息时已失效。不是普通 C# 异常，`crash.log` 通常没有记录。

## 3. 已定位并修复的触发点

| 触发 | 说明 | 修复 |
|---|---|---|
| Mica / 背景材质 | 本机 NVIDIA + 虚拟显示/WARP 下持续合成，CPU 高并可能黑屏 | 固定纯色背景 |
| `AppWindow.Hide/Show` | 关闭到托盘/恢复切换导致窗口句柄失效 | 去掉关闭到托盘；X 直接退出 |
| 在 `AppWindow.Changed` 里 `Resize` | 最小尺寸逻辑在窗口消息中重入 resize | 删除该逻辑，改为固定窗口 |
| `InitializeComponent` 之后调用 `AppWindow.Resize` | 启动显示正常，约 8–9 分钟后黑屏并以 `combase 0x80070578` 崩溃；WER 事件可见，`crash.log` 无记录 | 把尺寸设置**前置到 `InitializeComponent` 之前**，之后不再 resize；1280×840 连续 10+ 分钟、最小化/恢复均正常 |
| 运行中拖拽改变窗口尺寸 | WinUI 3 原生崩溃（本机必现） | 固定 1280×840，禁用 resize/最大化 |
| `StartBringIntoView()` | 打开映射编辑后滚动定位，随后切页会导致内容树消失 | 删除；改用 `ScrollViewer.ChangeView` 或固定位置 |
| `Flyout` 打开时改变窗口尺寸 | 弹层 + resize 组合崩溃 | 不使用 Flyout |
| `ContentDialog` 模态 | 早期黑屏/卡死可疑点 | 查找耳机改为页面内倒计时；映射改为内嵌面板 |
| 15s 刷新重复 `new BitmapImage` | 反复解码几百 KB 产品图 | 只在型号变化时创建并缓存 |
| 15s 刷新重置 `ItemsSource` | ComboBox/列表内部重建 | 用签名判断，只有变化才更新 |
| 大量动态控件每秒重建 | XAML 布局压力/句柄失效 | 静态构建 + 只改文本/数值 |
| 设置页二次进入 + `muxc:NumberBox` | 固定窗口内缓存页面重新加载时，UIA 树只剩 5 个非客户区元素；窗口 `Responding=True`，无 `crash.log` 记录 | 设置页不再用 `NumberBox`，低电量阈值改为 `ComboBox`；二次进入已验证正常 |
| 鼠标页缓存页中的 5 个 `NumberBox` + 6 个 `ComboBox` | 连续「鼠标→设置→设备中心」约 4 轮后，再进鼠标页时 UIA 树只剩 5 个元素；无 `crash.log` | 改为 XAML 静态 5 个 `TextBox` + 6 个 `ComboBox`，只在构造时设置一次 ItemsSource；6 轮切换已验证正常 |
| 耳机页缓存页中的 `RadioButtons` | 全页面轮转 2 轮后耳机页/下一页面 UIA 树只剩 5 个元素；无 `crash.log` | ANC/EQ 改为 `ComboBox`，ItemsSource 只在构造时设置一次；5 轮全页面轮转已验证正常 |
| 遥控器断线重连时重建 `DeviceCombo.ItemsSource` | 断开→扫描→连接后点“连接”时内容树只剩 5 个元素 | `DeviceCombo` 签名去掉 `IsConnected`，不再因连接状态变化重建；断线重连已验证恢复 |
| `Slider.ValueChanged` 里引用**后解析**的控件 | `Minimum="400"` 在 XAML 解析期就触发 ValueChanged；回调里访问 `OnboardToggle`/`StageIndexCombo`/`_stageBoxes`（滑条之后才创建）→ 空引用被解析器包装成 `Failed to assign to property RangeBase.Minimum`，页面导航即“XAML 解析失败”（2026-10-22 鼠标档位同步功能首版即踩） | `ValueChanged` 回调里所有控件/字段必须判空（`is not null` 模式）；报错行号可能指向与真实原因无关的属性，看到 RangeBase/赋值失败先查回调空引用 |

## 4. 当前稳定性规则

1. **窗口固定 1280×840，不可缩放/最大化**；尺寸只在 `InitializeComponent` 之前设置一次，之后绝不再调用 `AppWindow.Resize`。
2. **不要使用 Flyout / ContentDialog / StartBringIntoView** 做主交互。
3. 映射编辑使用页面内嵌 `ActionEditorCard`，通过 `PageScroll.ChangeView(null,0,null,true)` 定位。
4. 任何 15s 刷新只更新文本/数值；不要重建 ItemsSource、BitmapImage、控件树。
5. 设备事件回 UI 前必须 `DispatcherQueue.TryEnqueue`。
6. 页面 `Unloaded` 退订事件、停止计时器、恢复 `mappings.suspend false`。
7. 修改窗口/页面结构后，至少做：
   - 8 轮页面切换；
   - 映射编辑打开/保存/切换；
   - 闲置 10 分钟以上（曾有 8–9 分钟后才触发的 resize 原生崩溃）；
   - 最小化一次再恢复；
   - 检查 `%LOCALAPPDATA%\CrashDumps` 与 `%LOCALAPPDATA%\DeviceCenterPreview\dumps` 无新 dump。

## 5. 诊断命令

```powershell
# 最新 dump
Get-ChildItem "$env:LOCALAPPDATA\CrashDumps" -Filter "DeviceCenterPreview*" |
  Sort-Object LastWriteTime -Descending | Select-Object -First 1

# 用 WinDbg 的 cdb 看异常上下文
$cdb = "C:\Program Files\WindowsApps\Microsoft.WinDbg_1.2610.1001.0_x64__8wekyb3d8bbwe\amd64\cdb.exe"
& $cdb -z <dump> -y . -c ".ecxr; kb; q"
```

WER 报告：`C:\ProgramData\Microsoft\Windows\WER\ReportArchive\AppCrash_DeviceCenterPrev_*`。

## 6. 仍存在的风险

- 本机 WinUI 3 运行时对窗口句柄/主题消息较敏感；新增 XAML 控件后必须做稳定性回归。
- **缓存页面二次进入**是高风险场景：新增复杂控件（尤其是 `NumberBox`、带弹层/自动滚动的控件）后，
  必须做「设置→耳机→设置」至少 3 轮切换验证，不能只看第一次进入。
- 设置页备份/导入不使用系统文件选择器，改为固定目录 + 页面内下拉选择；避免 `FileSavePicker`
  在无包身份场景下等待不返回（现象是日志停在 `backup picker opening`、无对话框、无异常）。
- 页面内不要使用 `NumberBox`/`RadioButtons`；缓存页面二次进入会让 XAML 内容树整体消失。
  用静态 `TextBox`/`ComboBox`，ItemsSource 只在构造时设置一次。重复导航压力测试至少 5 轮。
- 固定尺寸是当前取舍；尺寸设置必须在 XAML 树创建前完成，构造后再 resize 会延迟黑屏/崩溃。如果未来要恢复自适应，先验证 WinUI 运行时版本已修复该崩溃。
- 大量设备图片/长列表仍可能放大 XAML 压力；新增前先做压力测试。
