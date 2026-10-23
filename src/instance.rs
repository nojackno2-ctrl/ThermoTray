//! 單一執行個體協調：互斥鎖、具名管道與舊版訊號。
//!
//! 確保同一個 Session 中只有一個 ThermoTray 持有系統匣圖示並輪詢硬體。管道協定（WHO / SHOW / EXIT，
//! 無 BOM 的 UTF-8、`\n` 換行）與 1.1.3 之後的 C# 版本完全相同，新舊版本可以互相接手。

use std::sync::Arc;
use std::thread::JoinHandle;
use std::time::{Duration, Instant};

use windows::core::PCWSTR;
use windows::Win32::Foundation::{
    GetLastError, LocalFree, ERROR_ALREADY_EXISTS, ERROR_IO_PENDING, ERROR_PIPE_BUSY, ERROR_PIPE_CONNECTED,
    GENERIC_READ, GENERIC_WRITE, HANDLE, HLOCAL, WAIT_OBJECT_0, WAIT_TIMEOUT,
};
use windows::Win32::Security::Authorization::{ConvertStringSecurityDescriptorToSecurityDescriptorW, SDDL_REVISION_1};
use windows::Win32::Security::{PSECURITY_DESCRIPTOR, SECURITY_ATTRIBUTES};
use windows::Win32::Storage::FileSystem::{
    CreateFileW, FlushFileBuffers, ReadFile, WriteFile, FILE_FLAG_FIRST_PIPE_INSTANCE, FILE_FLAG_OVERLAPPED,
    FILE_SHARE_NONE, OPEN_EXISTING, PIPE_ACCESS_DUPLEX,
};
use windows::Win32::System::Pipes::{
    ConnectNamedPipe, CreateNamedPipeW, DisconnectNamedPipe, WaitNamedPipeW, PIPE_READMODE_BYTE, PIPE_TYPE_BYTE,
    PIPE_WAIT,
};
use windows::Win32::System::RemoteDesktop::ProcessIdToSessionId;
use windows::Win32::System::Threading::{
    CreateEventW, CreateMutexW, GetCurrentProcessId, OpenEventW, OpenProcess, SetEvent, WaitForMultipleObjects,
    WaitForSingleObject, EVENT_MODIFY_STATE, INFINITE, PROCESS_SYNCHRONIZE,
};
use windows::Win32::System::IO::{CancelIoEx, GetOverlappedResult, OVERLAPPED};

use crate::win::{OwnedHandle, WideString};

/// Session 範圍的互斥鎖：所有執行個體都在同一個 Session 內以提權執行。
const OWNERSHIP_MUTEX_NAME: &str = "Local\\ThermoTray.SingleInstance";

/// 1.1.3 之前版本使用的顯示視窗事件，保留以向下相容。
const LEGACY_SHOW_WINDOW_EVENT_NAME: &str = "Local\\ThermoTray.ShowWindow";

/// Inno Setup 以一般權限執行，透過此全域互斥鎖（`AppMutex`）判斷 ThermoTray 是否執行中。
pub const SETUP_MUTEX_NAME: &str = "Global\\ThermoTray.Setup";

const OWNERSHIP_RETRIES: u32 = 20;
const OWNERSHIP_RETRY_DELAY: Duration = Duration::from_millis(100);
const EXCHANGE_TIMEOUT: Duration = Duration::from_secs(5);
const SERVER_READ_TIMEOUT: Duration = Duration::from_secs(10);
const BUFFER_SIZE: u32 = 256;
const MAXIMUM_LINE_BYTES: usize = 4096;

pub const IDENTIFY_REQUEST: &str = "WHO";
pub const SHOW_REQUEST: &str = "SHOW";
pub const EXIT_REQUEST: &str = "EXIT";
pub const ACKNOWLEDGEMENT: &str = "OK";
const IDENTITY_PREFIX: &str = "THERMOTRAY";
const UNKNOWN_VERSION: &str = "-";

