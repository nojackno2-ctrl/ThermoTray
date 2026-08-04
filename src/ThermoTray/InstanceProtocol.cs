using System.Globalization;

namespace ThermoTray;

/// <summary>What a starting instance should do when another instance already owns the tray.</summary>
internal enum InstanceAction
{
    /// <summary>Bring the running instance forward and exit without saying anything.</summary>
    ShowRunning,

    /// <summary>Same, but tell the user that what is running is newer than what they just launched.</summary>
    ShowNewerRunning,

    /// <summary>Offer to shut the older instance down and take over the tray.</summary>
    ReplaceRunning,
}

/// <summary>
/// The line protocol a later launch speaks to the instance that already owns the tray, plus the
/// version comparison that decides which of them survives. Both halves are pure so the decision
/// table can be tested without starting processes.
/// </summary>
internal static class InstanceProtocol
{
    internal const string IdentifyRequest = "WHO";
    internal const string ShowRequest = "SHOW";
    internal const string ExitRequest = "EXIT";
    internal const string Acknowledgement = "OK";

    /// <summary>Guards against answering some unrelated program that happened to open the pipe.</summary>
    private const string IdentityPrefix = "THERMOTRAY";

    /// <summary>Stands in for an assembly that carries no version at all.</summary>
    private const string UnknownVersion = "-";

    internal static string FormatIdentity(Version? version, int processId) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{IdentityPrefix} {version?.ToString() ?? UnknownVersion} {processId}");

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

        // An unparsable version is reported as unknown rather than rejecting an otherwise valid peer.
        version = Version.TryParse(parts[1], out var parsed) ? parsed : null;
        return true;
    }

    /// <summary>
    /// Only the first three components name a release; the fourth is always zero, so two builds that
    /// differ only there are the same product and the launch is an ordinary hand-over.
    /// </summary>
    internal static InstanceAction Decide(Version? running, Version? starting)
    {
        if (running is null || starting is null)
        {
            // Without both versions there is nothing to compare, and handing over is the safe outcome.
            return InstanceAction.ShowRunning;
        }

        return Normalize(starting).CompareTo(Normalize(running)) switch
        {
            > 0 => InstanceAction.ReplaceRunning,
            < 0 => InstanceAction.ShowNewerRunning,
            _ => InstanceAction.ShowRunning,
        };
    }

    private static Version Normalize(Version version) =>
        new(version.Major, version.Minor, Math.Max(version.Build, 0));
}
