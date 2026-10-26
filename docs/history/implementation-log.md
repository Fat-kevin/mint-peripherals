# 实现记录（截至 2026-10-21）

## 目标

把三个原项目聚合进 VigilWatt 风格的原生 WinUI 3 应用：

- `NICEHCK_OriG-in`（耳机）
- `openmouse`（鼠标）
- `remote-mic-app-windows`（遥控器）

要求：全功能对等、真实设备、原生 UI、常驻、任务栏电量、个人自用。

## 时间线（摘要）

1. **调研与方案**
   - 盘点三个项目的协议/素材/UI；
   - 输出设计/实现文档；
   - 确认 WinUI 3 + .NET 8，x64，原生重写。
2. **最小实现**
   - 设备发现/电量聚合、二级页、任务栏贴片、托盘；
   - 真实 DeviceManager + PnP；移除伪造数据。
3. **耳机**
   - 接入 SPP；修复聚合去重丢 Components/Firmware；
   - ANC/EQ/开关/查找；分量电量/固件。
4. **鼠标**
   - HID++ 读名称/电量/DPI/轮询率；
   - 修复 `FeatureFunc` 功能号问题；
   - G304 5 档 DPI、档位、配置名、板载模式、6 键映射、固件；
   - CRC 回读校验；20 个罗技型号图片。
5. **遥控器**
   - Rust sidecar：TCP/token/JSONL、1 Hz snapshot、按键边沿事件；
   - 补 key host，修复“收到手势但不注入”；
   - 13 键映射总表、页面内编辑、自动保存；
   - 接收测试（暂停注入、只观察）；
   - 语音工具提示；映射保存补齐三手势槽位。
6. **UI/视觉**
   - 新名称「外设中枢」、新 hub 图标；
   - 卡片等宽/对齐、状态胶囊、电量条、标题强调条；
   - ANC/EQ 改原生 RadioButtons；
   - 查找耳机 5 秒可取消倒计时；
   - 任务栏胶囊 35px 折中尺寸、全屏隐藏；
   - 固定窗口 1280×840。
7. **稳定性**
   - 定位 Mica、Hide/Show、AppWindow.Resize、StartBringIntoView、Flyout、图片/ItemsSource 重建等崩溃点；
   - 固定窗口、无弹层、静态控件 + 文本更新；
   - 多轮页面切换/映射编辑压力测试通过。
8. **设置与运维（2026-10-20）**
   - `settings.json` v2 持久化；模块开关真正启停；写入授权下沉到服务层；
   - 连接/低电量/写入失败通知 + 低电量阈值；
   - 耳机未知充电显示“未知”，断开/未知控制清空选择；
   - 鼠标 G304/G305 + profile 校验通过才可写；
   - 遥控器手动扫描/连接/断开可用：断开后扫描仍可用，选中受支持设备后可重新连接；
     修复连接状态变化重建 `DeviceCombo.ItemsSource` 导致的内容树消失；
   - 固定目录备份/恢复（manifest + 白名单）与脱敏诊断包；
   - 修复设置页二次进入 + NumberBox 导致的 XAML 内容树消失，改用 ComboBox；
   - 鼠标页 5 档 DPI/6 按键行改为 XAML 静态控件，修复约 4 轮导航后 XAML 内容树消失；
   - 耳机页 ANC/EQ 由缓存页 `RadioButtons` 改为 `ComboBox`，修复多页面轮转后的内容树消失；
   - Hub 鼠标图片改为静态缓存，避免每次进入页面重复解码。
9. **启动黑屏/延迟崩溃（2026-10-20 收尾）**
   - 现象：启动显示黑底，约 8–9 分钟后自行退出；WER 记录 `Microsoft.UI.Xaml.dll` / `combase 0x80070578`。
   - 根因：`AppWindow.Resize` 放在 `InitializeComponent` 之后，XAML 树已创建后再改窗口尺寸，
     原生窗口句柄在后续主题/窗口消息中失效。
   - 修复：尺寸设置前置到 `InitializeComponent` 之前，之后不再 resize；增加初始导航失败时的
     `Root.Loaded` 补导航，避免构造阶段导航失败留下黑底。

## 当前验证状态（2026-10-20）

- 构建 0 警告 0 错误；10/10 单元测试通过（新增 `AppSettingsStore` 5 项）。
- 独立项目已迁到 `E:\Project\Bluetooth\device-center-preview`；自身 slnx/.gitignore/docs，
  C#/Rust 依赖已收进 `external/`，不再引用 `vigil-watt`、`NICEHCK_OriG-in`、`remote-mic-app-windows`。
- 耳机真机：90%，固件 4.8，SPP 可控；充电盒未上报充电状态时显示“未知”。
- 鼠标真机：G304 20%，DPI 1350，1000 Hz，固件 BOT 69.02 / RQM 68.02；写入按钮可用并回读成功。
- 遥控器真机：MI RC 89%，引擎 ready，gate=true；手动扫描返回 1 个受支持设备；断开→扫描→连接已实测恢复；全按键开关当时禁用并标注 Helper 未安装（2026-10-21 已改为复用已连接 Helper，见文末）。
- 设置：开关修改后立即落盘；遥控器模块关闭后 sidecar/key host 进程退出，重新开启恢复 2 个进程。
- 备份：导出 zip 含 manifest + 5 个白名单文件；导入校验通过并重启遥控器引擎。
- 诊断：导出 zip 含 summary/settings/logs/crash；日志中 MAC、用户名路径、HID 路径已脱敏，summary 不含绝对用户路径。
- 稳定性：设置→耳机→设置 3 轮、鼠标→设置→设备中心 5 轮、全页面 5 轮轮转通过；已定位并替换
  缓存页中的 `NumberBox`/`RadioButtons`，若再出现“只剩标题栏”按 `docs/stability/crash-history.md` 排查。
- 启动/闲置：修复 `AppWindow.Resize` 时序后，1280×840 连续运行 10+ 分钟、最小化/恢复、
  二次启动激活均正常，UIA 内容树保持 50+ 元素，无新 WER/dump。

## 未完成 / 下一步

- 遥控器：映射导入导出、首次向导、Helper 安装/健康检查、断开后禁止自动重连、语音链路真机验证。
- 鼠标：全品牌 OpenMouse 驱动（建议 Node sidecar 路线）、配置导入导出、固件更新。
- 耳机：分量历史、托盘 ANC 快捷菜单、开机启动、逐项写入回归。
- 应用：备份任选路径（需先解决 FileSavePicker 挂起）、日志截断/轮转、正式发布流程（当前仅本机自用）。

## 关闭到托盘 / 安装包（2026-10-20）

