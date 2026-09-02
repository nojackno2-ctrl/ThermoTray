using System.Globalization;
using System.Windows.Data;

namespace ThermoTray;

/// <summary>
/// 將格式化後的溫度或使用率字串（如 "54.6 °C"、"8.5%"）解析為浮點數數值 (0~100)，供進度量表 (ProgressBar / Gauge) 綁定。
/// 若讀值為 "無法取得"、"…" 或無效格式，則安全回傳 0.0。
/// </summary>
public sealed class MetricToDoubleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string text || string.IsNullOrWhiteSpace(text))
        {
            return 0.0;
        }

        var span = text.AsSpan().Trim();
        var index = 0;
        while (index < span.Length && (char.IsDigit(span[index]) || span[index] == '.' || span[index] == ','))
        {
            index++;
        }

        if (index > 0 && double.TryParse(span[..index].ToString().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var result))
        {
            return Math.Clamp(result, 0.0, 100.0);
        }

        return 0.0;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
