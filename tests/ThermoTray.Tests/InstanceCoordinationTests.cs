using System.IO;
using System.Threading;
using Xunit;

namespace ThermoTray.Tests;

/// <summary>
/// <see cref="InstanceProtocol.Decide"/> 版本比較與處置決策單元測試。
/// </summary>
public sealed class InstanceDecisionTests
{
    /// <summary>
    /// 驗證相同版本時採無聲 Handover (ShowRunning)。
    /// </summary>
    [Fact]
    public void Decide_HandsOverToTheSameVersion() =>
        Assert.Equal(
            InstanceAction.ShowRunning,
            InstanceProtocol.Decide(new Version(1, 1, 3, 0), new Version(1, 1, 3, 0)));

    /// <summary>
    /// 驗證新版啟動且已存在舊版時，建議替換舊版 (ReplaceRunning)。
    /// </summary>
    [Fact]
    public void Decide_OffersToReplaceAnOlderVersion() =>
        Assert.Equal(
            InstanceAction.ReplaceRunning,
            InstanceProtocol.Decide(new Version(1, 1, 2, 0), new Version(1, 1, 3, 0)));

    /// <summary>
    /// 驗證舊版啟動且已存在新版時，建議保留新版並顯示新版視窗 (ShowNewerRunning)。
    /// </summary>
    [Fact]
    public void Decide_KeepsTheNewerRunningVersion() =>
        Assert.Equal(
            InstanceAction.ShowNewerRunning,
            InstanceProtocol.Decide(new Version(1, 1, 3, 0), new Version(1, 1, 2, 0)));

    /// <summary>
    /// 驗證比較版本時忽略第四位的 Revision。
    /// </summary>
    [Fact]
    public void Decide_IgnoresTheFourthComponent() =>
        Assert.Equal(
            InstanceAction.ShowRunning,
            InstanceProtocol.Decide(new Version(1, 1, 3), new Version(1, 1, 3, 0)));

    /// <summary>
    /// 驗證版號為數值比較（如 1.1.10 新於 1.1.9）。
    /// </summary>
    [Fact]
    public void Decide_ComparesNumerically() =>
        Assert.Equal(
            InstanceAction.ReplaceRunning,
            InstanceProtocol.Decide(new Version(1, 1, 9, 0), new Version(1, 1, 10, 0)));

    /// <summary>
    /// 驗證無法解析或其中一方版本為 null 時傳回安全的 ShowRunning。
    /// </summary>
    [Fact]
    public void Decide_HandsOverWhenAVersionIsUnknown()
    {
        Assert.Equal(InstanceAction.ShowRunning, InstanceProtocol.Decide(null, new Version(1, 1, 3, 0)));
        Assert.Equal(InstanceAction.ShowRunning, InstanceProtocol.Decide(new Version(1, 1, 3, 0), null));
    }
}

/// <summary>
/// <see cref="InstanceProtocol"/> 身份識別字串格式化與解析單元測試。
/// </summary>
public sealed class InstanceIdentityTests
{
    /// <summary>
    /// 驗證身份字串序列化與反序列化一致。
    /// </summary>
    [Fact]
    public void Identity_SurvivesTheRoundTrip()
    {
        var line = InstanceProtocol.FormatIdentity(new Version(1, 1, 3, 0), 4242);

        Assert.True(InstanceProtocol.TryParseIdentity(line, out var version, out var processId));
        Assert.Equal(new Version(1, 1, 3, 0), version);
        Assert.Equal(4242, processId);
    }

    /// <summary>
    /// 驗證版本號為 null 時仍可產出與解析包含 UnknownVersion 的身份字串。
    /// </summary>
    [Fact]
    public void Identity_CarriesAnUnknownVersion()
    {
        Assert.True(InstanceProtocol.TryParseIdentity(InstanceProtocol.FormatIdentity(null, 7), out var version, out var processId));
        Assert.Null(version);
        Assert.Equal(7, processId);
    }