- 需求：安装后点关闭按钮不应退出，主窗口隐藏，托盘图标和任务栏胶囊继续常驻。
- 修复：`MainWindow.AppWindow.Closing` 在非退出路径取消关闭并调用 `App.HideMainWindow()`；
  `App.ExitApplication()` 设置 `IsExiting=true` 后仍可真正退出。
- 恢复：托盘单击/双击和二次启动都走 `App.ShowWindow()`，先 `AppWindow.Show()` 再 restore/activate，
  隐藏窗口才能重新显示。
- 首次关闭弹一次托盘气泡，提示仍在后台运行；仅首次，不重复骚扰。
- 安装包：1.0.1 自包含（.NET 8 + Windows App SDK + sidecar），Inno Setup 按用户安装到
  `%LOCALAPPDATA%\Programs\DeviceCenterPreview`，卸载清理 `Run` 值；产物
  `dist\DeviceCenterPreview-Setup-1.0.1.exe`。
- 实测：安装后主窗口关闭 → 进程仍在、主窗口隐藏、任务栏胶囊可见；二次启动只保留一个实例并
  重新显示窗口；卸载 exit 0。

## 语音输入回归修复（2026-10-21）

- 现象：从豆包切到微信输入法后，遥控器语音键不触发输入法；切回豆包仍无效。
- 根因一：集成层 `voice.hotkey.set` 发的是顶层 `keys`，sidecar 读取 `params.chord`；参数缺失被当成
  `chord=null`，所以每次切工具/页面加载都把 `voice-hold-hotkey.json` 清成 `null`，遥控器只切音频、
  不注入按键。
- 根因二：未配置时 `voice.hotkey.get` 返回 `result:null`；`GetVoiceHotkeyAsync` 对 Null 调用
  `TryGetProperty` 抛 `InvalidOperationException`，在 `Tool_SelectionChanged` 的 async void 里变成
  未处理 UI 异常，进一步打断快捷键刷新。
- 修复：`voice.hotkey.set` 改发 `{ chord: { keys: [...] } }`；读取端识别 Null/非对象；
  引擎连接后若工具已选但快捷键为空，只补对应快捷键（不改输入法）；页面加载不再把程序化选中
  误当成用户切换；用户切换工具仍写工具 + 固定组合。
- 现状：当前 sidecar 的 `voice_input_tool=wechat`，`voice-hold-hotkey.json` 已补为
  `left_control + left_windows`；旧配置 doubao 的右 Alt 也验证可自动补。
- 版本：1.0.2，产物 `dist\DeviceCenterPreview-Setup-1.0.2.exe`。

## 设备中心卡片进入后左侧导航不同步（2026-10-21）

- 现象：在“设备中心”点卡片右下的“进入控制 →”后，内容区进入耳机/鼠标/遥控器页，但左侧
  `NavigationView` 仍停在“设备中心”。
- 根因：`DeviceCenterPage.Navigate()` 直接调用自身的 `Frame.Navigate()`；左侧选中项只在
  `Nav_SelectionChanged` 中由用户点击驱动，没有反向同步“内容帧已导航”这一事实。
- 修复：`MainWindow` 订阅 `ContentFrame.Navigated`，按页面类型映射到 `hub/headset/mouse/remote/settings`
  并同步 `Nav.SelectedItem`；用 `_syncingNav` 防止同步选中项时再次触发 `Navigate`。
- 验证：Release 构建启动后，从设备中心点击耳机卡片 → 左侧“耳机”选中；再从左侧点“鼠标” →
  页面切到鼠标且“鼠标”选中，未出现重复导航。

## 音量键映射不执行 / 全按键 Helper（2026-10-21）

- 现象：音量+/- 已保存映射，但按遥控器音量键无反应；页面提示全按键需要 Helper。用户反馈前一天可用。
- 根因：RC003 的音量键与返回键在 Windows `kbdhid` HID→VK 映射阶段被丢弃，普通 Raw Input 和
  键盘钩子都收不到；必须由管理员 Helper 在报告层接管。本机旧版 `sayall-helper.exe` 计划任务仍在
  运行，且能连上 sidecar 的 `rc003-bridge`，但集成 sidecar 在启动时强制
  `set_enhanced_capture_enabled(false)`，`capture.enable=true` 也被写死拒绝，导致 Helper 没有
  捕获目标，音量键边沿从未进入映射引擎。
- 修复：sidecar 启动时按 `rc003_capture_enabled` 恢复增强捕获目标；`capture.status` 改为读取
  `rc003_bridge` 的真实 `phase/helper_pid/last_rx_age_ms`；`capture.enable` 仅在 Helper 已连接时
  允许开启，关闭始终允许。
- 追加修复（同日）：计划任务没有登录/开机触发器，sidecar 若只等桥接，重启后 Helper 不会出现。
  现在启动时以及运行中每 30 秒调用 `schtasks /run /tn "SayAll RC003 Helper"`（已连接则跳过）；
  `schtasks` 的 stdout/stderr 必须置空，否则 “SUCCESS: ...” 会污染 sidecar 的 TCP 握手 stdout，
  宿主解析失败后会每 5 秒重启一个新的 sidecar。
- 验证：`schtasks /end` 结束 Helper 后重启应用，sidecar 自动拉起 Helper（新 PID），桥接
  `APP-BRIDGE connected`、`TARGETS-ACK gen=3 usages=7`，应用回到 `MI RC / ready`，只存在一份 sidecar。
