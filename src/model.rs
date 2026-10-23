//! 主視窗與系統匣共用的顯示狀態（舊版 MainViewModel / GpuViewModel 的對應）。
//! 不依賴任何 Win32 API，所有狀態規則都可直接單元測試。

use std::time::Duration;

use crate::instance::{format_version, product_version};
use crate::localizer::{format, Localizer};
use crate::reading::{
    sensor_name, tray_digits, GpuReading, HardwareSnapshot, TemperatureReading, Tenths, UtilizationReading,
    TRAY_PLACEHOLDER,
};
use crate::sensors::DriverStatus;
use crate::settings::{AppSettings, TrayDisplaySettings};

/// 主視窗顯示時的採樣週期。
pub const VISIBLE_POLLING_INTERVAL: Duration = Duration::from_secs(1);

/// 隱藏至系統匣時的採樣週期；背景執行時降低頻率可減半資源開銷。
pub const HIDDEN_POLLING_INTERVAL: Duration = Duration::from_secs(2);

/// 判定 GPU 不存在前允許的連續無讀值次數。
const MISSING_GPU_GRACE_SAMPLES: u32 = 5;

/// 尚未取得第一份讀值時顯示的文字。
const PENDING: &str = "…";

pub fn polling_interval(window_visible: bool) -> Duration {
    if window_visible {
        VISIBLE_POLLING_INTERVAL
    } else {
        HIDDEN_POLLING_INTERVAL
    }
}

/// 單一 GPU 的顯示狀態。
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct GpuState {
    pub id: String,
    /// 「GPU 0」「GPU 1」等序號標題。
    pub display_name: String,
    pub device_name: String,
    pub usage: String,
    pub temperature: String,
    pub usage_value: Option<Tenths>,
    pub temperature_value: Option<Tenths>,
    /// 卡片底部的感測器名稱。
    pub source: String,
    pub usage_tray_digits: String,
    pub temperature_tray_digits: String,
}

impl GpuState {
    fn new(id: String) -> Self {
        Self {
            id,
            display_name: String::new(),
            device_name: String::new(),
            usage: TRAY_PLACEHOLDER.to_string(),
            temperature: TRAY_PLACEHOLDER.to_string(),
            usage_value: None,
            temperature_value: None,
            source: String::new(),
            usage_tray_digits: TRAY_PLACEHOLDER.to_string(),
            temperature_tray_digits: TRAY_PLACEHOLDER.to_string(),
        }
    }
}

/// 套用快照後的變化摘要，供系統匣判斷是否需要重建圖示。
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct Changes {
    /// GPU 清單有新增或移除。
    pub gpu_list_changed: bool,
}

/// 主視窗與系統匣圖示共用的完整狀態。
pub struct MainState {
    pub settings: AppSettings,
    pub localizer: Localizer,
    decimal_separator: String,
    pub version: String,

    pub cpu_temperature: String,
    pub cpu_usage: String,
    pub cpu_temperature_value: Option<Tenths>,
    pub cpu_usage_value: Option<Tenths>,
    pub cpu_tray_digits: String,
    pub cpu_usage_tray_digits: String,
    pub cpu_device_name: String,
    pub cpu_source: String,
    last_cpu_reading: Option<TemperatureReading>,
    last_cpu_usage: Option<UtilizationReading>,

    pub gpus: Vec<GpuState>,
    pub status_message: String,
    pub driver_action_visible: bool,
    pub gpu_present: bool,
    gpu_ever_reported: bool,
    gpu_missing_samples: u32,
}

impl MainState {
    pub fn new(mut settings: AppSettings, decimal_separator: String) -> Self {
        let localizer = Localizer::new(&settings.language);
        settings.language = localizer.language().to_string();
        let status_message = localizer.get("WaitingForSensors").to_string();
        Self {
            settings,
            localizer,
            decimal_separator,
            version: format_version(Some(product_version())),
            cpu_temperature: PENDING.to_string(),
            cpu_usage: PENDING.to_string(),
            cpu_temperature_value: None,
            cpu_usage_value: None,
            cpu_tray_digits: TRAY_PLACEHOLDER.to_string(),
            cpu_usage_tray_digits: TRAY_PLACEHOLDER.to_string(),
            cpu_device_name: String::new(),
            cpu_source: String::new(),
            last_cpu_reading: None,
            last_cpu_usage: None,
            gpus: Vec::new(),
            status_message,
            driver_action_visible: false,
            gpu_present: true,
            gpu_ever_reported: false,
            gpu_missing_samples: 0,
        }
    }

