using LibreHardwareMonitor.Hardware;
using Xunit;

namespace ThermoTray.Tests;

public sealed class PreferredRankTests
{
    private static readonly string[] CpuPreferredNames = ["Tctl/Tdie", "Package", "CPU Package", "Core Average"];

    [Theory]
    [InlineData("Core (Tctl/Tdie)", 0)]
    [InlineData("CPU Package", 1)]
    [InlineData("Core Average", 3)]
    public void GetPreferredRank_MatchesOnSubstring(string sensorName, int expected) =>
        Assert.Equal(expected, HardwareSensorService.GetPreferredRank(sensorName, CpuPreferredNames));

    [Fact]
    public void GetPreferredRank_IsCaseInsensitive() =>
        Assert.Equal(0, HardwareSensorService.GetPreferredRank("core (tctl/tdie)", CpuPreferredNames));

    [Fact]
    public void GetPreferredRank_ReturnsMaxValue_WhenNothingMatches() =>
        Assert.Equal(int.MaxValue, HardwareSensorService.GetPreferredRank("Core #3", CpuPreferredNames));

    [Fact]
    public void GetPreferredRank_PrefersTheEarlierEntry_WhenSeveralMatch() =>
        Assert.Equal(1, HardwareSensorService.GetPreferredRank("CPU Package", CpuPreferredNames));
}

public sealed class UsableTemperatureTests
{
    [Theory]
    [InlineData(1f)]
    [InlineData(62.125f)]
    [InlineData(125f)]
    public void IsUsableTemperature_AcceptsValuesInsideTheRange(float celsius) =>
        Assert.True(HardwareSensorService.IsUsableTemperature(SensorType.Temperature, celsius, 1, 125));

    [Fact]
    public void IsUsableTemperature_RejectsZero() =>
        Assert.False(HardwareSensorService.IsUsableTemperature(SensorType.Temperature, 0f, 1, 125));

    [Fact]
    public void IsUsableTemperature_RejectsNull() =>
        Assert.False(HardwareSensorService.IsUsableTemperature(SensorType.Temperature, null, 1, 125));

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void IsUsableTemperature_RejectsNonFiniteValues(float celsius) =>
        Assert.False(HardwareSensorService.IsUsableTemperature(SensorType.Temperature, celsius, 1, 125));

    [Theory]
    [InlineData(-5f)]
    [InlineData(126f)]
    public void IsUsableTemperature_RejectsValuesOutsideTheRange(float celsius) =>
        Assert.False(HardwareSensorService.IsUsableTemperature(SensorType.Temperature, celsius, 1, 125));

    [Fact]
    public void IsUsableTemperature_RejectsNonTemperatureSensors() =>
        Assert.False(HardwareSensorService.IsUsableTemperature(SensorType.Load, 50f, 1, 125));
}

public sealed class GpuSensorRankTests
{
    [Fact]
    public void AnyCandidate_BeatsNone() =>
        Assert.True(Rank(nameRank: int.MaxValue, GpuSensorRank.IntegratedPriority, sequence: 9).IsBetterThan(GpuSensorRank.None));

    [Fact]
    public void SensorNameOutranksHardwareType()
    {
        var namedIntegrated = Rank(nameRank: 0, GpuSensorRank.IntegratedPriority, sequence: 5);
        var unnamedDiscrete = Rank(nameRank: 1, GpuSensorRank.DiscretePriority, sequence: 0);

        Assert.True(namedIntegrated.IsBetterThan(unnamedDiscrete));
        Assert.False(unnamedDiscrete.IsBetterThan(namedIntegrated));
    }

    [Fact]
    public void DiscreteOutranksIntegrated_AtEqualSensorName()
    {
        var discrete = Rank(nameRank: 0, GpuSensorRank.DiscretePriority, sequence: 3);
        var integrated = Rank(nameRank: 0, GpuSensorRank.IntegratedPriority, sequence: 0);

        Assert.True(discrete.IsBetterThan(integrated));
        Assert.False(integrated.IsBetterThan(discrete));
    }

    [Fact]
    public void DiscoveryOrderBreaksRemainingTies()
    {
        var first = Rank(nameRank: 0, GpuSensorRank.DiscretePriority, sequence: 0);
        var second = Rank(nameRank: 0, GpuSensorRank.DiscretePriority, sequence: 1);

        Assert.True(first.IsBetterThan(second));
        Assert.False(second.IsBetterThan(first));
    }

    [Fact]
    public void EqualRanksDoNotDisplaceEachOther()
    {
        var rank = Rank(nameRank: 0, GpuSensorRank.DiscretePriority, sequence: 2);

        Assert.False(rank.IsBetterThan(rank));
    }

    private static GpuSensorRank Rank(int nameRank, int hardwarePriority, int sequence) =>
        new(nameRank, hardwarePriority, sequence);
}
