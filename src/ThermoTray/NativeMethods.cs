using System.Runtime.InteropServices;

namespace ThermoTray;

/// <summary>
/// 封裝原生 Win32 API 呼叫的內部靜態類別。
/// </summary>
internal static class NativeMethods
{
    /// <summary>
    /// 將本處理程序的焦點切換權限移交給指定的處理程序。
    /// 若未呼叫此 API，執行中的處理程序呼叫 <c>Activate()</c> 時僅會在工作列閃爍，
    /// 因為 Windows 會阻擋非使用者當前互動處理程序的焦點切換請求。
    /// </summary>
    /// <param name="dwProcessId">目標處理程序 ID。</param>
    /// <returns>若成功授予權限傳回 true，否則傳回 false。</returns>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AllowSetForegroundWindow(int dwProcessId);

    /// <summary>
    /// 將指定視窗帶入前景並將其啟動。
    /// </summary>
    /// <param name="hWnd">目標視窗的控制項代碼 (Handle)。</param>
    /// <returns>若成功將視窗切換至前景傳回 true，否則傳回 false。</returns>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(IntPtr hWnd);
}