/// 與 .NET `System.Version` 相同語意的版本號；缺少的元件以 -1 表示。
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Version {
    pub major: i32,
    pub minor: i32,
    pub build: i32,
    pub revision: i32,
}

impl Version {
    pub const fn new(major: i32, minor: i32, build: i32, revision: i32) -> Self {
        Self { major, minor, build, revision }
    }

    /// 解析 2~4 個以「.」分隔的非負整數。
    pub fn parse(text: &str) -> Option<Version> {
        let parts: Vec<&str> = text.split('.').collect();
        if !(2..=4).contains(&parts.len()) {
            return None;
        }

        let mut values = [-1i32; 4];
        for (slot, part) in values.iter_mut().zip(&parts) {
            if part.is_empty() || !part.bytes().all(|byte| byte.is_ascii_digit()) {
                return None;
            }
            *slot = part.parse().ok()?;
        }

        Some(Version::new(values[0], values[1], values[2], values[3]))
    }

    /// 忽略 Revision，只比較 Major.Minor.Build。
    fn normalized(self) -> (i32, i32, i32) {
        (self.major, self.minor, self.build.max(0))
    }
}

impl std::fmt::Display for Version {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(formatter, "{}.{}", self.major, self.minor)?;
        for component in [self.build, self.revision] {
            if component < 0 {
                break;
            }
            write!(formatter, ".{component}")?;
        }

        Ok(())
    }
}

/// 本程式的版本（來自 Cargo.toml），以四段格式與 .NET 組件版本對齊。
pub fn product_version() -> Version {
    let mut parts = env!("CARGO_PKG_VERSION").split(['.', '-', '+']).map(|part| part.parse().unwrap_or(0));
    Version::new(parts.next().unwrap_or(0), parts.next().unwrap_or(0), parts.next().unwrap_or(0), 0)
}

/// 格式化為「v1.1.3」，供視窗與對話方塊使用。
pub fn format_version(version: Option<Version>) -> String {
    match version {
        Some(version) => format!("v{}.{}.{}", version.major, version.minor, version.build.max(0)),
        None => String::new(),
    }
}

/// 新啟動的執行個體面對既有執行個體時採取的處置。
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum InstanceAction {
    /// 顯示既有視窗，無聲結束新啟動的執行個體。
    ShowRunning,
    /// 顯示既有視窗，並提示執行中的版本較新。
    ShowNewerRunning,
    /// 詢問是否關閉舊版，由新啟動的執行個體接手。
    ReplaceRunning,
}

/// 比對版本決定處置；任一版本未知時一律顯示既有視窗。
pub fn decide(running: Option<Version>, starting: Option<Version>) -> InstanceAction {
    match (running, starting) {
        (Some(running), Some(starting)) => match starting.normalized().cmp(&running.normalized()) {
            std::cmp::Ordering::Greater => InstanceAction::ReplaceRunning,
            std::cmp::Ordering::Less => InstanceAction::ShowNewerRunning,
            std::cmp::Ordering::Equal => InstanceAction::ShowRunning,
        },
        _ => InstanceAction::ShowRunning,
    }
}

/// 格式化身份回應，例如 `THERMOTRAY 2.0.0.0 1234`。
pub fn format_identity(version: Option<Version>, process_id: u32) -> String {
    let version = version.map_or_else(|| UNKNOWN_VERSION.to_string(), |version| version.to_string());
    format!("{IDENTITY_PREFIX} {version} {process_id}")
}

/// 解析身份回應；版本無法解析時仍保留處理程序 ID。
pub fn parse_identity(line: Option<&str>) -> Option<(Option<Version>, u32)> {
    let parts: Vec<&str> = line?.split(' ').collect();
    if parts.len() != 3 || parts[0] != IDENTITY_PREFIX {
        return None;
    }

    if parts[2].is_empty() || !parts[2].bytes().all(|byte| byte.is_ascii_digit()) {
        return None;
    }

    let process_id: u32 = parts[2].parse().ok().filter(|&id| id > 0 && id <= i32::MAX as u32)?;
    Some((Version::parse(parts[1]), process_id))
}

