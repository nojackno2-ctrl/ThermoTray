using Xunit;

namespace ThermoTray.Tests;

public sealed class TrayIconLayoutTests
{
    /// <summary>Notification-area icon sizes for the display scales Windows offers.</summary>
    public static TheoryData<int> IconSizes => new(16, 20, 24, 32, 40, 48);

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

    [Theory]
    [MemberData(nameof(IconSizes))]
    public void GetLines_SeparatesTheTwoLines(int iconSize)
    {
        var canvasSize = TrayIconLayout.GetCanvasSize(iconSize);
        var (usage, temperature) = TrayIconLayout.GetLines(canvasSize);

        Assert.True(temperature.Top >= usage.Bottom, "the temperature line overlaps the utilization line");
    }

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
    /// The digits are only as tall as the line they are scaled into, so most of the icon has to reach
    /// them. A layout that spent its height on padding is what made the readings hard to make out.
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

    [Fact]
    public void GetCanvasSize_DrawsLargerThanTheIconSoStrokesCanBeAveragedDown() =>
        Assert.True(TrayIconLayout.GetCanvasSize(TrayIconLayout.MinimumIconSize) > TrayIconLayout.MinimumIconSize);
}
