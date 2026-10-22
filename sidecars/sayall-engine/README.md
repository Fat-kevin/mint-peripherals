# sayall-engine（遥控器无头引擎）

VigilWatt 设备中心的遥控器 sidecar：复用 `remote-mic-app-windows` 的
`sayall-windows` / `sayall-core`，不嵌入 Tauri/WebView2。

## 构建

```powershell
$env:PATH = "$env:USERPROFILE\.cargo\bin;$env:PATH"
cargo build --manifest-path device-center-preview/sidecars/sayall-engine/Cargo.toml
```

产物：`target/debug/sayall-engine.exe`。主应用构建目标会把它复制到
`DeviceCenterPreview.App/bin/.../win-x64/sidecars/`。

## 启动

引擎启动后向 stdout 输出一行：

```json
{"event":"ready","port":62176,"token":"...","protocol":1}
```

宿主连接 `127.0.0.1:<port>`，**先发 token 一行**，之后为 UTF-8 JSONL。

## 协议

请求：

```json
{"id":1,"cmd":"state.get","params":{}}
```

成功：

```json
{"id":1,"ok":true,"result":{...}}
```

失败：

```json
{"id":1,"ok":false,"error":{"code":"engine.error","message":"...","retryable":false}}
```

事件（1 Hz 快照）：

```json
{"event":"snapshot","seq":1,"runtime":{
  "platform":{...},"connection":{...},"audio":{...},"mappings":{...}
}}
```

按键边沿事件（用于接收测试/高亮，实时）：

```json
{"event":"button","button":"ok","pressed":true}
```

## 已实现命令

| 命令 | 参数 | 说明 |
|---|---|---|
| `system.hello` | — | 协议/版本 |
| `state.get` | — | 完整运行时快照 |
| `remote.scan` | — | 已配对遥控器列表（`id/name/model/token`） |
| `remote.connect` | `{deviceId}` | 连接；成功返回 `ConnectionSnapshot` |
| `remote.disconnect` | — | 断开 |
| `audio.list` | — | WASAPI 端点 |
| `audio.select` | `{endpointId}` | 选择渲染端点 |
| `voice.gain.set` | `{db}` | 0–24 dB |
| `voice.tool.get` / `voice.tool.set` | `{tool}` | doubao/wechat/vokie/other |
| `voice.hotkey.get` / `voice.hotkey.set` | `{chord}` | 按住说话热键 |
| `mappings.config.get` | — | 原始映射配置 |
| `mappings.get` | — | 映射快照 |
| `mappings.save` | `{mappings}` | 保存映射（每个键必须有 single/double/long） |
| `mappings.test` | `{button,trigger}` | 直接注入一个动作 |
| `mappings.suspend` | `{suspended}` | 接收测试：只观察、不注入 |
| `capture.enable` / `capture.status` | `{enabled}` | 全按键/增强捕获 |
| `raw.start` / `raw.stop` | — | 原始输入采集 |
| `diagnostics.logs` | — | 诊断日志 |
| `system.shutdown` | — | 排空后退出 |

## 启动关键点

`main.rs` 先启动 Raw Input，再调用 `key_host::start_global()` 启动 key host 子进程。
没有 key host 时 `gateActive=false`，引擎会记录手势但**不会注入按键**。退出时调用
`shutdown_ble_for_exit` 与 `key_host::shutdown_global`。

## 冲突与生命周期

- 原版 `sayall-windows-app` 与本 sidecar 不能同时运行（BLE/钩子/Helper 冲突）。
- 宿主退出时自动发送 `system.shutdown`；引擎 3 秒内未退出则宿主强杀进程树。
- 当前使用 loopback TCP + 一次性 token；后续可替换为命名管道，协议不变。
