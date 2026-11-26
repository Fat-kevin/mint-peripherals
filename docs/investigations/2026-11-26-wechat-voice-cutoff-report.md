# 微信输入法「按住说话」被截断：调研报告与方案再评估

> 日期：2026-11-26
> 状态：调研完成，结论与推荐方案已定；实施文档见 `2026-11-26-wechat-voice-cutoff-implementation.md`
> 关联：遥控器语音链路、`external/rust/crates/sayall-windows`、sidecar、微信输入法 3.0.3.11

## 1. 摘要

用户按住遥控器语音键说 1–3 秒，输入法自己结束听写。经诊断日志取证，根因确定：
sidecar 为「微信输入法热键钩子休眠」准备的**破坏性恢复阶梯**，在无法判断输入法是否响应时
误判为「未响应」，于约 0.7s 切换输入法配置、约 2.9s **释放并重按 Ctrl+Win**，把正在进行的
听写拆掉。误判的根源是该微信输入法版本上两个既有判据同时失明：

1. ConsentStore 开麦时间戳（微信输入法 ≥2.1.4.6 起不写）；
2. WTYP 0xFC 自注入存活标记（3.0.3.11 不再出现；键盘钩子本身正常）。

在初版方案（「能力位 + 判据不可靠时不动作」）之上，本次进一步调研了「直接观测输入法是否
真的在录音」的可行性，并用真机探针验证：**微信输入法开始语音时会在 Windows 音频会话层
出现一个归属于 WeType 的采集会话（CABLE Output），可稳定枚举、可归因、延迟 153–779ms。**
因此推荐比初版更优雅的方案：

> 用**音频采集会话**作为「输入法是否真的在响应」的主判据；只有直接证据确认输入法没响应，
> 才允许执行破坏性恢复；其余情况一律不动作。老版本微信的自动恢复能力保留，3.x 也不再误伤。

## 2. 现场与取证

### 2.1 环境

| 项 | 值 |
|---|---|
| 应用 | 外设中枢 1.1.1（安装版） |
| 遥控器 | MI RC（RC003），BLE 已连接，电量 51–54% |
| 语音工具 | `voice_input_tool=wechat` |
| 按住说话 | `left_control + left_windows` |
| 注入路径 | `send_input`（2 键和弦无法走报告层单键合成） |
| 微信输入法 | 3.0.3.11（`HKLM\...\WOW6432Node\...\Uninstall\WeType`） |

### 2.2 诊断日志时序（本地时间 08:25）

| 时间 | 日志（节选） | 说明 |
|---|---|---|
| 15.144 | `voice_attempt tool=wechat path=send_input synth=off` | 按下语音键 |
| 15.235 | `chord_press result=ok` | 注入 Ctrl+Win 并保持 |
| 15.936 | `wetype_check reacted=false attempt=0 reviving` | 700ms 误判未响应 |
| 16.010 | `wetype_revive result=ok attempt=0` | 切换微信输入法配置 |
| 18.179 | `chord_retry result=ok attempt=1` | 释放+重按（约 2.9s），听写被拆 |
| 18.881 | `wetype_check reacted=false attempt=1 reviving` | 再次误判 |
| 20.290 | `chord_release ... reason=released` | 用户松键 |
| 21.950 | `wetype_check skipped_retry reason=session_ended` | 后续重试取消 |

旁证：

- `voice_verify ... mic=not_observed`（800ms/2501ms）：ConsentStore 判据失明；
- `wetype_check armed ... marker_baseline=0` 到结束仍无标记：WTYP 标记缺失；
- `suppressor_ask ... swallow=1` 成片：键盘钩子正常，排除「钩子没装上」；
- `audio_session begin/finish ... submitted_samples=79200` 无错误：ATVV 音频链路正常。

### 2.3 为什么判据会失明

- **ConsentStore**：注册表里只有历史版本 `wetype_update.exe` 的旧条目（2.1.2.12–3.0.1.35），
  3.x 的语音录音不在其中；`mic_observation_for("wetype")` 能读到旧条目但时间戳不再前进，
  得到 `NotObserved` 的假阴性。