    pub fn t<'a>(&self, key: &'a str) -> &'a str {
        self.localizer.get(key)
    }

    /// 將讀到的硬體快照套用至顯示狀態。
    pub fn apply_snapshot(&mut self, snapshot: &HardwareSnapshot, driver: &DriverStatus) -> Changes {
        if self.last_cpu_reading.as_ref() != Some(&snapshot.cpu_temperature) {
            self.last_cpu_reading = Some(snapshot.cpu_temperature.clone());
            self.cpu_temperature = self.format_temperature(snapshot.cpu_temperature.celsius);
            self.cpu_temperature_value = snapshot.cpu_temperature.celsius;
            self.cpu_tray_digits = tray_digits(snapshot.cpu_temperature.celsius);
            self.cpu_device_name = if snapshot.cpu_temperature.is_available() {
                snapshot.cpu_temperature.device_name.clone()
            } else {
                snapshot.cpu_usage.device_name.clone()
            };
            self.cpu_source = sensor_name(&snapshot.cpu_temperature.source).to_string();
        }

        if self.last_cpu_usage.as_ref() != Some(&snapshot.cpu_usage) {
            self.last_cpu_usage = Some(snapshot.cpu_usage.clone());
            self.cpu_usage = self.format_usage(snapshot.cpu_usage.percent);
            self.cpu_usage_value = snapshot.cpu_usage.percent;
            self.cpu_usage_tray_digits = tray_digits(snapshot.cpu_usage.percent);
            if !snapshot.cpu_temperature.is_available() {
                self.cpu_device_name = snapshot.cpu_usage.device_name.clone();
                self.cpu_source = sensor_name(&snapshot.cpu_usage.source).to_string();
            }
        }

        let changes = self.apply_gpu_readings(&snapshot.gpus);
        self.update_gpu_presence(&snapshot.gpus);
        self.status_message = self.availability_message(snapshot, driver);
        changes
    }

    /// 感測器讀取失敗時只更新狀態列，保留最後一份可信讀值的顯示。
    pub fn apply_sensor_error(&mut self, key: &str) {
        self.status_message = self.t(key).to_string();
    }

    /// 將每張 GPU 同步至對應的顯示狀態，並移除已離開拓撲的裝置。
    fn apply_gpu_readings(&mut self, readings: &[GpuReading]) -> Changes {
        let mut changes = Changes::default();
        for (index, reading) in readings.iter().enumerate() {
            let position = match self.gpus.iter().position(|gpu| gpu.id == reading.id) {
                Some(position) => position,
                None => {
                    self.settings.gpu_tray_settings.entry(reading.id.clone()).or_default();
                    self.gpus.push(GpuState::new(reading.id.clone()));
                    changes.gpu_list_changed = true;
                    self.gpus.len() - 1
                }
            };

            let usage = self.format_usage(reading.usage.percent);
            let temperature = self.format_temperature(reading.temperature.celsius);
            let gpu = &mut self.gpus[position];
            gpu.display_name = format!("GPU {index}");
            gpu.device_name = reading.name.clone();
            gpu.usage = usage;
            gpu.temperature = temperature;
            gpu.usage_value = reading.usage.percent;
            gpu.temperature_value = reading.temperature.celsius;
            gpu.source = sensor_name(if reading.temperature.is_available() {
                &reading.temperature.source
            } else {
                &reading.usage.source
            })
            .to_string();
            gpu.usage_tray_digits = tray_digits(reading.usage.percent);
            gpu.temperature_tray_digits = tray_digits(reading.temperature.celsius);
        }

        let before = self.gpus.len();
        self.gpus.retain(|gpu| readings.iter().any(|reading| reading.id == gpu.id));
        changes.gpu_list_changed |= self.gpus.len() != before;
        changes
    }

    /// 依讀值判斷是否有可讀取的 GPU。曾經回報過就一直保留，未曾回報則在緩衝次數後隱藏。
    fn update_gpu_presence(&mut self, readings: &[GpuReading]) {
        if readings.iter().any(GpuReading::is_available) {
            self.gpu_ever_reported = true;
            self.gpu_missing_samples = 0;
            self.gpu_present = true;
            return;
        }

        if self.gpu_ever_reported {
            return;
        }

        if self.gpu_missing_samples < MISSING_GPU_GRACE_SAMPLES {
            self.gpu_missing_samples += 1;
        }

        self.gpu_present = self.gpu_missing_samples < MISSING_GPU_GRACE_SAMPLES;
    }

    /// 依快照回報狀態列訊息。
    fn availability_message(&mut self, snapshot: &HardwareSnapshot, driver: &DriverStatus) -> String {
        if !snapshot.cpu_temperature.is_available() {
            self.driver_action_visible = !driver.installed;
            if self.driver_action_visible {
                return self.t("DriverMissing").to_string();
            }

            return if snapshot.gpus.iter().any(GpuReading::is_available) {
                self.t("CpuSensorUnavailable").to_string()
            } else {
                self.t("NoSensor").to_string()
            };
        }

        self.driver_action_visible = false;
        if !snapshot.cpu_usage.is_available() {
            return self.t("CpuUsageUnavailable").to_string();
        }

        if !self.gpu_present {
            return String::new();
        }

        let separator = self.t("ListSeparator");
        let without_temperature =
            list_gpus_missing_reading(&snapshot.gpus, |gpu| gpu.temperature.is_available(), separator);
        if !without_temperature.is_empty() {
            return format(self.t("GpuSensorUnavailable"), &[&without_temperature]);
        }

        let without_usage = list_gpus_missing_reading(&snapshot.gpus, |gpu| gpu.usage.is_available(), separator);
        if without_usage.is_empty() {
            String::new()
        } else {
            format(self.t("GpuUsageUnavailable"), &[&without_usage])
        }
    }

    fn format_temperature(&self, value: Option<Tenths>) -> String {
        match value {
            Some(value) => format!("{} °C", value.format(&self.decimal_separator)),
            None => self.t("Unavailable").to_string(),
        }
    }

    fn format_usage(&self, value: Option<Tenths>) -> String {
        match value {
            Some(value) => format!("{}%", value.format(&self.decimal_separator)),
            None => self.t("Unavailable").to_string(),
        }
    }

    /// 切換介面語言；傳回 true 代表設定已變更需儲存。
    pub fn set_language(&mut self, language: &str) -> bool {
        let before = self.localizer.language();
        self.localizer.set_language(language);
        if self.localizer.language() == before {
            return false;
        }

        self.settings.language = self.localizer.language().to_string();
        // 清除快取，讓下一份快照以新語言重新產生「無法取得」等文字
        self.last_cpu_reading = None;
        self.last_cpu_usage = None;
        self.status_message = self.t("WaitingForSensors").to_string();
        true
    }

    pub fn gpu_tray_settings(&self, id: &str) -> TrayDisplaySettings {
        self.settings.gpu_tray_settings.get(id).copied().unwrap_or_default()
    }

    pub fn gpu_tray_settings_mut(&mut self, id: &str) -> &mut TrayDisplaySettings {
        self.settings.gpu_tray_settings.entry(id.to_string()).or_default()
    }
}