    /// <summary>
    /// 驗證非規範格式字串被拒絕。
    /// </summary>
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

/// <summary>
/// 具名管道實體通訊 (<see cref="InstanceServer"/> / <see cref="InstanceClient"/>) 單元測試。
/// </summary>
public sealed class InstanceHandoverTests
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CallbackTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 驗證客戶端能成功連接管道伺服器、讀取執行中版本並發送 SHOW 請求。
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

    /// <summary>
    /// 驗證發送 EXIT 請求時，伺服器發送 OK 確認後觸發退出委派。
    /// </summary>
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
    /// 驗證管道伺服器在單次通訊結束後能持續監聽回應後續請求。
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
    /// 驗證連接不存在的管道時傳回 null。
    /// </summary>
    [Fact]
    public void AnUnreachableInstanceReportsNoClient() =>
        Assert.Null(InstanceClient.TryConnect(UniquePipeName(), TimeSpan.FromMilliseconds(500)));

    /// <summary>
    /// 驗證處置 (Dispose) 伺服器後，新連線無法連通。
    /// </summary>
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

    private static string UniquePipeName() => "ThermoTray.Tests." + Guid.NewGuid().ToString("N");

    private sealed class TestServer(string pipeName, InstanceServer server) : IDisposable
    {
        internal string PipeName { get; } = pipeName;

        public void Dispose() => server.Dispose();
    }
}

/// <summary>
/// 驗證 Inno Setup 安裝腳本與專案組態間全域互斥鎖名稱與版本號一致性的單元測試。
/// </summary>
public sealed class SetupMutexContractTests
{
    /// <summary>
    /// 驗證 `installer/ThermoTray.iss` 中的 AppMutex 數值與程式碼中的 `InstanceCoordinator.SetupMutexName` 完全吻合。
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

    /// <summary>
    /// 驗證 `installer/ThermoTray.iss` 中的預設 AppVersion 與 `Directory.Build.props` 中的 Version 完全一致，防止版本無聲分歧。
    /// </summary>
    [Fact]
    public void TheInstallerDefaultAppVersionMatchesDirectoryBuildProps()
    {
        var repoRoot = FindRepositoryRoot();
        var propsPath = Path.Combine(repoRoot, "Directory.Build.props");
        var scriptPath = Path.Combine(repoRoot, "installer", "ThermoTray.iss");

        Assert.True(File.Exists(propsPath), $"Directory.Build.props not found at {propsPath}");
        Assert.True(File.Exists(scriptPath), $"Installer script not found at {scriptPath}");

        var propsXml = System.Xml.Linq.XDocument.Load(propsPath);
        var expectedVersion = propsXml.Root?.Element("PropertyGroup")?.Element("Version")?.Value?.Trim();
        Assert.False(string.IsNullOrWhiteSpace(expectedVersion), "Version not defined in Directory.Build.props");

        var scriptContent = File.ReadAllText(scriptPath);
        var match = System.Text.RegularExpressions.Regex.Match(scriptContent, @"#define\s+AppVersion\s+""([^""]+)""");
        Assert.True(match.Success, "Could not find '#define AppVersion' in installer/ThermoTray.iss");

        Assert.Equal(expectedVersion, match.Groups[1].Value);
    }

    /// <summary>
    /// 驗證 `installer/ThermoTray.iss` 註解範例中的版本參數與 `Directory.Build.props` 一致。
    /// </summary>
    [Fact]
    public void TheInstallerCommentExamplesMatchDirectoryBuildProps()
    {
        var repoRoot = FindRepositoryRoot();
        var propsPath = Path.Combine(repoRoot, "Directory.Build.props");
        var scriptPath = Path.Combine(repoRoot, "installer", "ThermoTray.iss");

        var propsXml = System.Xml.Linq.XDocument.Load(propsPath);
        var expectedVersion = propsXml.Root?.Element("PropertyGroup")?.Element("Version")?.Value?.Trim();

        var scriptContent = File.ReadAllText(scriptPath);
        Assert.Contains($"/DAppVersion={expectedVersion}", scriptContent, StringComparison.Ordinal);
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
