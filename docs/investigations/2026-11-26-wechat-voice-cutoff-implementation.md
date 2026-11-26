# 微信输入法语音截断修复：实施文档（1.1.2）

> 依据：[`2026-11-26-wechat-voice-cutoff-report.md`](2026-11-26-wechat-voice-cutoff-report.md)
> 目标：不再误伤按住说话；在可确认输入法未响应时保留自动恢复；任何不确定场景不动作。
> 版本：应用 1.1.2 / sidecar 同步重建；覆盖安装。

## 1. 改动范围

| 区域 | 文件 | 变更 |
|---|---|---|
| sidecar 核心 | `external/rust/crates/sayall-windows/src/voice_wetype_audio.rs`（新增） | 音频采集会话枚举、WeType 归因、版本探测、能力持久化 |
| sidecar 核心 | `external/rust/crates/sayall-windows/src/ble.rs` | 检查线程改为音频轮询 + 宽限期；恢复前二次确认；结构化日志 |
| crate 导出 | `external/rust/crates/sayall-windows/src/lib.rs` | 暴露 `initialize_voice_audio_capability` |
| sidecar 入口 | `sidecars/sayall-engine/src/main.rs` | 启动时初始化能力文件 |
| 探针（保留） | `external/rust/crates/sayall-windows/examples/voice_session_probe.rs` | 真机可观测性验证工具 |
| 应用 | `src/DeviceCenterPreview.App/DeviceCenterPreview.App.csproj`、`installer/DeviceCenterPreview.iss` | 版本 1.1.2 |
| 文档 | `docs/investigations/*`、`docs/internals/remote-sidecar.md`、`docs/features/remote.md`、`docs/history/implementation-log.md`、`docs/README.md` | 变更说明与索引 |

不改变：BLE/ATVV 协议、映射语义、注入路径选择、应用 UI、数据格式（新增状态文件向后兼容）。

## 2. 常量与时序

| 常量 | 值 | 含义 |
|---|---|---|
| `REACTION_GRACE_MS` | 1500 ms | 按下后等待音频证据的最长时间（实测 153–779ms） |
| `AUDIO_POLL_INTERVAL_MS` | 150 ms | 轮询间隔；单次枚举实测 6–10ms |
| `WETYPE_CHECK_DELAY_MS` | 150 ms（替代原 700ms 首检） | 进入轮询的首次延迟 |
| `WETYPE_RETRY_SETTLE_MS` | `[2000, 3000, 5000]`（不变） | 恢复阶梯轮次间隔 |
| `WETYPE_RETRY_MAX_ATTEMPT` | 3（不变） | 最多恢复轮次 |

流程：

```
StreamStarted（注入和弦）
  └─ spawn_wetype_check(attempt=0)
       每 150ms 轮询（最多 1500ms）：
         - 会话结束/纪元变化 → 结束
         - WeType 采集会话 Active → note_capability；日志；Reacted，结束
         - WTYP 标记前进 → 日志；Reacted，结束
       宽限期到：
         - 再取一次音频状态（迟到启动保护）
         - 合并判定（见 §3.3）
         - Unknown → 日志，不动作
         - Reacted → 不动作
         - NotReacted → cycle 输入法配置 → 2s 后 RetryVoiceChord
RetryVoiceChord：
   - 校验流式/纪元（不变）
   - 释放旧和弦前再枚举一次音频：
       Active      → 跳过重按，日志 chord_retry skipped reason=audio_reacted
       枚举失败    → 跳过重按，日志 chord_retry skipped reason=audio_unavailable
   - 重按 → spawn_wetype_check(attempt+1)
```

## 3. `voice_wetype_audio.rs` 设计

### 3.1 数据结构

```rust
#[derive(Clone, Debug)]
pub(crate) struct AudioSessionInfo {
    pub endpoint: String,
    pub session_id: String,
    pub pid: u32,
    pub active: bool,
}

pub(crate) struct WechatCaptureState {
    pub active: bool,     // 存在归属于 WeType 的活动采集会话
    pub available: bool,  // 所有采集端点的会话枚举都成功（缺席才可信）
    pub endpoint: Option<String>,
    pub pid: Option<u32>,
}
```