- 打包修正：Helper 不再依赖旧版安装。`vendor/rc003-helper/x64/` 存放 x64 Helper 与
  Frida Gadget（含 SHA-256/许可证说明），`DeviceCenterPreview.App.csproj` 在 Build/Publish
  时复制到 `helper/`；安装器把它装到 `%LOCALAPPDATA%\DeviceCenterPreview\Helper\`，在复制前
  `schtasks /end` 停旧任务，复制后以一次 UAC 执行 `--install-task`，卸载时 `--remove-task` +
  `--uninstall-cleanup`。全新安装默认 `rc003_capture_enabled=false`，所以 sidecar 启动 Helper
  不再以该设置为条件，否则页面一开始就不可用。
- 仍需验证：干净环境安装后检查 `TARGETS-ACK` 含 `0x0080/0x0081/0x00F1`，以及重启/Helper 崩溃
  后 30 秒内恢复；本机无法替代这一步。
- 验证：启动后 Helper 日志 `TARGETS-ACK gen=3 usages=7`，目标含 `0x0080/0x0081`（音量±）与
  `0x00F1`（返回）；UIA 显示“全按键支持（Helper 已就绪）”且 ToggleState=On。
- 旧限制已处理：此前 Helper 未随安装包打包，只复用旧版计划任务；现在载荷随安装包分发并由安装器注册任务，换机/卸载旧版不再影响。

## UI/UX 与图标 0–5 阶段落地（2026-10-21）

### 方向与提案

- 完成 `docs/ui/redesign-proposal-2026-10-21.md`：调研结论、现状审计、Graphite Signal Desk
  方向、token、五个页面线框/结构、组件抽象、资源组织、关键 XAML、图标规范、分阶段计划。
- 视觉方向：石墨色表面、3px 设备信号轨、象牙白主 CTA、Ion Blue 焦点/选中、
  仪表式大数字；不做大面积玻璃/渐变/重阴影。

### 资源与 shell

- 新增并接入 `Styles/Tokens.Color.xaml`、`Tokens.Type.xaml`、`Tokens.Layout.xaml`、
  `Controls.xaml`、`Cards.xaml`、`Navigation.xaml`、`IconGlyphs.xaml`；
  `App.xaml` 按顺序合并，并统一 `ContentControlThemeFontFamily` / `TextControlThemeFontFamily` 为 Segoe UI。
- Dark/Light 两套主题 token；覆盖 Fluent 的按钮/输入/下拉/开关/Accent 资源，
  修正系统紫色 Accent 泄漏到 ToggleSwitch 与导航选中态的问题。
- `MainWindow.xaml` 重做 48px 标题栏（16px 图标 + 分隔线 + 副标题）、208px 导航、
  pane footer（本地引擎/15 秒刷新/版本）、InfoBar overlay。
- 设备中心、耳机、鼠标、遥控器、设置五个页面全部按新 token 重写；保留原 `x:Name`、
  事件和 `NavigationCacheMode="Required"`。
- 清除页面里的响应式死代码（`_narrowLayout`、`OnPageSizeChanged`、`ApplyResponsiveLayout`），
  页面宽度由 `PageScroll.ViewportWidth` 在 `Loaded` 时设置一次，窗口仍固定 1280×840。

### 页面要点

- 设备中心：三张等宽 `Button` 卡（整卡可点、信号轨、图标盘、状态、34px 电量、4px 电量条、
  进入控制），底部「从这里开始」+「读数规则」。
- 耳机：300px 产品图 Hero + 三个分量电量 pod + 查找；2*/1* 降噪/快捷开关 + 连接/固件。
- 鼠标：紧凑 Hero + DPI/轮询率/板载读数；性能卡（DPI 滑块、5 个横向档位块、板载开关）；
  全宽映射表，保留静态 8 行与容器内滚动。
- 遥控器：连接条 + 三列映射（220 / * / 300）+ 语音/增益卡；音频下拉改用
  `DisplayMemberPath="Name"`，扫描结果和未接入控件补 placeholder，避免空白框。
- 设置：启动/模块 + 写入授权/通知两列，备份/诊断通栏；所有设置行 12px 以上；
  恢复默认 3 秒二次确认仍在页面内完成。

### 构建/运行修复

- XAML 编译器 `MSB3073`（无诊断）根因：`MainWindow.xaml` 使用了 UWP/WinUI 不存在的
  `Panel.ZIndex`。改为 `Canvas.ZIndex` 后 XAML codegen 恢复。
- `Cards.xaml` 中 `AppKpiCardStyle` 向前引用 `AppInnerPanelStyle`，已调整定义顺序，
  避免资源解析在编译期失败。
- 页面加载统一 `PageScroll.ChangeView(null,0,null,true)` 并再入队一次，
  修复缓存页二次进入被焦点带回页面中部的问题。
- 耳机 ANC/EQ 下拉增加「未接入」placeholder，未连接时不再显示空白输入框。
- 设备中心三张整卡按钮补 `AutomationProperties.Name/HelpText`，避免屏幕阅读器只读到空按钮。
- 补齐 Light 主题缺失的 Fluent 按钮/输入/开关/Accent 覆盖；Dark/Light 各 97 个 token，
  静态键集合已比对一致。

### 图标与托盘

- 采用 **Backplane / 中枢**：深青 `#0F6673` + 象牙白「中」；16/20/24px 手绘、32–256px 圆角底板。
- 通过 `design/icon-concepts/backplane-zhong/build-ico.py` 生成 16/20/24/32/40/48/64/128/256
  九帧 `PeripheralHub.ico`；替换 `PeripheralHub.png`、`-16.png`、`-24.png`、`-32.png`。
- 标题栏改用 16px PNG；托盘 `TrayService` 从固定 16×16 改为按 `GetDpiForSystem()` +
  `GetSystemMetricsForDpi(SM_CXSMICON)` 取尺寸；托盘窗口类名改为 `DeviceCenterTray`。
- 从 csproj 移除未使用的 `VigilWatt.ico/.png` 复制项，避免发布包继续带旧品牌资源。

### 视觉复查修复（2026-10-21）

用户对首版截图标注后做了一轮布局复查，修复的不只是两个红框：

- **设备中心卡片不等宽**：`AppDeviceCardButtonStyle` 未显式拉伸，Button 按内容宽度居中，
  三列出现 18/55/23px 的参差间距。样式补 `HorizontalAlignment/VerticalAlignment=Stretch`，
  三张卡现在同宽同高，红框标注的空隙消失。
- **鼠标按键映射重叠**：映射行固定 34px，但 ComboBox 最小高度 36px，行内上下溢出；
  改为 44px 行高，映射标题、标签、下拉不再互相压住。
- **鼠标底部主按钮像“浮在页面外”**：原 `应用设置` 是裸 Grid 左对齐，滚动到底部时孤立且被裁。
  改为 `AppInnerPanelStyle` 操作条：左侧状态、右侧「刷新鼠标 / 应用设置」，整行完整可见。
- **表格下拉宽度参差**：`AppComboBoxStyle` 补横向拉伸；按键映射、设置、音频路由的 ComboBox
  在 `*` 列中占满列宽，不再缩成文本宽度。
- **InfoBar 盖住页面标题**：原 InfoBar 是 `Canvas.ZIndex=10` 的 overlay，通知出现时覆盖
  「遥控器 · MI RC」标题。改为内容区 `Auto` 行，打开时下推页面、关闭不占高度；已验证
  关闭到托盘再唤醒时通知条位于标题上方，不遮挡内容。
- **长状态文本被裁掉**：耳机 `SaveStatus`、鼠标 `ProfileInfoText`、遥控器
  `MappingSaveStatus`/`SaveStatus` 原来放在横向 `StackPanel` 中，`TextWrapping` 实际不生效。
  四处都改为 `Grid` 的 `*` 列，长文本换行而不是被右侧裁切。
