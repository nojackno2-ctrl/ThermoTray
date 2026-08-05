using LibreHardwareMonitor.Hardware;

namespace ThermoTray;

/// <summary>
/// 負責直接存取硬體感測器並讀取 CPU/GPU 溫度與使用率的服務類別。
/// 嚴格遵循「不安裝假資料、不安裝估算數值」的原則，若無法讀取真實感測器則明確回報為無法取得。
/// </summary>
public sealed class HardwareSensorService : IDisposable
{
    private const float CpuMinimumCelsius = 1;
    private const float CpuMaximumCelsius = 125;
    private const float GpuMinimumCelsius = 1;
    private const float GpuMaximumCelsius = 150;

    private static readonly string[] CpuPreferredNames = ["Tctl/Tdie", "Package", "CPU Package", "Core Average"];
    private static readonly string[] GpuPreferredNames = ["GPU Core", "Core", "Hot Spot"];
    private static readonly string[] CpuUsagePreferredNames = ["CPU Total", "Total"];
    private static readonly string[] GpuUsagePreferredNames = ["GPU Core", "GPU Total", "Core"];
    private static readonly UpdateVisitor HardwareUpdater = new();

    private readonly Computer _computer = new()
    {
        IsCpuEnabled = true,
        IsGpuEnabled = true,
    };

    private readonly HardwareEventHandler _onHardwareChanged;
    private readonly SensorEventHandler _onSensorChanged;
    private readonly Dictionary<ISensor, string> _sourceNames = new(ReferenceEqualityComparer.Instance);

    // 在拓撲掃描時快取感測器與優先級，避免每次採樣時重新進行字串匹配
    private IHardware[] _hardware = [];
    private CpuCandidate[] _cpuCandidates = [];
    private GpuDeviceCandidate[] _gpuDevices = [];
    private CpuCandidate[] _cpuUsageCandidates = [];

    // 由 LibreHardwareMonitor 事件觸發的硬體變更標記
    private volatile bool _topologyChanged;
    private bool _opened;

    /// <summary>
    /// 初始化 HardwareSensorService 並訂閱硬體變更委派。
    /// </summary>
    public HardwareSensorService()
    {
        _onHardwareChanged = _ => _topologyChanged = true;
        _onSensorChanged = _ => _topologyChanged = true;
    }

    /// <summary>
    /// 讀取一次完整的硬體快照（包含 CPU/GPU 溫度與使用率）。
    /// </summary>
    /// <returns>包含當前讀值的 <see cref="HardwareSnapshot"/>。</returns>
    public HardwareSnapshot Read()
    {
        UpdateHardware();

        return new HardwareSnapshot(
            SelectCpuReading(),
            SelectCpuUsage(),
            SelectGpuReadings());
    }

    /// <summary>
    /// 讀取所有裸感測器數據（供 `--diagnostics` 命令列診斷輸出使用）。
    /// </summary>
    public IReadOnlyList<RawHardwareSensor> ReadRawSensors()
    {
        UpdateHardware();

        var sensors = new List<RawHardwareSensor>();
        foreach (var hardware in _hardware)
        {
            AppendRawSensors(hardware, sensors);
        }

        return sensors;
    }

    /// <summary>
    /// 更新硬體感測器數值，若檢測到拓撲結構改變（如硬體插拔）則重新計算感測器優先順序。
    /// </summary>
    private void UpdateHardware()
    {
        EnsureOpen();
        _computer.Accept(HardwareUpdater);

        if (_topologyChanged)
        {
            _topologyChanged = false;
            RefreshTopology();
        }
    }

    /// <summary>
    /// 確保 LibreHardwareMonitor Computer 物件已開啟並註冊變更監聽。
    /// </summary>
    private void EnsureOpen()
    {
        if (_opened)
        {
            return;
        }

        _computer.HardwareAdded += _onHardwareChanged;
        _computer.HardwareRemoved += _onHardwareChanged;

        try
        {
            _computer.Open();
        }
        catch
        {
            _computer.HardwareAdded -= _onHardwareChanged;
            _computer.HardwareRemoved -= _onHardwareChanged;
            throw;
        }

        _opened = true;
        _topologyChanged = false;
        RefreshTopology();
    }

