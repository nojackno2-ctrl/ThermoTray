//! 多語系 UI 字串（繁體中文與英文）。

/// 預設語言代碼（繁體中文）。
pub const DEFAULT_LANGUAGE: &str = "zh-TW";

/// 英文語言代碼。
pub const ENGLISH_LANGUAGE: &str = "en-US";

/// 雙語字串對照表：(Key, 繁體中文, 英文)。
const STRINGS: &[(&str, &str, &str)] = &[
    ("Subtitle", "即時硬體感測，不使用推測數值", "Live hardware sensors. No estimated values."),
    ("CpuTemperature", "CPU 溫度", "CPU temperature"),
    ("GpuTemperature", "GPU 溫度", "GPU temperature"),
    ("CpuUsage", "CPU 使用率", "CPU usage"),
    ("GpuUsage", "GPU 使用率", "GPU usage"),
    ("ShowInTray", "顯示在工具列", "Show in tray"),
    ("Unavailable", "無法取得", "Unavailable"),
    ("WaitingForSensors", "正在讀取硬體感測器…", "Reading hardware sensors…"),
    (
        "NoSensor",
        "找不到可用的溫度感測器。請確認硬體/驅動程式支援。",
        "No usable temperature sensor was found. Check hardware and driver support.",
    ),
    (
        "CpuSensorUnavailable",
        "CPU 溫度感測器未提供可信的即時讀值。",
        "The CPU temperature sensor did not provide a trustworthy live reading.",
    ),
    (
        "DriverMissing",
        "CPU 溫度需要 PawnIO 核心驅動程式。LibreHardwareMonitor 0.9.6 以 PawnIO 讀取 CPU 暫存器，未安裝時 AMD Ryzen 的 Tctl/Tdie 會固定為 0，因此顯示為無法取得。安裝 PawnIO 後重新啟動 ThermoTray 即可。",
        "CPU temperature needs the PawnIO kernel driver. LibreHardwareMonitor 0.9.6 reads CPU registers through PawnIO; without it the AMD Ryzen Tctl/Tdie sensor stays at 0, so it is reported as unavailable. Install PawnIO and restart ThermoTray.",
    ),
    ("DriverDownload", "下載並安裝 PawnIO", "Download and install PawnIO"),
    (
        "GpuSensorUnavailable",
        "{0} 的溫度感測器未提供可信的即時讀值。",
        "The temperature sensor on {0} did not provide a trustworthy live reading.",
    ),
    ("ListSeparator", "、", ", "),
    (
        "CpuUsageUnavailable",
        "CPU 使用率感測器未提供可信的即時讀值。",
        "The CPU utilization sensor did not provide a trustworthy live reading.",
    ),
    (
        "GpuUsageUnavailable",
        "{0} 的使用率感測器未提供可信的即時讀值。",
        "The utilization sensor on {0} did not provide a trustworthy live reading.",
    ),
    ("StartWithWindows", "隨 Windows 啟動", "Start with Windows"),
    ("HideWhenClosed", "關閉時縮小至系統匣", "Hide to tray when closed"),
    ("Language", "語言", "Language"),
    ("Open", "開啟 ThermoTray", "Open ThermoTray"),
    ("Exit", "結束", "Exit"),
    ("StartupError", "無法更新開機啟動設定。", "Could not update the startup setting."),
    ("SensorError", "讀取硬體感測器時發生錯誤。", "An error occurred while reading hardware sensors."),
    (
        "SidecarMissing",
        "找不到感測器元件 ThermoTray.Sensors.exe。請重新安裝 ThermoTray。",
        "The sensor component ThermoTray.Sensors.exe is missing. Reinstall ThermoTray.",
    ),
    ("OpenLinkError", "無法開啟瀏覽器，請手動前往下列網址：", "Could not open a browser. Visit this address manually:"),
    (
        "AlreadyRunning",
        "ThermoTray 已在執行中。請從系統匣圖示開啟主視窗。",
        "ThermoTray is already running. Open its window from the notification area.",
    ),
    (
        "LegacyInstanceRunning",
        "ThermoTray 已在執行中，但沒有回應接手要求（通常表示它是 1.1.3 之前的版本），因此改為顯示它的視窗。若要改用 {1}，請先從系統匣圖示結束執行中的版本。",
        "ThermoTray is already running but did not answer the hand-over request, which usually means it predates 1.1.3, so its window was shown instead. To use {1}, exit the running version from the notification area first.",
    ),
    (
        "NewerInstanceRunning",
        "已在執行較新的 ThermoTray {0}，將顯示該版本的視窗。您啟動的是 {1}。",
        "A newer ThermoTray {0} is already running and its window will be shown. You started {1}.",
    ),
    (
        "ReplaceRunningInstance",
        "偵測到 ThermoTray {0} 正在執行，您啟動的是 {1}。要關閉執行中的版本並改用這一個嗎？",
        "ThermoTray {0} is already running and you started {1}. Close the running version and use this one instead?",
    ),
    (
        "ReplaceFailed",
        "無法關閉執行中的 ThermoTray {0}。請從系統匣圖示結束它，然後再啟動這個版本。",
        "Could not close the running ThermoTray {0}. Exit it from the notification area, then start this version again.",
    ),
];

