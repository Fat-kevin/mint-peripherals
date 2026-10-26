# UI 视觉系统（Mint Studio / 薄荷工作室）

> 2026-10-22 第三轮：用户否定了暖色"铜台"（"很 AI、很腻"），全面转向
> **小清新浅色薄荷**，并**去卡片化**——内容直接放在画布上，靠留白与
> 细分隔线组织，不再把所有内容装进边框盒。

## 1. 产品与图标

- 名称：**薄荷外设**（副标题「设备控制台」）。v6 由「外设中枢」更名。
- 图标：**薄荷薄荷叶**——白色单叶 + 茎浮在 #12A886 圆角方块上（≥32px
  带主叶脉，16/20/24 省略保持锐利）。生成脚本
  `design/icon-concepts/mint-leaf/build-mint.py`（8x 超采样 + LANCZOS），
  输出固定文件名 `Assets/PeripheralHub.ico` 与 `PeripheralHub-*.png`
  （保持文件名不变以免动 csproj/清单）。

## 2. 形状语汇（v8 定稿）

- **状态标记 = 彩色圆点 + 同色文字**（StatusChip）：7px 圆点 + 13px SemiBold
  文字，无底无框；Live 状态加 14px 淡色光环。
- **开关 = 系统 ToggleSwitch 模板 + 薄荷 token**（2026-10-26 回退）：
  曾用 44×24 自绘胶囊模板（18px 滑块、悬停/按下缩放），但该模板在真机上
  **只响应键盘和 UIA，鼠标点击不触发 Toggle**（PointerPressed/Released/Tapped
  都到达控件，IsOn 不变；同页 ComboBox、Button 鼠标正常，换回系统模板后点击
  立即生效）。为保证可点击性，`AppToggleSwitchStyle` 只保留 MinWidth/FontSize/
  Foreground 等 setter，模板交回框架默认；颜色仍由 `ToggleSwitchFillOn/Off`、
  `ToggleSwitchKnobFillOn/Off`、`ToggleSwitchStrokeOn/Off` 六个 token 控制。
  尺寸随系统（40×20），不再强求 44×24。
  历史契约（如将来重做自绘模板）：VSM **必须挂在模板根元素**（GoToState 只查根）；
  CommonStates=Normal/PointerOver/Pressed/Disabled，ToggleStates=Dragging/Off/On；
  属性切换用 ObjectAnimationUsingKeyFrames。重做前必须先做鼠标点击回归。
- 控件圆角 4px；唯一保留描边：下拉展开浮层。

## 3. 视觉方向

**Mint Paper / 薄荷纸感**（2026-10-26 重构）：雾绿画布 + 白色纸面卡片 +
薄荷强调。浅色为默认主题（仍可切深色/跟随系统，深色是同色系绿调炭色）。

- **纸面层次**：内容重新回到卡片上——`AppCardStyle` = 白→微暖白渐变 +
  1px `#E2ECE6` 细边 + 18 圆角；`AppHeroCardStyle` = 薄荷淡渐变 +
  22 圆角，用于每页页头/仪表区。区块之间靠 18–24 间距与纸面层次区分，
  不再裸放在画布上。
- **页头 hero**：每页顶部一张渐变纸卡，含 11px overline（字距 80）、
  34px 标题、副标题和右侧状态/主操作。设备中心另有 hero 欢迎区。
- **设备实物图当主角**：设备中心图片 124px、电量环 104px；耳机页三枚
  56px 分量环；鼠标页 108px 实物图 + 26px 读数。
- **主按钮**改为实心薄荷 `#0C8F6E` + 白字（10 圆角、40 高）；次级按钮
  白纸面 + 细边。开关回到系统模板并隐藏 On/Off 文案，只留胶囊。
- **导航** 240px：品牌区（渐变图标 + 名称 + 副标题）、白色选中胶囊、
  底部引擎状态纸卡。
- 顶部内容区有一层 280px 高的薄荷淡出光晕（`#160E9A76 → 透明`），
  不参与命中测试；不做真实 Mica/重阴影。
- **壁纸染色**（v9）：仍是静态混色——启动/主题切换/开关切换时读壁纸
  主色给画布混色（浅 7% / 深 10%），零合成、零常驻开销（真 Mica 在本机
  会黑屏，见 crash-history）。设置页可关。
