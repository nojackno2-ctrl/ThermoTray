using System.IO;
using System.Threading;
using Xunit;

namespace ThermoTray.Tests;

public sealed class InstanceDecisionTests
{
    /// <summary>The ordinary second launch: same build, so it just raises the window that exists.</summary>
    [Fact]
    public void Decide_HandsOverToTheSameVersion() =>
        Assert.Equal(
            InstanceAction.ShowRunning,
            InstanceProtocol.Decide(new Version(1, 1, 3, 0), new Version(1, 1, 3, 0)));

    /// <summary>Launching an upgrade has to be able to take the tray from the version it replaces.</summary>
    [Fact]
    public void Decide_OffersToReplaceAnOlderVersion() =>
        Assert.Equal(
            InstanceAction.ReplaceRunning,
            InstanceProtocol.Decide(new Version(1, 1, 2, 0), new Version(1, 1, 3, 0)));

    /// <summary>A stale shortcut must not silently downgrade what the user is running.</summary>
    [Fact]
    public void Decide_KeepsTheNewerRunningVersion() =>
        Assert.Equal(
            InstanceAction.ShowNewerRunning,
            InstanceProtocol.Decide(new Version(1, 1, 3, 0), new Version(1, 1, 2, 0)));

    /// <summary>Only three components name a release, so the build's fourth one is not a difference.</summary>
    [Fact]
    public void Decide_IgnoresTheFourthComponent() =>
        Assert.Equal(
            InstanceAction.ShowRunning,
            InstanceProtocol.Decide(new Version(1, 1, 3), new Version(1, 1, 3, 0)));

    /// <summary>A numeric comparison, not a textual one: 1.1.10 is newer than 1.1.9, not older.</summary>
    [Fact]
    public void Decide_ComparesNumerically() =>
        Assert.Equal(
            InstanceAction.ReplaceRunning,
            InstanceProtocol.Decide(new Version(1, 1, 9, 0), new Version(1, 1, 10, 0)));

    /// <summary>With nothing to compare, taking the tray from a running instance is not justified.</summary>
    [Fact]
    public void Decide_HandsOverWhenAVersionIsUnknown()
    {
        Assert.Equal(InstanceAction.ShowRunning, InstanceProtocol.Decide(null, new Version(1, 1, 3, 0)));
        Assert.Equal(InstanceAction.ShowRunning, InstanceProtocol.Decide(new Version(1, 1, 3, 0), null));
    }
}

public sealed class InstanceIdentityTests
{
    [Fact]
    public void Identity_SurvivesTheRoundTrip()
    {
        var line = InstanceProtocol.FormatIdentity(new Version(1, 1, 3, 0), 4242);

        Assert.True(InstanceProtocol.TryParseIdentity(line, out var version, out var processId));
        Assert.Equal(new Version(1, 1, 3, 0), version);
        Assert.Equal(4242, processId);
    }

    /// <summary>An assembly with no version still has to identify itself as ThermoTray.</summary>
    [Fact]
    public void Identity_CarriesAnUnknownVersion()
    {
        Assert.True(InstanceProtocol.TryParseIdentity(InstanceProtocol.FormatIdentity(null, 7), out var version, out var processId));
        Assert.Null(version);
        Assert.Equal(7, processId);
    }

    /// <summary>Anything else that opens the pipe must not be mistaken for a running instance.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("OK")]
    [InlineData("SOMETHINGELSE 1.1.3 42")]
    [InlineData("THERMOTRAY 1.1.3")]
    [InlineData("THERMOTRAY 1.1.3 notaprocess")]
    [InlineData("THERMOTRAY 1.1.3 0")]
    [InlineData("THERMOTRAY 1.1.3 -1")]
    public void Identity_RejectsAnythingElse(string? line) =>
        Assert.False(InstanceProtocol.TryParseIdentity(line, out _, out _));
}