- **WTYP 标记**：该版本不再注入 `0xFC + extra="WTYP"`；`is_wetype_marker` 永不触发。
- 两个判据都「知道自己在盲区」时，代码本应返回 `Unknown`；但 `NotObserved` 被当成
  确定失败（初版遗留判据），进入 `NotReacted`。

## 3. 根因模型

```
语音键按下
  ├─ ATVV 音频流：正常（与输入法无关）
  ├─ SendInput 注入 Ctrl+Win 并保持
  │     └─ 微信输入法是否响应？——3.x 上两个旧判据不可观测
  └─ wetype_check（700ms 判定）
        └─ 盲判 ⇒ 误判 NotReacted
              ├─ 切换输入法配置（扰动）
              └─ 释放+重按和弦（破坏进行中的听写）← 症状
```

**被违反的不变量**：会打断用户输入的破坏性动作，必须有正面证据证明目标未响应；
「检测不到」不能作为中断依据。

历史背景：恢复阶梯诞生于 2026-09-05 真机实测的「微信输入法热键钩子休眠」（TSF 激活但
热键钩子睡眠），当时用「cycle 配置 + 重放和弦」7/7 次救回。直接删除会让老版本微信的
休眠问题回归。

## 4. 更优雅方案调研

### 4.1 调研方法

对「如何直接观测微信输入法是否真的在录音」做了三层验证：

1. **公开协议/API 排查**：Windows 音频会话 API（`IAudioSessionManager2`）是系统公开接口，
   `wasapi` crate（项目现有依赖）已封装设备、会话管理器、会话状态与进程 id；
2. **可观测性探针**（`examples/voice_session_probe.rs`）：
   - 打开记事本 → 枚举音频会话基线 → 注入 Ctrl+Win 保持 → 每 150ms 枚举；
   - 只读会话元数据（状态/进程 id/标识），不读取音频内容；
3. **真机复测两轮**（应用 sidecar 同时在跑，环境与真实使用一致）。

### 4.2 探针结果

| 指标 | 结果 |
|---|---|
| 会话枚举耗时 | 6–10ms / 次（全部采集+渲染端点） |
| 语音开始 → WeType 采集会话 Active | 第 1 轮 +153ms，第 2 轮 +779ms |
| 采集会话归属 | `wetype_update.exe`（pid 20236；标识含 `Program Files\Tencent\WeType\3.0.3.11\wetype_update.exe`） |
| 端点 | `CABLE Output (VB-Audio Virtual Cable)` 采集端点，Active |
| 松键后 | 会话回到非活动（第 1 轮 156ms，第 2 轮 >2s 仍在收尾） |
| 与 sidecar 共存 | 正常；sidecar 自身的渲染会话在渲染端点（可按进程过滤） |

结论：**微信输入法是否在录音，是可被 Windows 公开 API 直接、快速、稳定观测的**，
且能精确归因到 WeType 进程（路径含 `Tencent\WeType`）。

### 4.3 候选方案对比

| 方案 | 做法 | 优点 | 缺点/新风险 | 结论 |
|---|---|---|---|---|
| A. 删除恢复阶梯 | 直接移除 auto-heal | 最简单，立刻止血 | 老版本微信「钩子休眠」回归（实测真实存在） | 不采纳 |
| B. 按版本号硬 gate | 微信 ≥2.1.4.6 禁用恢复 | 改动小 | 版本解析脆弱；未来版本行为变化会误伤/漏救 | 仅作辅助，不作主判据 |
| C. 能力位盲判（初版） | 判据不可靠 ⇒ 不动作 | 安全、改动小 | 3.x 永远无法自动恢复；无法正面观测 | 作为降级与兜底 |
| **D. 音频采集会话（推荐）** | 直接观测 WeType 是否打开采集会话 | 直接证据、版本无关、延迟低、可归因；可安全恢复 | 首次运行需学习；枚举失败/异常模式需降级；新增少量 COM 调用 | **采纳为主判据** |
| E. 延后/非破坏恢复 | 不在按住期间释放和弦，改为下一次按下前修复 | 绝不打断 | 需要提前 cycle（1.3–2.3s 才生效），下一次按下延迟大；或用户需重按 | 仅作 P2 备选 |
| F. 读微信输入法诊断日志文件 | 用 `%TEMP%\WeTypeVoiceDiagnostic_*.log` 变化判定 | 无需音频 API | 第三方私有日志、缓冲写入不可靠、隐私边界 | **否决** |
| G. 检测输入法语音浮窗 | UI Automation/窗口枚举 | 直观 | 窗口类名/结构随版本变化，脆弱 | 否决 |

