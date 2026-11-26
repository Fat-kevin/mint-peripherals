//! 微信输入法语音会话的「直接证据」：Windows 音频采集会话。
//!
//! 背景（2026-11-26）：微信输入法 3.x 不再写 ConsentStore 开麦时间戳、也不再注入
//! WTYP 存活标记，导致「热键休眠自动恢复」在无法判断时误判未响应，释放/重按和弦
//! 打断正在进行的听写。真机探针验证：语音输入开始时，Windows 音频会话层会出现一个
//! 归属于 WeType 的采集会话（实测 153–779ms 内变 Active），枚举耗时 6–10ms。
//!
//! 本模块只做三件事：
//! 1. 枚举**采集端点**上的音频会话，判断 WeType 是否正在采集（直接证据）；
//! 2. 读取微信输入法版本，持久化「音频证据是否可靠」（按版本绑定，版本变化重置）；
//! 3. 提供纯函数 `merge_verdict` 供 BLE 语音判定使用（便于单测）。
//!
//! 隐私：只读取会话状态/进程 id/会话标识；不读取音频内容，日志不落会话标识与路径。

use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::OnceLock;

use wasapi::{DeviceEnumerator, Direction, SessionState};
use windows::core::PCWSTR;
use windows::Win32::Foundation::CloseHandle;
use windows::Win32::System::Diagnostics::ToolHelp::{
    CreateToolhelp32Snapshot, Process32FirstW, Process32NextW, PROCESSENTRY32W, TH32CS_SNAPPROCESS,
};
use windows::Win32::System::Registry::{RegGetValueW, HKEY_LOCAL_MACHINE, RRF_RT_REG_SZ};

use crate::wetype_revive::WetypeReaction;

/// 能力文件 schema 版本。
const CAPABILITY_SCHEMA: u64 = 1;
/// 微信输入法版本注册表路径（32 位卸载项；实测 3.0.3.11 在此）。
const WETYPE_UNINSTALL_SUBKEY: &str =
    "SOFTWARE\\WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\WeType";
const WETYPE_VERSION_VALUE: &str = "DisplayVersion";

static AUDIO_RELIABLE: AtomicBool = AtomicBool::new(false);
static CAPABILITY_VERSION: OnceLock<Option<String>> = OnceLock::new();
static CAPABILITY_FILE: OnceLock<PathBuf> = OnceLock::new();

/// 一次音频会话枚举的原始条目。
#[derive(Clone, Debug)]
pub(crate) struct AudioSessionInfo {
    pub endpoint: String,
    pub session_id: String,
    pub pid: u32,
    pub active: bool,
}

/// WeType 采集会话的观测结果。
#[derive(Clone, Debug, Default)]
pub(crate) struct WechatCaptureState {
    /// 是否存在归属于微信输入法的活动采集会话（正面证据）。
    pub active: bool,
    /// 所有活动采集端点的会话枚举是否全部成功（只有 true 时「缺席」才可信）。
    pub available: bool,
    pub endpoint: Option<String>,
    pub pid: Option<u32>,
}

/// 枚举所有活动采集端点的音频会话。返回（会话列表，枚举是否完整）。
pub(crate) fn capture_sessions() -> (Vec<AudioSessionInfo>, bool) {
    // 每个调用线程都可能是新线程；MTA 初始化是幂等的（重复调用返回 S_FALSE）。
    let _ = wasapi::initialize_mta();

    let mut sessions = Vec::new();
    let mut available = true;

    let Ok(enumerator) = DeviceEnumerator::new() else {
        return (sessions, false);
    };
    let Ok(collection) = enumerator.get_device_collection(&Direction::Capture) else {
        return (sessions, false);
    };
    let count = match collection.get_nbr_devices() {
        Ok(count) => count,
        Err(_) => return (sessions, false),
    };
    if count == 0 {
        return (sessions, false);
    }

    for index in 0..count {
        let Ok(device) = collection.get_device_at_index(index) else {
            available = false;
            continue;
        };
        let endpoint = device
            .get_friendlyname()
            .unwrap_or_else(|_| format!("capture-{index}"));
        let Ok(manager) = device.get_iaudiosessionmanager() else {
            available = false;
            continue;
        };
        let Ok(list) = manager.get_audiosessionenumerator() else {
            available = false;
            continue;
        };
        let total = match list.get_count() {
            Ok(total) => total,
            Err(_) => {
                available = false;
                continue;
            }
        };
        for session_index in 0..total {
            let Ok(control) = list.get_session(session_index) else {
                available = false;
                continue;
            };
            let active = control
                .get_state()
                .map(|state| state == SessionState::Active)
                .unwrap_or(false);
            let pid = control.get_process_id().unwrap_or(0);
            let session_id = control.get_session_identifier().unwrap_or_default();
            sessions.push(AudioSessionInfo {
                endpoint: endpoint.clone(),
                session_id,
                pid,
                active,
            });
        }
    }

    (sessions, available)
}

