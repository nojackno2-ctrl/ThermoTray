namespace ThermoTray;

public sealed class Localizer
{
    public const string DefaultLanguage = "zh-TW";
    public const string EnglishLanguage = "en-US";

    private static readonly IReadOnlyDictionary<string, (string TraditionalChinese, string English)> Strings =
        new Dictionary<string, (string, string)>
        {
            ["Subtitle"] = ("即時硬體感測，不使用推測數值", "Live hardware sensors. No estimated values."),
            ["CpuTemperature"] = ("CPU 溫度", "CPU temperature"),
            ["GpuTemperature"] = ("GPU 溫度", "GPU temperature"),
            ["Unavailable"] = ("無法取得", "Unavailable"),
            ["WaitingForSensors"] = ("正在讀取硬體感測器…", "Reading hardware sensors…"),
            ["NoSensor"] = ("找不到可用的溫度感測器。請確認硬體/驅動程式支援。", "No usable temperature sensor was found. Check hardware and driver support."),
            ["CpuSensorUnavailable"] = ("CPU 溫度感測器未提供可信的即時讀值。", "The CPU temperature sensor did not provide a trustworthy live reading."),
            ["DriverMissing"] = (
                "CPU 溫度需要 PawnIO 核心驅動程式。LibreHardwareMonitor 0.9.6 以 PawnIO 讀取 CPU 暫存器，未安裝時 AMD Ryzen 的 Tctl/Tdie 會固定為 0，因此顯示為無法取得。安裝 PawnIO 後重新啟動 ThermoTray 即可。",
                "CPU temperature needs the PawnIO kernel driver. LibreHardwareMonitor 0.9.6 reads CPU registers through PawnIO; without it the AMD Ryzen Tctl/Tdie sensor stays at 0, so it is reported as unavailable. Install PawnIO and restart ThermoTray."),
            ["DriverDownload"] = ("下載並安裝 PawnIO", "Download and install PawnIO"),
            ["GpuSensorUnavailable"] = ("GPU 溫度感測器未提供可信的即時讀值。", "The GPU temperature sensor did not provide a trustworthy live reading."),
            ["StartWithWindows"] = ("隨 Windows 啟動", "Start with Windows"),
            ["HideWhenClosed"] = ("關閉時縮小至系統匣", "Hide to tray when closed"),
            ["Language"] = ("語言", "Language"),
            ["Open"] = ("開啟 ThermoTray", "Open ThermoTray"),
            ["Exit"] = ("結束", "Exit"),
            ["StartupError"] = ("無法更新開機啟動設定。", "Could not update the startup setting."),
            ["SensorError"] = ("讀取硬體感測器時發生錯誤。", "An error occurred while reading hardware sensors."),
            ["OpenLinkError"] = ("無法開啟瀏覽器，請手動前往下列網址：", "Could not open a browser. Visit this address manually:"),
        };

    public Localizer(string language) => Language = Normalize(language);

    public string Language { get; private set; }

    public string this[string key] => Strings.TryGetValue(key, out var text)
        ? Language == EnglishLanguage ? text.English : text.TraditionalChinese
        : key;

    public void SetLanguage(string language) => Language = Normalize(language);

    /// <summary>Maps anything unrecognised, including a hand-edited settings file, onto a supported language.</summary>
    private static string Normalize(string? language) =>
        string.Equals(language, EnglishLanguage, StringComparison.OrdinalIgnoreCase) ? EnglishLanguage : DefaultLanguage;
}
