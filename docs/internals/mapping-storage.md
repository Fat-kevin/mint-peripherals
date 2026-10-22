# 内部：映射与配置存储

## 文件位置

```text
%APPDATA%\app.getsayall.remote-mic.windows\
├─ settings.json              # 音频端点、增益、语音工具、全按键等
├─ button-mappings.json       # 13 键 × 单击/双击/长按
├─ voice-hold-hotkey.json     # 按住说话热键
└─ other-voice-hotkey.json    # “其他工具”记忆（原版）
```

主应用与 sidecar 使用同一目录；不要另建副本。

## button-mappings.json 结构

```json
{
  "schemaVersion": 1,
  "enabled": true,
  "actions": {
    "ok": {
      "single": { "type": "shortcut", "chord": { "keys": ["enter"] } },
      "double": { "type": "disabled" },
      "long":   { "type": "disabled" }
    }
  }
}
```

- 键名：`power/up/down/left/right/ok/back/home/menu/volume_up/volume_down/volume_mute/tv`。
- 每个键必须同时存在 `single/double/long`；缺失会导致 sidecar 反序列化失败。
- 动作类型：
  - `disabled`
  - `shortcut`（`chord.keys`，如 `["control","c"]`）
  - `scroll`（`direction`、`steps`）
  - `mouse_click`（`kind`: left/right/double_left/middle）
  - `mouse_move`（`direction`、`distance`）
  - `open_app`（`target`）
  - `focus_input`
  - 宏/其他原版类型

## 保存链路

```text
RemotePage 选择功能
 → IPeripheralService.SaveRemoteMappingAsync
 → RemoteEngineClient.SaveMappingAsync
   ├─ mappings.config.get（读当前 JSON）
   ├─ 补齐该键缺失手势为 disabled
   ├─ 写入目标动作
   └─ mappings.save
 → sidecar set_button_mappings + save_mappings（原子写 .tmp → rename）
```

- 保存成功页面显示 `已自动保存 · HH:mm:ss`。
- 配置文件可由“打开配置文件夹”直接查看。
- 不保存到数据库；历史电量在 `history.db`，与映射无关。

## 恢复/排错

- 映射损坏：先备份 `button-mappings.json`，删除或恢复上一个版本，重启应用。
- `invalid mappings`：检查是否缺少手势槽位或动作类型/字段不合法。
- 原版 SayAll 与本应用不要同时修改同一份文件。
