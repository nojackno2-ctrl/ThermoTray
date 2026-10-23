//! 隨 Windows 啟動設定。
//!
//! ThermoTray 需要系統管理員權限（requireAdministrator），`HKCU\Run` 會在登入時被 Windows 阻擋，
//! 因此改用工作排程器建立 `HighestAvailable` 的登入觸發工作。

use std::io::Read;
use std::os::windows::process::CommandExt;
use std::path::Path;
use std::process::{Command, Stdio};
use std::time::{Duration, Instant};

use windows::Win32::System::Registry::{RegDeleteKeyValueW, HKEY_CURRENT_USER};

use crate::win::WideString;

const RUN_KEY_PATH: &str = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
const VALUE_NAME: &str = "ThermoTray";
const TASK_NAME: &str = "ThermoTray";
const TIMEOUT: Duration = Duration::from_secs(10);
const CREATE_NO_WINDOW: u32 = 0x0800_0000;

/// 寫入排程 Source 欄位的識別標記。XML 定義有重大變更時遞增，啟動時若缺少此標記即自動重建。
pub const DEFINITION_MARKER: &str = "ThermoTray startup task (definition 2)";

#[derive(Debug)]
pub struct StartupError(pub String);

/// 排程工作是否存在且為最新定義。舊版 `schtasks` 預設排程會在改用電池或執行 3 天後結束 ThermoTray。
pub fn is_up_to_date() -> bool {
    run_schtasks(&["/Query", "/TN", TASK_NAME, "/XML"]).is_some_and(|output| output.contains(DEFINITION_MARKER))
}

/// 開啟或關閉開機自動啟動。
pub fn set_enabled(enabled: bool) -> Result<(), StartupError> {
    if !enabled {
        let _ = run_schtasks(&["/Delete", "/TN", TASK_NAME, "/F"]);
        remove_run_value();
        return Ok(());
    }

    let executable = std::env::current_exe().map_err(|error| StartupError(error.to_string()))?;
    if !crate::win::is_elevated() || !create_elevated_task(&executable) {
        return Err(StartupError("Could not create the elevated startup task.".into()));
    }

    remove_run_value();
    Ok(())
}

/// 將 XML 定義寫入隨機命名的暫存檔後以 `schtasks /Create /XML` 匯入。
fn create_elevated_task(executable: &Path) -> bool {
    let random = random_suffix();
    let definition_path = std::env::temp_dir().join(format!("ThermoTray-{random}.xml"));
    let user_id = current_user_id();
    let definition = build_task_definition(&executable.to_string_lossy(), &user_id);

    // schtasks 要求 UTF-16 LE（含 BOM）的 XML 檔
    let mut bytes = vec![0xFF, 0xFE];
    bytes.extend(definition.encode_utf16().flat_map(u16::to_le_bytes));

    let created = std::fs::OpenOptions::new()
        .write(true)
        .create_new(true)
        .open(&definition_path)
        .and_then(|mut file| std::io::Write::write_all(&mut file, &bytes))
        .is_ok()
        && run_schtasks(&["/Create", "/TN", TASK_NAME, "/XML", &definition_path.to_string_lossy(), "/F"]).is_some();

    let _ = std::fs::remove_file(&definition_path);
    created
}

/// 不可預測的暫存檔名稱，避免預測性路徑替換。
fn random_suffix() -> String {
    use std::collections::hash_map::RandomState;
    use std::hash::{BuildHasher, Hasher};

    let mut hasher = RandomState::new().build_hasher();
    hasher.write_u128(Instant::now().elapsed().as_nanos());
    hasher.write_u32(std::process::id());
    format!("{:016x}", hasher.finish())
}

/// 優先使用 SID，使用者改名後排程仍有效。
fn current_user_id() -> String {
    crate::win::current_user_sid().unwrap_or_else(|| {
        let domain = std::env::var("USERDOMAIN").unwrap_or_default();
        let user = std::env::var("USERNAME").unwrap_or_default();
        format!("{domain}\\{user}")
    })
}

/// 完整的排程工作 XML。明確停用所有可能中止工作的設定（改用電池、閒置結束、72 小時時限）。
pub fn build_task_definition(executable_path: &str, user_id: &str) -> String {
    let marker = escape(DEFINITION_MARKER);
    let user = escape(user_id);
    let command = escape(executable_path);
    format!(
        r#"<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Source>{marker}</Source>
    <Description>Starts ThermoTray at logon, elevated and minimised to the notification area.</Description>
  </RegistrationInfo>
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
      <UserId>{user}</UserId>
    </LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id="Author">
      <UserId>{user}</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>false</AllowHardTerminate>
    <StartWhenAvailable>false</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings>
      <StopOnIdleEnd>false</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>{command}</Command>
      <Arguments>--minimized</Arguments>
    </Exec>
  </Actions>
</Task>"#
    )
}