- 设备色只用于身份/状态/进度，不铺满整页；一个页面只保留一个主操作。

## 4. 颜色令牌（Light 为默认）

### 3.1 表面与文字（Light）

| Token | 值 | 用途 |
|---|---|---|
| `AppCanvasBrush` | `#F2F7F3` | 页面画布（雾白） |
| `AppChromeBrush` | `#EAF1EC` | 导航/侧栏 |
| `AppTitleBarBrush` | `#FAFCFB` | 自定义标题栏 |
| `AppCardBrush` | `#FFFFFF` | 轻容器底（内容大区块） |
| `AppRaisedBrush` | `#FFFFFF` | 内层面板（输入组/表格） |
| `AppInputBrush` | `#FFFFFF` | 输入框/下拉 |
| `AppHoverBrush` | `#EAF3EE` | 悬停 |
| `AppBorderBrush` | `#E2ECE6` | 常规边框（仅控件用） |
| `AppBorderSubtleBrush` | `#E7EEEA` | 细分隔线 |
| `AppTextPrimaryBrush` | `#182620` | 主文字 |
| `AppTextSecondaryBrush` | `#4B6159` | 次文字 |
| `AppTextTertiaryBrush` | `#6E827A` | 说明/元信息 |

### 3.2 强调色与设备信号色（Light）

| Token | 值 | 用途 |
|---|---|---|
| `AppIonBrush` | `#0E9A76` | 焦点、链接、选中（薄荷绿） |
| `AppPrimaryFillBrush` | `#0C8F6E` | 主按钮软填充 |
| `AppPrimaryTextBrush` | `#FFFFFF` | 主按钮文字 |
| `AppHeadsetBrush` | `#7A6CC9` | 耳机（柔紫） |
| `AppMouseBrush` | `#C28A33` | 鼠标（蜜糖） |
| `AppRemoteBrush` | `#2E8FBF` | 遥控器（天蓝） |
| `AppSuccessBrush` | `#0F8F5C` | 已连接/成功 |
| `AppWarningBrush` | `#B07914` | 低电量/注意 |
| `AppDangerBrush` | `#C74E47` | 错误/危险 |

深色主题为同族绿调炭色（画布 `#101613`、薄荷 `#3CCFA5`），见
`Styles/Tokens.Color.xaml`。

- 设备色另有 `*TintBrush`（透明度约 12%）用于图片底板/轨道，不用于正文。
- `SystemAccentColor`、`AccentFillColorDefaultBrush`、`ToggleSwitch*` 已统一覆盖为
  薄荷 Ion，避免系统紫色 accent 泄漏。
- 状态永远同时有颜色和文字，不能只靠色点。

### 3.3 主题机制

`Styles/Tokens.Color.xaml` 的 `Light` 字典提供与 Dark 相同的键（表面/文字/信号色 +
Fluent 按钮/输入/开关/Accent 覆盖），页面只引用 token，不写死 hex。
Light 的成功/警告/离线和设备色比 Dark 更深，保证 12px 状态文字在白底上的对比度。

- 设置页「外观」写 `settings.json.themeMode`，主窗口通过 `Root.RequestedTheme` 即时换肤；
  `跟随系统` 交给 WinUI 的 `ElementTheme.Default`。
- XAML 画笔用 `{ThemeResource ...}` 自动更新；代码构造的画笔（设备中心电量条/状态胶囊、
  遥控器映射表）必须走 `AppTheme.GetBrush`，并在 `IThemeAware.OnAppThemeChanged()` /
  `StatusChip` 的 `AppTheme.ThemeChanged` 中重新解析，不能直接读
  `Application.Current.Resources`（它按应用主题解析，不按当前窗口）。
- 标题栏 caption 按钮颜色由 `AppTheme.ApplyTitleBar` 在换肤时重设，避免浅色下仍是白字。

## 5. 字体与字阶