/// 本地化服務。
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Localizer {
    language: &'static str,
}

impl Localizer {
    pub fn new(language: &str) -> Self {
        Self { language: normalize(language) }
    }

    pub fn language(&self) -> &'static str {
        self.language
    }

    pub fn set_language(&mut self, language: &str) {
        self.language = normalize(language);
    }

    pub fn is_english(&self) -> bool {
        self.language == ENGLISH_LANGUAGE
    }

    /// 取得本地化文字；找不到 Key 時傳回 Key 本身。
    pub fn get<'a>(&self, key: &'a str) -> &'a str {
        match STRINGS.iter().find(|(name, _, _)| *name == key) {
            Some((_, chinese, english)) => {
                if self.is_english() {
                    english
                } else {
                    chinese
                }
            }
            None => key,
        }
    }
}

/// 以位置參數取代 `{0}`、`{1}`。
pub fn format(template: &str, arguments: &[&str]) -> String {
    let mut text = template.to_string();
    for (index, argument) in arguments.iter().enumerate() {
        text = text.replace(&format!("{{{index}}}"), argument);
    }

    text
}

/// 未辨識的語言代碼一律退回繁體中文。
fn normalize(language: &str) -> &'static str {
    if language.eq_ignore_ascii_case(ENGLISH_LANGUAGE) {
        ENGLISH_LANGUAGE
    } else {
        DEFAULT_LANGUAGE
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn returns_traditional_chinese_for_the_default_language() {
        assert_eq!(Localizer::new(DEFAULT_LANGUAGE).get("Unavailable"), "無法取得");
    }

    #[test]
    fn returns_english_for_english() {
        assert_eq!(Localizer::new(ENGLISH_LANGUAGE).get("Unavailable"), "Unavailable");
    }

    #[test]
    fn returns_the_key_when_no_translation_exists() {
        assert_eq!(Localizer::new(ENGLISH_LANGUAGE).get("MissingKey"), "MissingKey");
    }

    #[test]
    fn falls_back_to_the_default_for_unsupported_languages() {
        for language in ["de-DE", "", "nonsense"] {
            assert_eq!(Localizer::new(language).language(), DEFAULT_LANGUAGE);
        }
    }

    #[test]
    fn accepts_english_regardless_of_casing() {
        assert_eq!(Localizer::new("EN-us").language(), ENGLISH_LANGUAGE);
    }

    #[test]
    fn set_language_normalises_unsupported_values() {
        let mut localizer = Localizer::new(ENGLISH_LANGUAGE);
        localizer.set_language("xx");
        assert_eq!(localizer.language(), DEFAULT_LANGUAGE);
    }

    #[test]
    fn every_key_is_unique() {
        for (index, (key, _, _)) in STRINGS.iter().enumerate() {
            assert!(STRINGS[index + 1..].iter().all(|(other, _, _)| other != key), "{key}");
        }
    }

    #[test]
    fn format_replaces_positional_arguments() {
        assert_eq!(format("{0} and {1}", &["a", "b"]), "a and b");
    }
}
