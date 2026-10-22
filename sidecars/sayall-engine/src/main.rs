use std::fs;
use std::io::{self, BufRead, BufReader, Write};
use std::net::TcpListener;
use std::path::PathBuf;
use std::sync::{mpsc, Arc};
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

use sayall_core::settings::VoiceInputTool;
use sayall_windows::raw_input::RemoteButton;
use sayall_windows::rc003_bridge::BridgePhase;
use sayall_windows::send_input::{ButtonAction, ButtonMappings, ButtonTrigger, KeyChord};
use sayall_windows::WindowsPlatform;
use serde_json::{json, Value};

fn main() {
    match sayall_windows::key_host::parse_host_args(std::env::args()) {
        sayall_windows::key_host::HostArgParse::Launch(launch) => {
            std::process::exit(sayall_windows::key_host::run_host(launch));
        }
        sayall_windows::key_host::HostArgParse::Invalid(reason) => {
            eprintln!("key host args invalid: {reason}");
            std::process::exit(64);
        }
        sayall_windows::key_host::HostArgParse::NotHost => {}
    }

    let platform = WindowsPlatform::default();
    let state_dir = state_dir();
    let selected_remote_id = apply_saved_settings(&platform, &state_dir);
    // 计划任务没有登录/开机触发器，必须由宿主按需 `/run`；否则重启后
    // Helper 不会自己出现，音量键/返回键会再次“映射没反应”。
    start_rc003_helper_if_needed(&platform);
    match platform.start_raw_input() {
        Ok(snapshot) => eprintln!("raw input started: {snapshot:?}"),
        Err(error) => eprintln!("raw input start failed: {error:?}"),
    }
    if sayall_windows::key_host::start_global() {
        eprintln!("key host started");
    } else {
        eprintln!("key host failed; mapping actions stay observation-only");
    }
    let listener = match TcpListener::bind("127.0.0.1:0") {
        Ok(listener) => listener,
        Err(error) => {
            eprintln!("bind failed: {error}");
            std::process::exit(2);
        }
    };
    let port = match listener.local_addr() {
        Ok(address) => address.port(),
        Err(error) => {
            eprintln!("local_addr failed: {error}");
            std::process::exit(2);
        }
    };
    let token = format!(
        "{:x}{:x}",
        SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .map(|value| value.as_nanos())
            .unwrap_or(0),
        std::process::id()
    );

    println!(
        "{{\"event\":\"ready\",\"port\":{port},\"token\":\"{token}\",\"protocol\":1,\"selectedRemoteId\":{}}}",
        serde_json::to_string(&selected_remote_id).unwrap_or_else(|_| "null".to_owned())
    );
    let _ = io::stdout().flush();

    let (mut writer, reader) = match listener.accept() {
        Ok((stream, _)) => {
            let _ = stream.set_nodelay(true);
            match stream.try_clone() {
                Ok(reader) => (stream, reader),
                Err(error) => {
                    eprintln!("clone failed: {error}");
                    std::process::exit(2);
                }
            }
        }
        Err(error) => {
            eprintln!("accept failed: {error}");
            std::process::exit(2);
        }
    };

    let mut reader = BufReader::new(reader);
    let mut hello = String::new();
    if reader.read_line(&mut hello).is_err() || hello.trim() != token {
        eprintln!("token mismatch");
        std::process::exit(3);
    }

    let (sender, receiver) = mpsc::channel::<String>();
    std::thread::spawn(move || {
        for line in reader.lines() {
            match line {
                Ok(line) => {
                    if sender.send(line).is_err() {
                        break;
                    }
                }
                Err(_) => break,
            }
        }
    });

    // 按键边沿事件：UI 的“接收测试”只观察语义按键是否到达，不依赖 1 Hz 快照。
    let (edge_sender, edge_receiver) = mpsc::channel::<String>();
    let _ = platform.subscribe_button_edges(Arc::new(move |edge| {
        if let Ok(text) = serde_json::to_string(&json!({
            "event": "button",
            "button": edge.button,
            "pressed": edge.is_pressed,
        })) {
            let _ = edge_sender.send(text);
        }
    }));

    let mut sequence: u64 = 0;
    let mut shutdown = false;
    let mut last_snapshot = Instant::now();
    let mut last_helper_check = Instant::now();
    loop {
        match receiver.recv_timeout(Duration::from_millis(50)) {
            Ok(line) => {
                if let Some(response) = handle_command(&platform, &state_dir, &line) {
                    let is_shutdown = response
                        .get("_shutdown")
                        .and_then(Value::as_bool)
                        .unwrap_or(false);
                    let payload = if is_shutdown {
                        let mut value = response;
                        value
                            .as_object_mut()
                            .map(|object| object.remove("_shutdown"));
                        value
                    } else {
                        response
                    };
                    if writeln!(writer, "{payload}").is_err() || writer.flush().is_err() {
                        break;
                    }
                    if is_shutdown {
                        shutdown = true;
                        break;
                    }
                }
            }
            Err(mpsc::RecvTimeoutError::Timeout) => {}
            Err(mpsc::RecvTimeoutError::Disconnected) => break,
        }

        let mut write_failed = false;
        while let Ok(edge) = edge_receiver.try_recv() {
            if writeln!(writer, "{edge}").is_err() || writer.flush().is_err() {
                write_failed = true;
                break;
            }
        }
        if write_failed {
            break;
        }

        if last_helper_check.elapsed() >= Duration::from_secs(30) {
            start_rc003_helper_if_needed(&platform);
            last_helper_check = Instant::now();
        }

        if last_snapshot.elapsed() < Duration::from_secs(1) {
            continue;
        }

        last_snapshot = Instant::now();
        sequence += 1;
        let event = json!({
            "event": "snapshot",
            "seq": sequence,
            "runtime": runtime_snapshot(&platform),
        });
        if writeln!(writer, "{event}").is_err() || writer.flush().is_err() {
            break;
        }
    }

    if shutdown {
        let _ = platform.shutdown_ble_for_exit(Duration::from_secs(3));
        let _ = sayall_windows::key_host::shutdown_global();
    }
    std::process::exit(0);
}

