using LibreHardwareMonitor.PawnIo;

namespace ThermoTray;

/// <summary>
/// 回報 LibreHardwareMonitor 0.9.6 存取 CPU 暫存器所需的核心驅動程式 (PawnIO) 是否已安裝。
/// 由於 LibreHardwareMonitor 以 PawnIO 取代 WinRing0，在 AMD Ryzen 等處理器上，若未安裝 PawnIO，
/// 雖然能找到 Tctl/Tdie 感測器，但讀值會恆為 0。
/// </summary>
/// <param name="IsInstalled">PawnIO 驅動程式是否已安裝。</param>
/// <param name="Version">PawnIO 驅動程式的版本字串。</param>
public sealed record SensorDriverStatus(bool IsInstalled, string Version)
{
    /// <summary>
    /// PawnIO 驅動程式的官方下載網址。
    /// </summary>
    public const string DownloadUrl = "https://pawnio.eu/";

    /// <summary>
    /// 查詢系統中 PawnIO 驅動程式的安裝狀態與版本。
    /// </summary>
    /// <returns>包含安裝狀態與版本號的 <see cref="SensorDriverStatus"/> 實例。</returns>
    public static SensorDriverStatus Query()
    {
        try
        {
            return PawnIo.IsInstalled
                ? new SensorDriverStatus(true, PawnIo.Version?.ToString() ?? string.Empty)
                : new SensorDriverStatus(false, string.Empty);
        }
        catch (Exception)
        {
            // 若探測過程發生任何例外，一律視為驅動程式不可用，切勿盲目假設其已安裝。
            return new SensorDriverStatus(false, string.Empty);
        }
    }
}
