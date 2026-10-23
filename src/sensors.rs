//! 硬體感測器讀取與挑選。
//!
//! LibreHardwareMonitor 與 PawnIO 沒有 Rust 對等實作，因此由 `ThermoTray.Sensors.exe`（.NET sidecar）
//! 忠實回傳原始感測器樹；本模組負責所有挑選、排序與 GPU 配對規則。
//! 嚴格遵循「不使用假資料、不使用估算數值」：沒有可信讀值時一律回報無法取得。

use std::io::{BufRead, BufReader, Write};
use std::path::{Path, PathBuf};
use std::process::{Child, ChildStdin, ChildStdout, Command, Stdio};
use std::time::{Duration, Instant};

use serde::Deserialize;

use crate::reading::{GpuReading, HardwareSnapshot, TemperatureReading, Tenths, UtilizationReading};

const CPU_MINIMUM_CELSIUS: f32 = 1.0;
const CPU_MAXIMUM_CELSIUS: f32 = 125.0;
const GPU_MINIMUM_CELSIUS: f32 = 1.0;
const GPU_MAXIMUM_CELSIUS: f32 = 150.0;

pub const CPU_PREFERRED_NAMES: &[&str] = &["Tctl/Tdie", "Package", "CPU Package", "Core Average"];
pub const GPU_PREFERRED_NAMES: &[&str] = &["GPU Core", "Core", "Hot Spot"];
pub const CPU_USAGE_PREFERRED_NAMES: &[&str] = &["CPU Total", "Total"];
pub const GPU_USAGE_PREFERRED_NAMES: &[&str] = &["GPU Core", "GPU Total", "Core"];

/// sidecar 執行檔名稱，與 ThermoTray.exe 放在同一個目錄。
pub const SIDECAR_FILE_NAME: &str = "ThermoTray.Sensors.exe";

/// PawnIO 驅動程式的官方下載網址。
pub const DRIVER_DOWNLOAD_URL: &str = "https://pawnio.eu/";

/// sidecar 回傳的感測器樹。
#[derive(Clone, Debug, Default, Deserialize)]
pub struct RawTree {
    #[serde(default)]
    pub hardware: Vec<RawHardware>,
    #[serde(default)]
    pub error: Option<String>,
}

/// LibreHardwareMonitor 的單一硬體節點。
#[derive(Clone, Debug, Default, Deserialize)]
pub struct RawHardware {
    /// `HardwareType` 列舉名稱，例如 `Cpu`、`GpuNvidia`。
    #[serde(rename = "type")]
    pub kind: String,
    pub name: String,
    #[serde(default)]
    pub sensors: Vec<RawSensor>,
    #[serde(default)]
    pub sub: Vec<RawHardware>,
}

/// 單一感測器。sidecar 只回傳溫度 (Temperature) 與使用率 (Load)。
#[derive(Clone, Debug, Default, Deserialize)]
pub struct RawSensor {
    #[serde(rename = "type")]
    pub kind: String,
    pub name: String,
    pub value: Option<f32>,
}

/// PawnIO 驅動程式狀態。
#[derive(Clone, Debug, Default, PartialEq, Eq, Deserialize)]
pub struct DriverStatus {
    pub installed: bool,
    #[serde(default)]
    pub version: String,
}

const TEMPERATURE: &str = "Temperature";
const LOAD: &str = "Load";

fn is_cpu(kind: &str) -> bool {
    kind == "Cpu"
}

fn is_gpu(kind: &str) -> bool {
    matches!(kind, "GpuNvidia" | "GpuAmd" | "GpuIntel")
}

/// 驗證溫度讀值可用且落在合理範圍；排除 null、非有限值與 0°C 等占位值。
pub fn is_usable_temperature(sensor_kind: &str, value: Option<f32>, minimum: f32, maximum: f32) -> bool {
    sensor_kind == TEMPERATURE
        && matches!(value, Some(celsius) if celsius.is_finite() && celsius >= minimum && celsius <= maximum)
}