/// 依 Session ID 建立管道名稱。
pub fn pipe_name() -> String {
    let mut session = 0;
    match unsafe { ProcessIdToSessionId(GetCurrentProcessId(), &mut session) } {
        Ok(()) => format!("ThermoTray.Instance.{session}"),
        Err(_) => "ThermoTray.Instance".to_string(),
    }
}

fn pipe_path(name: &str) -> WideString {
    WideString::new(&format!("\\\\.\\pipe\\{name}"))
}

/// 可傳遞至背景執行緒的控制代碼值（擁有權仍屬於建立者）。
#[derive(Clone, Copy)]
struct SendHandle(HANDLE);

unsafe impl Send for SendHandle {}

type Callback = Arc<dyn Fn() + Send + Sync>;

/// 以 overlapped I/O 讀寫具名管道，所有操作都有逾時與停止訊號。
struct PipeStream {
    handle: OwnedHandle,
    event: OwnedHandle,
    pending: Vec<u8>,
}

impl PipeStream {
    fn new(handle: HANDLE) -> Option<PipeStream> {
        let event = unsafe { CreateEventW(None, true, false, PCWSTR::null()) }.ok()?;
        Some(PipeStream { handle: OwnedHandle(handle), event: OwnedHandle(event), pending: Vec::new() })
    }

    /// 等待 overlapped 操作完成；逾時或收到停止訊號時取消並傳回 `None`。
    fn complete(
        &self,
        overlapped: &mut OVERLAPPED,
        started: windows::core::Result<()>,
        timeout: Duration,
        stop: Option<HANDLE>,
    ) -> Option<u32> {
        if let Err(error) = started {
            if error.code() != ERROR_IO_PENDING.to_hresult() {
                return None;
            }

            let mut handles = vec![self.event.0];
            handles.extend(stop);
            let waited = unsafe { WaitForMultipleObjects(&handles, false, timeout_millis(timeout)) };
            if waited != WAIT_OBJECT_0 {
                unsafe {
                    let _ = CancelIoEx(self.handle.0, Some(overlapped));
                    let mut ignored = 0;
                    let _ = GetOverlappedResult(self.handle.0, overlapped, &mut ignored, true);
                }
                return None;
            }
        }

        let mut transferred = 0;
        unsafe { GetOverlappedResult(self.handle.0, overlapped, &mut transferred, false) }.ok()?;
        Some(transferred)
    }

    fn write_line(&mut self, text: &str, timeout: Duration) -> bool {
        let line = format!("{text}\n");
        let mut written = 0usize;
        let bytes = line.as_bytes();
        while written < bytes.len() {
            let mut overlapped = OVERLAPPED { hEvent: self.event.0, ..Default::default() };
            let started = unsafe { WriteFile(self.handle.0, Some(&bytes[written..]), None, Some(&mut overlapped)) };
            match self.complete(&mut overlapped, started, timeout, None) {
                Some(count) if count > 0 => written += count as usize,
                _ => return false,
            }
        }

        true
    }

    fn read_line(&mut self, timeout: Duration, stop: Option<HANDLE>) -> Option<String> {
        let deadline = Instant::now() + timeout;
        loop {
            if let Some(position) = self.pending.iter().position(|&byte| byte == b'\n') {
                let mut line: Vec<u8> = self.pending.drain(..=position).collect();
                line.pop();
                if line.last() == Some(&b'\r') {
                    line.pop();
                }
                return String::from_utf8(line).ok();
            }

            if self.pending.len() > MAXIMUM_LINE_BYTES {
                return None;
            }

            let remaining = deadline.checked_duration_since(Instant::now())?;
            let mut buffer = [0u8; BUFFER_SIZE as usize];
            let mut overlapped = OVERLAPPED { hEvent: self.event.0, ..Default::default() };
            let started = unsafe { ReadFile(self.handle.0, Some(&mut buffer), None, Some(&mut overlapped)) };
            match self.complete(&mut overlapped, started, remaining, stop) {
                Some(count) if count > 0 => self.pending.extend_from_slice(&buffer[..count as usize]),
                _ => return None,
            }
        }
    }
}