### 4.4 为什么选 D（而不是 C/F/B）

- D 观测的是**用户真正在意的事实**（是否在录音），不是代理信号；微信输入法再升级，
  只要还在录音，采集会话就存在；
- D 同时覆盖正面（录音中 ⇒ 绝不打断）和负面（确认没录音 ⇒ 恢复才是安全的）两种决策；
- C 只是「盲时不动作」，能止血但永久失去 3.x 的自动恢复；D 在保留安全性的同时把能力拿回来；
- F 违反「只读公开协议/公开 API」的隐私边界，且实测缓冲写入不可靠；G 太脆。

## 5. 推荐方案（v2）

### 5.1 证据通道与判定优先级

| 优先级 | 证据 | 结论 |
|---|---|---|
| 1 | 音频采集会话出现 WeType 活动会话 | **Reacted**（输入法在工作，禁止一切恢复动作） |
| 2 | WTYP 标记计数前进 | Reacted（老版本；与版本解耦的正面证据） |
| 3 | ConsentStore 开麦观测为 Observed | Reacted（豆包/Vokie/旧版微信） |
| 4 | 音频枚举可用 + 本版本能力可靠 + 宽限期无活动会话 | **NotReacted**（确认没录音，允许恢复） |
| 5 | 标记能力可靠但当前无标记 | NotReacted（老版本兜底） |
| 6 | 开麦能力可靠且 NotObserved | NotReacted（旧版兜底） |
| 其他 | 判据不可用/不可靠 | **Unknown**（不动作，只记日志） |

### 5.2 关键时序（比初版更保守）

- 按下后检查线程每 **150ms** 轮询音频会话，一旦发现 WeType 采集会话立即判 Reacted
  （实测最慢 779ms，宽限期取 **1500ms**）；
- 宽限期结束仍无活动会话，且能力可靠、枚举完整 → NotReacted → 进入恢复阶梯
  （cycle 配置 → 等 2s → 重按）；
- **重按前二次确认**：`RetryVoiceChord` 释放旧和弦前立即再枚举一次；
  若发现 WeType 采集会话（迟到的启动）则跳过重按并判 Reacted——避免误伤慢启动的会话；
- 保持「按住内最多 3 轮、两次之间 ≥2s」的上限，防止异常循环。

### 5.3 能力可靠性（只影响「缺席是否可信」）

- `audioReliable`：当前微信输入法版本下，曾亲眼观测到 WeType 采集会话；
- 持久化到 sidecar 状态目录 `voice-audio-capability.json`，按微信输入法版本绑定：
  `{ "schema_version":1, "wechat_ime_version":"3.0.3.11", "audio_reliable":true, "observed_at":"..." }`；
- 版本变化或读取失败 → 重置为 false；版本探测失败 → 只在内存学习、不落盘；
- 首次使用（无历史）遇到「确实没响应」时只会 Unknown/不动作（安全侧），
  一次正常语音后即可建立可靠性，之后的钩子休眠可自动恢复。

### 5.4 降级与兜底

- 音频枚举任何设备/会话失败 → 视为不可用（不信任缺席），回落到初版能力位盲判；
- 微信输入法未安装/未运行 → 音频不可用，回落到盲判；
- 旧版微信仍可用 WTYP 标记/ConsentStore 作为正面证据与兜底恢复；
- 所有决策带结构化日志（见实施文档），不读音频内容、不记窗口内容。

## 6. 改完之后会不会带来新问题？（风险与对策）