/// 驗證使用率讀值可用且介於 0%~100%（允許閒置的 0%）。
pub fn is_usable_utilization(sensor_kind: &str, value: Option<f32>) -> bool {
    sensor_kind == LOAD && matches!(value, Some(percent) if percent.is_finite() && (0.0..=100.0).contains(&percent))
}

/// 感測器名稱在偏好清單中的位置（越小越優先，不區分大小寫的子字串比對）；沒有對應時為 `i32::MAX`。
pub fn preferred_rank(sensor_name: &str, preferred_names: &[&str]) -> i32 {
    let lower = sensor_name.to_lowercase();
    preferred_names.iter().position(|name| lower.contains(&name.to_lowercase())).map_or(i32::MAX, |index| index as i32)
}

/// GPU 感測器排序權重。比較順序：名稱偏好 → 獨顯優先於內顯 → 偵測順序。
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct GpuSensorRank {
    pub name_rank: i32,
    pub hardware_priority: i32,
    pub sequence: i32,
}

impl GpuSensorRank {
    pub const DISCRETE_PRIORITY: i32 = 0;
    pub const INTEGRATED_PRIORITY: i32 = 1;
    pub const ANY_NAME: i32 = 0;
    pub const NONE: GpuSensorRank = GpuSensorRank::new(i32::MAX, i32::MAX, i32::MAX);

    pub const fn new(name_rank: i32, hardware_priority: i32, sequence: i32) -> Self {
        Self { name_rank, hardware_priority, sequence }
    }

    pub fn is_better_than(self, other: GpuSensorRank) -> bool {
        (self.name_rank, self.hardware_priority, self.sequence)
            < (other.name_rank, other.hardware_priority, other.sequence)
    }
}

struct CpuCandidate<'a> {
    hardware: &'a RawHardware,
    sensor: &'a RawSensor,
    name_rank: i32,
}

struct GpuCandidate<'a> {
    hardware: &'a RawHardware,
    sensor: &'a RawSensor,
    preferred: GpuSensorRank,
    fallback: GpuSensorRank,
}

struct GpuDeviceBuilder<'a> {
    hardware: &'a RawHardware,
    temperatures: Vec<GpuCandidate<'a>>,
    usages: Vec<GpuCandidate<'a>>,
}

#[derive(Default)]
struct Candidates<'a> {
    cpu_temperatures: Vec<CpuCandidate<'a>>,
    cpu_usages: Vec<CpuCandidate<'a>>,
    gpus: Vec<GpuDeviceBuilder<'a>>,
}

/// 從原始感測器樹挑選 CPU 與每張 GPU 的最佳讀值。
pub fn select_snapshot(tree: &RawTree) -> HardwareSnapshot {
    let mut candidates = Candidates::default();
    for root in &tree.hardware {
        collect(root, &mut candidates, None);
    }

    let gpus = candidates
        .gpus
        .iter()
        .enumerate()
        .map(|(index, device)| GpuReading {
            id: format!("{}:{}:{}", device.hardware.kind, device.hardware.name, index),
            name: device.hardware.name.clone(),
            temperature: select_gpu_temperature(&device.temperatures),
            usage: select_gpu_usage(&device.usages),
        })
        .collect();

    HardwareSnapshot {
        cpu_temperature: select_cpu_temperature(&candidates.cpu_temperatures),
        cpu_usage: select_cpu_usage(&candidates.cpu_usages),
        gpus,
    }
}

