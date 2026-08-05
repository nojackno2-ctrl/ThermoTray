using Xunit;

namespace ThermoTray.Tests;

public sealed class TrayDisplaySettingsTests
{
    [Fact]
    public void NewSettingsShowEveryMetricByDefault()
    {
        var settings = new AppSettings();

        Assert.True(settings.ShowCpuUsageInTray);
        Assert.True(settings.ShowCpuTemperatureInTray);
        Assert.Empty(settings.GpuTraySettings);
    }

    [Fact]
    public void GpuViewModelUsesAndUpdatesItsPersistedChoices()
    {
        var settings = new TrayDisplaySettings
        {
            ShowUsage = false,
            ShowTemperature = true,
        };
        var saveCount = 0;
        var gpu = new GpuViewModel("gpu-0");

        gpu.ConfigureTraySettings(settings, () => saveCount++);

        Assert.False(gpu.ShowUsageInTray);
        Assert.True(gpu.ShowTemperatureInTray);

        gpu.ShowTemperatureInTray = false;

        Assert.False(settings.ShowTemperature);
        Assert.Equal(1, saveCount);
    }

    [Fact]
    public void GpuSourceShowsOnlyTheSensorNameWhenDeviceNameIsAlreadyDisplayed()
    {
        var gpu = new GpuViewModel("gpu-0");

        gpu.Apply(
            new GpuReading(
                "gpu-0",
                "AMD Radeon(TM) Graphics",
                new TemperatureReading(47, "AMD Radeon(TM) Graphics \u2022 GPU Core"),
                new UtilizationReading(3, "AMD Radeon(TM) Graphics \u2022 GPU Core")),
            0,
            "GPU usage",
            "GPU temperature",
            reading => $"{reading.Celsius:0.#} °C",
            reading => $"{reading.Percent:0.#}%");

        Assert.Equal("GPU Core", gpu.Source);
    }

    /// <summary>
    /// 雙 GPU 筆電的內顯常態沒有溫度感測器；只要獨顯仍給出溫度，狀態列就不該警告。
    /// </summary>
    [Fact]
    public void NoTemperatureWarningWhileAnotherGpuStillReportsOne()
    {
        var readings = new[]
        {
            Gpu("gpu-0", temperature: null, usage: 4),
            Gpu("gpu-1", temperature: 44, usage: 0),
        };

        Assert.Empty(MainViewModel.ListGpusMissingReading(readings, gpu => gpu.Temperature.IsAvailable, "、"));
    }

    /// <summary>
    /// 所有 GPU 都讀不到溫度時才警告，並指出是哪幾張。
    /// </summary>
    [Fact]
    public void EveryGpuMissingTheReadingIsListedByItsCardNumber()
    {
        var readings = new[]
        {
            Gpu("gpu-0", temperature: null, usage: 4),
            Gpu("gpu-1", temperature: null, usage: 0),
        };

        Assert.Equal("GPU 0、GPU 1", MainViewModel.ListGpusMissingReading(readings, gpu => gpu.Temperature.IsAvailable, "、"));
    }

    /// <summary>
    /// 單張 GPU 缺少讀值時仍會回報，序號與卡片標題一致。
    /// </summary>
    [Fact]
    public void ASingleGpuMissingTheReadingIsReported()
    {
        var readings = new[] { Gpu("gpu-0", temperature: null, usage: 4) };

        Assert.Equal("GPU 0", MainViewModel.ListGpusMissingReading(readings, gpu => gpu.Temperature.IsAvailable, "、"));
    }

    /// <summary>
    /// 沒有任何 GPU 時不該組出警告文字。
    /// </summary>
    [Fact]
    public void NoWarningWithoutAnyGpu() =>
        Assert.Empty(MainViewModel.ListGpusMissingReading([], gpu => gpu.Temperature.IsAvailable, "、"));

    /// <summary>
    /// 使用率採用同一條規則：內顯沒有使用率時，獨顯的可信讀值就足以取消警告。
    /// </summary>
    [Fact]
    public void TheSameRuleAppliesToUtilization()
    {
        var readings = new[]
        {
            Gpu("gpu-0", temperature: 47, usage: null),
            Gpu("gpu-1", temperature: 44, usage: 0),
        };

        Assert.Empty(MainViewModel.ListGpusMissingReading(readings, gpu => gpu.Usage.IsAvailable, "、"));
    }

    private static GpuReading Gpu(string id, decimal? temperature, decimal? usage) => new(
        id,
        id,
        temperature is decimal celsius ? new TemperatureReading(celsius, $"{id} • GPU Core") : TemperatureReading.Unavailable,
        usage is decimal percent ? new UtilizationReading(percent, $"{id} • GPU Core") : UtilizationReading.Unavailable);
}
