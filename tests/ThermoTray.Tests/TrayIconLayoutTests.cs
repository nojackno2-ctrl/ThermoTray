using Xunit;

namespace ThermoTray.Tests;

/// <summary>
/// <see cref="TrayIconLayout"/> 系統匣圖示幾何佈局計算單元測試。
/// </summary>
public sealed class TrayIconLayoutTests
{
    /// <summary>
    /// 各種 DPI 縮放比例下常見的系統匣圖示尺寸。
    /// </summary>
    public static TheoryData<int> IconSizes => new(16, 20, 24, 32, 40, 48);

    /// <summary>
    /// 驗證上下兩行區域完全在畫布範圍內。
    /// </summary>
    [Theory]
    [MemberData(nameof(IconSizes))]
    public void GetLines_KeepsBothLinesInsideTheCanvas(int iconSize)
    {
        var canvasSize = TrayIconLayout.GetCanvasSize(iconSize);
        var (usage, temperature) = TrayIconLayout.GetLines(canvasSize);

        foreach (var line in new[] { usage, temperature })
        {
            Assert.True(line.Left >= 0f, $"{line} starts left of the canvas");
            Assert.True(line.Top >= 0f, $"{line} starts above the canvas");
            Assert.True(line.Right <= canvasSize, $"{line} runs past the right edge of {canvasSize}");
            Assert.True(line.Bottom <= canvasSize, $"{line} runs past the bottom edge of {canvasSize}");
        }
    }

    /// <summary>
    /// 驗證兩行區域完全區隔不重疊。
    /// </summary>
    [Theory]
    [MemberData(nameof(IconSizes))]
    public void GetLines_SeparatesTheTwoLines(int iconSize)
    {
        var canvasSize = TrayIconLayout.GetCanvasSize(iconSize);
        var (usage, temperature) = TrayIconLayout.GetLines(canvasSize);

        Assert.True(temperature.Top >= usage.Bottom, "the temperature line overlaps the utilization line");
    }

    /// <summary>
    /// 驗證兩行區域具有相同的形狀與寬高。
    /// </summary>
    [Theory]
    [MemberData(nameof(IconSizes))]
    public void GetLines_GivesTheTwoLinesTheSameShape(int iconSize)
    {
        var canvasSize = TrayIconLayout.GetCanvasSize(iconSize);
        var (usage, temperature) = TrayIconLayout.GetLines(canvasSize);

        Assert.Equal(usage.Width, temperature.Width);
        Assert.Equal(usage.Height, temperature.Height);
        Assert.Equal(usage.Left, temperature.Left);
    }

    /// <summary>
    /// 驗證絕大部分畫布高度均分配給文字繪製（使用率超過 90%）。
    /// </summary>
    [Theory]
    [MemberData(nameof(IconSizes))]
    public void GetLines_SpendsNearlyAllTheHeightOnDigits(int iconSize)
    {
        var canvasSize = TrayIconLayout.GetCanvasSize(iconSize);
        var (usage, temperature) = TrayIconLayout.GetLines(canvasSize);

        Assert.True((usage.Height + temperature.Height) / canvasSize >= 0.9f);
        Assert.True(usage.Width / canvasSize >= 0.9f);
    }

    /// <summary>
    /// 驗證畫布放大倍率大於 1（Supersampling 超高採樣）。
    /// </summary>
    [Fact]
    public void GetCanvasSize_DrawsLargerThanTheIconSoStrokesCanBeAveragedDown() =>
        Assert.True(TrayIconLayout.GetCanvasSize(TrayIconLayout.MinimumIconSize) > TrayIconLayout.MinimumIconSize);
}
