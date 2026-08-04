using System.Runtime.InteropServices;

namespace ThermoTray;

internal static class NativeMethods
{
    /// <summary>
    /// Hands this process's right to take the foreground to another one. Without it the running
    /// instance's own <c>Activate()</c> only flashes its taskbar button, because Windows refuses a
    /// foreground change requested by a process the user did not just interact with.
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AllowSetForegroundWindow(int dwProcessId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(IntPtr hWnd);
}
