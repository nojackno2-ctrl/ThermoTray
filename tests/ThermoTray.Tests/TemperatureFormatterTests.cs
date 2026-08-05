using System.Globalization;
using Xunit;

namespace ThermoTray.Tests;

/// <summary>
/// <see cref="TemperatureFormatter"/> 溫度位元字串轉換單元測試。
/// </summary>
public sealed class TemperatureFormatterTests
{
    /// <summary>
    /// 驗證讀值無法取得時傳回占位符 "--"。
    /// </summary>
    [Fact]
    public void ToTrayDigits_ReturnsPlaceholder_WhenReadingIsUnavailable() =>
        Assert.Equal("--", TemperatureFormatter.ToTrayDigits(TemperatureReading.Unavailable));

    /// <summary>
    /// 驗證溫度小數數值會被截斷為整數位元數字。
    /// </summary>
    [Theory]
    [InlineData(45.0, "45")]
    [InlineData(45.6, "45")]
    [InlineData(7.4, "7")]
    [InlineData(99.9, "99")]
    [InlineData(100.4, "100")]
    public void ToTrayDigits_TruncatesToWholeDegrees(double celsius, string expected) =>
        Assert.Equal(expected, TemperatureFormatter.ToTrayDigits(Reading(celsius)));

    /// <summary>
    /// 驗證數字轉字串過程獨立於當前語系設定，不受小數點符號 (comma vs dot) 影響。
    /// </summary>
    [Theory]
    [InlineData("de-DE")]
    [InlineData("fr-FR")]
    [InlineData("zh-TW")]
    public void ToTrayDigits_IgnoresTheCurrentCultureDecimalSeparator(string culture)
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(culture);
            Assert.Equal("45", TemperatureFormatter.ToTrayDigits(Reading(45.5)));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    private static TemperatureReading Reading(double celsius) => new((decimal)celsius, "test");
}
