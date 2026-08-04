using System.Globalization;
using Xunit;

namespace ThermoTray.Tests;

public sealed class UtilizationFormatterTests
{
    [Fact]
    public void ToTrayDigits_ReturnsPlaceholder_WhenReadingIsUnavailable() =>
        Assert.Equal("--", UtilizationFormatter.ToTrayDigits(UtilizationReading.Unavailable));

    [Theory]
    [InlineData(0.0, "0")]
    [InlineData(42.5, "42")]
    [InlineData(99.9, "99")]
    [InlineData(100.0, "100")]
    public void ToTrayDigits_TruncatesToWholePercentagePoints(double percent, string expected) =>
        Assert.Equal(expected, UtilizationFormatter.ToTrayDigits(Reading(percent)));

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