/// XML 特殊字元轉義。
fn escape(value: &str) -> String {
    let mut escaped = String::with_capacity(value.len());
    for character in value.chars() {
        match character {
            '<' => escaped.push_str("&lt;"),
            '>' => escaped.push_str("&gt;"),
            '"' => escaped.push_str("&quot;"),
            '\'' => escaped.push_str("&apos;"),
            '&' => escaped.push_str("&amp;"),
            other => escaped.push(other),
        }
    }

    escaped
}

/// 執行 `schtasks.exe`；成功（ExitCode 0）時傳回標準輸出。逾時會終止處理程序。
fn run_schtasks(arguments: &[&str]) -> Option<String> {
    let mut child = Command::new("schtasks.exe")
        .args(arguments)
        .stdin(Stdio::null())
        .stdout(Stdio::piped())
        .stderr(Stdio::null())
        .creation_flags(CREATE_NO_WINDOW)
        .spawn()
        .ok()?;

    // 在背景讀取輸出，避免管線緩衝區塞滿造成死鎖
    let mut stdout = child.stdout.take()?;
    let reader = std::thread::spawn(move || {
        let mut output = Vec::new();
        let _ = stdout.read_to_end(&mut output);
        output
    });

    let deadline = Instant::now() + TIMEOUT;
    let status = loop {
        match child.try_wait() {
            Ok(Some(status)) => break status,
            Ok(None) if Instant::now() < deadline => std::thread::sleep(Duration::from_millis(25)),
            _ => {
                let _ = child.kill();
                let _ = child.wait();
                return None;
            }
        }
    };

    let output = reader.join().unwrap_or_default();
    status.success().then(|| String::from_utf8_lossy(&output).into_owned())
}

/// 清除舊版寫入的 `HKCU\Run` 值（已移轉至工作排程器）。
fn remove_run_value() {
    let key = WideString::new(RUN_KEY_PATH);
    let value = WideString::new(VALUE_NAME);
    unsafe {
        let _ = RegDeleteKeyValueW(HKEY_CURRENT_USER, key.as_pcwstr(), value.as_pcwstr());
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const EXECUTABLE: &str = r"C:\Users\someone\AppData\Local\Programs\ThermoTray\ThermoTray.exe";
    const USER: &str = "S-1-5-21-1111111111-2222222222-3333333333-1001";

    fn definition() -> String {
        build_task_definition(EXECUTABLE, USER)
    }

    fn element<'a>(xml: &'a str, name: &str) -> Vec<&'a str> {
        let open = format!("<{name}>");
        let close = format!("</{name}>");
        xml.match_indices(&open)
            .filter_map(|(start, _)| {
                let value_start = start + open.len();
                xml[value_start..].find(&close).map(|end| &xml[value_start..value_start + end])
            })
            .collect()
    }

    #[test]
    fn declares_the_encoding_schtasks_expects() {
        assert!(definition().starts_with(r#"<?xml version="1.0" encoding="UTF-16"?>"#));
    }

    #[test]
    fn turns_off_every_setting_that_can_stop_the_task() {
        let xml = definition();
        for setting in [
            "DisallowStartIfOnBatteries",
            "StopIfGoingOnBatteries",
            "AllowHardTerminate",
            "RunOnlyIfIdle",
            "StopOnIdleEnd",
        ] {
            assert_eq!(element(&xml, setting), ["false"], "{setting}");
        }
    }

    #[test]
    fn places_no_time_limit_on_how_long_thermotray_runs() {
        assert_eq!(element(&definition(), "ExecutionTimeLimit"), ["PT0S"]);
    }

    #[test]
    fn runs_elevated_as_the_current_user_at_logon() {
        let xml = definition();
        assert_eq!(element(&xml, "RunLevel"), ["HighestAvailable"]);
        assert_eq!(element(&xml, "LogonType"), ["InteractiveToken"]);
        assert_eq!(element(&xml, "UserId"), [USER, USER]);
    }

    #[test]
    fn starts_the_executable_minimised() {
        let xml = definition();
        assert_eq!(element(&xml, "Command"), [EXECUTABLE]);
        assert_eq!(element(&xml, "Arguments"), ["--minimized"]);
    }

    #[test]
    fn carries_the_definition_marker_launch_looks_for() {
        assert_eq!(element(&definition(), "Source"), [DEFINITION_MARKER]);
    }

    #[test]
    fn escapes_the_path_instead_of_breaking_the_document() {
        let awkward = r#"C:\Tools & <Stuff> "x" 'y'\ThermoTray.exe"#;
        let xml = build_task_definition(awkward, USER);
        assert_eq!(
            element(&xml, "Command"),
            [r#"C:\Tools &amp; &lt;Stuff&gt; &quot;x&quot; &apos;y&apos;\ThermoTray.exe"#]
        );
    }

    #[test]
    fn temporary_definition_names_are_unpredictable() {
        assert_ne!(random_suffix(), random_suffix());
    }
}
