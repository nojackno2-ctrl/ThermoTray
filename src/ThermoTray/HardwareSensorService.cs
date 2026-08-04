using LibreHardwareMonitor.Hardware;

namespace ThermoTray;

/// <summary>Reads hardware sensors only. It deliberately has no estimated-temperature path.</summary>
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

    // Everything about a sensor that cannot change between samples is resolved once, when the
    // hardware appears, so a sample only reads values and compares numbers.
    private IHardware[] _hardware = [];
    private CpuCandidate[] _cpuCandidates = [];
    private GpuCandidate[] _gpuCandidates = [];
    private CpuCandidate[] _cpuUsageCandidates = [];
    private GpuUsageCandidate[] _gpuUsageCandidates = [];

    // Written by LibreHardwareMonitor's change events, which need not run on the sampling thread.
    private volatile bool _topologyChanged;
    private bool _opened;

    public HardwareSensorService()
    {
        _onHardwareChanged = _ => _topologyChanged = true;
        _onSensorChanged = _ => _topologyChanged = true;
    }

    public HardwareSnapshot Read()
    {
        UpdateHardware();

        return new HardwareSnapshot(
            SelectCpuReading(),
            SelectGpuReading(),
            SelectCpuUsage(),
            SelectGpuUsage());
    }

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

    private void UpdateHardware()
    {
        EnsureOpen();
        _computer.Accept(HardwareUpdater);

        // The scan runs after the update because a hardware update is what activates sensors, so a
        // sensor that appears on this pass is still selectable in this sample. The flag is cleared
        // first, so anything that appears during the scan is picked up next sample instead of lost.
        if (_topologyChanged)
        {
            _topologyChanged = false;
            RefreshTopology();
        }
    }

    private void EnsureOpen()
    {
        if (_opened)
        {
            return;
        }

        _computer.HardwareAdded += _onHardwareChanged;
        _computer.HardwareRemoved += _onHardwareChanged;
        _computer.Open();
        _opened = true;
        _topologyChanged = false;
        RefreshTopology();
    }

    /// <summary>
    /// Rebuilds the cached sensor lists. <see cref="Computer.Hardware"/> and <see cref="IHardware.Sensors"/>
    /// both copy into a fresh array on every call, and the sensor-name matching below is pure string
    /// work, so all of it is done here rather than once per sample.
    /// </summary>
    private void RefreshTopology()
    {
        var hardware = _computer.Hardware;
        var roots = new IHardware[hardware.Count];
        hardware.CopyTo(roots, 0);
        _hardware = roots;

        var cpuCandidates = new List<CpuCandidate>();
        var gpuCandidates = new List<GpuCandidate>();
        var cpuUsageCandidates = new List<CpuCandidate>();
        var gpuUsageCandidates = new List<GpuUsageCandidate>();
        foreach (var root in roots)
        {
            CollectCandidates(root, cpuCandidates, gpuCandidates, cpuUsageCandidates, gpuUsageCandidates);
        }

        _cpuCandidates = cpuCandidates.ToArray();
        _gpuCandidates = gpuCandidates.ToArray();
        _cpuUsageCandidates = cpuUsageCandidates.ToArray();
        _gpuUsageCandidates = gpuUsageCandidates.ToArray();
    }

    private void CollectCandidates(
        IHardware hardware,
        List<CpuCandidate> cpuCandidates,
        List<GpuCandidate> gpuCandidates,
        List<CpuCandidate> cpuUsageCandidates,
        List<GpuUsageCandidate> gpuUsageCandidates)
    {
        // Subscribing twice would raise the flag twice; removing an absent handler is a no-op, so this
        // stays correct across the repeated scans that a hardware change triggers.
        hardware.SensorAdded -= _onSensorChanged;
        hardware.SensorAdded += _onSensorChanged;
        hardware.SensorRemoved -= _onSensorChanged;
        hardware.SensorRemoved += _onSensorChanged;

        var isCpu = hardware.HardwareType == HardwareType.Cpu;
        var isGpu = hardware.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel;
        var hardwarePriority = hardware.HardwareType == HardwareType.GpuIntel
            ? GpuSensorRank.IntegratedPriority
            : GpuSensorRank.DiscretePriority;

        foreach (var sensor in hardware.Sensors)
        {
            DisableValueHistory(sensor);

            if (sensor.SensorType == SensorType.Temperature)
            {
                if (isCpu)
                {
                    cpuCandidates.Add(new CpuCandidate(hardware, sensor, GetPreferredRank(sensor.Name, CpuPreferredNames)));
                }
                else if (isGpu)
                {
                    // Discovery order is the last tiebreaker, so the position in this list is the sequence.
                    var sequence = gpuCandidates.Count;
                    gpuCandidates.Add(new GpuCandidate(
                        hardware,
                        sensor,
                        new GpuSensorRank(GetPreferredRank(sensor.Name, GpuPreferredNames), hardwarePriority, sequence),
                        // The fallback ignores the sensor name so an unrecognised GPU still reports something.
                        new GpuSensorRank(GpuSensorRank.AnyName, hardwarePriority, sequence)));
                }
            }
            else if (sensor.SensorType == SensorType.Load)
            {
                if (isCpu)
                {
                    cpuUsageCandidates.Add(new CpuCandidate(hardware, sensor, GetPreferredRank(sensor.Name, CpuUsagePreferredNames)));
                }
                else if (isGpu)
                {
                    var sequence = gpuUsageCandidates.Count;
                    gpuUsageCandidates.Add(new GpuUsageCandidate(
                        hardware,
                        sensor,
                        new GpuSensorRank(GetPreferredRank(sensor.Name, GpuUsagePreferredNames), hardwarePriority, sequence)));
                }
            }
        }

        foreach (var subHardware in hardware.SubHardware)
        {
            CollectCandidates(subHardware, cpuCandidates, gpuCandidates, cpuUsageCandidates, gpuUsageCandidates);
        }
    }

    /// <summary>
    /// LibreHardwareMonitor keeps a day of averaged history for every sensor it exposes, including the
    /// dozens ThermoTray never displays. ThermoTray only ever shows the current value, so in a process
    /// that stays running that history is pure growth: each list fills all day, and once it is full every
    /// later update shifts the whole list down to drop the expired entry. Turning the window off keeps
    /// both the memory and the per-update cost flat.
    /// </summary>
    private static void DisableValueHistory(ISensor sensor)
    {
        if (sensor.ValuesTimeWindow != TimeSpan.Zero)
        {
            sensor.ValuesTimeWindow = TimeSpan.Zero;
        }
    }

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

    private TemperatureReading SelectGpuReading()
    {
        SensorCandidate preferred = default;
        var preferredRank = GpuSensorRank.None;
        SensorCandidate fallback = default;
        var fallbackRank = GpuSensorRank.None;

        foreach (var candidate in _gpuCandidates)
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
    /// Unlike the temperature paths there is no name-agnostic fallback: a load sensor whose name is
    /// not recognised may describe a single core or an engine, and showing that as "CPU usage" would
    /// be wrong in a way an unavailable value is not.
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

    private UtilizationReading SelectGpuUsage()
    {
        SensorCandidate preferred = default;
        var preferredRank = GpuSensorRank.None;

        foreach (var candidate in _gpuUsageCandidates)
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

    private static bool TryGetTemperature(ISensor sensor, float minimumCelsius, float maximumCelsius, out float celsius)
    {
        var value = sensor.Value;
        celsius = value.GetValueOrDefault();
        return IsUsableTemperature(sensor.SensorType, value, minimumCelsius, maximumCelsius);
    }

    private static bool TryGetUsage(ISensor sensor, out float percent)
    {
        percent = sensor.Value.GetValueOrDefault();
        return IsUsableUtilization(sensor.SensorType, sensor.Value);
    }

    /// <summary>
    /// A sensor that exists but reports zero, a placeholder, or an impossible value is treated as
    /// missing rather than displayed, because showing it would be indistinguishable from a real reading.
    /// </summary>
    internal static bool IsUsableTemperature(SensorType sensorType, float? value, float minimumCelsius, float maximumCelsius) =>
        sensorType == SensorType.Temperature
        && value is float celsius
        && float.IsFinite(celsius)
        && celsius >= minimumCelsius
        && celsius <= maximumCelsius;

    /// <summary>
    /// A zero load is valid, because an idle CPU or GPU can legitimately report 0 percent. Only
    /// null, non-finite, out-of-range, and non-load sensor values are rejected.
    /// </summary>
    internal static bool IsUsableUtilization(SensorType sensorType, float? value) =>
        sensorType == SensorType.Load
        && value is float percent
        && float.IsFinite(percent)
        && percent >= 0
        && percent <= 100;

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

    private TemperatureReading ToReading(SensorCandidate candidate)
    {
        if (!candidate.IsValid)
        {
            return TemperatureReading.Unavailable;
        }

        return new TemperatureReading(decimal.Round((decimal)candidate.Value, 1), GetSourceName(candidate));
    }

    private UtilizationReading ToUtilizationReading(SensorCandidate candidate)
    {
        if (!candidate.IsValid)
        {
            return UtilizationReading.Unavailable;
        }

        return new UtilizationReading(decimal.Round((decimal)candidate.Value, 1), GetSourceName(candidate));
    }

    private string GetSourceName(SensorCandidate candidate)
    {
        if (!_sourceNames.TryGetValue(candidate.Sensor!, out var source))
        {
            source = $"{candidate.Hardware!.Name} • {candidate.Sensor!.Name}";
            _sourceNames.Add(candidate.Sensor, source);
        }

        return source;
    }

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
        _gpuCandidates = [];
        _cpuUsageCandidates = [];
        _gpuUsageCandidates = [];
        _sourceNames.Clear();
        _opened = false;
    }

    private void UnsubscribeSensorEvents(IHardware hardware)
    {
        hardware.SensorAdded -= _onSensorChanged;
        hardware.SensorRemoved -= _onSensorChanged;

        foreach (var subHardware in hardware.SubHardware)
        {
            UnsubscribeSensorEvents(subHardware);
        }
    }

    /// <summary>A CPU sensor with its sensor-name preference resolved at discovery time.</summary>
    private readonly record struct CpuCandidate(IHardware Hardware, ISensor Sensor, int NameRank);

    /// <summary>A GPU temperature sensor with both of its rankings resolved at discovery time.</summary>
    private readonly record struct GpuCandidate(
        IHardware Hardware,
        ISensor Sensor,
        GpuSensorRank PreferredRank,
        GpuSensorRank FallbackRank);

    /// <summary>A GPU load sensor with its ranking resolved at discovery time.</summary>
    private readonly record struct GpuUsageCandidate(
        IHardware Hardware,
        ISensor Sensor,
        GpuSensorRank PreferredRank);

    /// <summary>A selected sensor together with the value (°C or percent) it reported this sample.</summary>
    private readonly record struct SensorCandidate(IHardware? Hardware, ISensor? Sensor, float Value)
    {
        public bool IsValid => Sensor is not null;
    }

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

public sealed record RawHardwareSensor(
    string HardwareType,
    string HardwareName,
    string SensorType,
    string SensorName,
    float? Value);

/// <summary>
/// Orders GPU sensors by sensor-name preference first, then discrete before integrated,
/// then discovery order. Lower is better in every component.
/// </summary>
internal readonly record struct GpuSensorRank(int NameRank, int HardwarePriority, int Sequence)
{
    internal const int DiscretePriority = 0;
    internal const int IntegratedPriority = 1;

    /// <summary>Used by the fallback selection, which accepts any sensor name.</summary>
    internal const int AnyName = 0;

    /// <summary>Loses to every real candidate, so the first sensor offered always wins.</summary>
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
