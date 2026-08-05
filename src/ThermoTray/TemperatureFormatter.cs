using System.Globalization;

namespace ThermoTray;

/// <summary>
/// 將溫度讀值轉換為在系統工作列圖示中繪製的簡短純數字字串。
/// </summary>
internal static class TemperatureFormatter
{
    /// <summary>
    /// 當尚無可靠溫度讀值時顯示的占位字串。
    /// </summary>
    internal const string TrayPlaceholder = "--";

    /// <summary>
    /// 從溫度讀值中擷取整數度數。
    /// 不直接解析格式化後的 UI 文字，以避免不同語系（如以逗號作為小數點 separator 的語系）造成解析錯誤。
    /// </summary>
    /// <param name="reading">溫度讀值。</param>
    /// <returns>代表整數度數的字串（如 "45"），若無讀值則傳回占位符 "--"。</returns>
    internal static string ToTrayDigits(TemperatureReading reading) => reading.Celsius is decimal celsius
        ? decimal.Truncate(celsius).ToString(CultureInfo.InvariantCulture)
        : TrayPlaceholder;
}
