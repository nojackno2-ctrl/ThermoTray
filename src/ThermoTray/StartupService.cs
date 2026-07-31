using System.Diagnostics;
using Microsoft.Win32;

namespace ThermoTray;

/// <summary>
/// Registers automatic startup as a highest-run-level logon task. A plain <c>HKCU\Run</c>
/// entry cannot launch an application whose manifest requires administrator rights.
/// </summary>
public sealed class StartupService
{
    private const string RunKeyPath = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
    private const string ValueName = "ThermoTray";
    private const string TaskName = "ThermoTray";
    private const int TimeoutMilliseconds = 10_000;

    public bool IsEnabled() => RunSchtasks($"/Query /TN {TaskName}");

    public void SetEnabled(bool enabled)
    {
        if (!enabled)
        {
            RemoveScheduledTask();
            RemoveRunValue();
            return;
        }

        var executablePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("The executable path is unavailable.");

        if (!ElevationService.IsElevated || !TryCreateElevatedTask(executablePath))
        {
            throw new UnauthorizedAccessException("Could not create the elevated startup task.");
        }

        RemoveRunValue();
    }

    private static bool TryCreateElevatedTask(string executablePath)
    {
        var arguments = string.Join(' ',
            "/Create",
            "/TN", TaskName,
            "/TR", $"\"\\\"{executablePath}\\\" --minimized\"",
            "/SC", "ONLOGON",
            "/RU", $"\"{Environment.UserDomainName}\\{Environment.UserName}\"",
            "/RL", "HIGHEST",
            "/F");
        return RunSchtasks(arguments);
    }

    private static void RemoveScheduledTask() => RunSchtasks($"/Delete /TN {TaskName} /F");

    private static bool RunSchtasks(string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("schtasks.exe", arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });

            if (process is null)
            {
                return false;
            }

            // Both pipes must be drained concurrently. Leaving either one unread lets schtasks block
            // forever on a full pipe buffer instead of exiting.
            var standardOutput = process.StandardOutput.ReadToEndAsync();
            var standardError = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(TimeoutMilliseconds))
            {
                Terminate(process);
                return false;
            }

            // The parameterless overload also waits for the redirected streams to finish.
            process.WaitForExit();

            // Observed so a failed read cannot resurface later as an unobserved task exception.
            _ = standardOutput.Exception;
            _ = standardError.Exception;
            return process.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Stops a schtasks call that outlived its timeout so it cannot linger as an orphan.</summary>
    private static void Terminate(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            // The process already exited or cannot be stopped; either way the call has failed.
        }
    }

    private static void RemoveRunValue()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