| # | 新风险 | 影响 | 对策 |
|---|---|---|---|
| 1 | 音频会话不可见（独占模式/异常捕获路径） | 缺席被误信 → 恢复可能打断会话 | 仅当「曾亲眼观测到」才信任缺席；重按前二次确认；枚举失败一律 Unknown |
| 2 | 慢启动（输入法 >1.5s 才开麦） | 可能在启动前进入恢复 | 宽限 1500ms + 重按前二次确认；能力可靠才恢复；实测 153–779ms |
| 3 | 旧采集会话残留 Active | 误判 Reacted，仅少一次恢复 | 安全侧（不动作），可接受 |
| 4 | 枚举耗时/频率 | 每次语音少量 CPU（6–10ms/次，约 10 次/秒上限） | 仅按住期间轮询、150ms 间隔、会话结束即停 |
| 5 | COM/WASAPI 初始化失败 | 判据不可用 | 失败即 Unknown；不阻塞语音主流程 |
| 6 | 微信输入法升级改变采集路径/进程 | 能力可能失效 | 能力按版本绑定并重置；首次正常语音重新学习 |
| 7 | 进程名/路径判定误伤其他腾讯应用 | 误判 Reacted（仅抑制恢复） | 只认路径含 `Tencent\WeType`；另用进程路径二次校验 |
| 8 | 首次安装即遇到钩子休眠 | 该次无法自动恢复 | 预期行为：提示手动重按/打开输入法；一次正常语音后恢复能力 |
| 9 | 新增状态文件损坏/不可写 | 能力丢失 | 读取失败按不可靠处理，仅影响恢复能力，不影响语音 |
| 10 | 维护成本上升 | 一个平台相关模块 | 模块边界清晰、纯函数可测、探针工具留档 |

## 7. 与初版方案（已向用户说明）的差异

初版：判据不可靠 ⇒ 永不动作（3.x 不被打断，但 3.x 也永远无法自动恢复）。
v2：增加「音频采集会话」作为主判据，**能确认没录音时才允许恢复**，因此：

- 3.x 正常语音：不再被打断（与初版一致）；
- 3.x 钩子休眠：在能力建立后可自动恢复（初版做不到）；
- 老版本微信：行为不变，仍有兜底恢复；
- 任何不确定场景：仍然不动作（安全侧）。

## 8. 实施与验证

实施细节（文件、函数、常量、测试、打包步骤、回滚）见
[`2026-11-26-wechat-voice-cutoff-implementation.md`](2026-11-26-wechat-voice-cutoff-implementation.md)。
验证以「真机按住 5–10 秒不中断 + 日志无 chord_retry」为验收线，并保留探针工具用于现场复测。

## 9. 验收标准

1. 微信输入法 3.0.3.11：按住语音键 5–10 秒，听写不中断；日志有 `voice_audio reacted`，
   无 `wetype_revive`/`chord_retry`；
2. 存在可靠能力且模拟缺席（单元测试覆盖）时，恢复阶梯只在音频确认无采集会话时执行；
3. 音频枚举失败/未知时，任何路径都不执行释放/重按；
4. `cargo test`（sayall-windows + 上游测试）与 Core 测试全绿；x64 Release 构建 0 警告；
5. 覆盖安装 1.1.2 后设置/映射/备份保留；回滚 1.1.1 可用。

## 附录 A：探针输出（节选）

```
baseline sessions=8 snapshot=10ms wetype_active=false
wetype capture active at +153ms
released; wetype inactive after 156ms
=== round 2 ===
baseline sessions=8 snapshot=6ms wetype_active=false
wetype capture active at +779ms
released; wetype inactive after >2000ms
```

## 附录 B：相关代码位置

| 逻辑 | 文件 |
|---|---|
| 微信输入法判定/恢复阶梯 | `external/rust/crates/sayall-windows/src/wetype_revive.rs` |
| 语音会话调度与检查 | `external/rust/crates/sayall-windows/src/ble.rs` |
| WTYP 标记 | `key_suppressor.rs` / `key_host.rs` |
| 音频会话 API | `wasapi` crate（项目现有依赖） |
| 探针 | `external/rust/crates/sayall-windows/examples/voice_session_probe.rs` |
| sidecar 状态目录 | `sidecars/sayall-engine/src/main.rs` |
