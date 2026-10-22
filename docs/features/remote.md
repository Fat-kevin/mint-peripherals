# 功能：遥控器

## 已实现

- Rust sidecar 常驻；真机 `MI RC`（RC003）自动连接，显示电量/型号/引擎状态。
- 音频端点选择、增益 0–24 dB、语音工具（豆包/微信/Vokie/其他）、全按键支持。
- 13 键 × 3 手势的映射总表：电源、上/下/左/右、确定、返回、主页、菜单、音量±、静音、TV。
- 表格内直接点功能 → 打开内嵌功能面板 → 选择后**立即保存**到原版 `button-mappings.json`。
- 语音工具切换显示对应快捷键/首次使用提示：
  - 豆包：右 Alt + CABLE Output + 首次授权；
  - 微信：左 Ctrl + 左 Win + 按住约半秒；
  - Vokie：右 Alt + 冲突提示；
  - 其他：自定义语音键。
- “执行单击/双击/长按”测试动作注入（经映射引擎）。
- “接收测试（10 秒）”：只观察按键边沿、暂停动作注入；每个键显示 ✓ 次数。
- 图片热点与表格双向悬停高亮；图片本身不再叠加文字标签。
- 映射状态：`已自动保存 · HH:mm:ss` + “打开配置文件夹”。
  - 保存后再次调用 `mappings.config.get` 回读；只有动作对象与提交内容一致才显示成功，
    避免 sidecar 吞掉写盘错误时“看起来成功”。
- 增益：滑块 0–24 dB，页面明确提示“引擎上限 24 dB；页面不回读旧值，移动滑块后再应用”。
  没有 `voice.gain.get` 时不伪造已保存的增益值。
- 语音工具下拉四项与 sidecar 枚举严格一致：豆包/微信输入法/Vokie/其他
  （tag 分别为 `doubao/wechat/vokie/other`）；旧版误写成 `push_to_talk/voice_activation`
  的问题已修复。
- 手动设备管理：扫描已配对遥控器 → 下拉选择 → 连接/断开；扫描状态显示“找到 N 个，其中 M 个受支持”。
    - 断开后扫描仍可用；选中受支持设备后“连接”才启用，可重新连回当前遥控器。
    - 手动断开后保持断开，直到重新扫描并点击“连接”；已验证不会自动重连。
- 按住说话快捷键：切换语音工具时同步写 `voice-hold-hotkey.json`（豆包/Vokie → 右 Alt；微信 → 左 Ctrl + 左 Win），
  页面显示引擎实际保存的快捷键，而不是只显示硬编码提示。
  - IPC 参数必须是 `voice.hotkey.set { chord: { keys: [...] } }`；旧集成发的是顶层 `keys`，sidecar 会当成清空快捷键。
  - 引擎连接后若发现“工具已选、快捷键为空”，只自动补对应快捷键，不改当前输入法；修复旧配置留下的空快捷键。
  - `voice.hotkey.get` 在未配置时返回 `result:null`，客户端按“空快捷键”处理，不能对 Null 调 `TryGetProperty`。
