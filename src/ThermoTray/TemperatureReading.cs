namespace ThermoTray;

/// <summary>
/// 表示單一硬體感測器的溫度讀值與來源名稱。
/// </summary>
/// <param name="Celsius">攝氏溫度數值（若無法取得則為 null）。</param>
/// <param name="Source">提供此溫度的硬體感測器名稱。</param>
public readonly record struct TemperatureReading(decimal? Celsius, string Source, string DeviceName = "")
{
    /// <summary>
    /// 代表無法存取或無效的溫度讀值。
    /// </summary>
    public static TemperatureReading Unavailable { get; } = new(null, string.Empty, string.Empty);

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
public readonly record struct UtilizationReading(decimal? Percent, string Source, string DeviceName = "")
{
    /// <summary>
    /// 代表無法存取或無效的使用率讀值。
    /// </summary>
    public static UtilizationReading Unavailable { get; } = new(null, string.Empty, string.Empty);

    /// <summary>
    /// 取得一個值，表示使用率讀值是否可用且有效。
    /// </summary>
    public bool IsAvailable => Percent.HasValue;
}

/// <summary>
/// 表示一張實體 GPU 的完整讀值。溫度與使用率都屬於同一個硬體裝置，
/// 不會再從不同 GPU 各自挑選後混合顯示。
/// </summary>
/// <param name="Id">拓撲掃描時產生的穩定裝置識別碼。</param>
/// <param name="Name">LibreHardwareMonitor 回報的 GPU 裝置名稱。</param>
/// <param name="Temperature">該 GPU 的溫度讀值。</param>
/// <param name="Usage">該 GPU 的使用率讀值。</param>
public readonly record struct GpuReading(
    string Id,
    string Name,
    TemperatureReading Temperature,
    UtilizationReading Usage)
{
    /// <summary>
    /// 取得一個值，表示該 GPU 至少有一種可用的即時讀值。
    /// </summary>
    public bool IsAvailable => Temperature.IsAvailable || Usage.IsAvailable;
}

/// <summary>
/// 包含 CPU 與所有 GPU 溫度及使用率的完整硬體快照結構。
/// </summary>
/// <param name="CpuTemperature">CPU 溫度讀值。</param>
/// <param name="CpuUsage">CPU 使用率讀值。</param>
/// <param name="Gpus">每張 GPU 各自配對的溫度與使用率讀值。</param>
public readonly record struct HardwareSnapshot(
    TemperatureReading CpuTemperature,
    UtilizationReading CpuUsage,
    IReadOnlyList<GpuReading> Gpus);