/// 微信输入法当前是否有一个活动的采集会话。
pub(crate) fn wechat_capture_state() -> WechatCaptureState {
    let (sessions, available) = capture_sessions();
    let hit = sessions
        .iter()
        .find(|session| session.active && is_wechat_ime_session(session));
    WechatCaptureState {
        active: hit.is_some(),
        available,
        endpoint: hit.map(|session| session.endpoint.clone()),
        pid: hit.map(|session| session.pid),
    }
}

/// 会话是否归属微信输入法。
///
/// 主判据：会话标识里的可执行文件路径含 `Tencent\WeType`（真机标识形如
/// `\\?\HID...|{endpoint}|\Device\HarddiskVolume3\Program Files\Tencent\WeType\3.0.3.11\wetype_update.exe%...`）。
/// 回退：进程名含 `wetype`（ToolHelp 快照，进程名不足以区分时仍只认 WeType 前缀）。
fn is_wechat_ime_session(session: &AudioSessionInfo) -> bool {
    let identifier = session.session_id.to_ascii_lowercase().replace('/', "\\");
    if identifier.contains("tencent\\wetype") {
        return true;
    }
    process_name_contains(session.pid, "wetype")
}

fn process_name_contains(pid: u32, needle: &str) -> bool {
    if pid == 0 {
        return false;
    }
    let Ok(snapshot) = (unsafe { CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0) }) else {
        return false;
    };
    let mut entry = PROCESSENTRY32W {
        dwSize: std::mem::size_of::<PROCESSENTRY32W>() as u32,
        ..Default::default()
    };
    let mut found = false;
    unsafe {
        if Process32FirstW(snapshot, &mut entry).is_ok() {
            loop {
                if entry.th32ProcessID == pid {
                    let length = entry
                        .szExeFile
                        .iter()
                        .position(|&ch| ch == 0)
                        .unwrap_or(entry.szExeFile.len());
                    let name = String::from_utf16_lossy(&entry.szExeFile[..length]);
                    found = name.to_ascii_lowercase().contains(needle);
                    break;
                }
                if Process32NextW(snapshot, &mut entry).is_err() {
                    break;
                }
            }
        }
        let _ = CloseHandle(snapshot);
    }
    found
}

/// 读取微信输入法版本（注册表 `Uninstall\WeType\DisplayVersion`）。
pub(crate) fn wechat_ime_version() -> Option<String> {
    read_display_version(WETYPE_UNINSTALL_SUBKEY)
}

fn read_display_version(subkey: &str) -> Option<String> {
    let subkey: Vec<u16> = subkey
        .encode_utf16()
        .chain(std::iter::once(0))
        .collect();
    let value: Vec<u16> = WETYPE_VERSION_VALUE
        .encode_utf16()
        .chain(std::iter::once(0))
        .collect();
    let mut buffer = [0u16; 128];
    let mut size: u32 = (buffer.len() * std::mem::size_of::<u16>()) as u32;
    let status = unsafe {
        RegGetValueW(
            HKEY_LOCAL_MACHINE,
            PCWSTR(subkey.as_ptr()),
            PCWSTR(value.as_ptr()),
            RRF_RT_REG_SZ,
            None,
            Some(buffer.as_mut_ptr() as *mut core::ffi::c_void),
            Some(&mut size),
        )
    };
    if status.0 != 0 || size < 2 {
        return None;
    }
    let length = (size as usize / std::mem::size_of::<u16>()).min(buffer.len());
    let text = String::from_utf16_lossy(&buffer[..length])
        .trim_end_matches('\0')
        .trim()
        .to_owned();
    (!text.is_empty()).then_some(text)
}