- 复查后扫描：源 XAML 无 `NumberBox`/`RadioButtons`/`Flyout`/`ContentDialog`/`StartBringIntoView`/
  `Canvas.ZIndex`/硬编码 `Segoe Fluent Icons`；无 10/11px 文字；Dark/Light token 仍为 97/97。

### 验证

- x64 Debug 最终构建 0 警告 0 错误；`DeviceCenterPreview.Core.Tests` 10/10 通过。
- 单次启动后通过 UIA + `PrintWindow` 检查五个页面：标题栏/导航/设备卡/三列映射/设置双列均正常；
  ToggleSwitch 填充采样为 `#8FA6FF`，导航选中条为 Ion，不再是系统紫色。
- 页面轮转 3 轮（设备中心→耳机→鼠标→遥控器→设置）UIA 内容树保持 87–96 个元素，进程存活。
- 截图复核：设备中心三卡等宽；鼠标映射行无重叠、底部操作条完整；InfoBar 打开时下推页面标题，不遮挡内容。
- 设备中心整卡按钮补 `AutomationProperties.Name/HelpText` 后，UIA 可读到
  「打开耳机控制/打开鼠标控制/打开遥控器控制」；点击耳机卡后左侧导航同步到「耳机」。
- 最小化→恢复通过；二次启动只保留 1 个实例，现有窗口可被激活。
- 遥控器映射：打开「确定 · Enter」单元格编辑器、关闭，进程保持存活，未写入设备映射。
- 连续闲置 9.7 分钟（接近既定 10 分钟观察窗口）：进程响应，`CrashDumps`/应用 dumps 无新增，
  Application 日志无新的 `DeviceCenterPreview`/`.NET Runtime` 错误。
- 最终构建启动后再次完成 3 轮页面轮转，进程保持存活；新 dump/事件均为 0。
- 截图保存在 `design/verification/final-*.png`（仅验证用，不随应用发布）。
- 真机映射保存/接收测试仍按发布前清单单独跑；本次未改服务层与映射语义。

### 发布包（2026-10-21）

- 关闭运行实例后，`publish/app` 清空并重新发布 Release 自包含目录：
  `dotnet publish ... -c Release -p:Platform=x64 -r win-x64 --self-contained true -o publish/app`；
  810 个文件、约 295MB，含 .NET 8 / Windows App SDK / `sidecars/sayall-engine.exe` /
  `helper/sayall-helper.exe` 与 `frida-gadget.dll`。
- 从 `publish/app` 启动发布版做过一次真实启动和截图（`design/verification/publish-check.png`），
  资源、图标、主题均正常；没有只验证 Debug。
- Inno Setup 6 编译：
  `ISCC.exe installer\DeviceCenterPreview.iss` →
  `dist/DeviceCenterPreview-Setup-1.0.2.exe`，83.1MB，未签名（本地构建正常现象）。
- SHA-256：`54E640F8121A863960724AA89D814C49ECD423AE4E5544C2FD655CC0C63C96B3`，
  已同步到 `dist/DeviceCenterPreview-Setup-1.0.2.exe.sha256`。
- 未在本机执行安装器，避免改动当前安装、UAC 和 `SayAll RC003 Helper` 计划任务；
  安装行为说明见 `installer/README.md`。

## 浅色模式、状态图标与三项功能审计（2026-10-21）

### 浅色模式

- 设置页新增「外观 → 主题」：深色/浅色/跟随系统，写 `settings.json.themeMode`，
  `schemaVersion` 升到 3；旧文件缺字段时默认深色，未知值收敛为深色。
- 主窗口去掉 `Root` 上写死的 `RequestedTheme="Dark"`，改为构造时按设置解析，
  运行中由 `MainWindow.ApplyTheme` + `Root.ActualThemeChanged` 切换。
- 代码构造的画笔统一走 `AppTheme.GetBrush`；XAML 的 `{ThemeResource}` 自动更新。
  缓存页实现 `IThemeAware`，在切肤后重算设备中心电量条和遥控器映射表。
- `StatusChip` 订阅 `AppTheme.ThemeChanged`，切肤后重新解析胶囊背景/边框/文字/图标，
  不依赖控件自身的 `ActualTheme`（缓存页里会短暂滞后）。
- 标题栏 caption 按钮颜色由 `AppTheme.ApplyTitleBar` 重设；Light 下不再残留白字。
- Light 的成功/警告/离线和三个设备色整体加深（如成功 `#0B7654`、离线 `#5F6876`），
  提高 12px 状态文字在白底上的对比度。
- 修正：第一次切浅色时 `StatusChip` 仍读到 Dark 的成功色（薄荷绿在白底上发虚）；
  根因是控件 `ActualTheme` 与窗口实际主题不同步，改为以主窗口/settings 的显式模式为准，
  并加 `ThemeChanged` 事件推送刷新。

### 状态图标

- 新增 `Controls/StatusChip.xaml(.cs)`：999px 胶囊、1px 边框、12px 自绘矢量状态符号。
  变体：Live（时钟）、Connected/Saved（勾）、Offline（断线）、Warning、Error。
- 设备中心三张卡、页头「自动刷新 15 秒」、遥控器「按键映射自动保存」、
  设置「更改即时保存」、页脚本地引擎均改用矢量符号；不再用 6px 色点。
- 页脚本地引擎改为勾形 `Path`；耳机页三个分量卡的圆点改为 2×12 设备色竖条。
- 导航与通用操作仍用 Segoe Fluent Icons；应用图标仍为 Backplane「中」，
  本轮没有为了图标而更换品牌标识。

### 遥控器回归修复

- 语音工具枚举恢复为 `doubao/wechat/vokie/other`（豆包/微信输入法/Vokie/其他）；
  旧版只有 `push_to_talk/voice_activation`，与 sidecar 枚举不一致，会让工具切换写坏配置。
- 增益滑块上限 30 → 24（引擎上限），并加说明：页面不回读旧值，移动后再应用。
- `SaveMappingAsync` 在 `mappings.save` 后再次 `mappings.config.get` 回读比对，
  sidecar 吞掉写盘错误时不再显示“已保存”。
- `mappings.test` 的 skipped 文案改为“该手势不能在此处测试（未配置，或动作需由
  真实按键/引擎执行）”，不再把所有跳过误报成“未配置”。
- 断开按钮只依赖引擎连接状态，不再受误导性的 `VoiceReady` 影响。
- 备份白名单补 `remote/other-voice-hotkey.json`。

### 功能审计

- 新增 `docs/features/integration-parity.md`：逐项对照三个原项目的协议/引擎能力、
  已对齐项、明确排除项、已知限制和真机验证清单。