fn timeout_millis(timeout: Duration) -> u32 {
    timeout.as_millis().min(u128::from(INFINITE - 1)) as u32
}

/// 回應後續啟動的 ThermoTray 傳來的命令。
pub struct InstanceServer {
    stop: OwnedHandle,
    listener: Option<JoinHandle<()>>,
}

impl InstanceServer {
    pub fn start(
        name: &str,
        version: Option<Version>,
        show_window: Callback,
        request_exit: Callback,
    ) -> Option<InstanceServer> {
        let stop = unsafe { CreateEventW(None, true, false, PCWSTR::null()) }.ok()?;
        let stop = OwnedHandle(stop);
        let stop_value = SendHandle(stop.0);
        let name = name.to_string();
        let listener = std::thread::Builder::new()
            .name("instance-pipe".into())
            .spawn(move || {
                let stop = stop_value;
                listen(&name, version, &show_window, &request_exit, stop.0);
            })
            .ok()?;

        Some(InstanceServer { stop, listener: Some(listener) })
    }
}

impl Drop for InstanceServer {
    fn drop(&mut self) {
        unsafe {
            let _ = SetEvent(self.stop.0);
        }
        if let Some(listener) = self.listener.take() {
            let _ = listener.join();
        }
    }
}

fn listen(name: &str, version: Option<Version>, show_window: &Callback, request_exit: &Callback, stop: HANDLE) {
    let path = pipe_path(name);
    loop {
        if unsafe { WaitForSingleObject(stop, 0) } == WAIT_OBJECT_0 {
            return;
        }

        let handle = unsafe {
            CreateNamedPipeW(
                path.as_pcwstr(),
                PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED | FILE_FLAG_FIRST_PIPE_INSTANCE,
                PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT,
                1,
                BUFFER_SIZE,
                BUFFER_SIZE,
                0,
                None,
            )
        };
        if handle.is_invalid() {
            // 另一個處理程序暫時持有同名管道；稍候重試，同時仍能及時回應停止訊號
            if unsafe { WaitForSingleObject(stop, 500) } == WAIT_OBJECT_0 {
                return;
            }
            continue;
        }

        let Some(mut stream) = PipeStream::new(handle) else {
            return;
        };

        let mut overlapped = OVERLAPPED { hEvent: stream.event.0, ..Default::default() };
        let started = unsafe { ConnectNamedPipe(stream.handle.0, Some(&mut overlapped)) };
        let connected = match started {
            Err(error) if error.code() == ERROR_PIPE_CONNECTED.to_hresult() => true,
            other => stream
                .complete(&mut overlapped, other, Duration::from_millis(u64::from(INFINITE - 1)), Some(stop))
                .is_some(),
        };
        if !connected {
            if unsafe { WaitForSingleObject(stop, 0) } == WAIT_OBJECT_0 {
                return;
            }
            continue;
        }

        serve(&mut stream, version, show_window, request_exit, stop);
        unsafe {
            let _ = DisconnectNamedPipe(stream.handle.0);
        }
    }
}

