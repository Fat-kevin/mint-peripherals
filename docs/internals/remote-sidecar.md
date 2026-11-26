# 内部：遥控器 sidecar

入口：`sidecars/sayall-engine/src/main.rs`（同时见该目录 `README.md`）。

## 启动与握手

1. 主应用启动 `sidecars/sayall-engine.exe`。
2. sidecar 向 stdout 输出一行：
   ```json
   {"event":"ready","port":62176,"token":"...","protocol":1,"selectedRemoteId":null}
   ```
3. 主应用连接 `127.0.0.1:<port>`，**第一行发送 token**。
4. 之后双方使用 UTF-8 JSONL。

请求/响应：

```json
{"id":1,"cmd":"state.get","params":{}}
{"id":1,"ok":true,"result":{...}}
{"id":1,"ok":false,"error":{"code":"engine.error","message":"...","retryable":false}}
```

事件：

```json
{"event":"snapshot","seq":1,"runtime":{"platform":{...},"connection":{...},"audio":{...},"mappings":{...}}}
{"event":"button","button":"ok","pressed":true}
```

- snapshot 1 Hz；button 为语义按键边沿（按下/释放），用于接收测试与 UI 高亮。
- 主应用 `RemoteEngineClient.StableChanged` 忽略高频计数器，避免 1 Hz 触发全应用刷新。

## 命令（当前实现）

| 命令 | 参数 | 说明 |
|---|---|---|
| `system.hello` | — | 协议/版本 |
| `state.get` | — | 完整快照 |
| `remote.scan` / `remote.connect` / `remote.disconnect` | `{deviceId}` | 扫描/连接/断开 |
| `audio.list` / `audio.select` | `{endpointId}` | WASAPI 端点 |
| `voice.gain.set` | `{db}` | 0–24 dB |
| `voice.tool.get` / `voice.tool.set` | `{tool}` | doubao/wechat/vokie/other |
| `voice.hotkey.get` / `voice.hotkey.set` | `{chord:{keys:["right_alt"]}}` | 按住说话热键；持久化到 `voice-hold-hotkey.json`；旧顶层 `keys` 会被当成清空 |
| `mappings.config.get` | — | 原始映射配置 |
| `mappings.get` | — | 映射快照 |
| `mappings.save` | `{mappings}` | 保存（每个键必须三个手势槽位） |
| `mappings.test` | `{button,trigger}` | 直接注入一个动作 |
| `mappings.suspend` | `{suspended}` | 只观察、不注入（接收测试） |
| `capture.enable` / `capture.status` | `{enabled}` | 全按键/增强捕获；`status` 读取 RC003 桥接真实状态（`helperAvailable/helperActive`），Helper 已连接时允许开启；关闭始终允许 |
| `raw.start` / `raw.stop` | — | Raw Input |
| `diagnostics.logs` | — | 诊断日志 |
| `system.shutdown` | — | 排空后退出 |

## 启动流程（重要）

1. `key_host::parse_host_args`：若是 key host 子进程参数 → 直接运行 host 并退出。
2. `WindowsPlatform::default()`。
3. 读取 state dir 的 `settings.json` / `button-mappings.json` / `voice-hold-hotkey.json` 并应用；
   `settings.rc003_capture_enabled=true` 时先下发增强捕获目标，Helper 稍后连上桥接会收到当前目标集。
   紧接着调用 `start_rc003_helper_if_needed`：桥接未连接时用
   `schtasks /run /tn "SayAll RC003 Helper"` 拉起已注册任务（不弹 UAC；不依赖 capture 设置，
   否则全新安装默认 false 会导致帮助器永远不启动、开关不可用）。`schtasks` 的 stdout/stderr
   必须重定向到 null，否则会污染 sidecar 给宿主的握手行，宿主解析失败并每 5 秒重启一个 sidecar。
4. `platform.start_raw_input()`。
5. **`sayall_windows::key_host::start_global()`**：启动 key host 子进程/钩子。
   没有它时 `gateActive=false`，引擎会记录手势但**不注入**（历史 bug）。
6. 建 TCP listener，等待宿主 token。
7. 订阅 `platform.subscribe_button_edges`，把边沿写入事件 channel。
8. 每 1 秒发 snapshot；每 30 秒复查一次 Helper（已连接则跳过，断连则重新 `/run` 计划任务）；
   退出时 `shutdown_ble_for_exit` + `key_host::shutdown_global`。

## 按键通道掉绑与自愈（2026-11-24）

- 现象：UI 显示遥控器已连接（引擎 BLE/GATT 正常、电量正常），但所有按键无反应、接收测试 0 个按键。
- 根因：Windows 侧 HOGP/HID 接口丢失（睡眠唤醒、断连或蓝牙栈抖动后常见）。raw input
  监听器绑定的是该接口，接口消失后 `mappings.listenerActive=false`，日志里 raw input
  `phase=Awaiting, matched_device_count=0`；`gate_active` 仍为 true，所以“看起来一切正常”。
- 判定：引擎 ready 且设备已连接，但 `listenerActive=false` 持续 ≥20 秒。
- 自愈（`RealPeripheralService.CheckRemoteListenerHealth`，5 秒看门狗）：
  - 仅在语音会话空闲（非 streaming/draining）时动作；
  - 自动重启一次遥控器引擎（`RestartRemoteEngine`），让 Windows 重新枚举 HOGP 链路；
  - 每次连接最多自动重启 3 次、间隔 ≥90 秒；监听器恢复后计数清零，避免重连抖动导致重启循环。
