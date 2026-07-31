using System.Security.Principal;

namespace ThermoTray;

/// <summary>
/// Reports whether the current process received the administrator rights required by PawnIO.
/// </summary>
public static class ElevationService
{
    public static bool IsElevated { get; } = DetectElevation();

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
