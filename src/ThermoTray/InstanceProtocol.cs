using System.Globalization;

namespace ThermoTray;

/// <summary>
/// 當另一個執行個體已佔用系統匣時，新啟動的執行個體應採取的處置動作。
/// </summary>
internal enum InstanceAction
{
    /// <summary>
    /// 直接顯示已在執行的視窗，並無聲結束當前新啟動的執行個體。
    /// </summary>
    ShowRunning,

    /// <summary>
    /// 顯示已在執行的視窗，並提示使用者目前執行中的版本較新。
    /// </summary>
    ShowNewerRunning,

    /// <summary>
    /// 提示使用者是否要關閉舊版執行個體，並由當前新啟動的執行個體接手系統匣。
    /// </summary>
    ReplaceRunning,
}

/// <summary>
/// 新啟動執行個體與已執行個體之間的具名管道溝通協定及版本比較邏輯。
/// 包含純邏輯轉換，方便測試而無需實際啟動背景處理程序。
/// </summary>
internal static class InstanceProtocol
{
    /// <summary>
    /// 查詢身份與版本的請求命令 ("WHO")。
    /// </summary>
    internal const string IdentifyRequest = "WHO";

    /// <summary>
    /// 請求顯示主視窗的命令 ("SHOW")。
    /// </summary>
    internal const string ShowRequest = "SHOW";

    /// <summary>
    /// 請求退出並釋放系統匣的命令 ("EXIT")。
    /// </summary>
    internal const string ExitRequest = "EXIT";

    /// <summary>
    /// 回應確認文字 ("OK")。
    /// </summary>
    internal const string Acknowledgement = "OK";

    /// <summary>
    /// 身份識別的前綴字串，防止誤讀非本應用的管道回應。
    /// </summary>
    private const string IdentityPrefix = "THERMOTRAY";

    /// <summary>
    /// 當元件無版本資訊時的占位字串。
    /// </summary>
    private const string UnknownVersion = "-";

    /// <summary>
    /// 格式化身份識別回應字串（如 "THERMOTRAY 1.1.3.0 1234"）。
    /// </summary>
    /// <param name="version">版本資訊。</param>
    /// <param name="processId">處理程序 ID。</param>
    /// <returns>格式化後的身份字串。</returns>
    internal static string FormatIdentity(Version? version, int processId) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{IdentityPrefix} {version?.ToString() ?? UnknownVersion} {processId}");

    /// <summary>
    /// 解析身份識別回應字串。
    /// </summary>
    /// <param name="line">接收到的管道字串。</param>
    /// <param name="version">解析出的版本號。</param>
    /// <param name="processId">解析出的處理程序 ID。</param>
    /// <returns>若成功解析傳回 true，否則傳回 false。</returns>
    internal static bool TryParseIdentity(string? line, out Version? version, out int processId)
    {
        version = null;
        processId = 0;

        if (line is null)
        {
            return false;
        }

        var parts = line.Split(' ');
        if (parts.Length != 3 || !string.Equals(parts[0], IdentityPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        if (!int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out processId) || processId <= 0)
        {
            processId = 0;
            return false;
        }

        // 若版本格式無法完整解析，仍保留處理程序 ID，版本視為 null
        version = Version.TryParse(parts[1], out var parsed) ? parsed : null;
        return true;
    }

    /// <summary>
    /// 比對已執行版本與新啟動版本，決定處置動作（只需比較 Major.Minor.Build 即可）。
    /// </summary>
    /// <param name="running">已在執行的版本。</param>
    /// <param name="starting">新啟動的版本。</param>
    /// <returns>建議執行的 <see cref="InstanceAction"/>。</returns>
    internal static InstanceAction Decide(Version? running, Version? starting)
    {
        if (running is null || starting is null)
        {
            // 無法確定版本時，安全性考量下直接顯示已執行的視窗
            return InstanceAction.ShowRunning;
        }

        return Normalize(starting).CompareTo(Normalize(running)) switch
        {
            > 0 => InstanceAction.ReplaceRunning,
            < 0 => InstanceAction.ShowNewerRunning,
            _ => InstanceAction.ShowRunning,
        };
    }

    /// <summary>
    /// 標準化版本號（將 Revision 忽略，主要比對主版號、次版號與組建編號）。
    /// </summary>
    /// <param name="version">原始版本物件。</param>
    /// <returns>標準化後的版本物件。</returns>
    private static Version Normalize(Version version) =>
        new(version.Major, version.Minor, Math.Max(version.Build, 0));
}
