using Xunit;

namespace ThermoTray.Tests;

/// <summary>
/// 採樣週期與頻率設定單元測試。
/// </summary>
public sealed class PollingIntervalTests
{
    /// <summary>
    /// 驗證當主視窗為可見狀態時，採樣週期為 1 秒。
    /// </summary>
    [Fact]
    public void GetPollingInterval_SamplesEverySecond_WhileTheWindowIsVisible() =>
        Assert.Equal(TimeSpan.FromSeconds(1), MainViewModel.GetPollingInterval(isWindowVisible: true));

    /// <summary>
    /// 驗證當主視窗隱藏至系統匣時，採樣頻率會降低以節省資源。
    /// </summary>
    [Fact]
    public void GetPollingInterval_SamplesLessOften_WhileHiddenInTheTray() =>
        Assert.True(MainViewModel.GetPollingInterval(isWindowVisible: false)
            > MainViewModel.GetPollingInterval(isWindowVisible: true));

    /// <summary>
    /// 驗證隱藏狀態下的採樣間隔仍保持在合理回應範圍內 (<= 5 秒)。
    /// </summary>
    [Fact]
    public void GetPollingInterval_StaysResponsive_WhileHiddenInTheTray() =>
        Assert.True(MainViewModel.GetPollingInterval(isWindowVisible: false) <= TimeSpan.FromSeconds(5));
}