public sealed class InstanceHandoverTests
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CallbackTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The whole exchange over a real named pipe: a launch learns who is running and asks for that
    /// instance's window. Everything the startup path decides depends on this answer being real.
    /// </summary>
    [Fact]
    public void AConnectingLaunchLearnsTheRunningVersionAndRaisesItsWindow()
    {
        using var shown = new ManualResetEventSlim();
        using var server = StartServer(new Version(1, 2, 3, 0), shown.Set, exited: () => { });

        using (var client = InstanceClient.TryConnect(server.PipeName, ConnectTimeout))
        {
            Assert.NotNull(client);
            Assert.Equal(new Version(1, 2, 3, 0), client.RunningVersion);
            Assert.Equal(Environment.ProcessId, client.RunningProcessId);
            Assert.True(client.RequestShow());
        }

        Assert.True(shown.Wait(CallbackTimeout));
    }

    /// <summary>The upgrade path: the older instance is asked to leave and acknowledges before it does.</summary>
    [Fact]
    public void AReplacingLaunchIsAcknowledgedBeforeTheRunningInstanceExits()
    {
        using var exited = new ManualResetEventSlim();
        using var server = StartServer(new Version(1, 1, 2, 0), shown: () => { }, exited.Set);

        using (var client = InstanceClient.TryConnect(server.PipeName, ConnectTimeout))
        {
            Assert.NotNull(client);
            Assert.True(client.RequestExit());
        }

        Assert.True(exited.Wait(CallbackTimeout));
    }

    /// <summary>
    /// Each exchange closes its connection, so the listener has to come back for the next launch.
    /// A one-shot server would leave every later launch unable to reach the running instance.
    /// </summary>
    [Fact]
    public void TheRunningInstanceKeepsAnsweringLaterLaunches()
    {
        var shownCount = 0;
        using var shownTwice = new ManualResetEventSlim();
        using var server = StartServer(
            new Version(1, 1, 3, 0),
            () =>
            {
                if (Interlocked.Increment(ref shownCount) == 2)
                {
                    shownTwice.Set();
                }
            },
            exited: () => { });

        for (var launch = 0; launch < 2; launch++)
        {
            using var client = InstanceClient.TryConnect(server.PipeName, ConnectTimeout);
            Assert.NotNull(client);
            Assert.True(client.RequestShow());
        }

        Assert.True(shownTwice.Wait(CallbackTimeout));
    }

    /// <summary>
    /// Nothing is listening under this name, which is also what a build older than the pipe looks
    /// like. The caller has to be told so it can fall back instead of waiting forever.
    /// </summary>
    [Fact]
    public void AnUnreachableInstanceReportsNoClient() =>
        Assert.Null(InstanceClient.TryConnect(UniquePipeName(), TimeSpan.FromMilliseconds(500)));

    /// <summary>Shutdown has to release the name, or the replacing instance would never be reachable.</summary>
    [Fact]
    public void ADisposedServerStopsAnsweringConnections()
    {
        var server = StartServer(new Version(1, 1, 3, 0), shown: () => { }, exited: () => { });
        server.Dispose();

        Assert.Null(InstanceClient.TryConnect(server.PipeName, TimeSpan.FromMilliseconds(500)));
    }

    private static TestServer StartServer(Version version, Action shown, Action exited)
    {
        var pipeName = UniquePipeName();
        var server = new InstanceServer(pipeName, version, shown, exited);
        server.Start();
        return new TestServer(pipeName, server);
    }

    /// <summary>A pipe name is machine wide, so tests must not collide with each other or a real instance.</summary>
    private static string UniquePipeName() => "ThermoTray.Tests." + Guid.NewGuid().ToString("N");

    private sealed class TestServer(string pipeName, InstanceServer server) : IDisposable
    {
        internal string PipeName { get; } = pipeName;

        public void Dispose() => server.Dispose();
    }
}

public sealed class SetupMutexContractTests
{
    /// <summary>
    /// The installer can only see a running ThermoTray through this exact name. A rename on either
    /// side would silently bring back overwriting a locked executable.
    /// </summary>
    [Fact]
    public void TheInstallerWaitsOnTheMutexTheApplicationCreates()
    {
        var script = Path.Combine(FindRepositoryRoot(), "installer", "ThermoTray.iss");

        Assert.True(File.Exists(script), $"Installer script not found at {script}");
        Assert.Contains(
            "AppMutex=" + InstanceCoordinator.SetupMutexName,
            File.ReadAllText(script),
            StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ThermoTray.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory.FullName;
    }
}