### 3.2 函数

```rust
/// 枚举所有活动采集端点的音频会话；available=false 表示枚举不完整。
pub(crate) fn capture_sessions() -> (Vec<AudioSessionInfo>, bool);

/// WeType 采集会话是否活动（直接证据）+ 枚举是否可用。
pub(crate) fn wechat_capture_state() -> WechatCaptureState;

/// 采集会话是否归属微信输入法：会话标识含 `\Tencent\WeType\`；
/// 回退：pid 对应进程名含 `wetype`（ToolHelp 快照）。
fn is_wechat_ime_session(session: &AudioSessionInfo) -> bool;

/// 微信输入法版本（注册表 Uninstall\WeType\DisplayVersion）。
pub(crate) fn wechat_ime_version() -> Option<String>;

/// 判定合并（纯函数，可单测）。
pub(crate) fn merge_verdict(
    base: WetypeReaction,     // 现有 marker/mic 判定
    audio_active: bool,
    audio_available: bool,
    audio_reliable: bool,
) -> WetypeReaction;

/// 能力持久化（供 sidecar main 调用）。
pub fn initialize_voice_audio_capability(path: std::path::PathBuf);
fn write_capability(path: &Path, version: &str, reliable: bool);
fn load_capability(path: &Path, version: Option<&str>) -> bool;
```

实现要点：

- 枚举只用 **capture** 端点（WeType 采集在 CABLE Output 采集端点；不涉及 sidecar 的 render 会话）；
- `wasapi::initialize_mta()` 在枚举线程调用（每次枚举前调用，幂等泄漏可接受，进程生命周期内）；
- `DeviceEnumerator::new()` → `get_device_collection(&Direction::Capture)` → 逐设备
  `get_iaudiosessionmanager()` → `get_audiosessionenumerator()` → 逐会话
  `get_state()`（`Active`）、`get_process_id()`、`get_session_identifier()`；
- 任一设备/管理器/枚举器/会话读取失败都置 `available=false`（但保留已读到的会话）；
- 归因：标识小写包含 `tencent\\wetype`（覆盖 `\Device\HarddiskVolumeN\Program Files\Tencent\WeType\...`），
  否则 pid≠0 时用 ToolHelp 快照取 exe 名，含 `wetype` 视为 WeType；
- 不记录完整会话标识到日志（只记录端点名/pid/是否活动），避免日志噪音与路径泄露；
- `merge_verdict` 规则：

```text
audio_active                                  => Reacted
base == Reacted                               => Reacted
audio_available && audio_reliable             => NotReacted（音频确认无会话）
otherwise                                     => base
```

### 3.3 能力持久化

