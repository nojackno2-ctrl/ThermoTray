//! 應用程式設定 (`%LOCALAPPDATA%\ThermoTray\settings.json`) 的載入與原子儲存。
//! 欄位名稱與舊版 C# 的 System.Text.Json 輸出相同，升級後既有設定可直接沿用。

use std::collections::BTreeMap;
use std::path::{Path, PathBuf};

use serde::{Deserialize, Deserializer, Serialize};

use crate::localizer::DEFAULT_LANGUAGE;

/// 應用程式設定資料模型。
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase", default)]
pub struct AppSettings {
    /// 是否隨 Windows 登入自動啟動。
    pub start_with_windows: bool,
    /// 關閉主視窗時是否縮小至系統匣而非結束程式。
    pub hide_when_closed: bool,
    /// UI 語言（`zh-TW` 或 `en-US`）。
    #[serde(deserialize_with = "language")]
    pub language: String,
    pub show_cpu_usage_in_tray: bool,
    pub show_cpu_temperature_in_tray: bool,
    /// 每張 GPU（以拓撲識別碼為 Key）的系統匣顯示選項。
    #[serde(deserialize_with = "gpu_tray_settings")]
    pub gpu_tray_settings: BTreeMap<String, TrayDisplaySettings>,
}

impl Default for AppSettings {
    fn default() -> Self {
        Self {
            start_with_windows: false,
            hide_when_closed: true,
            language: DEFAULT_LANGUAGE.to_string(),
            show_cpu_usage_in_tray: true,
            show_cpu_temperature_in_tray: true,
            gpu_tray_settings: BTreeMap::new(),
        }
    }
}

/// 單一 GPU 的系統匣顯示選項。
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase", default)]
pub struct TrayDisplaySettings {
    pub show_usage: bool,
    pub show_temperature: bool,
}

impl Default for TrayDisplaySettings {
    fn default() -> Self {
        Self { show_usage: true, show_temperature: true }
    }
}

/// `null` 的語言退回預設語言。
fn language<'de, D>(deserializer: D) -> Result<String, D::Error>
where
    D: Deserializer<'de>,
{
    Ok(Option::<String>::deserialize(deserializer)?.unwrap_or_else(|| DEFAULT_LANGUAGE.to_string()))
}

/// `null` 的字典或字典值都退回預設，與舊版 `??= new()` 的處理相同。
fn gpu_tray_settings<'de, D>(deserializer: D) -> Result<BTreeMap<String, TrayDisplaySettings>, D::Error>
where
    D: Deserializer<'de>,
{
    let map = Option::<BTreeMap<String, Option<TrayDisplaySettings>>>::deserialize(deserializer)?;
    Ok(map.unwrap_or_default().into_iter().map(|(id, settings)| (id, settings.unwrap_or_default())).collect())
}

/// 設定檔服務。
pub struct SettingsService {
    path: PathBuf,
}

impl Default for SettingsService {
    fn default() -> Self {
        Self::new()
    }
}

impl SettingsService {
    pub fn new() -> Self {
        Self { path: crate::win::local_app_data().join("ThermoTray").join("settings.json") }
    }

    #[cfg(test)]
    pub fn at(path: PathBuf) -> Self {
        Self { path }
    }

    /// 載入設定；檔案不存在或毀損時退回預設值，不阻礙感測器運作。
    pub fn load(&self) -> AppSettings {
        std::fs::read(&self.path).ok().and_then(|bytes| parse(&bytes)).unwrap_or_default()
    }

    /// 先寫入 `.tmp` 再取代原檔，避免中斷時留下半份設定。
    pub fn save(&self, settings: &AppSettings) -> std::io::Result<()> {
        if let Some(directory) = self.path.parent() {
            std::fs::create_dir_all(directory)?;
        }

        let temporary = temporary_path(&self.path);
        let json = serde_json::to_vec(settings).map_err(std::io::Error::other)?;
        let result = std::fs::write(&temporary, json).and_then(|()| std::fs::rename(&temporary, &self.path));
        if result.is_err() {
            // 殘留的暫存檔不影響運作，下次儲存時會覆蓋
            let _ = std::fs::remove_file(&temporary);
        }

        result
    }
}