fn serve(
    stream: &mut PipeStream,
    version: Option<Version>,
    show_window: &Callback,
    request_exit: &Callback,
    stop: HANDLE,
) {
    while let Some(request) = stream.read_line(SERVER_READ_TIMEOUT, Some(stop)) {
        match request.as_str() {
            IDENTIFY_REQUEST => {
                let identity = format_identity(version, unsafe { GetCurrentProcessId() });
                if !stream.write_line(&identity, EXCHANGE_TIMEOUT) {
                    return;
                }
            }
            SHOW_REQUEST => {
                acknowledge(stream);
                show_window();
                return;
            }
            EXIT_REQUEST => {
                acknowledge(stream);
                request_exit();
                return;
            }
            _ => return,
        }
    }
}

fn acknowledge(stream: &mut PipeStream) {
    if stream.write_line(ACKNOWLEDGEMENT, EXCHANGE_TIMEOUT) {
        // 等待用戶端讀走回應，相當於 WaitForPipeDrain
        unsafe {
            let _ = FlushFileBuffers(stream.handle.0);
        }
    }
}

/// 新啟動的執行個體連線至執行中執行個體的用戶端。
pub struct InstanceClient {
    stream: PipeStream,
    pub running_version: Option<Version>,
    pub running_process_id: u32,
}

impl InstanceClient {
    /// 嘗試連線並識別執行中的執行個體。管道尚未存在時會在逾時內持續重試，與 .NET 的 Connect(timeout) 相同。
    pub fn try_connect(name: &str, timeout: Duration) -> Option<InstanceClient> {
        let path = pipe_path(name);
        let deadline = Instant::now() + timeout;
        let handle = loop {
            let opened = unsafe {
                CreateFileW(
                    path.as_pcwstr(),
                    (GENERIC_READ | GENERIC_WRITE).0,
                    FILE_SHARE_NONE,
                    None,
                    OPEN_EXISTING,
                    FILE_FLAG_OVERLAPPED,
                    None,
                )
            };
            match opened {
                Ok(handle) => break handle,
                Err(error) => {
                    let remaining = deadline.checked_duration_since(Instant::now())?;
                    if error.code() == ERROR_PIPE_BUSY.to_hresult() {
                        unsafe {
                            let _ = WaitNamedPipeW(path.as_pcwstr(), timeout_millis(remaining).max(1));
                        }
                    } else {
                        std::thread::sleep(remaining.min(Duration::from_millis(50)));
                    }
                }
            }
        };

        let stream = PipeStream::new(handle)?;
        let mut client = InstanceClient { stream, running_version: None, running_process_id: 0 };
        let (version, process_id) = parse_identity(client.exchange(IDENTIFY_REQUEST).as_deref())?;
        client.running_version = version;
        client.running_process_id = process_id;
        Some(client)
    }

    /// 要求執行中的執行個體顯示主視窗。
    pub fn request_show(&mut self) -> bool {
        self.exchange(SHOW_REQUEST).as_deref() == Some(ACKNOWLEDGEMENT)
    }

    /// 要求執行中的執行個體結束並釋放系統匣。
    pub fn request_exit(&mut self) -> bool {
        self.exchange(EXIT_REQUEST).as_deref() == Some(ACKNOWLEDGEMENT)
    }

    fn exchange(&mut self, request: &str) -> Option<String> {
        if !self.stream.write_line(request, EXCHANGE_TIMEOUT) {
            return None;
        }

        self.stream.read_line(EXCHANGE_TIMEOUT, None)
    }
}

/// 持有單一執行個體互斥鎖、安裝程式互斥鎖、舊版訊號與管道伺服器。
pub struct InstanceCoordinator {
    server: Option<InstanceServer>,
    legacy_stop: Option<OwnedHandle>,
    legacy_listener: Option<JoinHandle<()>>,
    _legacy_signal: Option<OwnedHandle>,
    _setup_mutex: Option<OwnedHandle>,
    _ownership: Option<OwnedHandle>,
}

