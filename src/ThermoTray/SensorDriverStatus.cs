using LibreHardwareMonitor.PawnIo;

namespace ThermoTray;

/// <summary>
/// Reports whether the kernel driver LibreHardwareMonitor 0.9.6 needs for direct CPU register
/// access is present. That release replaced WinRing0 with PawnIO, so on AMD Ryzen the
/// Tctl/Tdie sensor exists but stays at 0 until PawnIO is installed.
/// </summary>
public sealed record SensorDriverStatus(bool IsInstalled, string Version)
{
    public const string DownloadUrl = "https://pawnio.eu/";

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
            // Any probe failure means the driver is not usable; never guess that it is present.
            return new SensorDriverStatus(false, string.Empty);
        }
    }
}