fn parse(bytes: &[u8]) -> Option<AppSettings> {
    // 容忍其他工具寫入的 UTF-8 BOM
    let bytes = bytes.strip_prefix(b"\xEF\xBB\xBF").unwrap_or(bytes);
    serde_json::from_slice::<Option<AppSettings>>(bytes).ok().map(Option::unwrap_or_default)
}

fn temporary_path(path: &Path) -> PathBuf {
    let mut name = path.as_os_str().to_owned();
    name.push(".tmp");
    PathBuf::from(name)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn new_settings_show_every_metric_by_default() {
        let settings = AppSettings::default();
        assert!(settings.show_cpu_usage_in_tray);
        assert!(settings.show_cpu_temperature_in_tray);
        assert!(settings.hide_when_closed);
        assert!(!settings.start_with_windows);
        assert_eq!(settings.language, DEFAULT_LANGUAGE);
        assert!(settings.gpu_tray_settings.is_empty());
    }

    #[test]
    fn reads_settings_written_by_the_csharp_release() {
        let json = br#"{"StartWithWindows":true,"HideWhenClosed":false,"Language":"en-US","ShowCpuUsageInTray":false,"ShowCpuTemperatureInTray":true,"GpuTraySettings":{"GpuNvidia:NVIDIA GeForce RTX 4080:0":{"ShowUsage":false,"ShowTemperature":true}}}"#;
        let settings = parse(json).unwrap();
        assert!(settings.start_with_windows);
        assert!(!settings.hide_when_closed);
        assert_eq!(settings.language, "en-US");
        assert!(!settings.show_cpu_usage_in_tray);
        let gpu = settings.gpu_tray_settings["GpuNvidia:NVIDIA GeForce RTX 4080:0"];
        assert!(!gpu.show_usage);
        assert!(gpu.show_temperature);
    }

    #[test]
    fn missing_and_null_fields_take_their_defaults() {
        let settings = parse(br#"{"GpuTraySettings":null,"Language":null}"#).unwrap();
        assert_eq!(settings, AppSettings::default());

        let settings = parse(br#"{"GpuTraySettings":{"a":null}}"#).unwrap();
        assert_eq!(settings.gpu_tray_settings["a"], TrayDisplaySettings::default());

        assert_eq!(parse(b"null").unwrap(), AppSettings::default());
        assert_eq!(parse(b"\xEF\xBB\xBF{}").unwrap(), AppSettings::default());
    }

    #[test]
    fn corrupt_files_are_rejected() {
        assert!(parse(b"{not json").is_none());
    }

    #[test]
    fn save_then_load_round_trips_atomically() {
        let directory = std::env::temp_dir().join(format!("thermotray-settings-{}", std::process::id()));
        let path = directory.join("settings.json");
        let service = SettingsService::at(path.clone());
        let mut settings = AppSettings { start_with_windows: true, ..AppSettings::default() };
        settings
            .gpu_tray_settings
            .insert("gpu".into(), TrayDisplaySettings { show_usage: false, show_temperature: true });

        service.save(&settings).unwrap();
        assert_eq!(service.load(), settings);
        assert!(!temporary_path(&path).exists());

        // 欄位名稱維持 PascalCase，舊版程式仍可讀取
        let written = std::fs::read_to_string(&path).unwrap();
        assert!(written.contains("\"StartWithWindows\":true"));
        assert!(written.contains("\"ShowUsage\":false"));

        let _ = std::fs::remove_dir_all(directory);
    }

    #[test]
    fn a_missing_file_loads_the_defaults() {
        let service = SettingsService::at(std::env::temp_dir().join("thermotray-does-not-exist").join("settings.json"));
        assert_eq!(service.load(), AppSettings::default());
    }
}
