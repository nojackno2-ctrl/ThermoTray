namespace ThermoTray;

public readonly record struct TemperatureReading(decimal? Celsius, string Source)
{
    public static TemperatureReading Unavailable { get; } = new(null, string.Empty);

    public bool IsAvailable => Celsius.HasValue;
}

public readonly record struct UtilizationReading(decimal? Percent, string Source)
{
    public static UtilizationReading Unavailable { get; } = new(null, string.Empty);

    public bool IsAvailable => Percent.HasValue;
}

public readonly record struct HardwareSnapshot(
    TemperatureReading CpuTemperature,
    TemperatureReading GpuTemperature,
    UtilizationReading CpuUsage,
    UtilizationReading GpuUsage);
