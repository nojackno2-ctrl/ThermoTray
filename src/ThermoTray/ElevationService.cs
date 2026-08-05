using System.Security.Principal;

namespace ThermoTray;

/// <summary>
/// 提供檢查當前處理程序是否具備管理員權限（Elevated）的服務。
/// PawnIO 驅動程式需要管理員權限才能允許讀取 CPU 暫存器溫度。
/// </summary>
public static class ElevationService
{
    /// <summary>
    /// 取得一個值，表示當前處理程序是否以最高權限（系統管理員）執行。
    /// </summary>
    public static bool IsElevated { get; } = DetectElevation();

    /// <summary>
    /// 檢測當前 Windows 帳戶識別碼是否屬於系統管理員角色。
    /// </summary>
    /// <returns>若為系統管理員傳回 true，否則傳回 false。</returns>
    private static bool DetectElevation()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
