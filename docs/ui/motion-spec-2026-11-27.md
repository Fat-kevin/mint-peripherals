# 动效规范（2026-11-27 · Still Life 方向）

> 目标：让界面“活起来”但不表演。只有一个编排好的入场，其余动效都回答用户动作或状态变化；
> 不动布局、不制造等待、不循环呼吸。WinUI 3 实现时用 `Storyboard`/`RenderTransform`，
> 不引入 Web 动画运行时。

## 1. 动效原则

1. **一个编排好的入场**：页面加载时设备依次落位、数字计数、状态点亮；之后静止。
2. **动作即反馈**：hover / focus / press / 数值变化 / 状态变化才动；没有“氛围循环”。
3. **只动不重排**：只改 `Opacity`、`TranslateTransform`、`ScaleTransform`、颜色；不改
   `Width/Height/Margin`，不重建控件树。
4. **快而克制**：微反馈 120–180ms，常规 240–320ms，入场 520–760ms；用户操作永远不被动画阻塞。
5. **可关闭**：系统关闭动画时可立即到终态。

## 2. 动效 tokens

| Token | 值 | 用途 |
|---|---|---|
| `MotionMicro` | 120ms | 按压、链接下划线 |
| `MotionSmall` | 180ms | hover 颜色、LED |
| `MotionMedium` | 280ms | 设备抬升、状态文字 |
| `MotionLarge` | 520ms | 图片落位、标签淡入 |
| `MotionCount` | 760ms | 电量大数字计数 |
| `EaseOutExpo` | `cubic-bezier(.16,1,.3,1)` | 入场、落位、计数（快起慢收） |
| `EaseOutCubic` | `cubic-bezier(.33,1,.68,1)` | 淡入、footer |
| `EaseSpringSoft` | `cubic-bezier(.34,1.4,.64,1)` | 状态点出现（极小过冲） |
| `EaseInExpo` | `cubic-bezier(.7,0,.84,0)` | 退出/收起 |
| `StaggerImage` | 90ms | 三台设备图片依次落位 |
| `StaggerLabel` | 52ms | 同一设备标签行依次淡入 |
| `LiftDistance` | 22px | 入场位移 |
| `HoverLift` | 7px | 设备 hover 抬升 |

WinUI 映射：`ExponentialEase(EasingMode=EaseOut)` / `CubicEase` / `BackEase(Amplitude=0.2)`
（对应 `EaseSpringSoft` 的轻微过冲），不要用 `BounceEase` 做全场。

## 3. 入场时间线（页面首次加载，只跑一次）

| 时间 | 元素 | 属性 | 从 → 到 | 时长 / 曲线 |
|---|---|---|---|---|
| 0ms | 顶部命令栏 | Opacity | 0 → 1 | 220ms EaseOutCubic |
| 80ms | 耳机图 | Opacity / TranslateY / Scale | 0 / 22 / .985 → 1 / 0 / 1 | 640ms EaseOutExpo |
| 170ms | 鼠标图 | 同上 | 同上 | 640ms EaseOutExpo |
| 260ms | 遥控器图 | 同上 | 同上 | 640ms EaseOutExpo |
| 300ms+ | 各设备标签行 | Opacity / TranslateY | 0 / 10 → 1 / 0 | 520ms EaseOutCubic，按 52ms 阶梯 |
| 420ms | 电量数字 | 数值 | 0 → 实际值 | 760ms EaseOutExpo（数字计数，非布局动画） |
| 420ms | 双耳/充电盒数字 | 数值 | 0 → 实际值 | 760ms EaseOutExpo |
| 860ms+ | 状态点 | Opacity / Scale | 0 / .5 → 1 / 1 | 420ms EaseSpringSoft，按 70ms 阶梯 |
| 760ms | 底部系统条 | Opacity | 0 → 1 | 260ms EaseOutCubic |

入场总时长约 1.2s；第 1.2s 后所有元素静止，没有待机呼吸/浮动/流光。

## 4. 交互与状态