fn save_mappings(directory: &PathBuf, mappings: &ButtonMappings) {
    let _ = fs::create_dir_all(directory);
    let Ok(text) = serde_json::to_string_pretty(mappings) else {
        return;
    };

    let temporary = directory.join("button-mappings.json.tmp");
    let target = directory.join("button-mappings.json");
    if fs::write(&temporary, text).is_ok() {
        let _ = fs::rename(temporary, target);
    }
}

fn update_settings(directory: &PathBuf, update: impl FnOnce(&mut serde_json::Map<String, Value>)) {
    let _ = fs::create_dir_all(directory);
    let path = directory.join("settings.json");
    let mut root = fs::read_to_string(&path)
        .ok()
        .and_then(|text| serde_json::from_str::<Value>(&text).ok())
        .unwrap_or_else(|| json!({}));
    let Some(object) = root.as_object_mut() else {
        return;
    };

    update(object);

    let Ok(text) = serde_json::to_string_pretty(&root) else {
        return;
    };
    let temporary = directory.join("settings.json.tmp");
    if fs::write(&temporary, text).is_ok() {
        let _ = fs::rename(temporary, path);
    }
}

fn save_hotkey(directory: &PathBuf, chord: Option<&KeyChord>) {
    let _ = fs::create_dir_all(directory);
    let path = directory.join("voice-hold-hotkey.json");
    let text = chord
        .and_then(|chord| serde_json::to_string_pretty(chord).ok())
        .unwrap_or_else(|| "null".to_owned());
    let temporary = directory.join("voice-hold-hotkey.json.tmp");
    if fs::write(&temporary, text).is_ok() {
        let _ = fs::rename(temporary, path);
    }
}

fn state_dir() -> PathBuf {
    let mut arguments = std::env::args().skip(1);
    while let Some(argument) = arguments.next() {
        if argument == "--state-dir" {
            if let Some(value) = arguments.next() {
                return PathBuf::from(value);
            }
        }
    }

    std::env::var_os("APPDATA")
        .map(PathBuf::from)
        .unwrap_or_else(|| PathBuf::from("."))
        .join("app.getsayall.remote-mic.windows")
}

