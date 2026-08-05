using System.Globalization;

namespace ThermoTray;

/// <summary>
/// 將硬體使用率讀值轉換為在系統工作列圖示中繪製的簡短純數字字串。
/// </summary>
internal static class UtilizationFormatter
{
    /// <summary>
    /// 當尚無可靠使用率讀值時顯示的占位字串。
    /// </summary>
    internal const string TrayPlaceholder = "--";

    /// <summary>
    /// 從使用率讀值中擷取整數百分比數字。獨立於目前語系設定。
    /// </summary>
    /// <param name="reading">使用率讀值。</param>
    /// <returns>代表整數百分比的字串（如 "85"），若無讀值則傳回占位符 "--"。</returns>
    internal static string ToTrayDigits(UtilizationReading reading) => reading.Percent is decimal percent
        ? decimal.Truncate(percent).ToString(CultureInfo.InvariantCulture)
        : TrayPlaceholder;
}
