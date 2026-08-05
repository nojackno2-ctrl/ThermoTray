using System.Globalization;
using Xunit;

namespace ThermoTray.Tests;

/// <summary>
/// <see cref="UtilizationFormatter"/> 使用率位元字串轉換單元測試。
/// </summary>
public sealed class UtilizationFormatterTests
{
    /// <summary>
    /// 驗證使用率讀值無法取得時傳回占位符 "--"。
    /// </summary>
    [Fact]
    public void ToTrayDigits_ReturnsPlaceholder_WhenReadingIsUnavailable() =>
        Assert.Equal("--", UtilizationFormatter.ToTrayDigits(UtilizationReading.Unavailable));

    /// <summary>
    /// 驗證使用率數值被截斷為整數百分比。
    /// </summary>
    [Theory]
    [InlineData(0.0, "0")]
    [InlineData(42.5, "42")]
    [InlineData(99.9, "99")]
    [InlineData(100.0, "100")]
    public void ToTrayDigits_TruncatesToWholePercentagePoints(double percent, string expected) =>
        Assert.Equal(expected, UtilizationFormatter.ToTrayDigits(Reading(percent)));

    /// <summary>
    /// 驗證轉字串獨立於當前語系設定。
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
            Assert.Equal("42", UtilizationFormatter.ToTrayDigits(Reading(42.5)));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    private static UtilizationReading Reading(double percent) => new((decimal)percent, "test");
}
