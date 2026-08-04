using LibreHardwareMonitor.Hardware;

namespace ThermoTray;

/// <summary>Reads hardware sensors only. It deliberately has no estimated-temperature path.</summary>
public sealed class HardwareSensorService : IDisposable
{
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

    private readonly Dictionary<ISensor, string> _sourceNames = new(ReferenceEqualityComparer.Instance);
    private bool _opened;

    public HardwareSnapshot Read()
    {
        UpdateHardware();

        var selection = new SensorSelection();
        foreach (var hardware in _computer.Hardware)
        {
            SelectSensors(hardware, ref selection);
        }

        return new HardwareSnapshot(
            ToReading(selection.CpuPreferred.IsValid ? selection.CpuPreferred : selection.CpuFallback),
            ToReading(selection.GpuPreferred.IsValid ? selection.GpuPreferred : selection.GpuFallback),
            ToUtilizationReading(selection.CpuUsagePreferred),
            ToUtilizationReading(selection.GpuUsagePreferred));
    }

    public IReadOnlyList<RawHardwareSensor> ReadRawSensors()
    {
        UpdateHardware();

        var sensors = new List<RawHardwareSensor>();
        foreach (var hardware in _computer.Hardware)
        {
            AppendRawSensors(hardware, sensors);
        }

        return sensors;
    }

    private void UpdateHardware()
    {
        EnsureOpen();
        _computer.Accept(HardwareUpdater);
    }

    private void EnsureOpen()
    {
        if (_opened)
        {
            return;
        }

        _computer.Open();
        _opened = true;
    }

    private static void SelectSensors(IHardware hardware, ref SensorSelection selection)
    {
        if (hardware.HardwareType == HardwareType.Cpu)
        {
            SelectCpuSensors(hardware, ref selection);
        }
        else if (hardware.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel)
        {
            SelectGpuSensors(hardware, ref selection);
        }

        foreach (var subHardware in hardware.SubHardware)
        {
            SelectSensors(subHardware, ref selection);
        }
    }

    private static void SelectCpuSensors(IHardware hardware, ref SensorSelection selection)
    {
        foreach (var sensor in hardware.Sensors)
        {
            if (TryGetTemperature(sensor, minimumCelsius: 1, maximumCelsius: 125, out var celsius))
            {
                var candidate = new SensorCandidate(hardware, sensor, celsius);
                var preferredRank = GetPreferredRank(sensor.Name, CpuPreferredNames);
                if (preferredRank < selection.CpuPreferredRank)
                {
                    selection.CpuPreferred = candidate;
                    selection.CpuPreferredRank = preferredRank;
                }

                if (!selection.CpuFallback.IsValid || celsius > selection.CpuFallback.Celsius)
                {
                    selection.CpuFallback = candidate;
                }
            }

            if (TryGetUsage(sensor, out var percent))
            {
                var candidate = new SensorCandidate(hardware, sensor, percent);
                var preferredRank = GetPreferredRank(sensor.Name, CpuUsagePreferredNames);
                if (preferredRank < selection.CpuUsagePreferredRank)
                {
                    selection.CpuUsagePreferred = candidate;
                    selection.CpuUsagePreferredRank = preferredRank;
                }
            }
        }
    }

    private static void SelectGpuSensors(IHardware hardware, ref SensorSelection selection)
    {
        var hardwarePriority = hardware.HardwareType == HardwareType.GpuIntel
            ? GpuSensorRank.IntegratedPriority
            : GpuSensorRank.DiscretePriority;

        foreach (var sensor in hardware.Sensors)
        {
            if (TryGetTemperature(sensor, minimumCelsius: 1, maximumCelsius: 150, out var celsius))
            {
                var candidate = new SensorCandidate(hardware, sensor, celsius);
                var sequence = selection.GpuSequence++;

                var preferredRank = new GpuSensorRank(
                    GetPreferredRank(sensor.Name, GpuPreferredNames),
                    hardwarePriority,
                    sequence);
                if (preferredRank.IsBetterThan(selection.GpuPreferredRank))
                {
                    selection.GpuPreferred = candidate;
                    selection.GpuPreferredRank = preferredRank;
                }

                // The fallback ignores the sensor name so an unrecognised GPU still reports something.
                var fallbackRank = new GpuSensorRank(GpuSensorRank.AnyName, hardwarePriority, sequence);
                if (fallbackRank.IsBetterThan(selection.GpuFallbackRank))
                {
                    selection.GpuFallback = candidate;
                    selection.GpuFallbackRank = fallbackRank;
                }
            }

            if (TryGetUsage(sensor, out var percent))
            {
                var candidate = new SensorCandidate(hardware, sensor, percent);
                var usageRank = new GpuSensorRank(
                    GetPreferredRank(sensor.Name, GpuUsagePreferredNames),
                    hardwarePriority,
                    selection.GpuUsageSequence++);
                if (usageRank.IsBetterThan(selection.GpuUsagePreferredRank))
                {
                    selection.GpuUsagePreferred = candidate;
                    selection.GpuUsagePreferredRank = usageRank;
                }
            }
        }
    }

    private static bool TryGetTemperature(ISensor sensor, float minimumCelsius, float maximumCelsius, out float celsius)
    {
        celsius = sensor.Value.GetValueOrDefault();
        return IsUsableTemperature(sensor.SensorType, sensor.Value, minimumCelsius, maximumCelsius);
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

        if (!_sourceNames.TryGetValue(candidate.Sensor!, out var source))
        {
            source = $"{candidate.Hardware!.Name} • {candidate.Sensor!.Name}";
            _sourceNames.Add(candidate.Sensor, source);
        }

        return new TemperatureReading(decimal.Round((decimal)candidate.Celsius, 1), source);
    }

    private UtilizationReading ToUtilizationReading(SensorCandidate candidate)
    {
        if (!candidate.IsValid)
        {
            return UtilizationReading.Unavailable;
        }

        if (!_sourceNames.TryGetValue(candidate.Sensor!, out var source))
        {
            source = $"{candidate.Hardware!.Name} • {candidate.Sensor!.Name}";
            _sourceNames.Add(candidate.Sensor, source);
        }

        return new UtilizationReading(decimal.Round((decimal)candidate.Celsius, 1), source);
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

        _computer.Close();
        _sourceNames.Clear();
        _opened = false;
    }

    private readonly record struct SensorCandidate(IHardware? Hardware, ISensor? Sensor, float Celsius)
    {
        public bool IsValid => Sensor is not null;
    }

    private struct SensorSelection
    {
        public SensorCandidate CpuPreferred;
        public int CpuPreferredRank = int.MaxValue;
        public SensorCandidate CpuFallback;
        public SensorCandidate CpuUsagePreferred;
        public int CpuUsagePreferredRank = int.MaxValue;
        public SensorCandidate GpuPreferred;
        public GpuSensorRank GpuPreferredRank = GpuSensorRank.None;
        public SensorCandidate GpuFallback;
        public GpuSensorRank GpuFallbackRank = GpuSensorRank.None;
        public SensorCandidate GpuUsagePreferred;
        public GpuSensorRank GpuUsagePreferredRank = GpuSensorRank.None;
        public int GpuSequence;
        public int GpuUsageSequence;

        public SensorSelection()
        {
        }
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
/// Orders GPU temperature sensors by sensor-name preference first, then discrete before integrated,
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