- 结论：协议/引擎能力已全部接入；未接入的是 OTA、触控载荷、codec、V2 快捷开关帧、
  鼠标全品牌驱动/导入导出、遥控器导入导出/首次向导/原生文件选择器等高风险或低价值项。
- 没有复制 OpenMouse AGPL 代码，没有引入 WebView/Tauri，没有改服务层协议。

### 验证与打包

- 最终 Debug 构建 0 警告 0 错误；`DeviceCenterPreview.Core.Tests` 11/11 通过。
- 浅色下逐页检查：标题栏、导航、设置双列、设备中心三卡、耳机 Hero/电量卡、
  鼠标性能/设备信息、遥控器连接条/映射表/语音卡，均已截图复核；Light token 的
  `AppSuccessBrush` 实际采样为 `#0B7654`，不是 Dark 的 `#57D6A5`。
- 主题切换用 UIA 实测：浅色 → 深色 → 浅色，`settings.json` 分别写入
  Light/Dark，三个设备页不重启即可换肤；标题栏 caption 同步变深/变浅。
- 最终 Release 从 `publish/app` 启动：三张设备卡、遥控器页、设置页均正常；
  3 轮页面轮转（设备中心→耳机→鼠标→遥控器→设置）UIA 元素数稳定在 96，进程存活。
- 最小化→恢复通过；二次启动只保留 1 个实例，现有窗口被激活。
- 连续闲置观察（从最后一次交互起算）已完成 10 分钟窗口；进程响应，
  `CrashDumps`/应用 dumps 无新增，Application 日志无新的 `DeviceCenterPreview`/`.NET Runtime` 错误。
- Release 发布目录 810 个文件、约 295.1MB，含 .NET 8 / Windows App SDK / sidecar / Helper；
  exe 版本 1.0.3.0。
- Inno Setup 6 编译：`dist/DeviceCenterPreview-Setup-1.0.3.exe`，83.1MB，未签名；
  SHA-256 `38FEB636B200DF4466DE9191C76F84200B9722171697F2E64D6FD4268A0A952D`，
  已同步到 `.sha256` 和 `installer/README.md`。
- 未在本机执行安装器，避免改动当前安装、UAC 和 `SayAll RC003 Helper` 计划任务；
  安装行为说明见 `installer/README.md`。
- 仍需真机：耳机写入逐项、鼠标 DPI/映射写入、遥控器四工具语音与映射保存回读。

## 遥控器麦克风启动入口测试（2026-10-21）

- 目标：区分“只有语音键能启动麦克风”“其他按键也能启动”“主机 ATVV 协议可绕过按键启动”。
- 新增硬件诊断工具 `tools/RemoteMicActivationProbe/`：
  - 直接连接 ATVV `AB5E0001-...`，订阅 control/audio；
  - 阶段：静默基线 → 语音键阳性对照 → 逐个普通按键 → 主机主动 `MIC_OPEN`；
  - 记录 control `0x08/0x04/0x00` 十六进制和音频包数/字节数，不落盘音频内容；
  - 输出 `.artifacts/remote-mic-activation-<时间>.json` 和 `voice_key_only` 等判读；
  - 运行前强制检查 `DeviceCenterPreview.App` / `sayall-engine` 已退出，避免 GATT 争用；
  - 退出前尝试 `MIC_CLOSE`、退订并释放 GATT；不写映射、不改设备配置。
- 新增 `external/rust/crates/sayall-core/tests/voice_activation.rs`：
  - `only_opcode_0x08_requests_microphone_open`
  - `stream_started_without_open_request_is_accepted`
  - `microphone_open_request_alone_does_not_start_audio`
  - 三测已运行通过（3/3）。这证明主机当前**不强制**语音键先行：
    没有 `0x08` 的 `0x04` 也会进入 Streaming，音频会被接收。
- 工具构建通过（0 警告 0 错误）；`--help` 和“有 app/sidecar 时拒绝运行”已实测。
- 判读以音频包为准：单独 `0x04` 只算“流开始”；完整跑测时若只有语音键有音频，
  会输出 `voice_key_transmits_audio_only_but_stream_can_start_without_audio`，
  避免主机 `MIC_OPEN` 的 0x04 把“只有语音键”误判成“协议可传输”。
- 2026-10-21 主机侧基线（未按键，`--host-open-only`）：
  - `MIC_OPEN` 写入 `Success`；遥控器回 `04 00 02 00`（0x04，session 0）；
  - 10 秒内 audio 通知 0 包；`MIC_EXTEND 0E 00` 连续写入均 Success；
  - `MIC_CLOSE 0D 00` 写入后回 `00 00`；
  - 判读 `host_protocol_starts_stream_without_audio`：协议能请求流开始，
    但这次没有证明麦克风实际传输。报告 `.artifacts/remote-mic-activation-host-only-2.json`；
    因为没有音频阳性对照，工具按设计返回退出码 2（证据不完整），不是失败。
- 真实遥控器按键测试仍未执行：需要退出当前预览、由人按住语音键并逐个按普通按键。
  硬件的最终结论以 `.artifacts` 报告为准；在拿到报告前，不把“只有语音键”当作已验证事实。
- 文档：[`docs/features/remote.md`](../features/remote.md) 和
  [`tools/RemoteMicActivationProbe/README.md`](../../tools/RemoteMicActivationProbe/README.md)。

## 2026-10-22 第二轮视觉重构：仪器面板（Instrument Console）

用户反馈第一轮只改了配色、"看起来跟原来一模一样"。本轮做结构性重设计：

- 新增 `Controls/BatteryRing`：原生 Path/Ellipse 弧线电量仪表盘（12 点钟起点、
  顺时针、圆头端帽、中心大号百分数、充电闪电角标）。刷新只改 Path.Data 与文本，
  符合 15s 刷新稳定性规则。
- `Styles/Cards.xaml` 新增 `AppConsolePanelStyle`（控制台面板）、
  `AppChannelButtonStyle`（通道按钮）、`AppGaugeNumberStyle`（大号读数）。
- 设备中心：三台设备从三张独立浮动卡片改为**一个面板上的三条通道**，
  通道间 1px 竖向刻度线；每条通道一枚 104px BatteryRing + 名称 + 状态。
  移除设备图片小方块与水平电量条。
- 耳机页：hero 重构为面板头（112px 图片板 + 标题 + 查找按钮）+
  三枚 76px 分量仪表（左耳/右耳/充电盒）；移除 300px 左图右文分栏。
- 鼠标页：hero 重构为面板头 + 右侧 84px 电量环 + 大号读数条
  （DPI / 轮询率 Hz / 板载配置，26px SemiBold，竖线分隔）。
