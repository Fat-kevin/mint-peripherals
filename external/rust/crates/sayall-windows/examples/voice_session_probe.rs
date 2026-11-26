//! 研究用探针：验证 Windows 音频会话能否观测到「微信输入法语音输入」的采集会话。
//!
//! 用法（在 external/rust 下）：
//!   cargo run -p sayall-windows --example voice_session_probe
//!
//! 流程：打开记事本 → 枚举基线会话 → 注入 Ctrl+Win 并保持 → 每 150ms 枚举一次音频
//! 会话 → 打印 WeType 采集会话的 active 变化与延迟 → 释放后确认回到 inactive。
//! 只读会话元数据（进程 id/状态/标识），不读取音频内容。
#![cfg(windows)]

use std::time::{Duration, Instant};

use wasapi::{DeviceEnumerator, Direction, SessionState};
use windows::Win32::System::Threading::GetCurrentProcessId;
use windows::Win32::UI::Input::KeyboardAndMouse::{
    keybd_event, KEYEVENTF_KEYUP, VK_LCONTROL, VK_LWIN,
};

#[derive(Clone, Debug, PartialEq, Eq)]
struct SessionInfo {
    endpoint: String,
    session_id: String,
    pid: u32,
    active: bool,
}

fn enumerate_devices() -> Vec<(String, wasapi::Device)> {
    let Ok(enumerator) = DeviceEnumerator::new() else {
        return Vec::new();
    };
    let mut devices = Vec::new();
    for direction in [Direction::Capture, Direction::Render] {
        let Ok(collection) = enumerator.get_device_collection(&direction) else {
            continue;
        };
        let count = collection.get_nbr_devices().unwrap_or(0);
        for index in 0..count {
            let Ok(device) = collection.get_device_at_index(index) else {
                continue;
            };
            let name = device
                .get_friendlyname()
                .unwrap_or_else(|_| format!("<device-{index}>"));
            devices.push((format!("{:?}:{name}", direction), device));
        }
    }
    devices
}

fn snapshot_sessions() -> Vec<SessionInfo> {
    let mut sessions = Vec::new();
    for (endpoint, device) in enumerate_devices() {
        let Ok(manager) = device.get_iaudiosessionmanager() else {
            continue;
        };
        let Ok(enumerator) = manager.get_audiosessionenumerator() else {
            continue;
        };
        let count = enumerator.get_count().unwrap_or(0);
        for index in 0..count {
            let Ok(control) = enumerator.get_session(index) else {
                continue;
            };
            let state = control.get_state().unwrap_or(SessionState::Expired);
            sessions.push(SessionInfo {
                endpoint: endpoint.clone(),
                session_id: control
                    .get_session_identifier()
                    .unwrap_or_else(|_| format!("<session-{index}>")),
                pid: control.get_process_id().unwrap_or(0),
                active: state == SessionState::Active,
            });
        }
    }
    sessions
}

fn is_wetype_session(session: &SessionInfo) -> bool {
    let id = session.session_id.to_ascii_lowercase();
    id.contains("tencent\\wetype") || id.contains("tencent/wetype")
}

fn wetype_active(sessions: &[SessionInfo]) -> bool {
    sessions.iter().any(|s| s.active && is_wetype_session(s))
}

fn press_chord() {
    unsafe {
        keybd_event(VK_LCONTROL.0 as u8, 0, Default::default(), 0);
        keybd_event(VK_LWIN.0 as u8, 0, Default::default(), 0);
    }
}

fn release_chord() {
    unsafe {
        keybd_event(VK_LWIN.0 as u8, 0, KEYEVENTF_KEYUP, 0);
        keybd_event(VK_LCONTROL.0 as u8, 0, KEYEVENTF_KEYUP, 0);
    }
}

fn run_press(round: u32, hold: Duration) {
    println!("=== round {round} ===");
    let started = Instant::now();
    let baseline = snapshot_sessions();
    let baseline_ms = started.elapsed().as_millis();
    println!(
        "baseline sessions={} snapshot={}ms wetype_active={}",
        baseline.len(),
        baseline_ms,
        wetype_active(&baseline)
    );

    press_chord();
    let press_at = Instant::now();
    let mut first_active: Option<u128> = None;
    while press_at.elapsed() < hold {
        std::thread::sleep(Duration::from_millis(150));
        let sessions = snapshot_sessions();
        if first_active.is_none() && wetype_active(&sessions) {
            first_active = Some(press_at.elapsed().as_millis());
            println!("  wetype capture active at +{}ms", first_active.unwrap());
        }
    }
    release_chord();
    let mut inactive_at: Option<u128> = None;
    let rel = Instant::now();
    while rel.elapsed() < Duration::from_secs(2) {
        std::thread::sleep(Duration::from_millis(150));
        let sessions = snapshot_sessions();
        if !wetype_active(&sessions) {
            inactive_at = Some(rel.elapsed().as_millis());
            break;
        }
    }
    println!(
        "  released; wetype inactive after {}ms",
        inactive_at.map(|v| v.to_string()).unwrap_or_else(|| ">2000".into())
    );
}

fn main() {
    let _ = wasapi::initialize_mta();
    let self_pid = unsafe { GetCurrentProcessId() };
    println!("probe pid={self_pid}");

    let mut notepad = std::process::Command::new("notepad.exe")
        .spawn()
        .expect("启动 notepad 失败");
    std::thread::sleep(Duration::from_millis(1500));

    run_press(1, Duration::from_millis(2500));
    std::thread::sleep(Duration::from_millis(800));
    run_press(2, Duration::from_millis(2500));

    let _ = notepad.kill();
    let _ = notepad.wait();
}
