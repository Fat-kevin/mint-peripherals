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