- 遥控器页：页头与工具栏合并为一个面板（图片板 + 标题 + 设备选择 +
  电量环 + 连接/断开），扫描行独立在分隔线下方。
- 所有仪表颜色逻辑：≤15% Danger、≤35% Warning、否则设备色。
- 验证：构建 0 警告 0 错误；11/11 测试通过；单次启动后 UIA 遍历五个页面
  全部正常（hub 49 / 耳机 61 / 鼠标 86 / 遥控器 161 / 设置 92 元素），
  仪表显示真实读数（耳机 90 / 鼠标 90 / 遥控器 83），无新增崩溃。

## 2026-10-22 第三轮视觉重构：薄荷浅色 + 去卡片化（Mint Studio）

用户否定第二轮的"铜台"暖色（"很 AI、很腻、不小清新"）并质疑
"为啥非得要这么明确的卡片"。本轮两个根本性改变：

- **去卡片化**：`AppCardStyle`/`AppHeroCardStyle`/`AppConsolePanelStyle`
  改为透明无边框；区块直接放在画布上，靠留白（页面 Padding 36,28、
  区块间距 28）与 1px 细分隔线组织。只有输入组和映射表保留白色
  内层面板（无边框微浮起）。状态提示条改为柔和 tint 底色无边框。
- **小清新配色（Mint Studio）**：Light 为默认主题（settings 默认值与
  Normalize 回退均改为 Light，测试同步更新）。雾白画布 #F5F8F6、
  薄荷强调 #0E9A76、主按钮软填充 #D8F1E6 + 深绿字 #0A6B4F、
  设备色：耳机柔紫 #7A6CC9 / 鼠标蜜糖 #C28A33 / 遥控天蓝 #2E8FBF。
  Dark 为同族绿调炭色 #101613 + 薄荷 #3CCFA5。
- 导航选中态改为薄荷 tint 底 + 深绿字，指示条薄荷色。
- 验证：构建 0 警告 0 错误；11/11 测试通过；单次启动 UIA 遍历五页
  全部正常；应用存活无新增崩溃。

## 2026-10-22 第四轮：对齐修复 + 找回设备实物图（v4）

用户指出：三列比例不一、垂直错位、白底方块仍像卡片、耳机分量实物图丢失。
方案见 `docs/ui/redesign-plan-v4-alignment.md`（网络调研在本环境被 DNS 拦截，
改以 WinUI 3 官方几何规范为基准）。落地：

- **共享固定行网格**：设备中心三通道行高全部固定像素（图片 100/环 96/名称 22/
  状态 26/说明 34），UIA 实测三个电量数字 Y=451 同线、三个状态胶囊 Y=563 同线。
- **实物图回归**：设备中心三列直接浮空展示 yuandao-case/g304(动态)/rc003；
  耳机页分量条恢复 yuandao-left、yuandao-right、yuandao-case 实物图 + 64px 电量环
  （共享行网格 80/12/64/8/20/4/18）。
- **白底方块清零**：AppInnerPanelStyle 转透明——输入控件自带白底外观，
  只有映射表保留白底数据面。设备图片全部去底板浮空。
- **几何对齐 Win11 标准**：按钮圆角 10→8；页标题 30→28。
- 验证：构建 0 警告 0 错误；11/11 测试；单次启动 UIA 五页正常
  （52/63/86/161/96 元素，hub 4 张图）；应用存活。

## 2026-10-22 第五轮：联网调研 + Win11 轻容器与控件规范（v5）

联网搜索恢复（全文抓取仍被 DNS 拦截，7 个域名均失败；以搜索定位来源，
内容据文档知识）。来源：Geometry/Layering/Typography in Windows 11、
Fluent 2、Galaxy Buds One UI 8、Logi Options+。
方案见 `docs/ui/redesign-plan-v5-surface.md`。

- **控件圆角 8 → 4**：几何文档规定按钮/输入 4px、卡片/浮层才是 8px；
  v4 用 8 是错的。
- **AppCardStyle 升级为 Win11 轻容器**（Layering 配方）：白底 + 1px
  极浅边 + 8px 圆角 + 无阴影。状态区（hero）保持完全开放无容器——
  "少量大容器 + 开放 hero"，不是"均质小盒堆"。设置页两列、耳机控制区、
  鼠标性能/映射区、遥控映射/语音区自动获得该容器。
- 设备中心图片行 100 → 120：实物图成为绝对主角（伴侣 App 模式）。
- 验证：构建 0 警告 0 错误；11/11 测试；单次启动 UIA 五页正常
  （51/62/90/165/96 元素）；对齐复测三列电量符号 Y=451 同线、
  状态胶囊 Y=563 同线；应用存活（期间窗口被最小化过，恢复后正常）。

## 2026-10-22 第六轮：应用更名「薄荷外设」+ 薄荷叶新图标（v6）

用户否定了旧图标与名称「外设中枢」。

- **更名**：全部面向用户字符串改为「薄荷外设」（窗口/标题栏/托盘提示/
  托盘菜单/通知/备份与诊断文件名/设置页文案），11 处；程序集名与文件名
  不动（避免动安装器/并排引用）。窗口标题经进程 MainWindowTitle 验证。
- **新图标**：薄荷叶——白色单叶+茎浮在 #12A886 圆角方块，呼应 Mint Studio。
  生成脚本 `design/icon-concepts/mint-leaf/build-mint.py`：每尺寸 8x 超采样
  渲染后 LANCZOS 收缩；16/20/24 省叶脉；ICO 沿用 32-bit DIB（256 为 PNG）。
  首版叶尖朝向反了（屏幕坐标系 y 向下，旋转角需取负），像素分析发现后修正。
  文件名保持 PeripheralHub.* 不变。
- **验证方法升级**：CopyFromScreen 会被用户自己的窗口遮挡（一度误判为
  灰屏死机），改用 `PrintWindow(PW_RENDERFULLCONTENT)` 直接渲染窗口内容。
  实测深色主题令牌全部精确命中：画布 #101613、标题栏 #090D0B、
  标题栏图标 201 个薄荷像素。注：用户已自行把主题切到 Dark。
- 回归：UIA 树 52 元素、五页导航正常、二次实例激活后仍 1 进程存活、
  最小化/恢复经受实际使用；构建 0 警告 0 错误、11/11 测试。

## 2026-10-22 第七轮：零外框（用户否定一切描边盒）（v7）

用户："不想要这种丑丑的外框，重新设计，全部修改，不止一处。"