/// 遞迴收集候選感測器。GPU 的子硬體沿用父 GPU 的裝置，確保讀值不會跨裝置混合。
fn collect<'a>(hardware: &'a RawHardware, candidates: &mut Candidates<'a>, gpu_device: Option<usize>) {
    let cpu = is_cpu(&hardware.kind);
    let gpu_device = if is_gpu(&hardware.kind) {
        candidates.gpus.push(GpuDeviceBuilder { hardware, temperatures: Vec::new(), usages: Vec::new() });
        Some(candidates.gpus.len() - 1)
    } else {
        gpu_device
    };
    let hardware_priority = match gpu_device {
        Some(index) if candidates.gpus[index].hardware.kind == "GpuIntel" => GpuSensorRank::INTEGRATED_PRIORITY,
        _ => GpuSensorRank::DISCRETE_PRIORITY,
    };

    for sensor in &hardware.sensors {
        if sensor.kind == TEMPERATURE {
            if cpu {
                candidates.cpu_temperatures.push(CpuCandidate {
                    hardware,
                    sensor,
                    name_rank: preferred_rank(&sensor.name, CPU_PREFERRED_NAMES),
                });
            } else if let Some(index) = gpu_device {
                let device = &mut candidates.gpus[index];
                let sequence = device.temperatures.len() as i32;
                device.temperatures.push(GpuCandidate {
                    hardware,
                    sensor,
                    preferred: GpuSensorRank::new(
                        preferred_rank(&sensor.name, GPU_PREFERRED_NAMES),
                        hardware_priority,
                        sequence,
                    ),
                    fallback: GpuSensorRank::new(GpuSensorRank::ANY_NAME, hardware_priority, sequence),
                });
            }
        } else if sensor.kind == LOAD {
            if cpu {
                candidates.cpu_usages.push(CpuCandidate {
                    hardware,
                    sensor,
                    name_rank: preferred_rank(&sensor.name, CPU_USAGE_PREFERRED_NAMES),
                });
            } else if let Some(index) = gpu_device {
                let device = &mut candidates.gpus[index];
                let sequence = device.usages.len() as i32;
                let rank = GpuSensorRank::new(
                    preferred_rank(&sensor.name, GPU_USAGE_PREFERRED_NAMES),
                    hardware_priority,
                    sequence,
                );
                device.usages.push(GpuCandidate { hardware, sensor, preferred: rank, fallback: rank });
            }
        }
    }

    for sub in &hardware.sub {
        collect(sub, candidates, gpu_device);
    }
}

type Chosen<'a> = Option<(&'a RawHardware, &'a RawSensor, f32)>;

/// CPU 溫度：優先採用符合偏好名稱者，否則退回最高的有效溫度。
fn select_cpu_temperature(candidates: &[CpuCandidate]) -> TemperatureReading {
    let mut preferred: Chosen = None;
    let mut preferred_rank = i32::MAX;
    let mut fallback: Chosen = None;

    for candidate in candidates {
        let value = candidate.sensor.value;
        if !is_usable_temperature(&candidate.sensor.kind, value, CPU_MINIMUM_CELSIUS, CPU_MAXIMUM_CELSIUS) {
            continue;
        }

        let celsius = value.unwrap_or_default();
        if candidate.name_rank < preferred_rank {
            preferred = Some((candidate.hardware, candidate.sensor, celsius));
            preferred_rank = candidate.name_rank;
        }

        if fallback.is_none_or(|(_, _, best)| celsius > best) {
            fallback = Some((candidate.hardware, candidate.sensor, celsius));
        }
    }

    to_temperature(preferred.or(fallback))
}

/// CPU 使用率：只採用符合「CPU Total」等偏好名稱的感測器，不以單核讀值推估。
fn select_cpu_usage(candidates: &[CpuCandidate]) -> UtilizationReading {
    let mut preferred: Chosen = None;
    let mut preferred_rank = i32::MAX;

    for candidate in candidates {
        let value = candidate.sensor.value;
        if !is_usable_utilization(&candidate.sensor.kind, value) {
            continue;
        }

        if candidate.name_rank < preferred_rank {
            preferred = Some((candidate.hardware, candidate.sensor, value.unwrap_or_default()));
            preferred_rank = candidate.name_rank;
        }
    }

    to_utilization(preferred)
}