/// 列出缺少指定讀值的 GPU 序號，但只在每一張 GPU 都缺少時才回報。
/// 雙 GPU 筆電的內顯常常永遠不提供溫度，只要還有一張 GPU 給出可信讀值，狀態列就不該常駐警告；
/// 卡片本身仍會以「無法取得」標示該張 GPU。
pub fn list_gpus_missing_reading(
    readings: &[GpuReading],
    is_available: impl Fn(&GpuReading) -> bool,
    separator: &str,
) -> String {
    if readings.is_empty() || readings.iter().any(is_available) {
        return String::new();
    }

    (0..readings.len()).map(|index| format!("GPU {index}")).collect::<Vec<_>>().join(separator)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn temperature(value: f64, device: &str, sensor: &str) -> TemperatureReading {
        TemperatureReading {
            celsius: Tenths::from_f64(value),
            source: format!("{device} \u{2022} {sensor}"),
            device_name: device.into(),
        }
    }

    fn usage(value: f64, device: &str, sensor: &str) -> UtilizationReading {
        UtilizationReading {
            percent: Tenths::from_f64(value),
            source: format!("{device} \u{2022} {sensor}"),
            device_name: device.into(),
        }
    }

    fn gpu(id: &str, temperature_value: Option<f64>, usage_value: Option<f64>) -> GpuReading {
        GpuReading {
            id: id.into(),
            name: id.into(),
            temperature: temperature_value.map(|value| temperature(value, id, "GPU Core")).unwrap_or_default(),
            usage: usage_value.map(|value| usage(value, id, "GPU Core")).unwrap_or_default(),
        }
    }

    fn installed() -> DriverStatus {
        DriverStatus { installed: true, version: "2.0".into() }
    }

    fn state() -> MainState {
        MainState::new(AppSettings::default(), ".".into())
    }

    fn snapshot(gpus: Vec<GpuReading>) -> HardwareSnapshot {
        HardwareSnapshot {
            cpu_temperature: temperature(54.6, "CPU", "Package"),
            cpu_usage: usage(8.5, "CPU", "CPU Total"),
            gpus,
        }
    }

    #[test]
    fn polling_slows_down_while_hidden() {
        assert_eq!(polling_interval(true), Duration::from_secs(1));
        assert_eq!(polling_interval(false), Duration::from_secs(2));
    }

    #[test]
    fn starts_waiting_for_sensors() {
        let state = state();
        assert_eq!(state.cpu_temperature, "…");
        assert_eq!(state.cpu_tray_digits, "--");
        assert_eq!(state.status_message, "正在讀取硬體感測器…");
    }

    #[test]
    fn a_complete_snapshot_formats_every_value() {
        let mut state = state();
        let changes = state.apply_snapshot(&snapshot(vec![gpu("gpu", Some(46.0), Some(23.0))]), &installed());

        assert!(changes.gpu_list_changed);
        assert_eq!(state.cpu_temperature, "54.6 °C");
        assert_eq!(state.cpu_usage, "8.5%");
        assert_eq!(state.cpu_tray_digits, "54");
        assert_eq!(state.cpu_usage_tray_digits, "8");
        assert_eq!(state.cpu_source, "Package");
        assert_eq!(state.gpus[0].display_name, "GPU 0");
        assert_eq!(state.gpus[0].temperature, "46 °C");
        assert_eq!(state.gpus[0].source, "GPU Core");
        assert_eq!(state.status_message, "");
        assert!(!state.driver_action_visible);
    }

    #[test]
    fn missing_driver_offers_the_download() {
        let mut state = state();
        let mut reading = snapshot(vec![]);
        reading.cpu_temperature = TemperatureReading::default();
        state.apply_snapshot(&reading, &DriverStatus::default());

        assert!(state.driver_action_visible);
        assert_eq!(state.cpu_temperature, "無法取得");
        assert_eq!(state.cpu_tray_digits, "--");
        assert!(state.status_message.contains("PawnIO"));
    }

    #[test]
    fn an_installed_driver_without_cpu_temperature_says_so() {
        let mut state = state();
        let mut reading = snapshot(vec![gpu("gpu", Some(46.0), None)]);
        reading.cpu_temperature = TemperatureReading::default();
        state.apply_snapshot(&reading, &installed());
        assert_eq!(state.status_message, "CPU 溫度感測器未提供可信的即時讀值。");

        reading.gpus.clear();
        state.apply_snapshot(&reading, &installed());
        assert_eq!(state.status_message, state.t("NoSensor"));
    }

    #[test]
    fn device_name_falls_back_to_the_usage_sensor() {
        let mut state = state();
        let mut reading = snapshot(vec![]);
        reading.cpu_temperature = TemperatureReading::default();
        state.apply_snapshot(&reading, &installed());
        assert_eq!(state.cpu_device_name, "CPU");
        assert_eq!(state.cpu_source, "CPU Total");
    }

    #[test]
    fn gpus_keep_their_identity_and_disappear_when_removed() {
        let mut state = state();
        state.apply_snapshot(
            &snapshot(vec![gpu("a", Some(40.0), Some(1.0)), gpu("b", Some(50.0), Some(2.0))]),
            &installed(),
        );
        assert_eq!(state.gpus.len(), 2);
        assert!(state.settings.gpu_tray_settings.contains_key("b"));

        let changes = state.apply_snapshot(&snapshot(vec![gpu("b", Some(51.0), Some(2.0))]), &installed());
        assert!(changes.gpu_list_changed);
        assert_eq!(state.gpus.len(), 1);
        assert_eq!(state.gpus[0].id, "b");
        assert_eq!(state.gpus[0].display_name, "GPU 0");

        let changes = state.apply_snapshot(&snapshot(vec![gpu("b", Some(52.0), Some(2.0))]), &installed());
        assert!(!changes.gpu_list_changed);
    }

    #[test]
    fn gpu_presence_hides_after_the_grace_samples_unless_one_ever_reported() {
        let mut state = state();
        for _ in 0..4 {
            state.apply_snapshot(&snapshot(vec![]), &installed());
            assert!(state.gpu_present);
        }
        state.apply_snapshot(&snapshot(vec![]), &installed());
        assert!(!state.gpu_present);

        state.apply_snapshot(&snapshot(vec![gpu("a", Some(40.0), None)]), &installed());
        assert!(state.gpu_present);
        for _ in 0..10 {
            state.apply_snapshot(&snapshot(vec![gpu("a", None, None)]), &installed());
        }
        assert!(state.gpu_present, "a GPU that reported once stays visible");
    }

    #[test]
    fn gpu_warnings_only_when_every_gpu_misses_the_reading() {
        let mut state = state();
        state.apply_snapshot(&snapshot(vec![gpu("a", Some(46.0), Some(2.0)), gpu("b", None, Some(2.0))]), &installed());
        assert_eq!(state.status_message, "");

        state.apply_snapshot(&snapshot(vec![gpu("a", None, Some(2.0)), gpu("b", None, Some(2.0))]), &installed());
        assert_eq!(state.status_message, "GPU 0、GPU 1 的溫度感測器未提供可信的即時讀值。");
    }

    #[test]
    fn list_gpus_missing_reading_rules() {
        let both = vec![gpu("a", None, Some(1.0)), gpu("b", None, None)];
        assert_eq!(list_gpus_missing_reading(&both, |gpu| gpu.temperature.is_available(), "、"), "GPU 0、GPU 1");

        let one_has = vec![gpu("a", Some(40.0), None), gpu("b", None, None)];
        assert_eq!(list_gpus_missing_reading(&one_has, |gpu| gpu.temperature.is_available(), "、"), "");

        let single = vec![gpu("a", None, None)];
        assert_eq!(list_gpus_missing_reading(&single, |gpu| gpu.temperature.is_available(), "、"), "GPU 0");

        assert_eq!(list_gpus_missing_reading(&[], |gpu| gpu.temperature.is_available(), "、"), "");
        assert_eq!(list_gpus_missing_reading(&one_has, |gpu| gpu.usage.is_available(), ", "), "GPU 0, GPU 1");
    }

    #[test]
    fn switching_language_relabels_on_the_next_snapshot() {
        let mut state = state();
        let mut reading = snapshot(vec![]);
        reading.cpu_temperature = TemperatureReading::default();
        state.apply_snapshot(&reading, &installed());
        assert_eq!(state.cpu_temperature, "無法取得");

        assert!(state.set_language("en-US"));
        assert_eq!(state.settings.language, "en-US");
        assert_eq!(state.status_message, "Reading hardware sensors…");
        state.apply_snapshot(&reading, &installed());
        assert_eq!(state.cpu_temperature, "Unavailable");
        assert!(!state.set_language("en-US"));
    }

    #[test]
    fn gpu_tray_choices_persist_per_device() {
        let mut state = state();
        assert_eq!(state.gpu_tray_settings("x"), TrayDisplaySettings::default());
        state.gpu_tray_settings_mut("x").show_usage = false;
        assert!(!state.gpu_tray_settings("x").show_usage);
        assert!(state.gpu_tray_settings("x").show_temperature);
    }

    #[test]
    fn locale_decimal_separator_is_used_for_display() {
        let mut state = MainState::new(AppSettings::default(), ",".into());
        state.apply_snapshot(&snapshot(vec![]), &installed());
        assert_eq!(state.cpu_temperature, "54,6 °C");
        assert_eq!(state.cpu_tray_digits, "54");
    }

    #[test]
    fn version_label_matches_the_product() {
        assert_eq!(state().version, format!("v{}", env!("CARGO_PKG_VERSION")));
    }
}
