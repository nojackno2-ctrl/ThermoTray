using System.Globalization;
using Xunit;

namespace ThermoTray.Tests;

public sealed class TemperatureFormatterTests
{
    [Fact]
    public void ToTrayDigits_ReturnsPlaceholder_WhenReadingIsUnavailable() =>
        Assert.Equal("--", TemperatureFormatter.ToTrayDigits(TemperatureReading.Unavailable));

    [Theory]
    [InlineData(45.0, "45")]
    [InlineData(45.6, "45")]
    [InlineData(7.4, "7")]
    [InlineData(99.9, "99")]
    [InlineData(100.4, "100")]
    public void ToTrayDigits_TruncatesToWholeDegrees(double celsius, string expected) =>
        Assert.Equal(expected, TemperatureFormatter.ToTrayDigits(Reading(celsius)));

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