fn select_gpu_temperature(candidates: &[GpuCandidate]) -> TemperatureReading {
    let mut preferred: Chosen = None;
    let mut preferred_rank = GpuSensorRank::NONE;
    let mut fallback: Chosen = None;
    let mut fallback_rank = GpuSensorRank::NONE;

    for candidate in candidates {
        let value = candidate.sensor.value;
        if !is_usable_temperature(&candidate.sensor.kind, value, GPU_MINIMUM_CELSIUS, GPU_MAXIMUM_CELSIUS) {
            continue;
        }

        let celsius = value.unwrap_or_default();
        if candidate.preferred.is_better_than(preferred_rank) {
            preferred = Some((candidate.hardware, candidate.sensor, celsius));
            preferred_rank = candidate.preferred;
        }

        if candidate.fallback.is_better_than(fallback_rank) {
            fallback = Some((candidate.hardware, candidate.sensor, celsius));
            fallback_rank = candidate.fallback;
        }
    }

    to_temperature(preferred.or(fallback))
}

fn select_gpu_usage(candidates: &[GpuCandidate]) -> UtilizationReading {
    let mut preferred: Chosen = None;
    let mut preferred_rank = GpuSensorRank::NONE;

    for candidate in candidates {
        let value = candidate.sensor.value;
        if !is_usable_utilization(&candidate.sensor.kind, value) {
            continue;
        }

        if candidate.preferred.is_better_than(preferred_rank) {
            preferred = Some((candidate.hardware, candidate.sensor, value.unwrap_or_default()));
            preferred_rank = candidate.preferred;
        }
    }

    to_utilization(preferred)
}

fn source_name(hardware: &RawHardware, sensor: &RawSensor) -> String {
    format!("{} \u{2022} {}", hardware.name, sensor.name)
}

fn to_temperature(chosen: Chosen) -> TemperatureReading {
    match chosen.and_then(|(hardware, sensor, value)| Some((hardware, sensor, Tenths::from_f32(value)?))) {
        Some((hardware, sensor, celsius)) => TemperatureReading {
            celsius: Some(celsius),
            source: source_name(hardware, sensor),
            device_name: hardware.name.clone(),
        },
        None => TemperatureReading::default(),
    }
}

fn to_utilization(chosen: Chosen) -> UtilizationReading {
    match chosen.and_then(|(hardware, sensor, value)| Some((hardware, sensor, Tenths::from_f32(value)?))) {
        Some((hardware, sensor, percent)) => UtilizationReading {
            percent: Some(percent),
            source: source_name(hardware, sensor),
            device_name: hardware.name.clone(),
        },
        None => UtilizationReading::default(),
    }
}

/// 一列診斷輸出用的原始感測器資料。
pub struct RawSensorLine<'a> {
    pub hardware_type: &'a str,
    pub hardware_name: &'a str,
    pub sensor_type: &'a str,
    pub sensor_name: &'a str,
    pub value: Option<f32>,
}

/// 依深度優先順序攤平所有溫度與使用率感測器（供 `--diagnostics` 使用）。
pub fn flatten(tree: &RawTree) -> Vec<RawSensorLine<'_>> {
    fn walk<'a>(hardware: &'a RawHardware, lines: &mut Vec<RawSensorLine<'a>>) {
        for sensor in &hardware.sensors {
            if sensor.kind == TEMPERATURE || sensor.kind == LOAD {
                lines.push(RawSensorLine {
                    hardware_type: &hardware.kind,
                    hardware_name: &hardware.name,
                    sensor_type: &sensor.kind,
                    sensor_name: &sensor.name,
                    value: sensor.value,
                });
            }
        }

        for sub in &hardware.sub {
            walk(sub, lines);
        }
    }

    let mut lines = Vec::new();
    for root in &tree.hardware {
        walk(root, &mut lines);
    }

    lines
}

/// 與 sidecar 溝通時發生的錯誤。
#[derive(Debug)]
pub enum SensorError {
    /// 找不到或無法啟動 sidecar。
    Launch(String),
    /// sidecar 意外結束或管道中斷。
    Io(String),
    /// 回應格式錯誤。
    Protocol(String),
    /// LibreHardwareMonitor 在 sidecar 內擲出例外。
    Sensor(String),
}