- 全按键支持：`capture.status` 以 RC003 桥接的真实连接状态为准（`helperAvailable/helperActive`）。
  Helper 已连接时开关可用，音量键、返回键与已映射按键走报告层增强捕获；未连接时开关禁用，
  `capture.enable=true` 返回明确错误，不写“假装生效”的设置。
  - 音量键/返回键在 Windows HID→VK 映射阶段被 `kbdhid` 丢弃，普通 Raw Input 和键盘钩子
    都收不到；只有增强捕获（Helper）能接管，因此“映射已保存但音量键无反应”先查 Helper。
  - Helper 随安装包分发，安装器放到 `%LOCALAPPDATA%\DeviceCenterPreview\Helper\` 并注册
    `SayAll RC003 Helper` 计划任务（安装时一次 UAC）。sidecar 在桥接未连接时执行
    `schtasks /run /tn "SayAll RC003 Helper"`，运行中每 30 秒补一次；因此重启、全新安装
    都不再依赖旧版「无线麦 SayAll」。
  - 桥接描述文件在 `%LOCALAPPDATA%\SayAll\rc003-bridge.ini`，Helper 日志在
    `%LOCALAPPDATA%\SayAll\Logs\`。如果安装时取消了 UAC，页面会明确提示 Helper 未就绪；
    重新运行安装包即可修复。页面加载时若 Helper 还在启动，会做 4 次、每次 1.5 秒的状态重试。
- 写入闸门：模块/写入关闭时扫描、连接、映射、音频、语音工具、接收测试全部禁用；服务层拒绝写入。

## 代码路径

| 内容 | 文件 |
|---|---|
| sidecar 客户端 | `Services/Remote/RemoteEngineClient.cs` |
| 页面 | `Views/RemotePage.xaml(.cs)` |
| sidecar | `sidecars/sayall-engine/src/main.rs` |
| 配置存储 | `%APPDATA%\app.getsayall.remote-mic.windows\button-mappings.json` |

## 页面结构

1. 标题 + 设备选择 + 电量。
2. 设备扫描行：扫描已配对遥控器 / 下拉选择 / 连接 / 断开 / 扫描状态。
3. 按键映射卡（优先于语音卡）：
   - 左：RC003 图片 + 透明热点 + 接收测试；
   - 右：当前键手势摘要、执行测试按钮、13 行映射表（每行 3 个功能单元格 + 接收计数）、自动保存状态 + 配置文件夹。
4. 语音链路卡：音频端点 / 语音工具 / 动态提示 / 已保存快捷键 / 增益 / 全按键 / 输入状态。
5. 底部：应用增益 + 保存状态。

## 实现要点

- 映射单元格是 **Button**，点击打开页面内嵌 `ActionEditorCard`（在映射卡顶部），不弹 Flyout/ContentDialog。
- 打开编辑时 `PageScroll.ChangeView(null,0,null,true)` 滚到顶部，让内嵌面板可见；**不要用 `StartBringIntoView`**（会触发 XAML 崩溃）。
- 保存：`RemoteEngineClient.SaveMappingAsync` 读取当前配置，补齐该键的 `single/double/long` 三个槽位（缺失写 `{"type":"disabled"}`），再发送 `mappings.save`；否则 sidecar 返回 `invalid mappings`。
- 接收测试：`mappings.suspend {suspended:true}` 暂停注入；sidecar 推送 `{"event":"button","button":"ok","pressed":true}`；页面只在 `_receiving` 时计数。
- 按键边沿来自 `platform.subscribe_button_edges`，由 key_gate、Raw Input 与 RC003 增强捕获桥接的并集产生；音量键/返回键只有最后一条通道能收到。
- 映射动作 JSON 与 sidecar `ButtonAction` 对齐：`disabled` / `shortcut{chord.keys}` / `scroll` / `mouse_click` / `mouse_move` / `open_app` / `focus_input`。

## 依赖与冲突

- 原版 `sayall-windows-app` 不能同时运行（BLE/钩子/Helper 冲突）。
- 需要 key host 子进程存活（`gateActive=true`）才会真正注入；日志 `key host started`。
- 接收测试结束/离开页面/引擎断开时必须恢复 `mappings.suspend false`。

## 待办 / 限制

- 映射导入/导出、首次使用 7 步向导尚未接入。
- sidecar 的 `mappings.save` 仍可能不规范化/不报告写盘错误；App 已做保存后回读，
  但没有修改 sidecar 的校验逻辑。非法历史配置需要真机回归。
- 按住连续执行（holdRepeat）、OK 5 秒上下文点击、映射总开关、打开应用/聚焦输入框
  动作：引擎已支持，UI 尚未暴露；本轮没有扩张编辑面板。
- 增益没有 `voice.gain.get`，页面不回读旧值；重启后滑块从 0 开始，移动后再应用。
- 全按键 Helper 未随 sidecar 打包；本机已由旧版安装/计划任务提供 Helper 时可复用。若卸载旧版或换机，
  音量键、返回键会退回“不可用”，需要重新安装 Helper（UAC）。
- 语音测试的真实链路需要 VB-CABLE 配置。
- 手动断开后保持断开，直到重新扫描并点击“连接”；不再自动重连。若以后需要“禁止自动重连”持久化配置，再单独加设置项。

## 麦克风启动判据测试

ATVV 的实际控制链路是：

1. 遥控器发 control `0x08`（麦克风打开请求）；
2. 主机回 `MIC_OPEN`（v1.0 编码为 `0C 00`）；
3. 遥控器发 control `0x04`（流开始，带 session id）；
4. audio 特征值开始上报，主机解码后写入所选 WASAPI 渲染端点（是否真的有音频包要单独验证）。

主机当前**没有**强制“先收到 `0x08` 才接受 `0x04`”：

- `external/rust/crates/sayall-core/tests/voice_activation.rs` 的
  `stream_started_without_open_request_is_accepted` 固定了这一行为；
- 同一测试文件还固定了 `0x08` 单独不会开始音频，以及只有 `0x08` 会返回
  `MicrophoneOpenRequested`。

真实遥控器固件是否只在语音键按下时才推流，需要硬件测试，不能用单元测试替代。
新增硬件诊断工具（会写 ATVV 控制命令，但不改按键映射/设备配置）：

- 工具源码：`tools/RemoteMicActivationProbe/`
- 说明：[`tools/RemoteMicActivationProbe/README.md`](../../tools/RemoteMicActivationProbe/README.md)
- 运行前退出「外设中枢」和 `sayall-engine`（同一时间只能有一个 ATVV GATT 客户端）。
- 阶段：静默基线 → 语音键阳性对照 → 逐个普通按键 → 主机主动 `MIC_OPEN`。
- 判读（先看 `evidenceQuality` 是否有语音键音频阳性对照；**只有真实 audio 包才算“麦克风在传输”，单独 0x04 只算“流开始”**）：
  - `voice_key_only`：语音键阶段有音频，普通按键和主机 `MIC_OPEN` 都没有音频/流开始；
  - `voice_key_transmits_audio_only_but_stream_can_start_without_audio`：只有语音键有音频，但主机/普通按键能触发 `0x04`；
  - `other_button_transmits_microphone_audio`：某个普通按键阶段出现真实音频；
  - `host_protocol_transmits_microphone_audio`：未按键时主机 `MIC_OPEN` 就产生音频；
  - `host_protocol_starts_stream_without_audio`：主机只拿到 `0x04`，没有音频；
  - `inconclusive_missing_voice_positive_control`：语音键没有阳性证据，不能下结论。
- 2026-10-21 本机 `--host-open-only` 基线：`MIC_OPEN` 写入 Success，遥控器回
  `04 00 02 00`（0x04 流开始，session 0），随后 10 秒无音频包；发 `MIC_CLOSE`
  后回 `00 00`。因此**协议能请求流开始，但这次没有证明麦克风实际传输**；
  还需要语音键阳性对照和普通按键阶段才能回答“是否只有语音键”。报告见
  `.artifacts/remote-mic-activation-host-only-2.json`。
- 报告写到 `.artifacts/remote-mic-activation-<时间>.json`，只记录控制帧十六进制和音频包数/字节数，不落盘音频内容。
- 工具会尝试发 `MIC_CLOSE`、退订并释放 GATT；不会写按键映射或修改设备配置。

这项测试只用于确认启动入口，不改变现有“语音键 + 按住说话热键”的产品行为。