| Style | 字号/行高 | 字重 | 用途 |
|---|---|---|---|
| `AppPageTitleStyle` | 30/36 | SemiBold | 页面标题 |
| `AppPageSubtitleStyle` | 14/22 | Regular | 页面副标题 |
| `AppSectionTitleStyle` | 18/24 | SemiBold | 区块标题 |
| `AppCardTitleStyle` | 15/20 | SemiBold | 卡片/行标题 |
| `AppBodyStyle` | 14/22 | Regular | 正文 |
| `AppCardLabelStyle` | 12/16 | Regular | 字段标签 |
| `AppCardFootnoteStyle` | 12/16 | Regular | 说明/脚注 |
| `AppBatteryStyle` | 34/40 | SemiBold | 电量/主读数 |
| `AppNumericMediumStyle` | 24/30 | SemiBold | 次级读数 |

- 字体：显示用 `Segoe UI Variable Display`，正文用 `Segoe UI`；系统缺字时回退。
- 最小正文 12px；不使用 10/11px 小字承载关键信息。
- 数字读数可大，但说明必须紧邻，不能只给一个孤立数字。

## 6. 圆角、间距、阴影

| Token | 值 |
|---|---|
| 输入/下拉 | 4px（Win11 控件标准） |
| 按钮 | 4px |
| 内容容器 | 8px（Win11 卡片标准） |
| 胶囊 | 999px |
| 页面内边距 | 36,28,36,24 |
| 区块间距 | 28px |
| 间距级差 | 4/8/12/16/24/28/32 |

- 固定窗口下不做响应式重排；用 `*` 列和固定最小宽度保证 1072px 内容区稳定。
- 阴影只允许极轻的层次（必要时 0 1px 2px），不叠加发光。
- **整卡/表格按钮必须显式 `HorizontalAlignment="Stretch"`**，否则 `Button` 会按内容宽度居中，
  三列卡片会出现不等宽、间距忽大忽小。
- **表单 `ComboBox` 默认拉伸**；放在 `Grid` 的 `*` 列里要占满列宽，不能缩成内容宽度。
- **固定行高必须大于控件最小高度**：36px 的输入控件至少给 44px 行高；宁可让页面滚动，
  也不要让 ComboBox/TextBox 在行内上下溢出重叠。
- **可换行状态文本必须放在 `Grid` 的 `*` 列**；横向 `StackPanel` 会给子项无限宽度，
  `TextWrapping` 不会生效，长错误/配置信息会被右侧裁掉。
- Shell 的 **InfoBar 占内容区独立 Auto 行**，打开时下推页面，关闭不占高度；不要用 overlay 盖住页面标题。

## 7. 核心组件

| 资源 | 说明 |
|---|---|
| `AppCardStyle` / `AppHeroCardStyle` / `AppConsolePanelStyle` | **透明无边框**，区块直接放在画布上 |
| `AppInnerPanelStyle` | 白底无边框微浮起，仅用于输入组和映射表 |
| `AppSettingsRowStyle` | 设置行：透明底、底部分隔线、16,12 内边距 |
| `BatteryRing`（UserControl） | 原生 Path/Ellipse 圆形电量仪表：12 点起点顺时针、圆头端帽、中心大号百分数、充电闪电角标；低电自动转红/黄 |
| `StatusChip`（UserControl） | 999px 胶囊，12px 矢量状态图标 + 文本；Live/Connected/Offline/Warning/Error/Saved |
| `AppChannelButtonStyle` | 设备中心通道按钮：透明底、悬停薄荷浮起、整列可点 |
| `AppPrimaryButtonStyle` | 薄荷软填充底 + 深绿字；每页最多一个 |
| `AppSecondaryButtonStyle` / `AppTertiaryButtonStyle` | 白底细边 / 透明底 |
| `AppToggleSwitchStyle` | 系统模板 + 薄荷填充 token（2026-10-26 起；自绘模板会吞鼠标点击） |
| `AppComboBoxStyle` / `AppTextBoxStyle` / `AppSliderStyle` | 统一输入控件外观；ComboBox 横向拉伸 |
| `AppNavigationViewItemStyle` | 38px 高、8px 圆角、13px 字号；选中为薄荷 tint 底 + 深绿字 |
| `AppTableRowButtonStyle` | 表格行按钮；横向拉伸，保证整行命中 |

