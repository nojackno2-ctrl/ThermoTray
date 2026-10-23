//! 小型 Win32 輔助函式：字串轉換、已知資料夾、提權檢查、工作物件與訊息方塊。

use std::ffi::OsString;
use std::os::windows::ffi::{OsStrExt, OsStringExt};
use std::os::windows::io::AsRawHandle;
use std::path::PathBuf;

use windows::core::{PCWSTR, PWSTR};
use windows::Win32::Foundation::{CloseHandle, LocalFree, HANDLE, HLOCAL, HWND};
use windows::Win32::Globalization::{GetLocaleInfoEx, LOCALE_SDECIMAL};
use windows::Win32::Security::Authorization::ConvertSidToStringSidW;
use windows::Win32::Security::{
    GetTokenInformation, TokenElevation, TokenUser, TOKEN_ELEVATION, TOKEN_QUERY, TOKEN_USER,
};
use windows::Win32::System::Com::CoTaskMemFree;
use windows::Win32::System::JobObjects::{
    AssignProcessToJobObject, CreateJobObjectW, JobObjectExtendedLimitInformation, SetInformationJobObject,
    JOBOBJECT_EXTENDED_LIMIT_INFORMATION, JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
};
use windows::Win32::System::Threading::{GetCurrentProcess, OpenProcessToken};
use windows::Win32::UI::Shell::{FOLDERID_LocalAppData, SHGetKnownFolderPath, ShellExecuteW, KF_FLAG_DEFAULT};
use windows::Win32::UI::WindowsAndMessaging::{MessageBoxW, MESSAGEBOX_RESULT, MESSAGEBOX_STYLE, SW_SHOWNORMAL};

/// 以 NUL 結尾的 UTF-16 字串。
pub struct WideString(Vec<u16>);

impl WideString {
    pub fn new(text: &str) -> Self {
        Self(text.encode_utf16().chain(std::iter::once(0)).collect())
    }

    pub fn from_os(text: &std::ffi::OsStr) -> Self {
        Self(text.encode_wide().chain(std::iter::once(0)).collect())
    }

    pub fn as_pcwstr(&self) -> PCWSTR {
        PCWSTR(self.0.as_ptr())
    }
}

/// 資源識別碼（MAKEINTRESOURCE）。
pub fn make_int_resource(id: u16) -> PCWSTR {
    PCWSTR(std::ptr::without_provenance(usize::from(id)))
}

/// 應用程式圖示的資源識別碼（assets/ThermoTray.rc）。
pub const APP_ICON_ID: u16 = 1;

/// 將 UTF-16 文字轉為 `String`，於第一個 NUL 截斷。
pub fn from_wide(text: &[u16]) -> String {
    let end = text.iter().position(|&unit| unit == 0).unwrap_or(text.len());
    String::from_utf16_lossy(&text[..end])
}

/// 複製到固定長度的 UTF-16 緩衝區（例如 NOTIFYICONDATAW.szTip），保留結尾 NUL。
pub fn copy_to_wide_buffer(text: &str, buffer: &mut [u16]) {
    buffer.fill(0);
    let capacity = buffer.len().saturating_sub(1);
    for (slot, unit) in buffer.iter_mut().take(capacity).zip(text.encode_utf16()) {
        *slot = unit;
    }
}

/// `%LOCALAPPDATA%`。
pub fn local_app_data() -> PathBuf {
    unsafe {
        match SHGetKnownFolderPath(&FOLDERID_LocalAppData, KF_FLAG_DEFAULT, None) {
            Ok(path) => {
                let value = OsString::from_wide(path.as_wide());
                CoTaskMemFree(Some(path.0 as *const _));
                PathBuf::from(value)
            }
            Err(_) => std::env::var_os("LOCALAPPDATA").map(PathBuf::from).unwrap_or_else(std::env::temp_dir),
        }
    }
}

/// 擁有並自動關閉的 Win32 控制代碼。
pub struct OwnedHandle(pub HANDLE);

impl Drop for OwnedHandle {
    fn drop(&mut self) {
        if !self.0.is_invalid() {
            unsafe {
                let _ = CloseHandle(self.0);
            }
        }
    }
}

unsafe impl Send for OwnedHandle {}
unsafe impl Sync for OwnedHandle {}

fn process_token() -> Option<OwnedHandle> {
    let mut token = HANDLE::default();
    unsafe { OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &mut token).ok()? };
    Some(OwnedHandle(token))
}

/// 目前處理程序是否以系統管理員權限（已提權）執行。PawnIO 只服務已提權的處理程序。
pub fn is_elevated() -> bool {
    let Some(token) = process_token() else {
        return false;
    };

    let mut elevation = TOKEN_ELEVATION::default();
    let mut length = 0;
    let queried = unsafe {
        GetTokenInformation(
            token.0,
            TokenElevation,
            Some(&mut elevation as *mut _ as *mut _),
            std::mem::size_of::<TOKEN_ELEVATION>() as u32,
            &mut length,
        )
    };
    queried.is_ok() && elevation.TokenIsElevated != 0
}