fn apply_saved_settings(platform: &WindowsPlatform, directory: &PathBuf) -> Option<String> {
    let settings = fs::read_to_string(directory.join("settings.json"))
        .ok()
        .and_then(|text| serde_json::from_str::<Value>(&text).ok());

    if let Some(settings) = &settings {
        let gain = settings
            .get("gain_db")
            .and_then(Value::as_f64)
            .unwrap_or(0.0) as f32;
        platform.set_gain_db(gain);

        if let Some(tool) = settings.get("voice_input_tool").and_then(Value::as_str) {
            platform.set_voice_input_tool(Some(match tool {
                "wechat" => VoiceInputTool::Wechat,
                "doubao" => VoiceInputTool::Doubao,
                "vokie" => VoiceInputTool::Vokie,
                _ => VoiceInputTool::Other,
            }));
        }

        if let Some(endpoint) = settings.get("audio_endpoint_id").and_then(Value::as_str) {
            let _ = platform.select_audio_endpoint(endpoint.to_owned());
        }

        // 增强捕获由已存在的 RC003 Helper 提供（计划任务/旧版安装均可复用）。
        // 启动时先按用户上次的意愿下发目标，Helper 稍后连上桥接时会收到当前
        // 目标集；如果本机根本没有 Helper，桥接只会停在 listening，不会伪造
        // “全按键已生效”。音量键/返回键属于恒捕获三键，必须走这条通道。
        if settings
            .get("rc003_capture_enabled")
            .and_then(Value::as_bool)
            .unwrap_or(false)
        {
            platform.set_enhanced_capture_enabled(true);
        }
    }

    if let Some(mappings) = fs::read_to_string(directory.join("button-mappings.json"))
        .ok()
        .and_then(|text| serde_json::from_str::<ButtonMappings>(&text).ok())
    {
        platform.set_button_mappings(mappings);
    }

    if let Some(hotkey) = fs::read_to_string(directory.join("voice-hold-hotkey.json"))
        .ok()
        .and_then(|text| serde_json::from_str::<KeyChord>(&text).ok())
    {
        platform.set_voice_hold_hotkey(Some(hotkey));
    }

    settings.and_then(|value| {
        value
            .get("selected_remote_id")
            .and_then(Value::as_str)
            .map(str::to_owned)
    })
}

/// 启动已注册的 RC003 Helper 计划任务（不弹 UAC；任务本身是最高权限）。
///
/// 旧版 Helper 的任务是 `On demand only`，没有登录触发器；主程序如果不主动
/// `/run`，重启后 Helper 就永远不会出现。这里不只看 `rc003_capture_enabled`：
/// 全新安装时设置默认是 false，但任务已经由安装器注册，必须先拉起 Helper，
/// 页面才能看到“全按键可用”并允许用户打开开关。桥接已连接时直接跳过。
fn start_rc003_helper_if_needed(platform: &WindowsPlatform) {
    if matches!(
        platform.rc003_bridge_snapshot().phase,
        BridgePhase::Connected
    ) {
        return;
    }

    #[cfg(windows)]
    {
        use std::os::windows::process::CommandExt;
        use std::process::Stdio;
        const CREATE_NO_WINDOW: u32 = 0x0800_0000;
        match std::process::Command::new("schtasks.exe")
            .args(["/run", "/tn", "SayAll RC003 Helper"])
            .creation_flags(CREATE_NO_WINDOW)
            // 必须吞掉 schtasks 的 stdout：sidecar 的 stdout 是给宿主读的
            // 握手行，混入 “SUCCESS: ...” 会让宿主解析失败并每 5 秒重启。
            .stdout(Stdio::null())
            .stderr(Stdio::null())
            .status()
        {
            Ok(status) if status.success() => {
                eprintln!("rc003 helper task started");
            }
            Ok(status) => {
                eprintln!("rc003 helper task start failed: exit={status}");
            }
            Err(error) => {
                eprintln!("rc003 helper task start failed: {error}");
            }
        }
    }
}

fn runtime_snapshot(platform: &WindowsPlatform) -> Value {
    json!({
        "platform": platform.snapshot(),
        "connection": platform.connection_snapshot(),
        "audio": platform.audio_snapshot(),
        "mappings": platform.button_mapping_snapshot(),
    })
}