- UI：遥控器页工具栏常驻「按键通道 · 就绪/未绑定」；未绑定时显示「修复」按钮（手动重启引擎），
  且接收测试会直接阻止并提示先修复，避免误导性的“测试结束：收到 0 个按键”。
- 日志：`remote listener unbound for …s … restarting engine … attempt n/3`；
  恢复后 `remote listener channel recovered; heal counter reset`。

## 微信输入法语音恢复：音频采集会话证据（2026-11-26）

- 现象：按住遥控器语音键说 1–3 秒，微信输入法自己结束；BLE/ATVV 音频链路正常。
- 根因：微信输入法 3.x 上 ConsentStore 开麦时间戳（≥2.1.4.6 不写）与 WTYP 存活标记
  （3.0.3.11 不发）两个判据同时失明；`wetype_check` 把「检测不到」当成「未响应」，
  于是 cycle 配置并释放/重按 Ctrl+Win（约 0.7s / 2.9s），把进行中的听写拆掉。
- 直接证据：微信输入法开始语音时，Windows 音频会话层会出现归属于 WeType 的**采集会话**
  （CABLE Output；实测 153–779ms 变 Active，枚举 6–10ms/次）。实现见
  `external/rust/crates/sayall-windows/src/voice_wetype_audio.rs`。
- 判定（`merge_verdict`）：
  - 看到 WeType 采集会话 / WTYP 标记前进 / ConsentStore Observed → `Reacted`（禁止恢复）；
  - 音频枚举完整且本版本能力可靠、宽限期 1500ms 内无采集会话 → `NotReacted`（允许恢复）；
  - 其它不可靠情况 → `Unknown`（只记日志，不动作）。
- 能力记忆：`%APPDATA%\app.getsayall.remote-mic.windows\voice-audio-capability.json`
  （按微信输入法版本绑定，版本变化重置；首次使用先学习，正常语音一次后建立）。
- 恢复前二次确认：`RetryVoiceChord` 释放旧和弦前再枚举一次音频会话；发现迟到启动的采集
  会话或枚举失败时跳过重按。恢复阶梯保持「按住内最多 3 轮」。
- 日志：`voice_audio capability_loaded/learned`、`voice_audio result=reacted|marker`、
  `wetype_check verdict=... audio=active|absent|unavailable`、
  `chord_retry skipped reason=audio_reacted|audio_unavailable`。
- 探针：`external/rust/crates/sayall-windows/examples/voice_session_probe.rs`
  （只读会话元数据，不读音频内容）。

## 映射保存的坑

`ButtonMappings` 要求每个按键同时有 `single/double/long` 三个槽位。
给新按键只写一个槽位会导致 `serde_json` 反序列化失败，sidecar 返回 `invalid mappings`。
主应用 `RemoteEngineClient.SaveMappingAsync` 会先补齐缺失槽位为 `{"type":"disabled"}`。

## 语音工具与热键

- `voice.tool.set` 只保存工具名；按住说话的实际 key chord 由宿主 `RemoteEngineClient.SetVoiceToolAsync`
  在同一次操作中通过 `voice.hotkey.set` 写入：
  - `doubao` / `vokie` → `["right_alt"]`；
  - `wechat` → `["left_control", "left_windows"]`；
  - `other` → 不自动改 chord，保留用户当前配置。
- 页面读取 `voice.hotkey.get` 显示“已保存的按住说话快捷键”，不再只显示硬编码提示。

## 冲突与生命周期

- 原版 `sayall-windows-app` 不能同时运行（BLE/钩子/Helper 冲突）。
- 宿主退出发送 `system.shutdown`；3 秒未退则 `Kill(entireProcessTree: true)`。
- 接收测试结束/离开页面必须 `mappings.suspend false`。

## 电量刷新链路（2026-11-02 修订）

1. **设备侧**：连接后订阅 GATT Battery Service `0x180F` / Battery Level `0x2A19` 的
   notify，并立即读一次初始值；notify 到达即解析为 0–100 的单字节百分比。
2. **活动触发的一次性刷新**：不做定时轮询。连接期间记录最近一次遥控器活动
   （语义按键/语音边沿，raw input 回调 → `WorkerMessage::RemoteActivity`）：
   - 活动间隔 ≥ **5 分钟**（闲置后首次使用），且距上次电量读取 ≥ **5 分钟**（冷却）时，
     延迟 **1.5 秒**（等被按键唤醒的 BLE 链路稳定）主动读一次 `0x2A19`；
   - 连续按键、刚被 notify/初始读/上一轮活动读更新过，都不会重复查询；
   - 未订阅（设备无 BAS）时仍走原有的 60 秒 Windows 属性缓存轮询。
   - 纯决策函数 `battery::activity_should_refresh`，单测覆盖闲置/冷却/首次使用。
3. **门控**：读数带连接代次，旧连接迟到读数丢弃；只有
   `AwaitingCapabilities/Ready/Streaming/Draining` 阶段接受；断开/重连时清空。
4. **上行**：1 Hz snapshot 带 `connection.batteryLevel`；宿主 `RemoteEngineClient`
   在电量等稳定字段变化时触发 `Changed`，页面随即刷新；另有 15 秒全量兜底刷新。