/// 目前使用者的 SID 字串（例如 `S-1-5-21-...`）。
pub fn current_user_sid() -> Option<String> {
    let token = process_token()?;
    let mut length = 0;
    unsafe {
        let _ = GetTokenInformation(token.0, TokenUser, None, 0, &mut length);
    }
    if length == 0 {
        return None;
    }

    // 以 u64 對齊緩衝區，TOKEN_USER 內含指標
    let mut buffer = vec![0u64; (length as usize).div_ceil(8)];
    unsafe {
        GetTokenInformation(token.0, TokenUser, Some(buffer.as_mut_ptr() as *mut _), length, &mut length).ok()?;
        let user = &*(buffer.as_ptr() as *const TOKEN_USER);
        let mut text = PWSTR::null();
        ConvertSidToStringSidW(user.User.Sid, &mut text).ok()?;
        let sid = text.to_string().ok();
        LocalFree(Some(HLOCAL(text.0 as *mut _)));
        sid
    }
}

/// 使用者語系的小數點符號，介面數值依此格式化（系統匣數字則一律使用整數）。
pub fn decimal_separator() -> String {
    let mut buffer = [0u16; 8];
    let length = unsafe { GetLocaleInfoEx(PCWSTR::null(), LOCALE_SDECIMAL, Some(&mut buffer)) };
    if length <= 1 {
        return ".".to_string();
    }

    let separator = from_wide(&buffer);
    if separator.is_empty() {
        ".".to_string()
    } else {
        separator
    }
}

/// 關閉時終止所有成員處理程序的工作物件，確保 sidecar 不會比主程式活得更久。
pub struct KillOnCloseJob {
    _job: Option<OwnedHandle>,
}

impl KillOnCloseJob {
    pub fn assign(child: &std::process::Child) -> Self {
        unsafe {
            let Ok(job) = CreateJobObjectW(None, PCWSTR::null()) else {
                return Self { _job: None };
            };
            let job = OwnedHandle(job);

            let mut limits = JOBOBJECT_EXTENDED_LIMIT_INFORMATION::default();
            limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
            let configured = SetInformationJobObject(
                job.0,
                JobObjectExtendedLimitInformation,
                &limits as *const _ as *const _,
                std::mem::size_of::<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>() as u32,
            );
            if configured.is_err() {
                return Self { _job: None };
            }

            let process = HANDLE(child.as_raw_handle());
            if AssignProcessToJobObject(job.0, process).is_err() {
                return Self { _job: None };
            }

            Self { _job: Some(job) }
        }
    }
}

/// 以系統預設程式開啟網址。
pub fn open_url(url: &str) -> bool {
    let operation = WideString::new("open");
    let target = WideString::new(url);
    let result = unsafe {
        ShellExecuteW(None, operation.as_pcwstr(), target.as_pcwstr(), PCWSTR::null(), PCWSTR::null(), SW_SHOWNORMAL)
    };

    // ShellExecuteW 以大於 32 的值表示成功
    result.0 as isize > 32
}

/// 顯示訊息方塊。
pub fn message_box(owner: Option<HWND>, text: &str, style: MESSAGEBOX_STYLE) -> MESSAGEBOX_RESULT {
    let text = WideString::new(text);
    let caption = WideString::new("ThermoTray");
    unsafe { MessageBoxW(owner, text.as_pcwstr(), caption.as_pcwstr(), style) }
}

/// ISO 8601 本地時間與 UTC 位移，例如 `2026-10-23T22:28:00.123+08:00`。
pub fn local_timestamp() -> String {
    use windows::Win32::Foundation::{FILETIME, SYSTEMTIME};
    use windows::Win32::System::SystemInformation::{GetLocalTime, GetSystemTime};
    use windows::Win32::System::Time::SystemTimeToFileTime;

    fn ticks(time: &SYSTEMTIME) -> i64 {
        let mut file_time = FILETIME::default();
        unsafe {
            let _ = SystemTimeToFileTime(time, &mut file_time);
        }
        ((u64::from(file_time.dwHighDateTime) << 32) | u64::from(file_time.dwLowDateTime)) as i64
    }

    let (local, utc) = unsafe { (GetLocalTime(), GetSystemTime()) };
    // FILETIME 以 100 奈秒為單位；四捨五入到分鐘以消除兩次呼叫之間的差距
    let offset_minutes = ((ticks(&local) - ticks(&utc)) as f64 / 600_000_000.0).round() as i64;
    let sign = if offset_minutes < 0 { '-' } else { '+' };
    let offset = offset_minutes.abs();
    format!(
        "{:04}-{:02}-{:02}T{:02}:{:02}:{:02}.{:03}{}{:02}:{:02}",
        local.wYear,
        local.wMonth,
        local.wDay,
        local.wHour,
        local.wMinute,
        local.wSecond,
        local.wMilliseconds,
        sign,
        offset / 60,
        offset % 60
    )
}
