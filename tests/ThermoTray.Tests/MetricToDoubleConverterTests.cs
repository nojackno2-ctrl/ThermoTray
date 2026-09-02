using System.Globalization;
using Xunit;

namespace ThermoTray.Tests;

/// <summary>
/// <see cref="MetricToDoubleConverter"/> 數值轉換單元測試。
/// 確保量表能正確解析使用率與溫度文字，並在無法取得讀值時回傳 0.0。
/// </summary>
public sealed class MetricToDoubleConverterTests
{
    private readonly MetricToDoubleConverter _converter = new();

    [Theory]
    [InlineData("8.5%", 8.5)]
    [InlineData("100%", 100.0)]
    [InlineData("0%", 0.0)]
    [InlineData("54.6 °C", 54.6)]
    [InlineData("42 °C", 42.0)]
    [InlineData("105 °C", 100.0)] // Clamped to 100
    [InlineData("12,5%", 12.5)] // Comma support
    public void Convert_ParsesValidTelemetryStrings(string input, double expected)
    {
        var result = _converter.Convert(input, typeof(double), null, CultureInfo.InvariantCulture);
        Assert.Equal(expected, (double)result, 1);
    }

    [Theory]
    [InlineData("無法取得")]
    [InlineData("Unavailable")]
    [InlineData("…")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Convert_ReturnsZero_WhenUnavailableOrInvalid(string? input)
    {
        var result = _converter.Convert(input, typeof(double), null, CultureInfo.InvariantCulture);
        Assert.Equal(0.0, (double)result);
    }

    [Fact]
    public void ConvertBack_ThrowsNotSupportedException()
    {
        Assert.Throws<NotSupportedException>(() =>
            _converter.ConvertBack(50.0, typeof(string), null, CultureInfo.InvariantCulture));
    }
}
