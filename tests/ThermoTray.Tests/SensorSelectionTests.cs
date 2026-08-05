using LibreHardwareMonitor.Hardware;
using Xunit;

namespace ThermoTray.Tests;

/// <summary>
/// <see cref="HardwareSensorService.GetPreferredRank"/> 感測器名稱權重評分單元測試。
/// </summary>
public sealed class PreferredRankTests
{
    private static readonly string[] CpuPreferredNames = ["Tctl/Tdie", "Package", "CPU Package", "Core Average"];

    /// <summary>
    /// 驗證子字串對應與權重計算。
    /// </summary>
    [Theory]
    [InlineData("Core (Tctl/Tdie)", 0)]
    [InlineData("CPU Package", 1)]
    [InlineData("Core Average", 3)]
    public void GetPreferredRank_MatchesOnSubstring(string sensorName, int expected) =>
        Assert.Equal(expected, HardwareSensorService.GetPreferredRank(sensorName, CpuPreferredNames));

    /// <summary>
    /// 驗證名稱匹配不區分大小寫。
    /// </summary>
    [Fact]
    public void GetPreferredRank_IsCaseInsensitive() =>
        Assert.Equal(0, HardwareSensorService.GetPreferredRank("core (tctl/tdie)", CpuPreferredNames));

    /// <summary>
    /// 驗證無匹配項時傳回 int.MaxValue。
    /// </summary>
    [Fact]
    public void GetPreferredRank_ReturnsMaxValue_WhenNothingMatches() =>
        Assert.Equal(int.MaxValue, HardwareSensorService.GetPreferredRank("Core #3", CpuPreferredNames));

    /// <summary>
    /// 驗證多項對應時優先使用較靠前的偏好名稱。
    /// </summary>
    [Fact]
    public void GetPreferredRank_PrefersTheEarlierEntry_WhenSeveralMatch() =>
        Assert.Equal(1, HardwareSensorService.GetPreferredRank("CPU Package", CpuPreferredNames));
}

/// <summary>
/// <see cref="HardwareSensorService.IsUsableTemperature"/> 溫度讀值有效性驗證單元測試。
/// </summary>
public sealed class UsableTemperatureTests
{
    /// <summary>
    /// 驗證範圍內數值可被接受。
    /// </summary>
    [Theory]
    [InlineData(1f)]
    [InlineData(62.125f)]
    [InlineData(125f)]
    public void IsUsableTemperature_AcceptsValuesInsideTheRange(float celsius) =>
        Assert.True(HardwareSensorService.IsUsableTemperature(SensorType.Temperature, celsius, 1, 125));

    /// <summary>
    /// 驗證拒絕 0°C 占位數值。
    /// </summary>
    [Fact]
    public void IsUsableTemperature_RejectsZero() =>
        Assert.False(HardwareSensorService.IsUsableTemperature(SensorType.Temperature, 0f, 1, 125));

    /// <summary>
    /// 驗證拒絕 null 讀值。
    /// </summary>
    [Fact]
    public void IsUsableTemperature_RejectsNull() =>
        Assert.False(HardwareSensorService.IsUsableTemperature(SensorType.Temperature, null, 1, 125));

    /// <summary>
    /// 驗證拒絕 NaN 或正負無窮大數值。
    /// </summary>
    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void IsUsableTemperature_RejectsNonFiniteValues(float celsius) =>
        Assert.False(HardwareSensorService.IsUsableTemperature(SensorType.Temperature, celsius, 1, 125));

    /// <summary>
    /// 驗證拒絕超出範圍的溫度數值（如低於 1°C 或高於 125°C）。
    /// </summary>
    [Theory]
    [InlineData(-5f)]
    [InlineData(126f)]
    public void IsUsableTemperature_RejectsValuesOutsideTheRange(float celsius) =>
        Assert.False(HardwareSensorService.IsUsableTemperature(SensorType.Temperature, celsius, 1, 125));

    /// <summary>
    /// 驗證拒絕非溫度類型的感測器。
    /// </summary>
    [Fact]
    public void IsUsableTemperature_RejectsNonTemperatureSensors() =>
        Assert.False(HardwareSensorService.IsUsableTemperature(SensorType.Load, 50f, 1, 125));
}

