using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DeviceCenterPreview.Core.Models;
using DeviceCenterPreview.Core.Services;

namespace DeviceCenterPreview.App.Services;

/// <summary>
/// 功 能： 遥控器 sidecar 客户端：启动 Rust 引擎、握手、JSONL 命令/事件、自动重连。
/// 2026/10/19 15:50:00 xliu
/// sidecar 用 127.0.0.1 随机端口 + 一次性 token；不嵌入 Tauri/WebView2。
/// </summary>
internal sealed class RemoteEngineClient : IDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(6);
    private CancellationTokenSource? _cts;
    private Task? _runTask;
    private bool _enabled;
    private readonly Dictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly object _writeGate = new();
    private readonly object _stateGate = new();
    private Process? _process;
    private TcpClient? _client;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private int _nextId;
    private string? _preferredRemoteId;
    private bool _disposed;
    private RemoteEngineState _state = RemoteEngineState.Offline;

    public event EventHandler? Changed;

    /// <summary>引擎线程推送的语义按键边沿。</summary>
    public event EventHandler<RemoteButtonEvent>? ButtonEventReceived;

    public RemoteEngineState State
    {
        get
        {
            lock (_stateGate)
            {
                return _state;
            }
        }
    }

    public void Start()
    {
        lock (_stateGate)
        {
            if (_enabled || _disposed)
            {
                return;
            }

            _enabled = true;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _runTask = Task.Run(() => RunAsync(token), token);
        }
    }

    /// <summary>模块关闭时真正停掉进程与读取循环；再次 Start 会重新启动。</summary>
    public void Stop()
    {
        CancellationTokenSource? cts;
        Task? runTask;
        lock (_stateGate)
        {
            if (!_enabled)
            {
                return;
            }

            _enabled = false;
            cts = _cts;
            _cts = null;
            runTask = _runTask;
            _runTask = null;
        }

        try
        {
            cts?.Cancel();
        }
        catch
        {
            // 已释放。
        }

        try
        {
            _writer?.Dispose();
            _reader?.Dispose();
            _client?.Dispose();
        }
        catch
        {
            // 读取循环可能已关闭。
        }

        if (_process is { HasExited: false })
        {
            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch
            {
                // 忽略退出竞态。
            }
        }

        _writer = null;
        _reader = null;
        _client = null;
        _process?.Dispose();
        _process = null;
        cts?.Dispose();
        runTask?.Wait(TimeSpan.FromSeconds(2));
        SetState(RemoteEngineState.Offline with { Phase = "disabled", Error = null });
    }

    public async Task<IReadOnlyList<AudioEndpointInfo>> GetAudioEndpointsAsync(CancellationToken token = default)
    {
        var result = await SendAsync("audio.list", null, token).ConfigureAwait(false);
        if (!result.HasValue || result.Value.ValueKind != JsonValueKind.Array)
        {
            AppLog.Warn("remote audio.list returned no array");
            return [];
        }

        var endpoints = new List<AudioEndpointInfo>();
        foreach (var endpoint in result.Value.EnumerateArray())
        {
            var id = String(endpoint, "id");
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var name = String(endpoint, "name") ?? id;
            var recommended = endpoint.TryGetProperty("isVirtualCableCandidate", out var flag) &&
                              flag.ValueKind == JsonValueKind.True;
            endpoints.Add(new AudioEndpointInfo(id, name, recommended));
        }

        AppLog.Info($"remote audio endpoints={endpoints.Count}");
        return endpoints;
    }

    public async Task<IReadOnlyList<KeyMapping>> GetKeyMappingsAsync(CancellationToken token = default)
    {
        var config = await SendAsync("mappings.config.get", null, token).ConfigureAwait(false);
        if (!config.HasValue || !config.Value.TryGetProperty("actions", out var actions) ||
            actions.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        var mappings = new List<KeyMapping>();
        foreach (var (key, label) in ButtonOrder)
        {
            if (!actions.TryGetProperty(key, out var entry))
            {
                continue;
            }

            foreach (var (gesture, gestureLabel) in Gestures)
            {
                if (entry.TryGetProperty(gesture, out var action))
                {
                    mappings.Add(new KeyMapping(label, gestureLabel, FormatAction(action), key, gesture,
                        ActionId(action)));
                }
            }
        }

        AppLog.Info($"remote mappings={mappings.Count}");
        return mappings;
    }

    public async Task<string?> GetVoiceToolAsync(CancellationToken token = default)
    {
        var result = await SendAsync("voice.tool.get", null, token).ConfigureAwait(false);
        return result is { } value && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    public async Task<WriteResult> SelectAudioEndpointAsync(string endpointId, CancellationToken token = default)
    {
        var result = await SendAsync("audio.select", new { endpointId }, token).ConfigureAwait(false);
        return result.HasValue ? WriteResult.Success : WriteResult.Fail("音频端点切换失败。");
    }

    public async Task<WriteResult> SetGainAsync(int gainDb, CancellationToken token = default)
    {
        var result = await SendAsync("voice.gain.set", new { db = gainDb }, token).ConfigureAwait(false);
        return result.HasValue ? WriteResult.Success : WriteResult.Fail("增益写入失败。");
    }

    public async Task<WriteResult> SetVoiceToolAsync(string tool, CancellationToken token = default)
    {
        var result = await SendAsync("voice.tool.set", new { tool }, token).ConfigureAwait(false);
        if (!result.HasValue)
        {
            return WriteResult.Fail("语音工具设置失败。");
        }

        // sidecar 只保存工具名，不会自动写“按住说话快捷键”。不写 hotkey 时，
        // 首次使用只会切音频、不会向输入法注入语音键，表现为“按了没反应”。
        var keys = VoiceHotkeyForTool(tool);
        if (keys is not null)
        {
            var hotkey = await SetVoiceHotkeyAsync(keys, token).ConfigureAwait(false);
            if (!hotkey.Ok)
            {
                return hotkey;
            }
        }

        return WriteResult.Success;
    }

    private static string[]? VoiceHotkeyForTool(string tool) => tool switch
    {
        "doubao" or "vokie" => ["right_alt"],
        "wechat" => ["left_control", "left_windows"],
        _ => null,
    };

    public async Task<IReadOnlyList<string>> GetVoiceHotkeyAsync(CancellationToken token = default)
    {
        var result = await SendAsync("voice.hotkey.get", null, token).ConfigureAwait(false);
        // sidecar 在未配置快捷键时返回 result:null；JsonElement.TryGetProperty
        // 不能对 Null 调用，否则会抛 InvalidOperationException 并中断 async void 事件。
        if (result is not { } value ||
            value.ValueKind != JsonValueKind.Object ||
            !value.TryGetProperty("keys", out var keys) ||
            keys.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return keys.EnumerateArray()
            .Select(key => key.GetString())
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Select(key => key!)
            .ToList();
    }

    public async Task<WriteResult> SetVoiceHotkeyAsync(
        IReadOnlyList<string> keys,
        CancellationToken token = default)
    {
        // sidecar 的 voice.hotkey.set 参数名是 chord（KeyChord { keys: [...] }），
        // 不是顶层 keys。发错时 sidecar 会按“chord=null”处理，把已保存快捷键清空。
        var result = await SendAsync(
                "voice.hotkey.set",
                new { chord = new { keys } },
                token)
            .ConfigureAwait(false);
        return result.HasValue
            ? WriteResult.Success
            : WriteResult.Fail("按住说话快捷键保存失败。");
    }

    /// <summary>
    /// 修复旧版本/旧配置留下的“工具已选、快捷键为空”状态。
    /// 只在工具是固定组合（豆包/微信/Vokie）且快捷键确实为空时写入，不动“其他”的自定义组合。
    /// </summary>
    private async Task EnsureVoiceSettingsAsync(CancellationToken token)
    {
        try
        {
            var tool = await GetVoiceToolAsync(token).ConfigureAwait(false);
            if (tool is null)
            {
                return;
            }

            var expected = VoiceHotkeyForTool(tool);
            if (expected is null)
            {
                return;
            }

            var keys = await GetVoiceHotkeyAsync(token).ConfigureAwait(false);
            if (keys.SequenceEqual(expected))
            {
                return;
            }

            AppLog.Warn($"remote voice hotkey missing or mismatched for tool={tool}; repairing");
            // 只补快捷键，不再调用 voice.tool.set，避免启动时把用户当前输入法强制切走。
            var repaired = await SetVoiceHotkeyAsync(expected, token).ConfigureAwait(false);
            AppLog.Info(repaired.Ok
                ? $"remote voice hotkey repaired tool={tool}"
                : $"remote voice hotkey repair failed tool={tool}: {repaired.Message}");
        }
        catch (Exception ex)
        {
            AppLog.Warn("remote voice settings ensure failed: " + ex.Message);
        }
    }

    public async Task<IReadOnlyList<RemoteDeviceInfo>> ScanRemotesAsync(CancellationToken token = default)
    {
        var result = await SendAsync("remote.scan", null, token).ConfigureAwait(false);
        if (result is not { } value || value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var devices = new List<RemoteDeviceInfo>();
        foreach (var device in value.EnumerateArray())
        {
            var id = String(device, "id");
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var tokenValue = String(device, "token") ?? "";
            var name = String(device, "name") ?? "小米遥控器";
            var model = String(device, "model") ?? "";
            var supported = device.TryGetProperty("isSupportedCandidate", out var flag) &&
                            flag.ValueKind == JsonValueKind.True;
            devices.Add(new RemoteDeviceInfo(id, tokenValue, name, model, supported));
        }

        return devices;
    }

    public async Task<WriteResult> ConnectRemoteAsync(string deviceId, CancellationToken token = default)
    {
        _preferredRemoteId = deviceId;
        var result = await SendAsync("remote.connect", new { deviceId }, token).ConfigureAwait(false);
        return result.HasValue ? WriteResult.Success : WriteResult.Fail("连接遥控器失败。");
    }

    public async Task<WriteResult> DisconnectRemoteAsync(CancellationToken token = default)
    {
        var result = await SendAsync("remote.disconnect", null, token).ConfigureAwait(false);
        return result.HasValue ? WriteResult.Success : WriteResult.Fail("断开遥控器失败。");
    }

    public async Task<WriteResult> SetFullKeyAsync(bool enabled, CancellationToken token = default)
    {
        var result = await SendAsync("capture.enable", new { enabled }, token).ConfigureAwait(false);
        return result.HasValue ? WriteResult.Success : WriteResult.Fail("全按键设置失败。");
    }

    public async Task<RemoteFullKeyStatus> GetFullKeyStatusAsync(CancellationToken token = default)
    {
        var result = await SendAsync("capture.status", null, token).ConfigureAwait(false);
        if (result is not { } value)
        {
            return new RemoteFullKeyStatus(false, false, "遥控器引擎未连接，无法确认 Helper 状态。");
        }

        var enabled = value.TryGetProperty("enabled", out var flag) &&
                      flag.ValueKind == JsonValueKind.True;
        var available = value.TryGetProperty("helperAvailable", out var helper) &&
                        helper.ValueKind == JsonValueKind.True;
        var message = String(value, "message");
        return new RemoteFullKeyStatus(enabled, available, message);
    }

    public async Task<WriteResult> SaveMappingAsync(string key, string gesture, string actionJson,
        CancellationToken token = default)
    {        var config = await SendAsync("mappings.config.get", null, token).ConfigureAwait(false);
        if (!config.HasValue)
        {
            return WriteResult.Fail("无法读取当前按键映射。");
        }

        JsonObject root;
        try
        {
            root = JsonNode.Parse(config.Value.GetRawText()) as JsonObject ?? new JsonObject();
        }
        catch (Exception ex)
        {
            return WriteResult.Fail($"映射配置损坏：{ex.Message}");
        }

        if (root["actions"] is not JsonObject actions)
        {
            actions = new JsonObject();
            root["actions"] = actions;
        }

        if (actions[key] is not JsonObject entry)
        {
            entry = new JsonObject();
            actions[key] = entry;
        }

        // ButtonMappings 的每个按键必须同时具备单击/双击/长按三个槽位；
        // 之前给新按键只写一个槽位会被 sidecar 判为 invalid mappings。
        foreach (var slot in new[] { "single", "double", "long" })
        {
            if (entry[slot] is null)
            {
                entry[slot] = JsonNode.Parse("{\"type\":\"disabled\"}");
            }
        }

        try
        {
            entry[gesture] = JsonNode.Parse(actionJson);
        }
        catch (Exception ex)
        {
            return WriteResult.Fail($"动作格式错误：{ex.Message}");
        }

        var saved = await SendAsync("mappings.save", new { mappings = root }, token).ConfigureAwait(false);
        if (!saved.HasValue)
        {
            AppLog.Warn("mappings.save failed payload=" + root.ToJsonString());
            return WriteResult.Fail("保存按键映射失败：引擎未响应。");
        }

        // sidecar 的 mappings.save 可能忽略写盘错误；保存后强制回读一次，
        // 只有设备端配置里真的出现该动作才向页面报告成功。
        var verified = await SendAsync("mappings.config.get", null, token).ConfigureAwait(false);
        if (!verified.HasValue)
        {
            return WriteResult.Fail("保存已提交，但回读映射失败；请刷新后确认。");
        }

        try
        {
            var persisted = JsonNode.Parse(verified.Value.GetRawText());
            var actual = persisted?["actions"]?[key]?[gesture];
            var expected = JsonNode.Parse(actionJson);
            if (actual is null || expected is null || !JsonNode.DeepEquals(actual, expected))
            {
                return WriteResult.Fail("保存后回读不一致，映射可能未落盘。");
            }
        }
        catch (Exception ex)
        {
            return WriteResult.Fail($"保存后回读失败：{ex.Message}");
        }

        return WriteResult.Success;
    }

    public async Task<WriteResult> TestMappingAsync(string key, string gesture,
        CancellationToken token = default)
    {
        var result = await SendAsync("mappings.test", new { button = key, trigger = gesture }, token)
            .ConfigureAwait(false);
        if (!result.HasValue)
        {
            return WriteResult.Fail("测试动作失败：引擎无响应。");
        }

        if (result.Value.TryGetProperty("skipped", out var skipped) &&
            skipped.ValueKind == JsonValueKind.True)
        {
            return WriteResult.Fail("该手势不能在此处测试（未配置，或动作需由真实按键/引擎执行）。");
        }

        return WriteResult.Success;
    }

    public async Task<WriteResult> SetMappingSuspensionAsync(bool suspended,
        CancellationToken token = default)
    {
        var result = await SendAsync("mappings.suspend", new { suspended }, token).ConfigureAwait(false);
        return result.HasValue ? WriteResult.Success : WriteResult.Fail("接收测试开关失败。");
    }

    private static readonly (string Key, string Label)[] ButtonOrder =
    [
        ("back", "返回"),
        ("ok", "确定"),
        ("tv", "TV"),
        ("home", "主页"),
        ("right", "右"),
        ("left", "左"),
        ("down", "下"),
        ("up", "上"),
        ("menu", "菜单"),
        ("power", "电源"),
        ("volume_mute", "静音"),
        ("volume_up", "音量+"),
        ("volume_down", "音量-"),
    ];

    private static readonly (string Key, string Label)[] Gestures =
    [
        ("single", "单击"),
        ("double", "双击"),
        ("long", "长按"),
    ];

    private static string ActionId(JsonElement action)
    {
        if (!action.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
        {
            return "disabled";
        }

        return type.GetString() switch
        {
            "disabled" => "disabled",
            "shortcut" => ShortcutId(action),
            "open_app" => "open_app",
            "focus_input" => "focus_input",
            "scroll" => "scroll_" + (String(action, "direction") ?? "up"),
            "mouse_click" => "mouse_" + (String(action, "kind") ?? "left"),
            "mouse_move" => "move_" + (String(action, "direction") ?? "up"),
            var other => other ?? "disabled",
        };
    }

    private static string ShortcutId(JsonElement action)
    {
        if (!action.TryGetProperty("chord", out var chord) ||
            !chord.TryGetProperty("keys", out var keys) ||
            keys.ValueKind != JsonValueKind.Array)
        {
            return "shortcut";
        }

        return string.Join("+", keys.EnumerateArray()
            .Where(key => key.ValueKind == JsonValueKind.String)
            .Select(key => key.GetString()));
    }

    private static string FormatAction(JsonElement action)
    {
        if (!action.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
        {
            return "—";
        }

        return type.GetString() switch
        {
            "disabled" => "禁用",
            "shortcut" => FormatShortcut(action),
            "open_app" => "打开 " + (String(action, "target") ?? "?"),
            "focus_input" => "聚焦输入框",
            "scroll" => $"滚动 {(String(action, "direction") == "down" ? "下" : "上")} " +
                        (action.TryGetProperty("steps", out var steps) ? steps.GetInt32() : 1),
            "mouse_click" => "鼠标 " + (String(action, "kind") switch
            {
                "right" => "右键",
                "double_left" => "双击",
                "middle" => "中键",
                _ => "左键",
            }),
            "mouse_move" => "移动 " + (String(action, "direction") switch
            {
                "down" => "下",
                "left" => "左",
                "right" => "右",
                _ => "上",
            }) + " " + (action.TryGetProperty("distance", out var distance) ? distance.GetInt32() : 0),
            var other => other ?? "—",
        };
    }

    private static string FormatShortcut(JsonElement action)
    {
        if (!action.TryGetProperty("chord", out var chord) ||
            !chord.TryGetProperty("keys", out var keys) ||
            keys.ValueKind != JsonValueKind.Array)
        {
            return "快捷键";
        }

        return string.Join("+", keys.EnumerateArray().Select(key => FriendlyKey(key.GetString())));
    }

    private static string FriendlyKey(string? key) => key switch
    {
        "left_control" or "right_control" or "control" => "Ctrl",
        "left_shift" or "right_shift" or "shift" => "Shift",
        "left_alt" or "right_alt" or "alt" => "Alt",
        "left_windows" or "right_windows" => "Win",
        "volume_up" => "音量+",
        "volume_down" => "音量-",
        "volume_mute" => "静音",
        "media_play_pause" => "播放/暂停",
        "media_prev" => "上一曲",
        "media_next" => "下一曲",
        "enter" => "Enter",
        "escape" => "Esc",
        "backspace" => "退格",
        "tab" => "Tab",
        null => "?",
        _ => key.Replace('_', ' '),
    };

    private async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await ConnectAndReadAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                AppLog.Warn($"remote engine unavailable: {ex.Message}");
                SetState(RemoteEngineState.Offline with { Error = ex.Message });
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task ConnectAndReadAsync(CancellationToken token)
    {
        var executable = Path.Combine(AppContext.BaseDirectory, "sidecars", "sayall-engine.exe");
        if (!File.Exists(executable))
        {
            throw new FileNotFoundException($"未找到遥控器引擎：{executable}");
        }

        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
        };
        _process = Process.Start(startInfo) ?? throw new InvalidOperationException("遥控器引擎启动失败。");
        _process.ErrorDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data))
            {
                AppLog.Warn("remote engine: " + args.Data);
            }
        };
        _process.BeginErrorReadLine();

        var readyLine = await _process.StandardOutput.ReadLineAsync(token).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(readyLine))
        {
            throw new InvalidOperationException("遥控器引擎没有返回握手信息。");
        }

        using var ready = JsonDocument.Parse(readyLine);
        var port = ready.RootElement.GetProperty("port").GetInt32();
        var tokenValue = ready.RootElement.GetProperty("token").GetString() ?? "";
        if (ready.RootElement.TryGetProperty("selectedRemoteId", out var preferred) &&
            preferred.ValueKind == JsonValueKind.String)
        {
            _preferredRemoteId = preferred.GetString();
        }

        _client = new TcpClient();
        await _client.ConnectAsync("127.0.0.1", port, token).ConfigureAwait(false);
        var stream = _client.GetStream();
        _reader = new StreamReader(stream, Encoding.UTF8, false, 8192, leaveOpen: true);
        _writer = new StreamWriter(stream, new UTF8Encoding(false), 8192, leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n",
        };
        _writer.WriteLine(tokenValue);
        SetState(RemoteEngineState.Connecting);

        _ = Task.Run(() => EnsureVoiceSettingsAsync(token), token);
        _ = Task.Run(() => ScanAndConnectAsync(token), token);

        while (!token.IsCancellationRequested)
        {
            var line = await _reader.ReadLineAsync(token).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            HandleLine(line);
        }

        throw new IOException("遥控器引擎连接已断开。");
    }

    private async Task ScanAndConnectAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(800), token).ConfigureAwait(false);
            if (State.Connected)
            {
                return;
            }

            var scan = await SendAsync("remote.scan", null, token).ConfigureAwait(false);
            if (!scan.HasValue || scan.Value.ValueKind != JsonValueKind.Array)
            {
                AppLog.Warn("remote scan returned no array");
                return;
            }

            AppLog.Info($"remote scan devices={scan.Value.GetArrayLength()}");
            var candidates = new List<string>();
            foreach (var device in scan.Value.EnumerateArray())
            {
                if (!device.TryGetProperty("isSupportedCandidate", out var supported) ||
                    !supported.GetBoolean())
                {
                    continue;
                }

                var id = device.TryGetProperty("id", out var idValue) ? idValue.GetString() : null;
                if (!string.IsNullOrWhiteSpace(id))
                {
                    candidates.Add(id);
                }
            }

            var target = candidates.FirstOrDefault(id => id == _preferredRemoteId)
                         ?? candidates.FirstOrDefault();
            if (target is not null)
            {
                AppLog.Info($"remote auto-connect id='{target}'");
                await SendAsync("remote.connect", new { deviceId = target }, token).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("remote auto-connect failed: " + ex.Message);
        }
    }

    private void HandleLine(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.TryGetProperty("event", out var eventName) &&
                eventName.GetString() == "button")
            {
                var button = String(root, "button");
                if (!string.IsNullOrWhiteSpace(button))
                {
                    var pressed = root.TryGetProperty("pressed", out var pressedValue) &&
                                  pressedValue.ValueKind == JsonValueKind.True;
                    if (pressed)
                    {
                        AppLog.Info($"remote button {button}");
                    }

                    ButtonEventReceived?.Invoke(this, new RemoteButtonEvent(button, pressed));
                }

                return;
            }

            if (root.TryGetProperty("event", out eventName) &&
                eventName.GetString() == "snapshot" &&
                root.TryGetProperty("runtime", out var runtime))
            {
                ApplyRuntime(runtime);
                return;
            }

            if (!root.TryGetProperty("id", out var idValue) || !idValue.TryGetInt32(out var id))
            {
                return;
            }

            TaskCompletionSource<JsonElement>? pending;
            lock (_pending)
            {
                if (!_pending.Remove(id, out pending))
                {
                    return;
                }
            }

            if (root.TryGetProperty("ok", out var ok) && ok.GetBoolean() &&
                root.TryGetProperty("result", out var result))
            {
                pending.SetResult(result.Clone());
            }
            else
            {
                var message = root.TryGetProperty("error", out var error) &&
                              error.TryGetProperty("message", out var text)
                    ? text.GetString()
                    : "遥控器引擎请求失败";
                AppLog.Warn("remote engine error: " + message);
                pending.SetException(new InvalidOperationException(message));
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("remote engine message invalid: " + ex.Message);
        }
    }

    private void ApplyRuntime(JsonElement runtime)
    {
        if (!runtime.TryGetProperty("connection", out var connection))
        {
            return;
        }

        var name = String(connection, "remoteName") ?? "";
        var model = String(connection, "remoteModel") ?? "";
        var phase = String(connection, "phase") ?? "idle";
        var token = String(connection, "remoteToken") ?? "";
        var error = String(connection, "lastError");
        var voice = String(connection, "voiceState") ?? "";
        int? battery = connection.TryGetProperty("batteryLevel", out var batteryValue) &&
                       batteryValue.ValueKind == JsonValueKind.Number
            ? batteryValue.GetInt32()
            : null;

        var connected = !string.IsNullOrWhiteSpace(name) &&
                        phase is not ("idle" or "error" or "scanning");
        var audioId = "";
        var audioName = "";
        if (runtime.TryGetProperty("audio", out var audio))
        {
            audioId = String(audio, "selectedEndpointId") ?? "";
            audioName = String(audio, "selectedEndpointName") ?? "";
        }

        var rawPhase = "";
        long rawEvents = 0;
        long semanticEdges = 0;
        var matchedDevices = 0;
        var lastButton = "";
        var lastPressed = false;
        var activeButtons = "";
        if (runtime.TryGetProperty("platform", out var platform) &&
            platform.TryGetProperty("rawInput", out var raw))
        {
            rawPhase = String(raw, "phase") ?? "";
            rawEvents = Number(raw, "rawEventCount");
            semanticEdges = Number(raw, "semanticEdgeCount");
            matchedDevices = (int)Number(raw, "matchedDeviceCount");
            lastButton = String(raw, "lastButton") ?? "";
            lastPressed = raw.TryGetProperty("lastIsPressed", out var pressed) &&
                          pressed.ValueKind == JsonValueKind.True;
            if (raw.TryGetProperty("activeButtons", out var active) &&
                active.ValueKind == JsonValueKind.Array)
            {
                activeButtons = string.Join(",", active.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString()));
            }
        }

        long firedGestures = 0;
        var gateActive = false;
        var listenerActive = false;
        var mappingsEnabled = false;
        if (runtime.TryGetProperty("mappings", out var mappings))
        {
            firedGestures = Number(mappings, "firedGestures");
            gateActive = mappings.TryGetProperty("gateActive", out var gate) && gate.ValueKind == JsonValueKind.True;
            listenerActive = mappings.TryGetProperty("listenerActive", out var listener) &&
                             listener.ValueKind == JsonValueKind.True;
            mappingsEnabled = mappings.TryGetProperty("enabled", out var enabled) &&
                              enabled.ValueKind == JsonValueKind.True;
        }

        SetState(new RemoteEngineState(
            connected,
            name,
            model,
            battery,
            phase,
            voice,
            error,
            token,
            audioId,
            audioName,
            rawPhase,
            matchedDevices,
            rawEvents,
            semanticEdges,
            firedGestures,
            gateActive,
            listenerActive,
            lastButton,
            lastPressed,
            activeButtons,
            mappingsEnabled));
    }

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt64()
            : 0;

    private void SetState(RemoteEngineState state)
    {
        RemoteEngineState previous;
        lock (_stateGate)
        {
            previous = _state;
            if (_state == state)
            {
                return;
            }

            _state = state;
        }

        // 高频计数器（events/semantic/last/active）只更新状态，不触发全应用刷新；
        // 页面用自己的 1s 计时器读取这些诊断值，避免每秒重建 UI。
        if (!StableChanged(previous, state))
        {
            return;
        }

        AppLog.Info($"remote engine connected={state.Connected} name='{state.Name}' " +
                    $"phase={state.Phase} battery={state.BatteryPercent?.ToString() ?? "-"} " +
                    $"mapEnabled={state.MappingsEnabled} listener={state.ListenerActive} gate={state.GateActive}");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static bool StableChanged(RemoteEngineState previous, RemoteEngineState current) =>
        previous.Connected != current.Connected ||
        previous.Name != current.Name ||
        previous.Model != current.Model ||
        previous.BatteryPercent != current.BatteryPercent ||
        previous.Phase != current.Phase ||
        previous.VoiceState != current.VoiceState ||
        previous.Error != current.Error ||
        previous.DeviceToken != current.DeviceToken ||
        previous.AudioEndpointId != current.AudioEndpointId ||
        previous.AudioEndpointName != current.AudioEndpointName ||
        previous.MappingsEnabled != current.MappingsEnabled ||
        previous.ListenerActive != current.ListenerActive ||
        previous.GateActive != current.GateActive ||
        previous.RawInputPhase != current.RawInputPhase;

    private async Task<JsonElement?> SendAsync(string command, object? parameters, CancellationToken token)
    {
        StreamWriter? writer;
        lock (_writeGate)
        {
            writer = _writer;
        }

        if (writer is null)
        {
            return null;
        }

        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pending)
        {
            _pending[id] = completion;
        }

        var payload = JsonSerializer.Serialize(new
        {
            id,
            cmd = command,
            @params = parameters ?? new { },
        });

        try
        {
            lock (_writeGate)
            {
                writer.WriteLine(payload);
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(RequestTimeout);
            return await completion.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch
        {
            lock (_pending)
            {
                _pending.Remove(id);
            }

            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            if (_writer is not null)
            {
                SendAsync("system.shutdown", null, CancellationToken.None).GetAwaiter().GetResult();
            }
        }
        catch
        {
            // 引擎可能已退出。
        }

        Stop();
    }
}

internal sealed record RemoteEngineState(
    bool Connected,
    string Name,
    string Model,
    int? BatteryPercent,
    string Phase,
    string VoiceState,
    string? Error,
    string DeviceToken,
    string AudioEndpointId = "",
    string AudioEndpointName = "",
    string RawInputPhase = "",
    int MatchedDeviceCount = 0,
    long RawEventCount = 0,
    long SemanticEdgeCount = 0,
    long FiredGestures = 0,
    bool GateActive = false,
    bool ListenerActive = false,
    string LastButton = "",
    bool LastPressed = false,
    string ActiveButtons = "",
    bool MappingsEnabled = false)
{
    public static RemoteEngineState Offline { get; } =
        new(false, "", "", null, "offline", "", null, "");

    public static RemoteEngineState Connecting { get; } =
        new(false, "", "", null, "connecting", "", null, "");
}
