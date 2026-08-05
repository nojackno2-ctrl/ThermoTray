namespace ThermoTray;

/// <summary>
/// 表示單一硬體感測器的溫度讀值與來源名稱。
/// </summary>
/// <param name="Celsius">攝氏溫度數值（若無法取得則為 null）。</param>
/// <param name="Source">提供此溫度的硬體感測器名稱。</param>
public readonly record struct TemperatureReading(decimal? Celsius, string Source)
{
    /// <summary>
    /// 代表無法存取或無效的溫度讀值。
    /// </summary>
    public static TemperatureReading Unavailable { get; } = new(null, string.Empty);

    /// <summary>
    /// 取得一個值，表示溫度讀值是否可用且有效。
    /// </summary>
    public bool IsAvailable => Celsius.HasValue;
}

/// <summary>
/// 表示單一硬體元件的使用率讀值與來源名稱。
/// </summary>
/// <param name="Percent">使用率百分比數值（若無法取得則為 null）。</param>
/// <param name="Source">提供此使用率的硬體感測器名稱。</param>
public readonly record struct UtilizationReading(decimal? Percent, string Source)
{
    /// <summary>
    /// 代表無法存取或無效的使用率讀值。
    /// </summary>
    public static UtilizationReading Unavailable { get; } = new(null, string.Empty);

    /// <summary>
    /// 取得一個值，表示使用率讀值是否可用且有效。
    /// </summary>
    public bool IsAvailable => Percent.HasValue;
}

/// <summary>
/// 包含 CPU 與 GPU 溫度及使用率的完整硬體快照結構。
/// </summary>
/// <param name="CpuTemperature">CPU 溫度讀值。</param>
/// <param name="GpuTemperature">GPU 溫度讀值。</param>
/// <param name="CpuUsage">CPU 使用率讀值。</param>
/// <param name="GpuUsage">GPU 使用率讀值。</param>
public readonly record struct HardwareSnapshot(
    TemperatureReading CpuTemperature,
    TemperatureReading GpuTemperature,
    UtilizationReading CpuUsage,
    UtilizationReading GpuUsage);
