using System.Globalization;

namespace ThermoTray;

/// <summary>Turns utilization readings into short display text and tray-icon digits.</summary>
internal static class UtilizationFormatter
{
    /// <summary>Shown instead of digits while no trustworthy utilization value exists.</summary>
    internal const string TrayPlaceholder = "--";

    /// <summary>
    /// Derives whole-percentage digits from the reading itself, independent of the current culture.
    /// </summary>
    internal static string ToTrayDigits(UtilizationReading reading) => reading.Percent is decimal percent
        ? decimal.Truncate(percent).ToString(CultureInfo.InvariantCulture)
        : TrayPlaceholder;
}