impl std::fmt::Display for SensorError {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            SensorError::Launch(message) => write!(formatter, "could not start the sensor sidecar: {message}"),
            SensorError::Io(message) => write!(formatter, "sensor sidecar I/O failed: {message}"),
            SensorError::Protocol(message) => write!(formatter, "sensor sidecar answered unexpectedly: {message}"),
            SensorError::Sensor(message) => write!(formatter, "LibreHardwareMonitor failed: {message}"),
        }
    }
}

/// 執行中的 sidecar 處理程序與其 stdio 管道。
struct Sidecar {
    child: Child,
    stdin: ChildStdin,
    stdout: BufReader<ChildStdout>,
    _job: crate::win::KillOnCloseJob,
}

impl Sidecar {
    fn launch(path: &Path) -> Result<Sidecar, SensorError> {
        use std::os::windows::process::CommandExt;
        const CREATE_NO_WINDOW: u32 = 0x0800_0000;

        let mut child = Command::new(path)
            .current_dir(path.parent().unwrap_or(Path::new(".")))
            .stdin(Stdio::piped())
            .stdout(Stdio::piped())
            .stderr(Stdio::null())
            .creation_flags(CREATE_NO_WINDOW)
            .spawn()
            .map_err(|error| SensorError::Launch(format!("{}: {error}", path.display())))?;

        // 主程式異常結束時，工作物件會一併終止 sidecar，避免殘留的提權處理程序
        let job = crate::win::KillOnCloseJob::assign(&child);
        let stdin = child.stdin.take().ok_or_else(|| SensorError::Launch("stdin unavailable".into()))?;
        let stdout = child.stdout.take().ok_or_else(|| SensorError::Launch("stdout unavailable".into()))?;
        Ok(Sidecar { child, stdin, stdout: BufReader::new(stdout), _job: job })
    }

    fn request(&mut self, command: &str) -> Result<String, SensorError> {
        self.stdin
            .write_all(format!("{command}\n").as_bytes())
            .and_then(|()| self.stdin.flush())
            .map_err(|error| SensorError::Io(error.to_string()))?;

        let mut line = String::new();
        let read = self.stdout.read_line(&mut line).map_err(|error| SensorError::Io(error.to_string()))?;
        if read == 0 {
            return Err(SensorError::Io("the sidecar exited".into()));
        }

        Ok(line)
    }
}

impl Drop for Sidecar {
    fn drop(&mut self) {
        let _ = self.stdin.write_all(b"EXIT\n");
        let _ = self.stdin.flush();

        // 給 LibreHardwareMonitor 一點時間關閉驅動程式控制代碼，再強制結束
        let deadline = Instant::now() + Duration::from_secs(2);
        while Instant::now() < deadline {
            match self.child.try_wait() {
                Ok(Some(_)) => return,
                Ok(None) => std::thread::sleep(Duration::from_millis(20)),
                Err(_) => break,
            }
        }

        let _ = self.child.kill();
        let _ = self.child.wait();
    }
}

/// 透過 sidecar 讀取硬體感測器的服務。sidecar 失效時會在下次讀取時重新啟動。
pub struct HardwareSensorService {
    path: PathBuf,
    sidecar: Option<Sidecar>,
    retry_after: Option<Instant>,
}

/// sidecar 啟動或通訊失敗後，至少等待這段時間才重新啟動，避免反覆啟動 .NET 處理程序。
const RELAUNCH_BACKOFF: Duration = Duration::from_secs(10);

impl Default for HardwareSensorService {
    fn default() -> Self {
        Self::new()
    }
}

impl HardwareSensorService {
    pub fn new() -> Self {
        Self { path: sidecar_path(), sidecar: None, retry_after: None }
    }