文件：`<state_dir>/voice-audio-capability.json`（`%APPDATA%\app.getsayall.remote-mic.windows\`）。

```json
{
  "schema_version": 1,
  "wechat_ime_version": "3.0.3.11",
  "audio_reliable": true,
  "observed_at_unix": 1795627515
}
```

规则：

- `initialize_voice_audio_capability(path)`：读文件；若 `schema_version=1`、`audio_reliable=true`、
  且（当前版本未知 **或** `wechat_ime_version` 等于当前版本）→ 置位；否则重置为 false；
  保存当前版本到全局变量；
- 版本探测成功时：能力变化/版本变化写盘（`temp + rename` 原子写）；版本探测失败：只内存学习，不落盘；
- `note_wechat_audio_observed()`：置位 `AUDIO_RELIABLE=true`，仅首次变化时落盘 + `gatt_note`；
- 读取/解析/写盘失败、版本未知或版本变化时一律按不可靠处理（安全侧），不影响语音主流程；
- 版本变化时把新版本写回文件（`audio_reliable=false`），避免旧版本信任残留。

## 4. `ble.rs` 集成

### 4.1 `spawn_wetype_check`

保留签名，重写线程体：

1. 循环 `attempt` 次（每 150ms）：
   - 取 `snapshot.voice_state == Streaming` 与 `voice_session_epoch == epoch`；不满足 → `wetype_check skipped reason=session_ended|session_replaced`，返回；
   - `wechat_capture_state()`：`active` → `note_wechat_audio_observed()`，`gatt_note("voice_audio result=reacted latency_ms=... endpoint=... pid=...")`，返回；
   - `wetype_marker_count() > marker_baseline` → `voice_audio` 不适用（标记证据），`gatt_note("wetype_check reacted=true evidence=marker latency_ms=...")`，返回；
2. 宽限期结束：再取一次 `wechat_capture_state()`（迟到启动保护）；`active` → 同上返回；
3. `wetype_reaction(baseline, marker_baseline)` 得 `(base, evidence, mic)`；
4. `verdict = merge_verdict(base, audio.active, audio.available, wechat_audio_reliable())`；
5. 日志：`wetype_check verdict={:?} base={:?} evidence={} mic={} audio=active|absent|unavailable audio_reliable={} attempt={} epoch={}`；
6. `Reacted` → 返回；`Unknown` → `gatt_note("wetype_check skipped reason=evidence_unavailable action=none ...")` 返回；
7. `NotReacted` → 原恢复阶梯（attempt 上限、cycle、`device` 等逻辑不变）。

注意：原 `WETYPE_CHECK_DELAY_MS=700` 常量的含义改为轮询首延迟 150ms；文档/注释同步。

### 4.2 `WorkerMessage::RetryVoiceChord`

在 `send_input.release(old)` 之前插入：

```rust
let capture = crate::voice_wetype_audio::wechat_capture_state();
if capture.active {
    crate::voice_wetype_audio::note_wechat_audio_observed();
    gatt_note(format!("chord_retry skipped reason=audio_reacted epoch={epoch}"));
    continue;
}
if !capture.available {
    gatt_note(format!("chord_retry skipped reason=audio_unavailable epoch={epoch}"));
    continue;
}
```

其余校验与重按逻辑不变；重按后仍 `spawn_wetype_check(attempt+1)`。

### 4.3 日志字段约定

新增/变更日志（gatt_note，进入 `SAYALL_GATT_LOG` 诊断文件或宿主 stderr）：

- `voice_audio capability_loaded version=... reliable=...`
- `voice_audio capability_reset reason=version_changed|unreadable`
- `voice_audio capability_learned version=...`
- `voice_audio result=reacted latency_ms=... endpoint=... pid=...`
- `voice_audio result=absent covered=true audio_reliable=...`
- `voice_audio result=unavailable reason=...`
- `wetype_check verdict=... base=... evidence=... mic=... audio=... action=...`
- `chord_retry skipped reason=audio_reacted|audio_unavailable`

## 5. sidecar 入口

`sidecars/sayall-engine/src/main.rs`：

```rust
let state_dir = state_dir();
sayall_windows::initialize_voice_audio_capability(state_dir.join("voice-audio-capability.json"));
```

放置在 `state_dir()` 之后、`apply_saved_settings`/`start_raw_input` 之前（BLE 启动前）。

## 6. 测试

### 6.1 Rust 单元测试（`cargo test -p sayall-windows`）

- `is_wechat_ime_session`：WeType 路径（不同卷/大小写）→ true；`Weixin.exe`、`QQMusic.exe`、
  空标识 → false；
- `merge_verdict` 真值表（4×2×2×2 关键分支）：
  - audio_active → 永远 Reacted；
  - base=Reacted → Reacted；
  - audio 可用且可靠、无活动会话 → NotReacted；
  - audio 不可用/不可靠 → 保留 base（Unknown/NotReacted）；
- 能力文件：写读往返；版本不一致重置；损坏 JSON 回 false；不可写路径不 panic；
- 现有 `sayall-core` 测试保持通过。

### 6.2 真机探针

`examples/voice_session_probe.rs`（研究时验证）：

- 基线 8 个会话、枚举 6–10ms；
- 注入 Ctrl+Win 后 WeType 采集会话在 153–779ms 变 Active；
- 松键后回到非活动。

### 6.3 应用侧

- `dotnet test tests/DeviceCenterPreview.Core.Tests`（11 项）；
- x64 Debug/Release 构建 0 警告；
- 覆盖安装后按住语音键 5–10 秒：不中断；诊断日志无 `wetype_revive`/`chord_retry`。

## 7. 构建与打包步骤

```powershell
# 0) 退出运行中的 app
# 1) sidecar（需要 cargo）
$env:PATH = "$env:USERPROFILE\.cargo\bin;$env:PATH"
cargo build --release --manifest-path sidecars/sayall-engine/Cargo.toml
cargo test -p sayall-windows --manifest-path external/rust/Cargo.toml
# 2) 应用
dotnet test tests/DeviceCenterPreview.Core.Tests/DeviceCenterPreview.Core.Tests.csproj
dotnet publish src/DeviceCenterPreview.App/DeviceCenterPreview.App.csproj -c Release -p:Platform=x64 `
  -r win-x64 --self-contained true -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false -o publish/app
# 3) 安装器
& "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" installer/DeviceCenterPreview.iss
# 4) SHA-256 + 发布说明 + 快速启动验证
```