- 遥控器映射表保持静态行，按钮单元格就地打开右侧编辑器；不用动态 `ItemsRepeater` 重建。

## 8. 页面结构

### 设备中心
- 标题 + 右侧「自动刷新 15 秒」StatusChip。
- **三条开放通道**（无外框）：每条 104px BatteryRing + 设备名 + 状态胶囊 + 两行说明，
  通道间 1px 竖向细分隔线；整列是 `AppChannelButtonStyle` 按钮，悬停薄荷浮起。
- 底部一条状态行（细分隔线上方）：真实性说明 + 刷新/设置按钮。
- 离线设备不伪造电量，仪表显示「—」并说明原因。

### 耳机
- 仪表区：112px 图片板 + 标题/说明 + 右侧「查找耳机」主按钮；
  分隔线下方**三枚 76px 分量仪表**（左耳/右耳/充电盒），各带充电状态文字。
- 下方 2*/1*：左「降噪与音效」（ANC/EQ + 快捷开关），右「连接与固件」。
- ANC/EQ 使用 `ComboBox` + `PlaceholderText="未接入"`，未连接时不显示空白框。
- 查找耳机为 5 秒可取消倒计时；所有写入先读后写并校验。

### 鼠标
- 仪表区：112px 图片板 + 标题/说明/设备下拉 + 右侧 84px 电量环；
  分隔线下方**大号读数条**（DPI / 轮询率 Hz / 板载配置，26px SemiBold，竖线分隔）。
- 2*/1*：左「性能」（DPI 滑块、轮询率/档位、5 个横向档位块、板载开关、配置信息），
  右「设备信息」。
- 按键映射全宽；映射表在页面内滚动，不嵌套 `ScrollViewer`，保留 8 行静态结构。

### 遥控器
- 仪表区：112px 图片板 + 标题/说明/设备下拉 + 84px 电量环 + 连接/断开（竖排）；
  分隔线下方扫描行（扫描按钮 + 扫描结果下拉 + 状态文字）。
- 映射区三列 220 / * / 300：左产品图热点，中映射表，右当前按键编辑器。
- 下方语音区：左音频路由/工具/提示，右增益/语音状态/全按键/输入状态/应用。
- 映射保存、接收测试、语音应用都显示真实结果，不假装成功。

### 设置
- 顶部两列：左「启动」「模块」，右「外观」「写入授权」「通知」。
- 「外观」主题下拉：浅色/深色/跟随系统（默认浅色）；切换立即保存并同步主窗口。
- 下方通栏：备份/恢复、诊断、配置目录。
- 设置行是静态 12px+ 标签 + 说明 + 右侧开关/下拉；即时保存。
- 恢复默认有 3 秒二次确认（按钮文案变化），不做 `ContentDialog`。

## 9. 稳定性约束（设计的一部分）

- 固定窗口 1280×840、不可缩放/最大化；尺寸只在 `InitializeComponent` 之前设置一次。
- 不使用 `Flyout`、`ContentDialog`、`StartBringIntoView` 做主交互。
- 15 秒刷新只更新文本/数值，不重建 `BitmapImage`、`ItemsSource` 或大控件树。
- 后台事件回 UI 线程必须 `DispatcherQueue.TryEnqueue`。
- 页面 `Unloaded` 退订 `Changed`/`RemoteButtonReceived`、停计时器、恢复 `mappings.suspend=false`。
- 主题切换不重建页面：XAML token 自动更新；代码画笔只重设 Brush，不重建
  `ItemsSource`/`BitmapImage`/控件树。
- 页面加载时用 `PageScroll.ChangeView(null,0,null,true)` 复位到顶部，避免缓存页被焦点带回中部。

## 10. 可访问性

- 状态不只靠颜色：有「已连接/离线/未知」文字。
- 电量条有数字；低电量同时用 warning 色 + 文字说明。
- 长文本换行，不用省略号隐藏关键原因；设备名可截断，但 tooltip/详细信息保留全名。
- 最小可点击高度 32px；导航项 40px。
- 设备中心整卡按钮有 `AutomationProperties.Name/HelpText`，屏幕阅读器不会读到空按钮。
- `ComboBox`/`ToggleSwitch` 保留键盘操作和系统 focus visual（Ion）。