/// 判定合并（纯函数）：
/// - 音频证据为「正在采集」→ Reacted（正面证据，禁止恢复）；
/// - 否则保留现有 marker/mic 判定；
/// - 音频枚举完整且本版本能力可靠 → NotReacted（确认没有采集会话，恢复安全）。
pub(crate) fn merge_verdict(
    base: WetypeReaction,
    audio_active: bool,
    audio_available: bool,
    audio_reliable: bool,
) -> WetypeReaction {
    if audio_active {
        return WetypeReaction::Reacted;
    }
    if base == WetypeReaction::Reacted {
        return WetypeReaction::Reacted;
    }
    if audio_available && audio_reliable {
        return WetypeReaction::NotReacted;
    }
    base
}

/// 当前版本下「音频缺席可信」是否已建立。
pub(crate) fn wechat_audio_reliable() -> bool {
    AUDIO_RELIABLE.load(Ordering::Relaxed)
}

/// 已亲眼观测到 WeType 采集会话：置位能力并在版本已知时原子落盘（仅首次变化）。
pub(crate) fn note_wechat_audio_observed() {
    if AUDIO_RELIABLE.swap(true, Ordering::Relaxed) {
        return;
    }
    if let (Some(path), Some(version)) = (
        CAPABILITY_FILE.get(),
        CAPABILITY_VERSION.get().and_then(|value| value.as_ref()),
    ) {
        write_capability(path, version, true);
    }
    crate::ble::gatt_note("voice_audio capability_learned".to_owned());
}

/// sidecar 启动时初始化：读取能力文件、探测微信输入法版本、绑定状态文件路径。
///
/// 版本变化或文件不可读 → 能力重置为 false（安全侧）；版本探测失败 → 只内存学习。
pub fn initialize_voice_audio_capability(path: PathBuf) {
    let version = wechat_ime_version();
    let reliable = load_capability(&path, version.as_deref());
    AUDIO_RELIABLE.store(reliable, Ordering::Relaxed);
    let _ = CAPABILITY_VERSION.set(version.clone());
    let _ = CAPABILITY_FILE.set(path.clone());
    crate::ble::gatt_note(format!(
        "voice_audio capability_loaded version={} reliable={reliable}",
        version.as_deref().unwrap_or("unknown")
    ));
    // 版本已知时重写一次：清掉旧版本残留、统一 schema（失败无副作用）。
    if let Some(current) = version.as_deref() {
        write_capability(&path, current, reliable);
    }
}

fn capability_value(version: &str, reliable: bool) -> serde_json::Value {
    serde_json::json!({
        "schema_version": CAPABILITY_SCHEMA,
        "wechat_ime_version": version,
        "audio_reliable": reliable,
        "observed_at_unix": std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .map(|duration| duration.as_secs())
            .unwrap_or(0),
    })
}

fn write_capability(path: &Path, version: &str, reliable: bool) {
    if let Some(parent) = path.parent() {
        let _ = std::fs::create_dir_all(parent);
    }
    let temporary = path.with_extension("json.tmp");
    let body = capability_value(version, reliable).to_string();
    if std::fs::write(&temporary, body).is_ok() && std::fs::rename(&temporary, path).is_err() {
        let _ = std::fs::remove_file(&temporary);
    }
}

