using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text;
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

    /// <summary>
    /// Written into the task's Source field and checked at every launch. Bump it whenever
    /// <see cref="BuildTaskDefinition"/> changes, so an installation still carrying an older
    /// definition is registered again instead of keeping settings this version no longer uses.
    /// </summary>
    internal const string DefinitionMarker = "ThermoTray startup task (definition 2)";

    /// <summary>
    /// True when the logon task exists and carries this version's definition. A task written by an
    /// earlier version counts as absent, so startup is registered again: those tasks were created from
    /// <c>schtasks</c> switches alone and therefore kept its defaults, which Task Scheduler enforces by
    /// terminating ThermoTray. See <see cref="BuildTaskDefinition"/>.
    /// </summary>
    public bool IsUpToDate() =>
        RunSchtasks($"/Query /TN {TaskName} /XML", out var definition)
        && definition.Contains(DefinitionMarker, StringComparison.Ordinal);

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
        // A random name rather than a fixed one, because the file is read back by an elevated
        // Task Scheduler and must not be a path another process can predict and replace first.
        var definitionPath = Path.Combine(Path.GetTempPath(), $"ThermoTray-{Path.GetRandomFileName()}.xml");

        try
        {
            // schtasks reads the definition as UTF-16 and rejects the file outright otherwise.
            File.WriteAllText(definitionPath, BuildTaskDefinition(executablePath, GetCurrentUserId()), Encoding.Unicode);
            return RunSchtasks($"/Create /TN {TaskName} /XML \"{definitionPath}\" /F", out _);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or SecurityException)
        {
            return false;
        }
        finally
        {
            TryDeleteDefinition(definitionPath);
        }
    }

    /// <summary>
    /// The full definition ThermoTray registers, rather than the <c>schtasks /Create</c> switches that
    /// wrote earlier versions. The settings that matter here have no switch, and the defaults schtasks
    /// leaves in their place are wrong for an application meant to sit in the notification area
    /// indefinitely: Task Scheduler stops the task after three days of uptime, refuses to start it while
    /// a laptop is on battery, and hard-terminates it the moment the machine switches to battery power.
    /// A tray icon that disappears when the charger is unplugged is indistinguishable from a crash, and
    /// because the process is killed rather than faulted it leaves nothing in the event log to explain it.
    /// </summary>
    internal static string BuildTaskDefinition(string executablePath, string userId) =>
        $"""
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo>
            <Source>{Escape(DefinitionMarker)}</Source>
            <Description>Starts ThermoTray at logon, elevated and minimised to the notification area.</Description>
          </RegistrationInfo>
          <Triggers>
            <LogonTrigger>
              <Enabled>true</Enabled>
              <UserId>{Escape(userId)}</UserId>
            </LogonTrigger>
          </Triggers>
          <Principals>
            <Principal id="Author">
              <UserId>{Escape(userId)}</UserId>
              <LogonType>InteractiveToken</LogonType>
              <RunLevel>HighestAvailable</RunLevel>
            </Principal>
          </Principals>
          <Settings>
            <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
            <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
            <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
            <AllowHardTerminate>false</AllowHardTerminate>
            <StartWhenAvailable>false</StartWhenAvailable>
            <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
            <IdleSettings>
              <StopOnIdleEnd>false</StopOnIdleEnd>
              <RestartOnIdle>false</RestartOnIdle>
            </IdleSettings>
            <AllowStartOnDemand>true</AllowStartOnDemand>
            <Enabled>true</Enabled>
            <Hidden>false</Hidden>
            <RunOnlyIfIdle>false</RunOnlyIfIdle>
            <WakeToRun>false</WakeToRun>
            <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
            <Priority>7</Priority>
          </Settings>
          <Actions Context="Author">
            <Exec>
              <Command>{Escape(executablePath)}</Command>
              <Arguments>--minimized</Arguments>
            </Exec>
          </Actions>
        </Task>
        """;

    /// <summary>
    /// The account's SID rather than its name, so the definition survives a renamed account and does
    /// not depend on the domain form this machine happens to report.
    /// </summary>
    private static string GetCurrentUserId()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            if (identity.User is SecurityIdentifier user)
            {
                return user.Value;
            }
        }
        catch (SecurityException)
        {
            // Fall through to the account name, which Task Scheduler also accepts.
        }

        return $"{Environment.UserDomainName}\\{Environment.UserName}";
    }

    private static string Escape(string value) => SecurityElement.Escape(value) ?? string.Empty;

    private static void TryDeleteDefinition(string definitionPath)
    {
        try
        {
            File.Delete(definitionPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The task is already registered; a leftover temporary file is not worth failing over.
        }
    }

    private static void RemoveScheduledTask() => RunSchtasks($"/Delete /TN {TaskName} /F", out _);

    private static bool RunSchtasks(string arguments, out string standardOutput)
    {
        standardOutput = string.Empty;

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
            var outputRead = process.StandardOutput.ReadToEndAsync();
            var errorRead = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(TimeoutMilliseconds))
            {
                Terminate(process);
                return false;
            }

            // The parameterless overload also waits for the redirected streams to finish.
            process.WaitForExit();

            // Observed so a failed read cannot resurface later as an unobserved task exception.
            _ = errorRead.Exception;
            if (outputRead.Exception is null)
            {
                standardOutput = outputRead.Result;
            }

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