产物：`dist/DeviceCenterPreview-Setup-1.1.2.exe` + `.sha256` + `release-notes-v1.1.2.md`。

## 8. 验收清单

- [ ] 3.0.3.11 按住 5–10s 不中断，日志 `voice_audio result=reacted`，无 `chord_retry`；
- [ ] 无音频证据/枚举失败时绝不 release/repress；
- [ ] 单元测试与 Core 测试全绿；Release 0 警告；
- [ ] 安装 1.1.2 覆盖旧版，设置/映射/备份保留；
- [ ] 探针工具可用于现场复测（保留示例不进入发布包）。

## 9. 回滚

- 保留 1.1.1 安装包；`voice-audio-capability.json` 删除或缺失时视为「能力不可靠」，
  行为回落到安全侧（不动作），不影响启动；
- sidecar 可用旧版 exe 替换验证（发布包不自带升级逻辑）。

## 10. 文档更新清单

- 本文件 + 调研报告；
- `docs/internals/remote-sidecar.md`（音频证据与恢复策略）；
- `docs/features/remote.md`（行为变化：正常按住不再被打断；首次使用/极端情况的手动兜底）；
- `docs/history/implementation-log.md`（本次修复条目）；
- `docs/README.md`（investigations 索引）；
- 初版设计文档 `2026-11-26-wechat-voice-cutoff.md` 已删除（由本文件 + 调研报告取代）。

## 11. 实施结果（2026-11-26 完成）

- 新增 `voice_wetype_audio.rs`（采集会话枚举/归因、版本探测、能力持久化、`merge_verdict`）
  与探针示例 `examples/voice_session_probe.rs`；
- `spawn_wetype_check` 改为 150ms 轮询、1500ms 宽限期；`RetryVoiceChord` 释放前二次确认；
- 能力文件 `voice-audio-capability.json`（按版本绑定；版本未知/变化时不信任历史）；
- sidecar 启动日志验证：`voice_audio capability_loaded version=3.0.3.11 reliable=false`；
- 测试：sayall-windows 358 passed / 11 ignored（含新增 4 项单测），sayall-core 与 Core 测试全绿；
- 发布：`dist/DeviceCenterPreview-Setup-1.1.2.exe`（83 MB，未签名）+
  `dist/DeviceCenterPreview-Setup-1.1.2.exe.sha256`
  （`543736dced09ea9d244e8cee59a26ec757a097b6075d91bfe5c25c87752b9083`）。