    /// 讀取原始感測器樹。
    pub fn read_raw(&mut self) -> Result<RawTree, SensorError> {
        let line = self.request("READ")?;
        let tree: RawTree = serde_json::from_str(&line).map_err(|error| SensorError::Protocol(error.to_string()))?;
        match tree.error {
            Some(message) => Err(SensorError::Sensor(message)),
            None => Ok(tree),
        }
    }

    /// 讀取一次完整的硬體快照。
    pub fn read(&mut self) -> Result<HardwareSnapshot, SensorError> {
        self.read_raw().map(|tree| select_snapshot(&tree))
    }

    /// 查詢 PawnIO 驅動程式狀態。任何失敗都視為未安裝，絕不假設驅動程式可用。
    pub fn driver_status(&mut self) -> DriverStatus {
        self.request("DRIVER").ok().and_then(|line| serde_json::from_str(&line).ok()).unwrap_or_default()
    }

    fn request(&mut self, command: &str) -> Result<String, SensorError> {
        if self.sidecar.is_none() {
            if self.retry_after.is_some_and(|retry| Instant::now() < retry) {
                return Err(SensorError::Launch("waiting before relaunching the sidecar".into()));
            }

            match Sidecar::launch(&self.path) {
                Ok(sidecar) => self.sidecar = Some(sidecar),
                Err(error) => {
                    self.retry_after = Some(Instant::now() + RELAUNCH_BACKOFF);
                    return Err(error);
                }
            }
        }

        let result = self
            .sidecar
            .as_mut()
            .map_or_else(|| Err(SensorError::Launch("sidecar unavailable".into())), |sidecar| sidecar.request(command));
        if result.is_err() {
            self.sidecar = None;
            self.retry_after = Some(Instant::now() + RELAUNCH_BACKOFF);
        }

        result
    }

    /// 結束 sidecar。
    pub fn close(&mut self) {
        self.sidecar = None;
    }
}

/// sidecar 必須與 ThermoTray.exe 位於同一個目錄。
fn sidecar_path() -> PathBuf {
    let directory =
        std::env::current_exe().ok().and_then(|path| path.parent().map(Path::to_path_buf)).unwrap_or_default();
    let installed = directory.join(SIDECAR_FILE_NAME);

    // 開發時 cargo run 的輸出目錄沒有 sidecar，改用 dotnet build 的輸出
    #[cfg(debug_assertions)]
    if !installed.exists() {
        let development = Path::new(env!("CARGO_MANIFEST_DIR"))
            .join("sensors/ThermoTray.Sensors/bin/Release/net8.0-windows/win-x64")
            .join(SIDECAR_FILE_NAME);
        if development.exists() {
            return development;
        }
    }

    installed
}

#[cfg(test)]
mod tests {
    use super::*;

    fn sensor(kind: &str, name: &str, value: Option<f32>) -> RawSensor {
        RawSensor { kind: kind.into(), name: name.into(), value }
    }

    fn hardware(kind: &str, name: &str, sensors: Vec<RawSensor>, sub: Vec<RawHardware>) -> RawHardware {
        RawHardware { kind: kind.into(), name: name.into(), sensors, sub }
    }

    #[test]
    fn preferred_rank_matches_on_substring() {
        assert_eq!(preferred_rank("Core (Tctl/Tdie)", CPU_PREFERRED_NAMES), 0);
        assert_eq!(preferred_rank("CPU Package", CPU_PREFERRED_NAMES), 1);
        assert_eq!(preferred_rank("Core Average", CPU_PREFERRED_NAMES), 3);
    }

    #[test]
    fn preferred_rank_is_case_insensitive() {
        assert_eq!(preferred_rank("core (tctl/tdie)", CPU_PREFERRED_NAMES), 0);
    }

    #[test]
    fn preferred_rank_is_max_when_nothing_matches() {
        assert_eq!(preferred_rank("Core #3", CPU_PREFERRED_NAMES), i32::MAX);
    }

    #[test]
    fn usable_temperature_accepts_values_inside_the_range() {
        for value in [1.0, 62.125, 125.0] {
            assert!(is_usable_temperature(TEMPERATURE, Some(value), 1.0, 125.0), "{value}");
        }
    }