    /// <summary>
    /// 重新整理並重新掃描硬體樹狀結構，將感測器過濾與排序結果快取至陣列中。
    /// </summary>
    private void RefreshTopology()
    {
        try
        {
            // 在重新整理拓撲時先解綁舊硬體的事件，防止移除的硬體殘留委派導致記憶體洩漏
            foreach (var root in _hardware)
            {
                UnsubscribeSensorEvents(root);
            }

            // 清除過期的感測器名稱快取
            _sourceNames.Clear();

            var hardware = _computer.Hardware;
            var roots = new IHardware[hardware.Count];
            hardware.CopyTo(roots, 0);
            _hardware = roots;

            var cpuCandidates = new List<CpuCandidate>();
            var cpuUsageCandidates = new List<CpuCandidate>();
            var gpuBuilders = new List<GpuDeviceBuilder>();
            var gpuBuildersByHardware = new Dictionary<IHardware, GpuDeviceBuilder>(ReferenceEqualityComparer.Instance);
            foreach (var root in roots)
            {
                CollectCandidates(
                    root,
                    cpuCandidates,
                    cpuUsageCandidates,
                    gpuBuilders,
                    gpuBuildersByHardware,
                    gpuHardware: null);
            }

            _cpuCandidates = cpuCandidates.ToArray();
            _cpuUsageCandidates = cpuUsageCandidates.ToArray();

            var gpuDevices = new GpuDeviceCandidate[gpuBuilders.Count];
            for (var index = 0; index < gpuBuilders.Count; index++)
            {
                var builder = gpuBuilders[index];
                gpuDevices[index] = new GpuDeviceCandidate(
                    $"{builder.Hardware.HardwareType}:{builder.Hardware.Name}:{index}",
                    builder.Hardware.Name,
                    builder.TemperatureCandidates.ToArray(),
                    builder.UsageCandidates.ToArray());
            }

            _gpuDevices = gpuDevices;
        }
        catch
        {
            // 發生暫時性拓撲掃描錯誤時標記重新整理，供下次採樣重試
            _topologyChanged = true;
            throw;
        }
    }

    /// <summary>
    /// 遞迴收集指定硬體及其子硬體中的 CPU/GPU 溫度與使用率候選感測器。
    /// </summary>
    private void CollectCandidates(
        IHardware hardware,
        List<CpuCandidate> cpuCandidates,
        List<CpuCandidate> cpuUsageCandidates,
        List<GpuDeviceBuilder> gpuBuilders,
        Dictionary<IHardware, GpuDeviceBuilder> gpuBuildersByHardware,
        IHardware? gpuHardware)
    {
        hardware.SensorAdded -= _onSensorChanged;
        hardware.SensorAdded += _onSensorChanged;
        hardware.SensorRemoved -= _onSensorChanged;
        hardware.SensorRemoved += _onSensorChanged;

        var isCpu = hardware.HardwareType == HardwareType.Cpu;
        var isGpu = hardware.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel;
        var currentGpuHardware = isGpu ? hardware : gpuHardware;
        var hardwarePriority = currentGpuHardware?.HardwareType == HardwareType.GpuIntel
            ? GpuSensorRank.IntegratedPriority
            : GpuSensorRank.DiscretePriority;
        GpuDeviceBuilder? gpuBuilder = null;
        if (currentGpuHardware is not null)
        {
            if (!gpuBuildersByHardware.TryGetValue(currentGpuHardware, out gpuBuilder))
            {
                gpuBuilder = new GpuDeviceBuilder(currentGpuHardware);
                gpuBuildersByHardware.Add(currentGpuHardware, gpuBuilder);
                gpuBuilders.Add(gpuBuilder);
            }
        }

        foreach (var sensor in hardware.Sensors)
        {
            DisableValueHistory(sensor);

            if (sensor.SensorType == SensorType.Temperature)
            {
                if (isCpu)
                {
                    cpuCandidates.Add(new CpuCandidate(hardware, sensor, GetPreferredRank(sensor.Name, CpuPreferredNames)));
                }
                else if (gpuBuilder is not null)
                {
                    var sequence = gpuBuilder.TemperatureCandidates.Count;
                    gpuBuilder.TemperatureCandidates.Add(new GpuCandidate(
                        hardware,
                        sensor,
                        new GpuSensorRank(GetPreferredRank(sensor.Name, GpuPreferredNames), hardwarePriority, sequence),
                        new GpuSensorRank(GpuSensorRank.AnyName, hardwarePriority, sequence)));
                }
            }
            else if (sensor.SensorType == SensorType.Load)
            {
                if (isCpu)
                {
                    cpuUsageCandidates.Add(new CpuCandidate(hardware, sensor, GetPreferredRank(sensor.Name, CpuUsagePreferredNames)));
                }
                else if (gpuBuilder is not null)
                {
                    var sequence = gpuBuilder.UsageCandidates.Count;
                    gpuBuilder.UsageCandidates.Add(new GpuUsageCandidate(
                        hardware,
                        sensor,
                        new GpuSensorRank(GetPreferredRank(sensor.Name, GpuUsagePreferredNames), hardwarePriority, sequence)));
                }
            }
        }

        foreach (var subHardware in hardware.SubHardware)
        {
            CollectCandidates(
                subHardware,
                cpuCandidates,
                cpuUsageCandidates,
                gpuBuilders,
                gpuBuildersByHardware,
                currentGpuHardware);
        }
    }