- **全面去描边**：StatusChip 边框删除（保留柔色底）；AppCardStyle 回归
  全透明；AppDeviceCardButtonStyle 中性化；次级按钮改软填充无边框
  （悬停靠 ButtonBackgroundPointerOver 加深一档，原先与静止色相同是死悬停）；
  输入框/下拉静止与悬停描边令牌透明（聚焦保留薄荷）；开关 StrokeOn/Off
  透明 = 纯填充胶囊；按钮边框令牌透明。
- **验证方法**：像素级"结构线检测"——扫描截图中 ≥16px 连续描边色线段
  （文字抗锯齿不会形成长线）。首轮检出耳机页左下 100 条横线，UIA 反查
  定位到 ToggleSwitch 胶囊描边，修令牌后五页全部归零。开关边缘剖面
  实测：画布→旋钮→填充，无描边环。
- 保留：hairline 分隔线（不是框）、下拉展开浮层描边（浮层需边界）。
- 构建 0 警告 0 错误；11/11 测试；单次启动五页 UIA 正常、全页截图零结构线。

## 2026-10-22 第八轮：形状重造——圆点状态 + 自绘开关（v8）

用户："我要的是修改这个形状，太丑了，一点都没有美感"——上一轮只去了边框，
形状本身（胶囊/默认开关）仍被否定。本轮直接重造形状：

- **StatusChip → 彩色圆点 + 同色文字**：7px 圆点（Live 带 14px 淡光环）+
  13px SemiBold 文字，无底无框。hub 实测 281 个绿点像素（含文字）。
- **ToggleSwitch → 自绘 44×24 胶囊**：18px 滑块，悬停 20 / 按下 16 的尺寸
  反馈，140ms 二次缓动滑动，开=薄荷关=灰绿，去掉「开/关」赘字。
  22 处实例经 AppToggleSwitchStyle 模板一次性生效。
- **排查记录（重要教训）**：自绘模板三轮无效——最终从本机 NuGet 包
  `microsoft.windowsappsdk\1.7\lib\...\Themes\generic.xaml`（25958 行起）
  读到真实契约：ToggleStates(Dragging/Off/On) 独立组；且 **VSM 必须挂在
  模板根元素**（GoToState 只查根，之前挂在嵌套 SwitchArea 上永远不触发）；
  属性切换用 ObjectAnimationUsingKeyFrames（同官方）而非 Setters。
  修复后 4 个开启开关渲染出 952 个薄荷像素。
- 事故：一次 pwsh Get-Content 未指定编码把 Controls.xaml 中文注释写成乱码，
  立即用 UTF-8 重写两段注释修复，XAML 结构未受影响。
- 交互回归：UIA TogglePattern 拨动成功（游戏模式 Off→On，硬件写入成功），
  应用存活；构建 0 警告 0 错误、11/11 测试。

## 2026-10-22 打包：薄荷外设 1.0.4（覆盖升级）

- 版本 1.0.3 → 1.0.4；安装器产品名「外设中枢」→「薄荷外设」；
  AppId 保持 {0D3595D8-89B7-4F28-8811-D48FB4899713} 不变 →
  对已装 1.0.3 直接覆盖升级（同目录 {localappdata}\Programs\DeviceCenterPreview，
  InstallDelete 清旧文件），控制面板条目原位更名为「薄荷外设」。
- 自包含 publish（.NET 8 + WinAppSDK + sidecar + RC003 Helper），
  Inno Setup 6 编译：dist\DeviceCenterPreview-Setup-1.0.4.exe（83.1MB，
  未签名本地构建）+ .sha256。设置/映射存于 %LOCALAPPDATA%\DeviceCenterPreview，
  升级不触碰。

## 2026-10-22 第九轮：壁纸染色——类 Mica 的静态实现（v9）

用户问 Mica 能否实现。结论：API 可行但本机 crash-history 记录真 Mica 会
持续合成 + 黑屏，提供 A/B/C 三方案，用户选 B（静态壁纸染色）。

- **WallpaperTintService**：读 `%APPDATA%\...\TranscodedWallpaper`（注册表
  兜底）→ BitmapDecoder 缩到 24×24 → 每通道 4bit 量化取最大桶平均得主色；
  结果缓存。解码在线程池，UI 更新走 DispatcherQueue.TryEnqueue。
- **应用方式**：ThemeResource 解析出的 SolidColorBrush 是共享实例，直接改
  Root.Background 实例的 Color（画布浅色混 7% / 深色 10% 壁纸色）。
  第一版走 Application.Resources.ThemeDictionaries 失败（看不到合并字典
  里的主题字典，静默 no-op），改为从视觉树已解析实例入手。
- **设置**：AppSettings.WallpaperTintEnabled（默认开）+ 设置页外观区开关；
  App.UpdateSettings → MainWindow.ApplyWallpaperTint 联动，主题切换重染。
- **验证**：壁纸主色 #190014（近黑）→ 深色画布实测 (17,20,19)=#111413，
  与独立 Python 实现预测逐字节一致；开关往返 ON→OFF→ON 画布
  (17,20,19)→(16,22,19)→(17,20,19)；settings.json 持久化；
  11/11 测试、0 警告 0 错误。当前壁纸近黑，染色幅度细微；换彩色壁纸
  效果更明显。

## 2026-10-22 打包：薄荷外设 1.0.5（壁纸染色版）

- 第九轮壁纸染色（类 Mica 静态实现）+ 设置页开关随包发布。
- dist\DeviceCenterPreview-Setup-1.0.5.exe（83.1MB）+ .sha256；
  AppId 不变，对 1.0.3/1.0.4 直接覆盖升级。

## 2026-10-22 第十轮：鼠标页板载模式修复（v10）

用户报告：滑动条没法调节；DPI/轮询改完不生效。定位（G304 板载模式）：

- **滑条被禁用无解释**：`DpiSlider.IsEnabled = _canWrite && !OnboardMode`，
  板载模式下整个灰掉。改为板载模式不禁用——滑条即「当前档位」编辑器，
  拖动自动同步到对应档位输入框（应用走板载 profile 写入）；标签动态显示
  「当前档位 DPI（档位 N）」并附模式说明 hint。
- **改完不更新（显示假象）**：读侧 DPI/轮询 hero 取主机寄存器，板载模式
  下固件执行的是 profile 值（档位 DPI + data[0] 轮询间隔）→ 显示永远旧值。
  读侧板载模式改报 profile 派生值（active stage DPI + 1000/interval）。
- **写侧顺序 bug**：先写 profile 后切模式，且寄存器直写用切换前模式判断
  →「关板载 + 调 DPI」一次点击 DPI 直写被跳过。重排：模式切换先行，
  DPI/轮询寄存器直写按目标模式判断；板载模式下由 profile 携带（不再静默
  跳过后还显示已应用）。