    #[test]
    fn usable_temperature_rejects_placeholders_and_garbage() {
        for value in
            [Some(0.0), None, Some(f32::NAN), Some(f32::INFINITY), Some(f32::NEG_INFINITY), Some(-5.0), Some(126.0)]
        {
            assert!(!is_usable_temperature(TEMPERATURE, value, 1.0, 125.0), "{value:?}");
        }
        assert!(!is_usable_temperature(LOAD, Some(50.0), 1.0, 125.0));
    }

    #[test]
    fn usable_utilization_accepts_zero_to_hundred() {
        for value in [0.0, 42.5, 100.0] {
            assert!(is_usable_utilization(LOAD, Some(value)), "{value}");
        }
        for value in [None, Some(f32::NAN), Some(f32::INFINITY), Some(-0.1), Some(100.1)] {
            assert!(!is_usable_utilization(LOAD, value), "{value:?}");
        }
        assert!(!is_usable_utilization(TEMPERATURE, Some(50.0)));
    }

    #[test]
    fn gpu_rank_orders_name_then_hardware_then_sequence() {
        let any = GpuSensorRank::new(i32::MAX, GpuSensorRank::INTEGRATED_PRIORITY, 9);
        assert!(any.is_better_than(GpuSensorRank::NONE));

        let named_integrated = GpuSensorRank::new(0, GpuSensorRank::INTEGRATED_PRIORITY, 5);
        let unnamed_discrete = GpuSensorRank::new(1, GpuSensorRank::DISCRETE_PRIORITY, 0);
        assert!(named_integrated.is_better_than(unnamed_discrete));
        assert!(!unnamed_discrete.is_better_than(named_integrated));

        let discrete = GpuSensorRank::new(0, GpuSensorRank::DISCRETE_PRIORITY, 3);
        let integrated = GpuSensorRank::new(0, GpuSensorRank::INTEGRATED_PRIORITY, 0);
        assert!(discrete.is_better_than(integrated));

        let first = GpuSensorRank::new(0, GpuSensorRank::DISCRETE_PRIORITY, 0);
        let second = GpuSensorRank::new(0, GpuSensorRank::DISCRETE_PRIORITY, 1);
        assert!(first.is_better_than(second));
        assert!(!first.is_better_than(first));
    }

    #[test]
    fn cpu_prefers_a_named_sensor_over_a_hotter_core() {
        let tree = RawTree {
            hardware: vec![hardware(
                "Cpu",
                "AMD Ryzen 7 5800H",
                vec![
                    sensor(TEMPERATURE, "Core #1", Some(80.0)),
                    sensor(TEMPERATURE, "Core (Tctl/Tdie)", Some(55.25)),
                    sensor(LOAD, "CPU Core #1", Some(90.0)),
                    sensor(LOAD, "CPU Total", Some(12.5)),
                ],
                vec![],
            )],
            error: None,
        };

        let snapshot = select_snapshot(&tree);
        assert_eq!(snapshot.cpu_temperature.celsius, Some(Tenths(552)));
        assert_eq!(snapshot.cpu_temperature.source, "AMD Ryzen 7 5800H \u{2022} Core (Tctl/Tdie)");
        assert_eq!(snapshot.cpu_temperature.device_name, "AMD Ryzen 7 5800H");
        assert_eq!(snapshot.cpu_usage.percent, Some(Tenths(125)));
    }

    #[test]
    fn cpu_falls_back_to_the_hottest_usable_sensor() {
        let tree = RawTree {
            hardware: vec![hardware(
                "Cpu",
                "CPU",
                vec![
                    sensor(TEMPERATURE, "Core #1", Some(50.0)),
                    sensor(TEMPERATURE, "Core #2", Some(61.0)),
                    sensor(TEMPERATURE, "Core #3", Some(0.0)),
                ],
                vec![],
            )],
            error: None,
        };

        assert_eq!(select_snapshot(&tree).cpu_temperature.celsius, Some(Tenths(610)));
    }