fn load_capability(path: &Path, version: Option<&str>) -> bool {
    let Ok(text) = std::fs::read_to_string(path) else {
        return false;
    };
    let Ok(value) = serde_json::from_str::<serde_json::Value>(&text) else {
        return false;
    };
    if value.get("schema_version").and_then(|item| item.as_u64()) != Some(CAPABILITY_SCHEMA) {
        return false;
    }
    let stored_reliable = value
        .get("audio_reliable")
        .and_then(|item| item.as_bool())
        .unwrap_or(false);
    let stored_version = value
        .get("wechat_ime_version")
        .and_then(|item| item.as_str())
        .unwrap_or("");
    match version {
        // 版本一致才信任历史能力。
        Some(current) if stored_version == current => stored_reliable,
        // 版本未知：不信任历史文件（避免「旧版能观测 → 新版不能观测」时
        // 沿用旧信任去做破坏性恢复）；仅本次运行内存学习。
        // 版本变化：必须重新学习。
        _ => false,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn session(identifier: &str, pid: u32) -> AudioSessionInfo {
        AudioSessionInfo {
            endpoint: "CABLE Output".to_owned(),
            session_id: identifier.to_owned(),
            pid,
            active: true,
        }
    }

    #[test]
    fn identifies_wetype_sessions_by_identifier_path() {
        let volume = session(
            r"\\?\HID#...|\Device\HarddiskVolume3\Program Files\Tencent\WeType\3.0.3.11\wetype_update.exe%b{0}",
            0,
        );
        assert!(is_wechat_ime_session(&volume));

        let lowercase = session(
            r"{endpoint}|\device\harddiskvolume6\program files\tencent\wetype\3.0.3.11\wetype_server.exe",
            0,
        );
        assert!(is_wechat_ime_session(&lowercase));

        assert!(!is_wechat_ime_session(&session(
            r"|\Device\HarddiskVolume5\Program Files\Tencent\Weixin\Weixin.exe",
            0
        )));
        assert!(!is_wechat_ime_session(&session(
            r"|C:\Program Files (x86)\Tencent\QQMusic\QQMusic.exe",
            0
        )));
        assert!(!is_wechat_ime_session(&session("", 0)));
    }

    #[test]
    fn audio_active_always_wins() {
        for base in [
            WetypeReaction::Reacted,
            WetypeReaction::NotReacted,
            WetypeReaction::Unknown,
        ] {
            assert_eq!(
                merge_verdict(base, true, true, false),
                WetypeReaction::Reacted
            );
        }
    }

    #[test]
    fn absent_audio_only_when_available_and_reliable() {
        assert_eq!(
            merge_verdict(WetypeReaction::NotReacted, false, true, true),
            WetypeReaction::NotReacted
        );
        // 枚举不完整或能力未建立：不得把缺席当作证据。
        assert_eq!(
            merge_verdict(WetypeReaction::Unknown, false, false, true),
            WetypeReaction::Unknown
        );
        assert_eq!(
            merge_verdict(WetypeReaction::Unknown, false, true, false),
            WetypeReaction::Unknown
        );
        // 老版本的 marker/mic 结论保留。
        assert_eq!(
            merge_verdict(WetypeReaction::NotReacted, false, false, false),
            WetypeReaction::NotReacted
        );
        assert_eq!(
            merge_verdict(WetypeReaction::Reacted, false, true, true),
            WetypeReaction::Reacted
        );
    }

    #[test]
    fn capability_file_round_trip_and_version_reset() {
        let path = std::env::temp_dir().join(format!(
            "voice-audio-capability-test-{}-{}.json",
            std::process::id(),
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .map(|duration| duration.as_nanos())
                .unwrap_or(0)
        ));

        assert!(!load_capability(&path, Some("3.0.3.11")));

        write_capability(&path, "3.0.3.11", true);
        assert!(load_capability(&path, Some("3.0.3.11")));
        // 版本未知时不信任历史能力（安全侧）。
        assert!(!load_capability(&path, None));

        // 版本变化 → 重置；版本未知 → 不信任。
        assert!(!load_capability(&path, Some("3.1.0.0")));
        assert!(!load_capability(&path, None));

        std::fs::write(&path, "{ not json").unwrap();
        assert!(!load_capability(&path, Some("3.0.3.11")));

        let _ = std::fs::remove_file(&path);
    }
}
