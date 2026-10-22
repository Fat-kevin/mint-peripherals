# RemoteMicActivationProbe

测试「遥控器麦克风是否只能由语音键启动」。这是硬件诊断工具，不进入安装包。

## 它测什么

工具直接连接遥控器的 ATVV 服务，记录：

| 通道 | 判读 |
|---|---|
| control `0x08` | 遥控器请求主机打开麦克风 |
| control `0x04` | 遥控器开始推流（session id 在第 4 字节） |
| control `0x00` | 遥控器停止推流 |
| audio 任意通知 | 确实有麦克风音频数据到达 |

它按阶段记录：

1. `baseline`：不按键，确认没有自发推流；
2. `voice_key`：按住语音键，作为阳性对照；
3. `key:*`：逐个按普通按键，不按语音键；
4. `host_mic_open`：主机主动发 `MIC_OPEN`，不按任何键。

## 前置条件

- 遥控器已在 Windows 中配对；
- 已退出「外设中枢」和 `sayall-engine`。Windows 同一时间只允许一个 GATT 客户端占用 ATVV；
- 建议先关闭原版 SayAll，避免 BLE/键盘钩子冲突；
- 工具只写 ATVV 的 `GET_CAPABILITIES`、`MIC_OPEN`、`MIC_CLOSE`，不写按键映射，不改设备配置。

## 构建与运行

```powershell
# 在 device-center-preview 根目录
dotnet build tools/RemoteMicActivationProbe/RemoteMicActivationProbe.csproj -c Debug -p:Platform=x64

# 默认从原版 settings.json 读取已配对遥控器地址
tools/RemoteMicActivationProbe/bin/x64/Debug/net8.0-windows10.0.19041.0/win-x64/RemoteMicActivationProbe.exe
```

或使用包装脚本（会自动构建并切到项目根目录）：

```powershell
powershell -ExecutionPolicy Bypass -File tools/RemoteMicActivationProbe/run.ps1
```

只想测「主机协议能否请求流开始」、不需要按任何键：

```powershell
powershell -ExecutionPolicy Bypass -File tools/RemoteMicActivationProbe/run.ps1 --host-open-only
```

常用参数：

```text
--mac AA:BB:CC:DD:EE:FF     手动指定地址（报告会脱敏成 **:**:**:**:EE:FF）
--keys "返回,菜单,音量+"     只测指定普通按键
--voice-seconds 8           给语音键更长的按压窗口
--out .artifacts/my-run.json
```

## 怎么读结果

先看 `evidenceQuality`：

- `voice_audio_positive_control_present`：语音键阶段真的收到了音频，后续结论可信；
- `voice_stream_positive_control_present_but_no_audio`：只看到 `0x08`/`0x04`，没有音频，阳性对照不完整；
- `voice_positive_control_missing`：语音键阶段什么都没有，不能下结论。

**只有真实 audio 包才算“麦克风在传输”；单独 `0x04` 只算“流开始”。**

再看 `verdict`：

- `voice_key_only`
  - 语音键阶段有真实音频；
  - 所有普通按键和主机 `MIC_OPEN` 都没有音频，也没有其他流开始；
  - 结论：**只有语音键能启动麦克风传输**。
- `voice_key_transmits_audio_only_but_stream_can_start_without_audio`
  - 只有语音键阶段有真实音频；
  - 主机 `MIC_OPEN` 或某个普通按键让遥控器回过 `0x04`，但没有音频；
  - 结论：**语音键仍是唯一实际传输入口**，但协议/按键能制造“流开始”状态。
- `voice_key_and_other_paths_transmit_audio` / `voice_key_and_other_button_transmit_audio` / `voice_key_and_host_protocol_transmit_audio`
  - 语音键与另一条路径都产生了真实音频；语音键不是独占入口。
- `other_button_transmits_microphone_audio`
  - 某个 `key:*` 阶段出现音频包；
  - 结论：**别的按键也能启动麦克风传输**；`otherActivatedKeys` 会列出具体按键。
- `host_protocol_transmits_microphone_audio`
  - 没按键时主机发 `MIC_OPEN` 就出现音频包；
  - 结论：**协议层可以绕过语音键启动真实麦克风传输**。
- `other_button_and_host_protocol_transmit_audio`
  - 两条路径都产生了真实音频。
- `host_protocol_starts_stream_without_audio`
  - 主机 `MIC_OPEN` 让遥控器回了 `0x04`，但 10 秒观察窗内没有音频包；
  - 结论：**协议能请求“流开始”，但不能证明麦克风在传输**。2026-10-21 的本机基线就是这一种。
- `other_button_starts_stream_without_audio`
  - 某个普通按键让遥控器回了 `0x04`，但没有音频包；需要继续确认固件是否只是请求了流。
- `other_button_requests_microphone_without_audio` / `host_protocol_requests_microphone_without_audio`
  - 只看到 `0x08` 或请求，没有 `0x04`/音频；不能证明实际传输。
- `voice_key_requests_stream_but_no_audio`
  - 语音键阶段只看到 `0x08`/`0x04`，没有音频；阳性对照不完整，不能下结论。
- `inconclusive_missing_voice_positive_control`
  - 语音键阶段什么都没出现；先确认遥控器已连接、按的是正确的语音键，否则本次不能下结论。

报告默认写到项目根的 `.artifacts/remote-mic-activation-<时间>.json`，包含每个阶段的控制帧十六进制、音频包数/字节数和最终判读。音频内容不落盘。

退出码：`0` 表示拿到了音频级结论；`2` 表示只看到 `0x08`/`0x04`、没有音频（证据不完整）；`3` 表示连接/前置条件失败。

## 代码层的既有事实

`external/rust/crates/sayall-core/tests/voice_activation.rs` 固定了主机侧行为：

- 只有 control `0x08` 会返回 `MicrophoneOpenRequested`；
- 但 `0x04` 不需要先收到 `0x08` 就会被接受为流开始，音频也会被接收。

因此「主机是否强制语音键先行」的答案是：**当前不强制**。硬件工具回答的是另一半问题：真实遥控器固件会不会在非语音键或主机 `MIC_OPEN` 时自己推流。

本机 2026-10-21 的 `--host-open-only` 结果：主机 `MIC_OPEN` 得到 `04 00 02 00`，10 秒内音频 0 包，`MIC_CLOSE` 后得到 `00 00`。因此主机协议能请求“流开始”，但**不能证明麦克风实际传输**；语音键阳性对照和普通按键阶段仍需人工跑完整测试。

## 清理

工具在退出前会尝试发 `MIC_CLOSE`、退订 control/audio 并释放 GATT 对象。遇到 `0x08` 时会自动回 `MIC_OPEN`，否则语音键阳性对照不会进入 `0x04/audio`。若进程被强制结束，重新打开一次「外设中枢」让 sidecar 重连即可。
