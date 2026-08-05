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
}
