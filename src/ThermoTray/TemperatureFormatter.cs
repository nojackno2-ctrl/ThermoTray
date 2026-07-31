using System.Globalization;

namespace ThermoTray;

/// <summary>Turns readings into the short text drawn inside a tray icon.</summary>
internal static class TemperatureFormatter
{
    /// <summary>Shown instead of digits while no trustworthy value exists.</summary>
    internal const string TrayPlaceholder = "--";

    /// <summary>
    /// Derives the whole-degree digits from the reading itself. Parsing them back out of the
    /// displayed text would break wherever the current culture writes a comma decimal separator.
    /// </summary>
    internal static string ToTrayDigits(TemperatureReading reading) => reading.Celsius is decimal celsius
        ? decimal.Truncate(celsius).ToString(CultureInfo.InvariantCulture)
        : TrayPlaceholder;
}