    #[test]
    fn missing_driver_readings_stay_unavailable() {
        // 未安裝 PawnIO 時 LibreHardwareMonitor 會回報 null 或 0，絕不能顯示為溫度
        let tree = RawTree {
            hardware: vec![hardware(
                "Cpu",
                "13th Gen Intel Core i9-13900K",
                vec![
                    sensor(TEMPERATURE, "CPU Package", None),
                    sensor(TEMPERATURE, "Core Average", Some(0.0)),
                    sensor(LOAD, "CPU Core #1 Thread #1", Some(40.0)),
                ],
                vec![],
            )],
            error: None,
        };

        let snapshot = select_snapshot(&tree);
        assert!(!snapshot.cpu_temperature.is_available());
        assert!(!snapshot.cpu_usage.is_available(), "per-thread load must not stand in for CPU Total");
    }

    #[test]
    fn each_gpu_keeps_its_own_readings_and_identity() {
        let tree = RawTree {
            hardware: vec![
                hardware(
                    "GpuNvidia",
                    "NVIDIA GeForce RTX 4080",
                    vec![
                        sensor(TEMPERATURE, "GPU Hot Spot", Some(58.0)),
                        sensor(TEMPERATURE, "GPU Core", Some(46.0)),
                        sensor(LOAD, "GPU Memory Controller", Some(16.0)),
                        sensor(LOAD, "GPU Core", Some(23.0)),
                    ],
                    vec![],
                ),
                hardware("GpuIntel", "Intel(R) UHD Graphics 770", vec![sensor(LOAD, "D3D 3D", Some(3.0))], vec![]),
            ],
            error: None,
        };

        let snapshot = select_snapshot(&tree);
        assert_eq!(snapshot.gpus.len(), 2);
        assert_eq!(snapshot.gpus[0].id, "GpuNvidia:NVIDIA GeForce RTX 4080:0");
        assert_eq!(snapshot.gpus[0].temperature.celsius, Some(Tenths(460)));
        assert_eq!(snapshot.gpus[0].usage.percent, Some(Tenths(230)));
        assert_eq!(snapshot.gpus[1].id, "GpuIntel:Intel(R) UHD Graphics 770:1");
        assert!(!snapshot.gpus[1].temperature.is_available());
        assert_eq!(snapshot.gpus[1].usage.percent, Some(Tenths(30)));
    }

    #[test]
    fn gpu_sub_hardware_belongs_to_the_parent_device() {
        let tree = RawTree {
            hardware: vec![hardware(
                "GpuAmd",
                "Radeon",
                vec![],
                vec![hardware("Storage", "Radeon memory", vec![sensor(TEMPERATURE, "Core", Some(70.0))], vec![])],
            )],
            error: None,
        };

        let snapshot = select_snapshot(&tree);
        assert_eq!(snapshot.gpus.len(), 1);
        assert_eq!(snapshot.gpus[0].temperature.celsius, Some(Tenths(700)));
        assert_eq!(snapshot.gpus[0].temperature.device_name, "Radeon memory");
    }

    #[test]
    fn sidecar_json_parses_null_values() {
        let tree: RawTree = serde_json::from_str(
            r#"{"hardware":[{"type":"Cpu","name":"CPU","sensors":[{"type":"Temperature","name":"CPU Package","value":null},{"type":"Load","name":"CPU Total","value":17.3}],"sub":[]}]}"#,
        )
        .unwrap();

        let snapshot = select_snapshot(&tree);
        assert!(!snapshot.cpu_temperature.is_available());
        assert_eq!(snapshot.cpu_usage.percent, Some(Tenths(173)));
        assert_eq!(flatten(&tree).len(), 2);
    }

    #[test]
    fn sidecar_errors_are_reported() {
        let tree: RawTree = serde_json::from_str(r#"{"error":"IOException: boom"}"#).unwrap();
        assert_eq!(tree.error.as_deref(), Some("IOException: boom"));
    }
}