| 触发 | 动效 | 说明 |
|---|---|---|
| 设备 hover | 整体 `TranslateY(-7px)`，图片 `TranslateY(-5px) scale(1.012)`，320ms EaseOutExpo | 只抬设备本体，不改变布局；离开 280ms 回到 0 |
| 链接 hover / focus | 底部 1px 线从左侧 `scaleX(0→1)` 220ms；键盘 focus 加 2px 外框 | 键盘和鼠标都有同样反馈 |
| 按压 | `scale(.996)` / 1px 下沉，120ms EaseOutCubic | 不弹跳、不改变尺寸 |
| 电量变化（15s 刷新或手动） | 数字 360ms 上移淡出再落回；仅数值变化时才动 | 刷新但值没变 → 完全不动 |
| 低电量 | 数字/耳朵/状态文字 240ms 渐变为红；状态点单次 `ledPulse` 700ms；不循环闪烁 | 颜色 + “低电量”文字双编码 |
| 状态断开/恢复 | 状态点一次 pop / 文字 180ms 淡换；设备整体 Opacity 到 0.55 | 不抖动布局 |
| 打开设备页 | 当前内容 160ms 淡出，目标页 220ms 淡入 + 8px 上移；返回同理反向 | 页面内导航，不做大幅转场 |

禁止：滚动视差、无限循环呼吸灯、粒子、渐变流动、数字每次刷新都计数、动画期间阻塞点击。

## 5. 可访问性与性能

- 读取系统 `UISettings.AnimationsEnabled == false` 时：所有动画时长 0，直接终态。
- 所有动画只作用于 `Opacity` 和 `RenderTransform`；不触发布局。
- `Storyboard` 在页面 `Loaded` 启动、`Unloaded` 停止；不跨页残留。
- 数字计数只改 `TextBlock.Text`，每帧一次，760ms 内完成；15s 刷新不重复计数。
- 首帧必须显示真实内容（Opacity 0 只在入场瞬间，且无网络等待）。
- 60fps 目标；单次入场之外没有常驻动画。

## 6. 演示文件

- 深色动效演示：`design/redesign-2026-11-27/motion-dark.gif`
- 浅色动效演示：`design/redesign-2026-11-27/motion-light.gif`
- 可交互 HTML（用浏览器打开，含悬停/focus）：`design/redesign-2026-11-27/still-life-dark.html`
- 演示时间线：0–1.2s 入场 → 1.25s hover → 2.35s 低电变化 → 3.45s 恢复 → 4.2s 收尾。


---

## 7. 实际实现（2026-11-27 · 等权卡片版）

实现文件：`src/DeviceCenterPreview.App/Services/UiMotion.cs`，页面 code-behind 只负责调用。

| 位置 | 实际动效 |
|---|---|
| 总览 | 三卡 110ms 错峰，620ms `CubicEase(EaseOut)`，opacity + translateY(20) + scale(.985→1)；数字 760ms 计数 |
| 总览 hover | 卡片 `TranslateY(-4)` 260ms EaseOut；图片跟随上移 |
| 总览状态变化 | 电量大数字/状态点颜色即时更新；连接变化只做一次短脉冲（Release 下使用低风险实现） |
| 图标栏 | 选中底色即时切换；图标 hover 1.06 / press .94（160/90ms） |
| 页面切换 | 每次进入详情/设置页，顶层分区 52ms 阶梯、520ms `ExponentialEase(EaseOut)` 落位；从缓存返回也重播 |
| 工作区/分类 | 鼠标工作区、遥控器页签、设置分类切换时，内容 220ms 淡入 + 8px 上移 |
| 耳机模式/EQ | 选中瓦片即时更新，无弹跳动画 |
| 数字变化 | 15 秒刷新只改文本；不重复计数 |
| 系统设置 | 遵循 `UISettings.AnimationsEnabled`；关闭后全部直接到终态 |

统一 easing：入场/落位 `ExponentialEase(EaseOut)`，常规状态 `CubicEase(EaseOut)`；不使用线性、不做循环。所有动画只改 `Opacity` / `RenderTransform` / `SolidColorBrush.Color`。