    /// <summary>
    /// 停用 LibreHardwareMonitor 感測器的歷史紀錄視窗 (ValuesTimeWindow = TimeSpan.Zero)，
    /// 避免在背景長期執行時 List 成長與頻繁移位造成的記憶體與 CPU 開銷。
    /// </summary>
    private static void DisableValueHistory(ISensor sensor)
    {
        if (sensor.ValuesTimeWindow != TimeSpan.Zero)
        {
            sensor.ValuesTimeWindow = TimeSpan.Zero;
        }
    }

    /// <summary>
    /// 從 CPU 候選者中評選最佳溫度讀值。優先使用符合名稱條件者，否則退回使用最高有效溫度。
    /// </summary>
    private TemperatureReading SelectCpuReading()
    {
        SensorCandidate preferred = default;
        var preferredRank = int.MaxValue;
        SensorCandidate fallback = default;

        foreach (var candidate in _cpuCandidates)
        {
            if (!TryGetTemperature(candidate.Sensor, CpuMinimumCelsius, CpuMaximumCelsius, out var celsius))
            {
                continue;
            }

            if (candidate.NameRank < preferredRank)
            {
                preferred = new SensorCandidate(candidate.Hardware, candidate.Sensor, celsius);
                preferredRank = candidate.NameRank;
            }

            if (!fallback.IsValid || celsius > fallback.Value)
            {
                fallback = new SensorCandidate(candidate.Hardware, candidate.Sensor, celsius);
            }
        }

        return ToReading(preferred.IsValid ? preferred : fallback);
    }

    /// <summary>
    /// 評選 CPU 總使用率讀值。
    /// </summary>
    private UtilizationReading SelectCpuUsage()
    {
        SensorCandidate preferred = default;
        var preferredRank = int.MaxValue;

        foreach (var candidate in _cpuUsageCandidates)
        {
            if (!TryGetUsage(candidate.Sensor, out var percent))
            {
                continue;
            }

            if (candidate.NameRank < preferredRank)
            {
                preferred = new SensorCandidate(candidate.Hardware, candidate.Sensor, percent);
                preferredRank = candidate.NameRank;
            }
        }

        return ToUtilizationReading(preferred);
    }

    /// <summary>
    /// 逐張 GPU 評選最佳溫度與使用率，確保兩種讀值來自同一個實體裝置。
    /// </summary>
    private IReadOnlyList<GpuReading> SelectGpuReadings()
    {
        var readings = new GpuReading[_gpuDevices.Length];
        for (var index = 0; index < _gpuDevices.Length; index++)
        {
            var device = _gpuDevices[index];
            readings[index] = new GpuReading(
                device.Id,
                device.Name,
                SelectGpuTemperature(device.TemperatureCandidates),
                SelectGpuUsage(device.UsageCandidates));
        }

        return readings;
    }

    /// <summary>
    /// 從單一 GPU 的候選者中評選最佳溫度讀值。
    /// </summary>
    private TemperatureReading SelectGpuTemperature(IReadOnlyList<GpuCandidate> candidates)
    {
        SensorCandidate preferred = default;
        var preferredRank = GpuSensorRank.None;
        SensorCandidate fallback = default;
        var fallbackRank = GpuSensorRank.None;

        foreach (var candidate in candidates)
        {
            if (!TryGetTemperature(candidate.Sensor, GpuMinimumCelsius, GpuMaximumCelsius, out var celsius))
            {
                continue;
            }

            if (candidate.PreferredRank.IsBetterThan(preferredRank))
            {
                preferred = new SensorCandidate(candidate.Hardware, candidate.Sensor, celsius);
                preferredRank = candidate.PreferredRank;
            }

            if (candidate.FallbackRank.IsBetterThan(fallbackRank))
            {
                fallback = new SensorCandidate(candidate.Hardware, candidate.Sensor, celsius);
                fallbackRank = candidate.FallbackRank;
            }
        }

        return ToReading(preferred.IsValid ? preferred : fallback);
    }

