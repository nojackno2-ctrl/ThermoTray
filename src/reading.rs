//! 感測器讀值資料模型與格式化。
//!
//! 讀值一律以「十分之一」為單位的整數保存，與舊版 C# `decimal.Round((decimal)value, 1)` 相同：
//! 先依 `float → decimal` 轉換保留 7 位有效數字，再以銀行家捨入（ToEven）四捨五入至小數第一位。
//! 以整數保存可避免浮點誤差，也讓系統匣數字與介面文字不受語系小數點影響。

/// 當尚無可靠讀值時，系統匣圖示顯示的占位字串。
pub const TRAY_PLACEHOLDER: &str = "--";

/// 以 0.1 為單位的已捨入讀值（例如 452 代表 45.2）。
#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub struct Tenths(pub i64);

impl Tenths {
    /// 將感測器回報的 `f32` 讀值轉換為十分位整數。非有限數值傳回 `None`。
    pub fn from_f32(value: f32) -> Option<Tenths> {
        if !value.is_finite() {
            return None;
        }

        // 與 .NET 的 (decimal)float 相同，先取 7 位有效數字
        let scientific = format!("{:.6e}", value);
        let (mantissa, exponent) = scientific.split_once('e')?;
        let exponent: i32 = exponent.parse().ok()?;
        let negative = mantissa.starts_with('-');
        let digits: i64 = mantissa.chars().filter(char::is_ascii_digit).collect::<String>().parse().ok()?;

        // digits × 10^(exponent − 6) 為原值；換算為十分位需再乘 10
        let scale = exponent - 6 + 1;
        let magnitude = if scale >= 0 {
            digits.checked_mul(10_i64.checked_pow(scale as u32)?)?
        } else if scale < -18 {
            0
        } else {
            round_half_even(digits, 10_i64.pow((-scale) as u32))
        };

        Some(Tenths(if negative { -magnitude } else { magnitude }))
    }

    /// 由十進位數值建立（供測試與常數使用）。
    pub fn from_f64(value: f64) -> Option<Tenths> {
        Tenths::from_f32(value as f32)
    }

    /// 捨去小數部分後的整數（與 `decimal.Truncate` 相同，朝零捨去）。
    pub fn whole(self) -> i64 {
        self.0 / 10
    }

    /// 轉為浮點數，供量表繪製使用。
    pub fn as_f64(self) -> f64 {
        self.0 as f64 / 10.0
    }

    /// 依 `0.#` 樣式格式化：整數不顯示小數，否則顯示一位小數。
    pub fn format(self, decimal_separator: &str) -> String {
        let sign = if self.0 < 0 { "-" } else { "" };
        let magnitude = self.0.unsigned_abs();
        let whole = magnitude / 10;
        let fraction = magnitude % 10;
        if fraction == 0 {
            format!("{sign}{whole}")
        } else {
            format!("{sign}{whole}{decimal_separator}{fraction}")
        }
    }
}

/// 以銀行家捨入（ToEven）將 `value / divisor` 取整。
fn round_half_even(value: i64, divisor: i64) -> i64 {
    let quotient = value / divisor;
    let remainder = value % divisor;
    let twice = remainder * 2;
    if twice > divisor || (twice == divisor && quotient % 2 != 0) {
        quotient + 1
    } else {
        quotient
    }
}

/// 單一硬體感測器的溫度讀值與來源名稱。
#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct TemperatureReading {
    /// 攝氏溫度（無法取得時為 `None`）。
    pub celsius: Option<Tenths>,
    /// 「硬體名稱 • 感測器名稱」格式的來源字串。
    pub source: String,
    /// 提供此讀值的硬體名稱。
    pub device_name: String,
}

impl TemperatureReading {
    pub fn is_available(&self) -> bool {
        self.celsius.is_some()
    }
}

/// 單一硬體元件的使用率讀值與來源名稱。
#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct UtilizationReading {
    /// 使用率百分比（無法取得時為 `None`）。
    pub percent: Option<Tenths>,
    pub source: String,
    pub device_name: String,
}

impl UtilizationReading {
    pub fn is_available(&self) -> bool {
        self.percent.is_some()
    }
}

/// 一張實體 GPU 的配對讀值。溫度與使用率一定來自同一個裝置，不會跨 GPU 混合。
#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct GpuReading {
    /// 拓撲掃描產生的穩定識別碼，例如 `GpuNvidia:NVIDIA GeForce RTX 4080:0`。
    pub id: String,
    pub name: String,
    pub temperature: TemperatureReading,
    pub usage: UtilizationReading,
}

