namespace ThermoTray;

/// <summary>
/// 提供多語系 UI 字串本地化服務（支援繁體中文與英文）。
/// </summary>
public sealed class Localizer
{
    /// <summary>
    /// 預設語言代碼（繁體中文 "zh-TW"）。
    /// </summary>
    public const string DefaultLanguage = "zh-TW";

    /// <summary>
    /// 英文語言代碼 ("en-US")。
    /// </summary>
    public const string EnglishLanguage = "en-US";

    /// <summary>
    /// 雙語字串對照字典。索引鍵為字串 Key，值為 (繁體中文, 英文) 的二元組。
    /// </summary>
    private static readonly IReadOnlyDictionary<string, (string TraditionalChinese, string English)> Strings =
        new Dictionary<string, (string, string)>
        {
            ["Subtitle"] = ("即時硬體感測，不使用推測數值", "Live hardware sensors. No estimated values."),
            ["CpuTemperature"] = ("CPU 溫度", "CPU temperature"),
            ["GpuTemperature"] = ("GPU 溫度", "GPU temperature"),
            ["CpuUsage"] = ("CPU 使用率", "CPU usage"),
            ["GpuUsage"] = ("GPU 使用率", "GPU usage"),
            ["Unavailable"] = ("無法取得", "Unavailable"),
            ["WaitingForSensors"] = ("正在讀取硬體感測器…", "Reading hardware sensors…"),
            ["NoSensor"] = ("找不到可用的溫度感測器。請確認硬體/驅動程式支援。", "No usable temperature sensor was found. Check hardware and driver support."),
            ["CpuSensorUnavailable"] = ("CPU 溫度感測器未提供可信的即時讀值。", "The CPU temperature sensor did not provide a trustworthy live reading."),
            ["DriverMissing"] = (
                "CPU 溫度需要 PawnIO 核心驅動程式。LibreHardwareMonitor 0.9.6 以 PawnIO 讀取 CPU 暫存器，未安裝時 AMD Ryzen 的 Tctl/Tdie 會固定為 0，因此顯示為無法取得。安裝 PawnIO 後重新啟動 ThermoTray 即可。",
                "CPU temperature needs the PawnIO kernel driver. LibreHardwareMonitor 0.9.6 reads CPU registers through PawnIO; without it the AMD Ryzen Tctl/Tdie sensor stays at 0, so it is reported as unavailable. Install PawnIO and restart ThermoTray."),
            ["DriverDownload"] = ("下載並安裝 PawnIO", "Download and install PawnIO"),
            ["GpuSensorUnavailable"] = ("GPU 溫度感測器未提供可信的即時讀值。", "The GPU temperature sensor did not provide a trustworthy live reading."),
            ["CpuUsageUnavailable"] = ("CPU 使用率感測器未提供可信的即時讀值。", "The CPU utilization sensor did not provide a trustworthy live reading."),
            ["GpuUsageUnavailable"] = ("GPU 使用率感測器未提供可信的即時讀值。", "The GPU utilization sensor did not provide a trustworthy live reading."),
            ["StartWithWindows"] = ("隨 Windows 啟動", "Start with Windows"),
            ["HideWhenClosed"] = ("關閉時縮小至系統匣", "Hide to tray when closed"),
            ["Language"] = ("語言", "Language"),
            ["Open"] = ("開啟 ThermoTray", "Open ThermoTray"),
            ["Exit"] = ("結束", "Exit"),
            ["StartupError"] = ("無法更新開機啟動設定。", "Could not update the startup setting."),
            ["SensorError"] = ("讀取硬體感測器時發生錯誤。", "An error occurred while reading hardware sensors."),
            ["OpenLinkError"] = ("無法開啟瀏覽器，請手動前往下列網址：", "Could not open a browser. Visit this address manually:"),
            ["AlreadyRunning"] = (
                "ThermoTray 已在執行中。請從系統匣圖示開啟主視窗。",
                "ThermoTray is already running. Open its window from the notification area."),
            ["LegacyInstanceRunning"] = (
                "ThermoTray 已在執行中，但沒有回應接手要求（通常表示它是 1.1.3 之前的版本），因此改為顯示它的視窗。若要改用 {1}，請先從系統匣圖示結束執行中的版本。",
                "ThermoTray is already running but did not answer the hand-over request, which usually means it predates 1.1.3, so its window was shown instead. To use {1}, exit the running version from the notification area first."),
            ["NewerInstanceRunning"] = (
                "已在執行較新的 ThermoTray {0}，將顯示該版本的視窗。您啟動的是 {1}。",
                "A newer ThermoTray {0} is already running and its window will be shown. You started {1}."),
            ["ReplaceRunningInstance"] = (
                "偵測到 ThermoTray {0} 正在執行，您啟動的是 {1}。要關閉執行中的版本並改用這一個嗎？",
                "ThermoTray {0} is already running and you started {1}. Close the running version and use this one instead?"),
            ["ReplaceFailed"] = (
                "無法關閉執行中的 ThermoTray {0}。請從系統匣圖示結束它，然後再啟動這個版本。",
                "Could not close the running ThermoTray {0}. Exit it from the notification area, then start this version again."),
        };

    /// <summary>
    /// 初始化 Localizer 的新實例。
    /// </summary>
    /// <param name="language">語系代碼（如 "zh-TW" 或 "en-US"）。</param>
    public Localizer(string language) => Language = Normalize(language);

    /// <summary>
    /// 取得當前使用的語言代碼。
    /// </summary>
    public string Language { get; private set; }

    /// <summary>
    /// 依據指定的 Key 取得本地化後的文字。
    /// </summary>
    /// <param name="key">字串 Key。</param>
    /// <returns>若找到 Key 則傳回對應語言的文字，否則傳回 Key 原字串。</returns>
    public string this[string key] => Strings.TryGetValue(key, out var text)
        ? Language == EnglishLanguage ? text.English : text.TraditionalChinese
        : key;

    /// <summary>
    /// 切換當前的語言。
    /// </summary>
    /// <param name="language">目標語言代碼。</param>
    public void SetLanguage(string language) => Language = Normalize(language);

    /// <summary>
    /// 將未辨識或格式不符合的語言代碼標準化為系統支援的語言代碼（預設為繁體中文）。
    /// </summary>
    /// <param name="language">輸入的語言字串。</param>
    /// <returns>標準化後的語言代碼。</returns>
    private static string Normalize(string? language) =>
        string.Equals(language, EnglishLanguage, StringComparison.OrdinalIgnoreCase) ? EnglishLanguage : DefaultLanguage;
}
