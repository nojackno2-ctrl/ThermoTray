namespace ThermoTray;

public readonly record struct TemperatureReading(decimal? Celsius, string Source)
{
    public static TemperatureReading Unavailable { get; } = new(null, string.Empty);

    public bool IsAvailable => Celsius.HasValue;
}

public readonly record struct TemperatureSnapshot(TemperatureReading Cpu, TemperatureReading Gpu);