impl GpuReading {
    /// 至少有一種可用的即時讀值。
    pub fn is_available(&self) -> bool {
        self.temperature.is_available() || self.usage.is_available()
    }
}

/// CPU 與所有 GPU 的完整硬體快照。
#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct HardwareSnapshot {
    pub cpu_temperature: TemperatureReading,
    pub cpu_usage: UtilizationReading,
    pub gpus: Vec<GpuReading>,
}

/// 系統匣圖示上顯示的整數數字，不受語系影響。
pub fn tray_digits(value: Option<Tenths>) -> String {
    match value {
        Some(value) => value.whole().to_string(),
        None => TRAY_PLACEHOLDER.to_string(),
    }
}

/// 進度量表的填滿比例 (0.0 ~ 1.0)。量表以 0~100 為刻度；無讀值時為 0。
pub fn gauge_fraction(value: Option<Tenths>) -> f32 {
    match value {
        Some(value) => (value.as_f64().clamp(0.0, 100.0) / 100.0) as f32,
        None => 0.0,
    }
}

/// 取出來源字串中「•」之後的感測器名稱；卡片已顯示裝置名稱，不需重複。
pub fn sensor_name(source: &str) -> &str {
    match source.find('\u{2022}') {
        Some(index) => source[index + '\u{2022}'.len_utf8()..].trim(),
        None => source,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn tenths(value: f64) -> Option<Tenths> {
        Tenths::from_f64(value)
    }

    #[test]
    fn tray_digits_returns_placeholder_when_unavailable() {
        assert_eq!(tray_digits(None), "--");
    }

    #[test]
    fn tray_digits_truncates_to_whole_units() {
        for (value, expected) in [
            (45.0, "45"),
            (45.6, "45"),
            (7.4, "7"),
            (99.9, "99"),
            (100.4, "100"),
            (0.0, "0"),
            // 先四捨五入至 13.0 再取整數，與舊版 decimal.Round 後 Truncate 相同
            (12.95, "13"),
        ] {
            assert_eq!(tray_digits(tenths(value)), expected, "{value}");
        }
    }

    #[test]
    fn rounding_matches_decimal_round_to_even() {
        // 45.25f 可精確表示，ToEven 捨入為 45.2；45.35f 轉為 decimal 後是 45.35，捨入為 45.4
        assert_eq!(Tenths::from_f32(45.25), Some(Tenths(452)));
        assert_eq!(Tenths::from_f32(45.75), Some(Tenths(458)));
        assert_eq!(Tenths::from_f32(45.35), Some(Tenths(454)));
        assert_eq!(Tenths::from_f32(62.125), Some(Tenths(621)));
        assert_eq!(Tenths::from_f32(8.917), Some(Tenths(89)));
        assert_eq!(Tenths::from_f32(16.524397), Some(Tenths(165)));
        assert_eq!(Tenths::from_f32(100.0), Some(Tenths(1000)));
        assert_eq!(Tenths::from_f32(0.04), Some(Tenths(0)));
    }

    #[test]
    fn non_finite_values_are_unavailable() {
        assert_eq!(Tenths::from_f32(f32::NAN), None);
        assert_eq!(Tenths::from_f32(f32::INFINITY), None);
        assert_eq!(Tenths::from_f32(f32::NEG_INFINITY), None);
    }

    #[test]
    fn format_drops_a_zero_fraction_and_uses_the_given_separator() {
        assert_eq!(Tenths(450).format("."), "45");
        assert_eq!(Tenths(452).format("."), "45.2");
        assert_eq!(Tenths(452).format(","), "45,2");
        assert_eq!(Tenths(5).format("."), "0.5");
    }

    #[test]
    fn gauge_clamps_to_the_zero_to_hundred_scale() {
        assert_eq!(gauge_fraction(None), 0.0);
        assert_eq!(gauge_fraction(Some(Tenths(546))), 0.546);
        assert_eq!(gauge_fraction(Some(Tenths(1500))), 1.0);
        assert_eq!(gauge_fraction(Some(Tenths(-10))), 0.0);
    }

    #[test]
    fn sensor_name_keeps_only_the_part_after_the_bullet() {
        assert_eq!(sensor_name("NVIDIA GeForce RTX 4080 • GPU Core"), "GPU Core");
        assert_eq!(sensor_name("GPU Core"), "GPU Core");
        assert_eq!(sensor_name(""), "");
    }

    #[test]
    fn gpu_is_available_with_either_reading() {
        let mut gpu = GpuReading::default();
        assert!(!gpu.is_available());
        gpu.usage.percent = Some(Tenths(10));
        assert!(gpu.is_available());
    }
}