/// <summary>
/// <see cref="HardwareSensorService.IsUsableUtilization"/> 使用率讀值有效性驗證單元測試。
/// </summary>
public sealed class UsableUtilizationTests
{
    /// <summary>
    /// 驗證範圍內使用率 (0% ~ 100%) 可被接受（允許 0% 閒置讀值）。
    /// </summary>
    [Theory]
    [InlineData(0f)]
    [InlineData(42.5f)]
    [InlineData(100f)]
    public void IsUsableUtilization_AcceptsValuesInsideTheRange(float percent) =>
        Assert.True(HardwareSensorService.IsUsableUtilization(SensorType.Load, percent));

    /// <summary>
    /// 驗證拒絕 null 使用率。
    /// </summary>
    [Fact]
    public void IsUsableUtilization_RejectsNull() =>
        Assert.False(HardwareSensorService.IsUsableUtilization(SensorType.Load, null));

    /// <summary>
    /// 驗證拒絕非有限數值。
    /// </summary>
    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void IsUsableUtilization_RejectsNonFiniteValues(float percent) =>
        Assert.False(HardwareSensorService.IsUsableUtilization(SensorType.Load, percent));

    /// <summary>
    /// 驗證拒絕超出 0%~100% 範圍的數值。
    /// </summary>
    [Theory]
    [InlineData(-0.1f)]
    [InlineData(100.1f)]
    public void IsUsableUtilization_RejectsValuesOutsideTheRange(float percent) =>
        Assert.False(HardwareSensorService.IsUsableUtilization(SensorType.Load, percent));

    /// <summary>
    /// 驗證拒絕非 Load 類型的感測器。
    /// </summary>
    [Fact]
    public void IsUsableUtilization_RejectsNonLoadSensors() =>
        Assert.False(HardwareSensorService.IsUsableUtilization(SensorType.Temperature, 50f));
}

/// <summary>
/// <see cref="GpuSensorRank"/> GPU 感測器優先順序評分單元測試。
/// </summary>
public sealed class GpuSensorRankTests
{
    /// <summary>
    /// 驗證任何有效候選者均優於 None。
    /// </summary>
    [Fact]
    public void AnyCandidate_BeatsNone() =>
        Assert.True(Rank(nameRank: int.MaxValue, GpuSensorRank.IntegratedPriority, sequence: 9).IsBetterThan(GpuSensorRank.None));

    /// <summary>
    /// 驗證感測器名稱匹配優先於硬體類別（內顯 vs 獨顯）。
    /// </summary>
    [Fact]
    public void SensorNameOutranksHardwareType()
    {
        var namedIntegrated = Rank(nameRank: 0, GpuSensorRank.IntegratedPriority, sequence: 5);
        var unnamedDiscrete = Rank(nameRank: 1, GpuSensorRank.DiscretePriority, sequence: 0);

        Assert.True(namedIntegrated.IsBetterThan(unnamedDiscrete));
        Assert.False(unnamedDiscrete.IsBetterThan(namedIntegrated));
    }

    /// <summary>
    /// 驗證名稱權重相同時，獨顯優先於內顯。
    /// </summary>
    [Fact]
    public void DiscreteOutranksIntegrated_AtEqualSensorName()
    {
        var discrete = Rank(nameRank: 0, GpuSensorRank.DiscretePriority, sequence: 3);
        var integrated = Rank(nameRank: 0, GpuSensorRank.IntegratedPriority, sequence: 0);

        Assert.True(discrete.IsBetterThan(integrated));
        Assert.False(integrated.IsBetterThan(discrete));
    }

    /// <summary>
    /// 驗證其他條件相同時，先偵測到的感測器（較早的探索順序）勝出。
    /// </summary>
    [Fact]
    public void DiscoveryOrderBreaksRemainingTies()
    {
        var first = Rank(nameRank: 0, GpuSensorRank.DiscretePriority, sequence: 0);
        var second = Rank(nameRank: 0, GpuSensorRank.DiscretePriority, sequence: 1);

        Assert.True(first.IsBetterThan(second));
        Assert.False(second.IsBetterThan(first));
    }

    /// <summary>
    /// 驗證同等 Rank 比較傳回 false。
    /// </summary>
    [Fact]
    public void EqualRanksDoNotDisplaceEachOther()
    {
        var rank = Rank(nameRank: 0, GpuSensorRank.DiscretePriority, sequence: 2);

        Assert.False(rank.IsBetterThan(rank));
    }

    private static GpuSensorRank Rank(int nameRank, int hardwarePriority, int sequence) =>
        new(nameRank, hardwarePriority, sequence);
}
