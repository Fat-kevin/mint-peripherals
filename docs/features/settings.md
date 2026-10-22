# 功能：设置、通知与备份

## 已实现

### 设置持久化

- 设置文件：`%LOCALAPPDATA%\DeviceCenterPreview\settings.json`，`schemaVersion = 3`。
- 修改后**立即保存并生效**；保存使用临时文件 + `File.Move(overwrite)`，避免写到一半损坏。
- 文件损坏时保留原文件为 `settings.json.corrupt-yyyyMMdd-HHmmss`，本次回退默认值并在日志中告警。
- 未知字段被忽略；`lowBatteryThreshold` 超出 0–100 时自动夹紧。
- 代码：`Core/Settings/AppSettings.cs`（模型 + `AppSettingsStore`）。

### 外观

- 「外观 → 主题」提供 深色 / 浅色 / 跟随系统，写 `themeMode`（Dark/Light/System）。
- 选择后立即应用：主窗口 `Root.RequestedTheme`、标题栏 caption 颜色、当前页代码画笔一起更新；
  设置页本身和三个设备页都能在切换后直接使用，不需要重启。
- `跟随系统` 使用 `ElementTheme.Default`；显式深/浅色不依赖系统设置。
- Light token：`Styles/Tokens.Color.xaml` 的 `Light` 字典；代码画笔统一走
  `AppTheme.GetBrush`，不直接读按应用主题解析的 `Application.Current.Resources`。

### 模块开关（真正停止/启动）

| 开关 | 关闭后的行为 |
|---|---|
| 耳机 | `RealHeadsetController.Stop()`；停止 SPP 扫描/连接，设备中心不再显示耳机 |
| 鼠标 | `LogitechMouseService.SetEnabled(false)`；停止轮询并释放 HID++ 会话 |
| 遥控器 | `RemoteEngineClient.Stop()` + `system.shutdown`；终止 sidecar 与 key host |
| 常驻电量监控 | `DeviceManager.Dispose()`；停止 VigilWatt 后台发现/采样 |

重新开启会重建控制器/管理器并启动扫描。`Start()`/`SetEnabled()` 均幂等，重复应用设置不会重复扫描。

### 写入授权（服务层闸门）

- 耳机 / 鼠标 / 遥控器各有一个独立写入开关。
- 关闭后不是“只把按钮变灰”：`IPeripheralService` 的写入方法直接返回
  `WriteResult.Fail("写入已在设置中关闭…")`，页面同时收到 `WriteAllowed=false` 禁用控件。
- 页面横幅显示“写入已在设置中关闭；当前只能查看，不能修改设备。”，不使用弹层。
- 耳机查找、鼠标 `ApplyAsync`、遥控器映射/音频/语音工具/全按键都经过同一闸门。

### 通知

- 设备连接、低电量、写入失败三类通知，可独立开关。
- 低电量阈值可设 0（关闭阈值提醒）或 10–80%（步进值见下拉框）。
- 通知统一走 `App.Notify` → 主窗口顶部 `InfoBar`（7 秒自动关闭）；托盘气泡做兜底。
- 首次刷新只建立连接基线，不会为启动前已连接的设备补发“已连接”通知。

### 开机自启

- 设置页「启动」卡片位于设置页最上方；开启/关闭立即写入当前用户注册表：
  `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`，值名 `DeviceCenterPreview`。
- 值为带引号的当前 exe 绝对路径，不需要管理员权限，也不依赖 MSIX 包身份。
- 注册表是实际状态的唯一来源；`settings.json` 不重复保存，避免界面与系统状态不一致。
- 本应用启动时如果发现启动项仍指向旧 exe（移动/重新发布后），会自动改回当前 exe。
- 恢复默认设置会同时关闭开机自启；备份/导入不包含该启动项。
- 写入失败时开关回退到注册表实际状态，并在卡片下方显示失败原因，不伪造成功。

### 备份 / 恢复

- 固定目录：`%LOCALAPPDATA%\DeviceCenterPreview\backups\`；导出文件名 `外设中枢备份-yyyyMMdd-HHmmss.zip`。
- 备份白名单：`manifest.json`、`settings.json`、`remote/settings.json`、
  `remote/button-mappings.json`、`remote/voice-hold-hotkey.json`、
  `remote/other-voice-hotkey.json`。
- 导入先解压到临时目录，校验 `manifest.kind = device-center-preview-backup` 和白名单；
  出现未知文件直接拒绝，不会把任意路径写进配置目录。
- 导入后 `App.UpdateSettings` 应用设置并重启遥控器引擎，使 sidecar 重新读取映射/热键。
- 页面内下拉框选择备份，不调用系统文件选择器（避免无包身份下 `FileSavePicker` 挂起）。

### 诊断包

- 固定目录：`%LOCALAPPDATA%\DeviceCenterPreview\diagnostics\`。
- 内容：`summary.json`（应用/OS/设置/三个设备状态/遥控器状态）、`settings.json`、
  `logs/*.log`、`crash.log`。
- 脱敏：MAC 地址、`C:\Users\<name>` 路径、`\\?\hid#...` 路径；`summary.json` 不写用户绝对路径。
- 诊断包不包含语音内容、按键文本或第三方 App 数据库。

## 代码路径

| 内容 | 文件 |
|---|---|
| 设置模型/存储 | `src/DeviceCenterPreview.Core/Settings/AppSettings.cs` |
| 模块启动/停止、写入闸门、通知 | `src/DeviceCenterPreview.App/Services/RealPeripheralService.cs` |
| 通知模型 | `src/DeviceCenterPreview.App/Services/AppNotification.cs` |
| 备份/恢复/诊断 | `src/DeviceCenterPreview.App/Services/AppBackupService.cs` |
| 页面 | `src/DeviceCenterPreview.App/Views/SettingsPage.xaml(.cs)` |
| 通知展示 | `src/DeviceCenterPreview.App/MainWindow.xaml(.cs)` |
| 开机自启 | `src/DeviceCenterPreview.App/Services/StartupService.cs` |

## 页面结构

1. 启动：开机自启 `ToggleSwitch`，下方显示当前用户启动项状态/失败原因。
2. 模块：耳机 / 鼠标 / 遥控器 / 常驻电量监控 四个 `ToggleSwitch`。
3. 写入授权：三个独立 `ToggleSwitch`。
4. 通知：连接 / 低电量 / 写入失败 + 低电量阈值 `ComboBox`。
5. 配置备份与恢复：导出、下拉选择、导入选中、恢复默认、打开备份/配置目录。
6. 诊断：导出诊断包、打开诊断目录、打开日志目录。
7. 底部显示 `settings.json` 绝对路径。

## 稳定性注意

- **不要**把低电量阈值改回 `NumberBox`：设置页是缓存页面，二次进入时 `NumberBox` 会让 XAML 内容树整体消失
  （UIA 只剩标题栏 5 个元素，无 `crash.log`）。
- 备份/导入的 `ComboBox.ItemsSource` 只在页面加载或导出后重建，不在 15s 刷新里重建。
- 恢复默认是二次点击确认（4 秒窗口），不弹 `ContentDialog`。

## 待办 / 限制

- 备份暂不支持用户自选任意路径；需要跨目录交换时，先导出后从备份目录复制。
- 诊断包会包含完整日志，体积可能到数 MB；暂未做日志截断/轮转上限。
- 设置导入不做跨 schema 迁移；`schemaVersion` 高于当前版本时按默认值兜底。