fn handle_command(platform: &WindowsPlatform, state_dir: &PathBuf, line: &str) -> Option<Value> {
    let request: Value = serde_json::from_str(line).ok()?;
    let id = request.get("id").cloned().unwrap_or(Value::Null);
    let command = request.get("cmd").and_then(Value::as_str).unwrap_or("");
    let params = request.get("params").cloned().unwrap_or_else(|| json!({}));

    let result = match command {
        "system.hello" => Ok(json!({
            "protocol": 1,
            "engine": "sayall-engine",
            "version": env!("CARGO_PKG_VERSION"),
        })),
        "state.get" => Ok(runtime_snapshot(platform)),
        "remote.scan" => platform
            .scan_paired_remotes()
            .map_err(|error| format!("{error:?}"))
            .and_then(|remotes| serde_json::to_value(remotes).map_err(|error| error.to_string())),
        "remote.connect" => {
            let device_id = params
                .get("deviceId")
                .and_then(Value::as_str)
                .unwrap_or("")
                .to_string();
            match platform.connect_remote(device_id.clone()) {
                Ok(snapshot) => {
                    update_settings(state_dir, |object| {
                        object.insert("selected_remote_id".into(), json!(device_id));
                    });
                    serde_json::to_value(snapshot).map_err(|error| error.to_string())
                }
                Err(error) => Err(format!("{error:?}")),
            }
        }
        "remote.disconnect" => platform
            .disconnect_remote()
            .map_err(|error| format!("{error:?}"))
            .and_then(|snapshot| serde_json::to_value(snapshot).map_err(|error| error.to_string())),
        "audio.list" => platform
            .list_audio_endpoints()
            .map_err(|error| format!("{error:?}"))
            .and_then(|endpoints| {
                serde_json::to_value(endpoints).map_err(|error| error.to_string())
            }),
        "audio.select" => {
            let endpoint_id = params
                .get("endpointId")
                .and_then(Value::as_str)
                .unwrap_or("")
                .to_string();
            match platform.select_audio_endpoint(endpoint_id.clone()) {
                Ok(snapshot) => {
                    let name = platform.audio_snapshot().selected_endpoint_name;
                    update_settings(state_dir, |object| {
                        object.insert("audio_endpoint_id".into(), json!(endpoint_id));
                        object.insert(
                            "audio_endpoint_name".into(),
                            json!(name.unwrap_or_default()),
                        );
                    });
                    serde_json::to_value(snapshot).map_err(|error| error.to_string())
                }
                Err(error) => Err(format!("{error:?}")),
            }
        }
        "voice.gain.set" => {
            let db = params.get("db").and_then(Value::as_f64).unwrap_or(0.0) as f32;
            platform.set_gain_db(db);
            update_settings(state_dir, |object| {
                object.insert("gain_db".into(), json!(db));
            });
            Ok(serde_json::to_value(platform.audio_snapshot()).unwrap_or(Value::Null))
        }
        "voice.hotkey.get" => {
            Ok(serde_json::to_value(platform.voice_hold_hotkey()).unwrap_or(Value::Null))
        }
        "voice.hotkey.set" => {
            let chord = params.get("chord").cloned().unwrap_or(Value::Null);
            if chord.is_null() {
                platform.set_voice_hold_hotkey(None);
                save_hotkey(state_dir, None);
                Ok(Value::Null)
            } else {
                match serde_json::from_value::<KeyChord>(chord) {
                    Ok(chord) => {
                        platform.set_voice_hold_hotkey(Some(chord.clone()));
                        save_hotkey(state_dir, Some(&chord));
                        Ok(serde_json::to_value(platform.voice_hold_hotkey())
                            .unwrap_or(Value::Null))
                    }
                    Err(error) => Err(format!("invalid chord: {error}")),
                }
            }
        }
        "voice.tool.get" => {
            Ok(serde_json::to_value(platform.voice_input_tool()).unwrap_or(Value::Null))
        }
        "voice.tool.set" => {
            let tool = match params
                .get("tool")
                .and_then(Value::as_str)
                .unwrap_or("other")
            {
                "wechat" => VoiceInputTool::Wechat,
                "doubao" => VoiceInputTool::Doubao,
                "vokie" => VoiceInputTool::Vokie,
                _ => VoiceInputTool::Other,
            };
            platform.set_voice_input_tool(Some(tool));
            update_settings(state_dir, |object| {
                object.insert(
                    "voice_input_tool".into(),
                    json!(match tool {
                        VoiceInputTool::Wechat => "wechat",
                        VoiceInputTool::Doubao => "doubao",
                        VoiceInputTool::Vokie => "vokie",
                        VoiceInputTool::Other => "other",
                    }),
                );
            });
            Ok(serde_json::to_value(platform.voice_input_tool()).unwrap_or(Value::Null))
        }
        "mappings.config.get" => {
            Ok(serde_json::to_value(platform.button_mappings()).unwrap_or(Value::Null))
        }
        "mappings.get" => {
            Ok(serde_json::to_value(platform.button_mapping_snapshot()).unwrap_or(Value::Null))
        }
        "mappings.save" => {
            let mappings = params
                .get("mappings")
                .cloned()
                .and_then(|value| serde_json::from_value::<ButtonMappings>(value).ok());
            match mappings {
                Some(mappings) => {
                    platform.set_button_mappings(mappings);
                    let normalized = platform.button_mappings();
                    save_mappings(state_dir, &normalized);
                    Ok(serde_json::to_value(platform.button_mapping_snapshot())
                        .unwrap_or(Value::Null))
                }
                None => Err("invalid mappings".to_string()),
            }
        }
        "mappings.test" => (|| -> Result<Value, String> {
            let button: RemoteButton =
                serde_json::from_value(params.get("button").cloned().unwrap_or(Value::Null))
                    .map_err(|error| format!("invalid button: {error}"))?;
            let trigger: ButtonTrigger =
                serde_json::from_value(params.get("trigger").cloned().unwrap_or(Value::Null))
                    .map_err(|error| format!("invalid trigger: {error}"))?;
            let action = platform.button_mappings().action_for(button, trigger);
            let result: Result<Value, String> = match &action {
                ButtonAction::Shortcut { chord } => platform
                    .test_shortcut(chord.clone())
                    .map_err(|error| format!("{error:?}"))
                    .and_then(|snapshot| {
                        serde_json::to_value(snapshot).map_err(|error| error.to_string())
                    }),
                ButtonAction::Scroll { direction, steps } => platform
                    .test_scroll(*direction, *steps)
                    .map_err(|error| format!("{error:?}"))
                    .and_then(|snapshot| {
                        serde_json::to_value(snapshot).map_err(|error| error.to_string())
                    }),
                ButtonAction::MouseClick { .. } | ButtonAction::MouseMove { .. } => platform
                    .test_mouse_action(action.clone())
                    .map_err(|error| format!("{error:?}"))
                    .and_then(|snapshot| {
                        serde_json::to_value(snapshot).map_err(|error| error.to_string())
                    }),
                _ => Ok(json!({ "skipped": true })),
            };
            result
        })(),
        "mappings.suspend" => {
            let suspended = params
                .get("suspended")
                .and_then(Value::as_bool)
                .unwrap_or(false);
            platform.set_mapping_suspension(suspended);
            eprintln!("mapping suspension suspended={suspended}");
            Ok(json!({ "suspended": suspended }))
        }
        "capture.enable" => {
            let enabled = params
                .get("enabled")
                .and_then(Value::as_bool)
                .unwrap_or(false);
            let bridge = platform.rc003_bridge_snapshot();
            let helper_available =
                matches!(bridge.phase, BridgePhase::Connected) && bridge.helper_pid != 0;
            if enabled && !helper_available {
                Err("全按键 Helper 未连接；音量键与返回键需要 Helper。请确认 Helper 正在运行，稍等几秒再打开。".to_owned())
            } else {
                platform.set_enhanced_capture_enabled(enabled);
                update_settings(state_dir, |object| {
                    object.insert("rc003_capture_enabled".into(), json!(enabled));
                });
                Ok(runtime_snapshot(platform))
            }
        }
        "capture.status" => {
            let bridge = platform.rc003_bridge_snapshot();
            let helper_available =
                matches!(bridge.phase, BridgePhase::Connected) && bridge.helper_pid != 0;
            let helper_active =
                helper_available && bridge.last_rx_age_ms.is_some_and(|age_ms| age_ms <= 3_000);
            // enabled 是“实际生效”的状态：设置文件里的愿望值只有在 Helper
            // 已连接时才可能真正捕获音量键/返回键。
            let enabled = platform.enhanced_capture_enabled() && helper_available;
            let message = if !helper_available {
                "未检测到全按键 Helper；音量键和返回键暂不可用。"
            } else if helper_active {
                "全按键 Helper 已连接；音量键与返回键走增强捕获。"
            } else {
                "全按键 Helper 已连接，正在等待心跳。"
            };
            Ok(json!({
                "enabled": enabled,
                "helperAvailable": helper_available,
                "helperActive": helper_active,
                "message": message
            }))
        }
        "raw.start" => platform
            .start_raw_input()
            .map_err(|error| format!("{error:?}"))
            .and_then(|snapshot| serde_json::to_value(snapshot).map_err(|error| error.to_string())),
        "raw.stop" => platform
            .stop_raw_input()
            .map_err(|error| format!("{error:?}"))
            .and_then(|snapshot| serde_json::to_value(snapshot).map_err(|error| error.to_string())),
        "system.shutdown" => Ok(json!({"_shutdown": true})),
        _ => Err(format!("unknown command: {command}")),
    };

    Some(match result {
        Ok(value) => json!({"id": id, "ok": true, "result": value}),
        Err(message) => json!({
            "id": id,
            "ok": false,
            "error": {"code": "engine.error", "message": message, "retryable": false}
        }),
    })
}