impl InstanceCoordinator {
    /// 嘗試取得系統匣持有權；已有其他執行個體時傳回 `None`。
    pub fn try_claim(
        version: Option<Version>,
        show_window: Callback,
        request_exit: Callback,
    ) -> Option<InstanceCoordinator> {
        let name = WideString::new(OWNERSHIP_MUTEX_NAME);
        let ownership = match unsafe { CreateMutexW(None, false, name.as_pcwstr()) } {
            Ok(handle) => {
                let handle = OwnedHandle(handle);
                if unsafe { GetLastError() } == ERROR_ALREADY_EXISTS {
                    return None;
                }
                Some(handle)
            }
            // 無法建立互斥鎖（例如權限不足）時不阻擋啟動，與舊版行為相同
            Err(_) => None,
        };

        let (legacy_signal, legacy_stop, legacy_listener) = register_legacy_signal(show_window.clone());
        Some(InstanceCoordinator {
            server: InstanceServer::start(&pipe_name(), version, show_window, request_exit),
            legacy_stop,
            legacy_listener,
            _legacy_signal: legacy_signal,
            _setup_mutex: create_setup_mutex(),
            _ownership: ownership,
        })
    }

    /// 要求前一個執行個體結束後，帶重試地重新取得持有權。
    pub fn claim_after_handover(
        version: Option<Version>,
        show_window: Callback,
        request_exit: Callback,
    ) -> Option<InstanceCoordinator> {
        for _ in 0..OWNERSHIP_RETRIES {
            if let Some(coordinator) = Self::try_claim(version, show_window.clone(), request_exit.clone()) {
                return Some(coordinator);
            }
            std::thread::sleep(OWNERSHIP_RETRY_DELAY);
        }

        None
    }
}

impl Drop for InstanceCoordinator {
    fn drop(&mut self) {
        self.server = None;
        if let Some(stop) = &self.legacy_stop {
            unsafe {
                let _ = SetEvent(stop.0);
            }
        }
        if let Some(listener) = self.legacy_listener.take() {
            let _ = listener.join();
        }
    }
}

type LegacyRegistration = (Option<OwnedHandle>, Option<OwnedHandle>, Option<JoinHandle<()>>);

fn register_legacy_signal(show_window: Callback) -> LegacyRegistration {
    let name = WideString::new(LEGACY_SHOW_WINDOW_EVENT_NAME);
    let Ok(signal) = (unsafe { CreateEventW(None, false, false, name.as_pcwstr()) }) else {
        return (None, None, None);
    };
    let signal = OwnedHandle(signal);
    let Ok(stop) = (unsafe { CreateEventW(None, true, false, PCWSTR::null()) }) else {
        return (Some(signal), None, None);
    };
    let stop = OwnedHandle(stop);

    let handles = [SendHandle(signal.0), SendHandle(stop.0)];
    let listener = std::thread::Builder::new().name("legacy-signal".into()).spawn(move || {
        let handles = handles;
        let handles = [handles[0].0, handles[1].0];
        loop {
            let waited = unsafe { WaitForMultipleObjects(&handles, false, INFINITE) };
            if waited != WAIT_OBJECT_0 {
                return;
            }
            show_window();
        }
    });

    (Some(signal), Some(stop), listener.ok())
}

/// 建立供 Inno Setup 探測的全域互斥鎖：目前使用者完整控制，Everyone 只有 SYNCHRONIZE 與讀取權限。
fn create_setup_mutex() -> Option<OwnedHandle> {
    let owner = crate::win::current_user_sid().unwrap_or_else(|| "OW".to_string());
    let sddl = WideString::new(&format!("D:(A;;0x1F0001;;;{owner})(A;;0x120000;;;WD)"));
    let mut descriptor = PSECURITY_DESCRIPTOR::default();
    unsafe {
        ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl.as_pcwstr(), SDDL_REVISION_1, &mut descriptor, None)
            .ok()?;
        let attributes = SECURITY_ATTRIBUTES {
            nLength: std::mem::size_of::<SECURITY_ATTRIBUTES>() as u32,
            lpSecurityDescriptor: descriptor.0,
            bInheritHandle: false.into(),
        };
        let name = WideString::new(SETUP_MUTEX_NAME);
        let mutex = CreateMutexW(Some(&attributes), false, name.as_pcwstr());
        LocalFree(Some(HLOCAL(descriptor.0)));
        mutex.ok().map(OwnedHandle)
    }
}

