using System.Text.Json;
using System.IO;

namespace ThermoTray;

/// <summary>
/// 負責應用程式設定 (JSON 檔) 之載入與安全儲存的服務類別。
/// </summary>
public sealed class SettingsService
{
    /// <summary>
    /// 設定檔存放路徑 (`%LOCALAPPDATA%\ThermoTray\settings.json`)。
    /// </summary>
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ThermoTray",
        "settings.json");

    /// <summary>
    /// 載入應用程式設定。若檔案不存在或毀損，會自動退回使用預設設定值。
    /// </summary>
    /// <returns>載入的 <see cref="AppSettings"/> 實例。</returns>
    public AppSettings Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings()
                : new AppSettings();
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            // 無法讀取或毀損的設定檔不應阻礙溫度檢測運作，退回預設值
            return new AppSettings();
        }
    }

    /// <summary>
    /// 將應用程式設定寫入檔案。使用暫存檔 (.tmp) 進行原子寫入，避免中斷造成檔案損毀。
    /// </summary>
    /// <param name="settings">要儲存的設定實例。</param>
    /// <exception cref="IOException">當檔案被鎖定時擲出。</exception>
    /// <exception cref="UnauthorizedAccessException">當存取被拒絕時擲出。</exception>
    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temporaryPath = FilePath + ".tmp";

        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings));
            File.Move(temporaryPath, FilePath, true);
        }
        catch
        {
            Delete(temporaryPath);
            throw;
        }
    }

    /// <summary>
    /// 安全刪除檔案，忽略可能產生的權限或 IO 例外。
    /// </summary>
    /// <param name="path">要刪除的檔案路徑。</param>
    private static void Delete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 殘留的暫存檔不會影響系統，下次儲存時會自動覆蓋
        }
    }
}

/// <summary>
/// 應用程式設定資料模型。
/// </summary>
public sealed class AppSettings
{
    /// <summary>
    /// 取得或設定是否隨 Windows 開機自動啟動。
    /// </summary>
    public bool StartWithWindows { get; set; }

    /// <summary>
    /// 取得或設定關閉主視窗時是否縮小至系統工作列圖示而非直接結束程式。預設為 true。
    /// </summary>
    public bool HideWhenClosed { get; set; } = true;

    /// <summary>
    /// 取得或設定 UI 語言設定（如 "zh-TW" 或 "en"）。預設為繁體中文。
    /// </summary>
    public string Language { get; set; } = Localizer.DefaultLanguage;

    public bool ShowCpuUsageInTray { get; set; } = true;

    public bool ShowCpuTemperatureInTray { get; set; } = true;

    public Dictionary<string, TrayDisplaySettings> GpuTraySettings { get; set; } = new();
}

public sealed class TrayDisplaySettings
{
    public bool ShowUsage { get; set; } = true;

    public bool ShowTemperature { get; set; } = true;
}
