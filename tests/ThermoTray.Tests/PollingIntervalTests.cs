using Xunit;

namespace ThermoTray.Tests;

public sealed class PollingIntervalTests
{
    [Fact]
    public void GetPollingInterval_SamplesEverySecond_WhileTheWindowIsVisible() =>
        Assert.Equal(TimeSpan.FromSeconds(1), MainViewModel.GetPollingInterval(isWindowVisible: true));

    [Fact]
    public void GetPollingInterval_SamplesLessOften_WhileHiddenInTheTray() =>
        Assert.True(MainViewModel.GetPollingInterval(isWindowVisible: false)
            > MainViewModel.GetPollingInterval(isWindowVisible: true));

    /// <summary>A tray icon shows whole degrees, so its refresh must still be well inside a second-scale change.</summary>
    [Fact]
    public void GetPollingInterval_StaysResponsive_WhileHiddenInTheTray() =>
        Assert.True(MainViewModel.GetPollingInterval(isWindowVisible: false) <= TimeSpan.FromSeconds(5));
}