/// 無法透過管道連線時，嘗試觸發舊版事件讓對方顯示視窗。
pub fn try_signal_legacy_instance() -> bool {
    let name = WideString::new(LEGACY_SHOW_WINDOW_EVENT_NAME);
    match unsafe { OpenEventW(EVENT_MODIFY_STATE, false, name.as_pcwstr()) } {
        Ok(handle) => {
            let handle = OwnedHandle(handle);
            unsafe { SetEvent(handle.0) }.is_ok()
        }
        Err(_) => false,
    }
}

/// 等待指定處理程序結束；處理程序已不存在時視為已結束。
pub fn wait_for_process_exit(process_id: u32, timeout: Duration) -> bool {
    match unsafe { OpenProcess(PROCESS_SYNCHRONIZE, false, process_id) } {
        Ok(handle) => {
            let handle = OwnedHandle(handle);
            let waited = unsafe { WaitForSingleObject(handle.0, timeout_millis(timeout)) };
            waited != WAIT_TIMEOUT
        }
        Err(_) => true,
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::atomic::{AtomicUsize, Ordering};
    use std::sync::mpsc;

    const CALLBACK_TIMEOUT: Duration = Duration::from_secs(5);

    fn version(text: &str) -> Option<Version> {
        Version::parse(text)
    }

    #[test]
    fn decide_hands_over_to_the_same_version() {
        assert_eq!(decide(version("1.1.3.0"), version("1.1.3.0")), InstanceAction::ShowRunning);
    }

    #[test]
    fn decide_offers_to_replace_an_older_version() {
        assert_eq!(decide(version("1.1.2.0"), version("1.1.3.0")), InstanceAction::ReplaceRunning);
    }

    #[test]
    fn decide_keeps_the_newer_running_version() {
        assert_eq!(decide(version("1.2.0.0"), version("1.1.3.0")), InstanceAction::ShowNewerRunning);
    }

    #[test]
    fn decide_ignores_the_fourth_component() {
        assert_eq!(decide(version("1.1.3.7"), version("1.1.3.0")), InstanceAction::ShowRunning);
    }

    #[test]
    fn decide_compares_numerically() {
        assert_eq!(decide(version("1.1.9.0"), version("1.1.10.0")), InstanceAction::ReplaceRunning);
    }

    #[test]
    fn decide_treats_a_missing_build_as_zero() {
        assert_eq!(decide(version("1.1"), version("1.1.0")), InstanceAction::ShowRunning);
    }

    #[test]
    fn decide_hands_over_when_a_version_is_unknown() {
        assert_eq!(decide(None, version("1.1.3.0")), InstanceAction::ShowRunning);
        assert_eq!(decide(version("1.1.3.0"), None), InstanceAction::ShowRunning);
    }

    #[test]
    fn identity_survives_the_round_trip() {
        let line = format_identity(version("1.1.3.0"), 4242);
        assert_eq!(line, "THERMOTRAY 1.1.3.0 4242");
        assert_eq!(parse_identity(Some(&line)), Some((version("1.1.3.0"), 4242)));
    }

    #[test]
    fn identity_carries_an_unknown_version() {
        assert_eq!(parse_identity(Some(&format_identity(None, 7))), Some((None, 7)));
    }

    #[test]
    fn identity_rejects_anything_else() {
        for line in [
            None,
            Some(""),
            Some("OK"),
            Some("SOMETHINGELSE 1.1.3 42"),
            Some("THERMOTRAY 1.1.3"),
            Some("THERMOTRAY 1.1.3 notaprocess"),
            Some("THERMOTRAY 1.1.3 0"),
            Some("THERMOTRAY 1.1.3 -1"),
        ] {
            assert_eq!(parse_identity(line), None, "{line:?}");
        }
    }

    #[test]
    fn version_formatting_matches_the_csharp_release() {
        assert_eq!(format_version(Some(Version::new(1, 1, 2, 0))), "v1.1.2");
        assert_eq!(format_version(Version::parse("1.1")), "v1.1.0");
        assert_eq!(format_version(None), "");
        assert_eq!(Version::parse("1.1.3").unwrap().to_string(), "1.1.3");
        assert!(Version::parse("1").is_none());
        assert!(Version::parse("1.a").is_none());
        assert!(Version::parse("1.2.3.4.5").is_none());
    }

    #[test]
    fn product_version_matches_the_cargo_manifest() {
        let expected = format!("v{}", env!("CARGO_PKG_VERSION"));
        assert_eq!(format_version(Some(product_version())), expected);
        assert_eq!(product_version().revision, 0);
    }

    fn unique_pipe_name() -> String {
        static COUNTER: AtomicUsize = AtomicUsize::new(0);
        format!("ThermoTray.Tests.{}.{}", std::process::id(), COUNTER.fetch_add(1, Ordering::Relaxed))
    }

    fn start_server(name: &str, version: Option<Version>) -> (InstanceServer, mpsc::Receiver<&'static str>) {
        let (sender, receiver) = mpsc::channel();
        let show_sender = std::sync::Mutex::new(sender.clone());
        let exit_sender = std::sync::Mutex::new(sender);
        let server = InstanceServer::start(
            name,
            version,
            Arc::new(move || {
                let _ = show_sender.lock().unwrap().send("show");
            }),
            Arc::new(move || {
                let _ = exit_sender.lock().unwrap().send("exit");
            }),
        )
        .unwrap();
        (server, receiver)
    }

    #[test]
    fn a_connecting_launch_learns_the_running_version_and_raises_its_window() {
        let name = unique_pipe_name();
        let (_server, events) = start_server(&name, version("1.2.3.0"));

        let mut client = InstanceClient::try_connect(&name, Duration::from_secs(2)).unwrap();
        assert_eq!(client.running_version, version("1.2.3.0"));
        assert_eq!(client.running_process_id, std::process::id());
        assert!(client.request_show());
        assert_eq!(events.recv_timeout(CALLBACK_TIMEOUT), Ok("show"));
    }

    #[test]
    fn a_replacing_launch_is_acknowledged_before_the_running_instance_exits() {
        let name = unique_pipe_name();
        let (_server, events) = start_server(&name, version("1.2.3.0"));

        let mut client = InstanceClient::try_connect(&name, Duration::from_secs(2)).unwrap();
        assert!(client.request_exit());
        assert_eq!(events.recv_timeout(CALLBACK_TIMEOUT), Ok("exit"));
    }

    #[test]
    fn the_running_instance_keeps_answering_later_launches() {
        let name = unique_pipe_name();
        let (_server, events) = start_server(&name, version("1.2.3.0"));

        for _ in 0..2 {
            let mut client = InstanceClient::try_connect(&name, Duration::from_secs(2)).unwrap();
            assert!(client.request_show());
            drop(client);
            assert_eq!(events.recv_timeout(CALLBACK_TIMEOUT), Ok("show"));
        }
    }

    #[test]
    fn an_unreachable_instance_reports_no_client() {
        assert!(InstanceClient::try_connect(&unique_pipe_name(), Duration::from_millis(300)).is_none());
    }

    #[test]
    fn a_dropped_server_stops_answering_connections() {
        let name = unique_pipe_name();
        let (server, _events) = start_server(&name, None);
        drop(server);
        assert!(InstanceClient::try_connect(&name, Duration::from_millis(300)).is_none());
    }
}