    /// <summary>
    /// 從單一 GPU 的候選者中評選最佳使用率讀值。
    /// </summary>
    private UtilizationReading SelectGpuUsage(IReadOnlyList<GpuUsageCandidate> candidates)
    {
        SensorCandidate preferred = default;
        var preferredRank = GpuSensorRank.None;

        foreach (var candidate in candidates)
        {
            if (!TryGetUsage(candidate.Sensor, out var percent))
            {
                continue;
            }

            if (candidate.PreferredRank.IsBetterThan(preferredRank))
            {
                preferred = new SensorCandidate(candidate.Hardware, candidate.Sensor, percent);
                preferredRank = candidate.PreferredRank;
            }
        }

        return ToUtilizationReading(preferred);
    }

    /// <summary>
    /// 嘗試取得並校驗溫度數值是否落在合理範圍（如 1°C ~ 125°C）。
    /// </summary>
    private static bool TryGetTemperature(ISensor sensor, float minimumCelsius, float maximumCelsius, out float celsius)
    {
        var value = sensor.Value;
        celsius = value.GetValueOrDefault();
        return IsUsableTemperature(sensor.SensorType, value, minimumCelsius, maximumCelsius);
    }

    /// <summary>
    /// 嘗試取得並校驗使用率數值是否落在合理範圍 (0% ~ 100%)。
    /// </summary>
    private static bool TryGetUsage(ISensor sensor, out float percent)
    {
        percent = sensor.Value.GetValueOrDefault();
        return IsUsableUtilization(sensor.SensorType, sensor.Value);
    }

    /// <summary>
    /// 驗證溫度讀值是否可用且合理。排除 null、NaN、無窮大及超出邊界的異常讀值（如 0°C 或 200°C）。
    /// </summary>
    internal static bool IsUsableTemperature(SensorType sensorType, float? value, float minimumCelsius, float maximumCelsius) =>
        sensorType == SensorType.Temperature
        && value is float celsius
        && float.IsFinite(celsius)
        && celsius >= minimumCelsius
        && celsius <= maximumCelsius;

    /// <summary>
    /// 驗證使用率讀值是否可用且合理。允許 0% 讀值（閒置狀態）。
    /// </summary>
    internal static bool IsUsableUtilization(SensorType sensorType, float? value) =>
        sensorType == SensorType.Load
        && value is float percent
        && float.IsFinite(percent)
        && percent >= 0
        && percent <= 100;