- **新坑（crash-history 已记）**：ValueChanged 回调在 XAML 解析期触发，
  引用后解析控件 → 空引用被包装成 RangeBase.Minimum 解析失败，页面导航
  即崩。全部判空修复。定位耗时久的教训：报错行号指向无辜属性。
- **验证**：滑条 enabled+3200（档位值）；设 3400 → 档位框自动同步 →
  应用设置 → 设备真实写入（回读校验）→ hero/板载信息/SaveStatus 全部
  更新为 3400 → 18s 后无回弹；3 轮页面轮转 + 最小化恢复 PASS；
  11/11 测试。

## 2026-10-22 打包：薄荷外设 1.0.6（鼠标板载修复版）

- 第十轮鼠标页板载模式修复随包发布。
- dist\DeviceCenterPreview-Setup-1.0.6.exe（83.1MB）+ .sha256；
  AppId 不变，直接覆盖升级。

## 2026-10-22 开源发布：github.com/Fat-kevin/mint-peripherals

- 仅 device-center-preview 入库（439 文件 17.5MB）；external/rust/target/
  与 vendor/rc003-helper/x64/（构建产物）已 gitignore。
- 根 README 改为对外宣传页（徽章+截图+亮点）；技术总览移至
  docs/00-agent-readme.md，AGENTS.md 阅读顺序同步。
- MIT + rc003-helper GPL-3.0 随包说明；LICENSE 入库。
- Release v1.0.6：Setup-1.0.6.exe + .sha256 + 发布说明。

## 2026-10-26 耳机快捷开关「鼠标点不动」排查：自绘 ToggleSwitch 模板吞点击 + 写后回读

- 现象：耳机页五个快捷开关用鼠标点无反应；UIA TogglePattern / 键盘 Space 能拨动，设备有提示音。
- 排查：PointerPressed / PointerReleased / Tapped 都到达控件，但 IsOn 不变；同页 ComboBox、
  Button 用真实鼠标点击正常；把其中一个开关临时换成 `DefaultToggleSwitchStyle` 后鼠标立即生效
  → 结论是自绘 ControlTemplate 导致控件不响应指针（VSM/命中测试细节未完全定位）。
- 修复：`AppToggleSwitchStyle` 移除自定义 ControlTemplate，交回框架默认模板，仅保留
  MinWidth / FontSize / Foreground；颜色继续由 ToggleSwitch* token 控制。开关尺寸由 44×24
  回到系统 40×20（可点击性优先）。
- 顺带修复：
  - `ApplyAsync` 加写后回读校验：设备没接受时返回失败并在状态栏说明，不再假报「设置已发送」；
  - `ControlsAvailable` 额外要求 `ConnectionState == Connected`，避免 attach 成功到
    Connected 之间页面用 Default 值覆盖设备真实 ANC/EQ/开关；
  - `HeldKeyGuard`：启动、遥控引擎停止、进程退出时补发 Ctrl/Win/Shift/Alt 的 key-up，
    防止 key host 被强杀后语音热键卡在按下状态（本次调试中真实触发过 Win+Ctrl 系统快捷键）。
- 验证：x64 Debug 0 警告 0 错误；11/11 单测；真实鼠标逐个点击五个开关 Off→On→Off 全部生效，
  日志 write/readback 一致；遥控器模块关闭时不启动 sidecar/helper。

## 2026-10-26 鼠标 DPI 联动修复（代码检查，未做 UI 测试）

- 发现：`Refresh()` 每 15 秒无条件回填滑条/轮询/档位/板载开关，覆盖用户未应用的编辑；
  `StageIndexCombo`、5 个 `StageBox` 完全没有事件接线，只有滑条单向写当前档位框。
- 修复：加 `_dirty`（有未应用修改时刷新不回填表单）与 `_syncingDpi`（防回环）；
  档位下拉/档位输入/滑条/轮询/板载开关双向联动；应用成功、手动刷新、重新进页清除 dirty；
  状态栏提示未应用修改。
- 硬件 DPI 键：G304 只有 0x2201/0x02 getSensorDpi，该值只随主机写入变化（4072 次回读恒为 1350）；
  profile `[1]` 是默认档位而非运行时档位，按 DPI 键不改写扇区。页面无法得知当前档位，
  需要后续接 HID++ 通知或新查询，未做猜测性写入。
- 验证：x64 Debug 构建 0 警告 0 错误（构建到临时输出目录，未启动 UI）；11/11 单测。

## 2026-10-26 UI 重构：Mint Paper / 薄荷纸感（v10）

- 用户反馈「太简洁、太 AI」；方向选「层叠浅色」：雾绿画布 + 白色纸面卡片 + 薄荷强调。
- 令牌：画布 #F2F7F3、纸面白→#FBFDFB 渐变、卡片边 #E2ECE6、18 圆角；
  新增 AppPaperGradientBrush / AppHeroGradientBrush / AppAccentGradientBrush /
  AppInsetBrush / AppCardBorderBrush / AppNavActiveBrush 等。
- 组件：AppCardStyle/AppHeroCardStyle 回到纸面；主按钮改实心薄荷 #0C8F6E + 白字；
  次级按钮白纸面 + 细边；输入控件 40 高、10 圆角、细边；开关隐藏 On/Off 文案。
- Shell：导航 240px 品牌区（渐变图标 + 名称 + 副标题）+ 白色选中胶囊 + 底部引擎纸卡；
  标题栏右侧去掉与窗口按钮重叠的文字；内容区顶部 280px 薄荷光晕（不参与命中）。
- 页面：设备中心改 hero 欢迎区 + 三张 22 圆角设备纸卡（124px 实物图 / 104px 电量环 /
  「进入控制」箭头）；耳机/鼠标 hero 包进 AppHeroCardStyle 并压缩高度；四页加 overline。
- 验证：x64 Debug 0 警告 0 错误；单次启动截图复核（hub / 耳机 / 鼠标 / 设置），
  15s 刷新与交互未改。截图：docs/screenshots/redesign-*.png。
- 未打包安装版。

## 2026-10-26 鼠标「应用后不切档」修复

- 现象：板载模式下选 1/2 档点「应用」，鼠标 DPI 不变。
- 根因：`ApplyAsync` 在板载模式只写 profile 扇区，未发 setSensorDpi；固件只在加载 profile 时读默认档位。
- 修复：写 profile 后始终下发 `WriteDpi`；板载模式直写被拒时切一次板载模式强制重载 profile，
  再失败才报错。日志记录 dpi applied / reload。
- 验证：x64 Debug 构建 0 警告 0 错误。