    /// <summary>
    /// 計算感測器名稱在偏好清單中的優先度索引（越小越優先）。
    /// </summary>
    internal static int GetPreferredRank(string sensorName, IReadOnlyList<string> preferredNames)
    {
        for (var index = 0; index < preferredNames.Count; index++)
        {
            if (sensorName.Contains(preferredNames[index], StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return int.MaxValue;
    }

    /// <summary>
    /// 將感測器候選者包裝為四捨五入至小數第一位的 <see cref="TemperatureReading"/>。
    /// </summary>
    private TemperatureReading ToReading(SensorCandidate candidate)
    {
        if (!candidate.IsValid)
        {
            return TemperatureReading.Unavailable;
        }

        return new TemperatureReading(
            decimal.Round((decimal)candidate.Value, 1),
            GetSourceName(candidate),
            candidate.Hardware!.Name);
    }

    /// <summary>
    /// 將感測器候選者包裝為四捨五入至小數第一位的 <see cref="UtilizationReading"/>。
    /// </summary>
    private UtilizationReading ToUtilizationReading(SensorCandidate candidate)
    {
        if (!candidate.IsValid)
        {
            return UtilizationReading.Unavailable;
        }

        return new UtilizationReading(
            decimal.Round((decimal)candidate.Value, 1),
            GetSourceName(candidate),
            candidate.Hardware!.Name);
    }

    /// <summary>
    /// 取得硬體與感測器的完整組合來源名稱（例如 "AMD Ryzen 7 5800H • Core (Tctl/Tdie)"）。
    /// </summary>
    private string GetSourceName(SensorCandidate candidate)
    {
        if (!_sourceNames.TryGetValue(candidate.Sensor!, out var source))
        {
            source = $"{candidate.Hardware!.Name} • {candidate.Sensor!.Name}";
            _sourceNames.Add(candidate.Sensor, source);
        }

        return source;
    }

    /// <summary>
    /// 遞迴將硬體的所有感測器資訊加入診斷列表。
    /// </summary>
    private static void AppendRawSensors(IHardware hardware, ICollection<RawHardwareSensor> sensors)
    {
        foreach (var sensor in hardware.Sensors)
        {
            if (sensor.SensorType is SensorType.Temperature or SensorType.Load)
            {
                sensors.Add(new RawHardwareSensor(
                    hardware.HardwareType.ToString(),
                    hardware.Name,
                    sensor.SensorType.ToString(),
                    sensor.Name,
                    sensor.Value));
            }
        }

        foreach (var subHardware in hardware.SubHardware)
        {
            AppendRawSensors(subHardware, sensors);
        }
    }

    /// <summary>
    /// 關閉 LibreHardwareMonitor 並清理所有事件綁定與快整陣列。
    /// </summary>
    public void Dispose()
    {
        if (!_opened)
        {
            return;
        }

        _computer.HardwareAdded -= _onHardwareChanged;
        _computer.HardwareRemoved -= _onHardwareChanged;
        foreach (var hardware in _hardware)
        {
            UnsubscribeSensorEvents(hardware);
        }

        _computer.Close();
        _hardware = [];
        _cpuCandidates = [];
        _gpuDevices = [];
        _cpuUsageCandidates = [];
        _sourceNames.Clear();
        _opened = false;
    }

    /// <summary>
    /// 遞迴解綁感測器事件。
    /// </summary>
    private void UnsubscribeSensorEvents(IHardware hardware)
    {
        hardware.SensorAdded -= _onSensorChanged;
        hardware.SensorRemoved -= _onSensorChanged;

        foreach (var subHardware in hardware.SubHardware)
        {
            UnsubscribeSensorEvents(subHardware);
        }
    }

    private readonly record struct CpuCandidate(IHardware Hardware, ISensor Sensor, int NameRank);

    private readonly record struct GpuCandidate(
        IHardware Hardware,
        ISensor Sensor,
        GpuSensorRank PreferredRank,
        GpuSensorRank FallbackRank);

    private readonly record struct GpuUsageCandidate(
        IHardware Hardware,
        ISensor Sensor,
        GpuSensorRank PreferredRank);

    /// <summary>
    /// 拓撲掃描期間暫存單一實體 GPU 的所有候選感測器。
    /// </summary>
    private sealed class GpuDeviceBuilder(IHardware hardware)
    {
        public IHardware Hardware { get; } = hardware;

        public List<GpuCandidate> TemperatureCandidates { get; } = [];

        public List<GpuUsageCandidate> UsageCandidates { get; } = [];
    }

    /// <summary>
    /// 拓撲掃描完成後快取的單一 GPU 候選感測器集合。
    /// </summary>
    private readonly record struct GpuDeviceCandidate(
        string Id,
        string Name,
        GpuCandidate[] TemperatureCandidates,
        GpuUsageCandidate[] UsageCandidates);

    private readonly record struct SensorCandidate(IHardware? Hardware, ISensor? Sensor, float Value)
    {
        public bool IsValid => Sensor is not null;
    }

    /// <summary>
    /// LibreHardwareMonitor 的硬體造訪者類別，用於觸發 <c>hardware.Update()</c>。
    /// </summary>
    private sealed class UpdateVisitor : IVisitor
    {
        public void VisitComputer(IComputer computer) => computer.Traverse(this);

        public void VisitHardware(IHardware hardware)
        {
            hardware.Update();
            foreach (var subHardware in hardware.SubHardware)
            {
                subHardware.Accept(this);
            }
        }

        public void VisitSensor(ISensor sensor) { }

        public void VisitParameter(IParameter parameter) { }
    }
}

/// <summary>
/// 裸硬體感測器診斷資料模型。
/// </summary>
public sealed record RawHardwareSensor(
    string HardwareType,
    string HardwareName,
    string SensorType,
    string SensorName,
    float? Value);

/// <summary>
/// GPU 感測器排序權重結構體。
/// 比較順序：名稱偏好 -> 獨顯優先於內顯 -> 偵測順序。
/// </summary>
internal readonly record struct GpuSensorRank(int NameRank, int HardwarePriority, int Sequence)
{
    internal const int DiscretePriority = 0;
    internal const int IntegratedPriority = 1;
    internal const int AnyName = 0;

    internal static GpuSensorRank None { get; } = new(int.MaxValue, int.MaxValue, int.MaxValue);

    internal bool IsBetterThan(GpuSensorRank other)
    {
        if (NameRank != other.NameRank)
        {
            return NameRank < other.NameRank;
        }

        if (HardwarePriority != other.HardwarePriority)
        {
            return HardwarePriority < other.HardwarePriority;
        }

        return Sequence < other.Sequence;
    }
}
