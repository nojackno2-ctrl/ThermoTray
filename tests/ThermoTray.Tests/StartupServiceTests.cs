using System.Reflection;
using System.Xml.Linq;
using Xunit;

namespace ThermoTray.Tests;

/// <summary>
/// Guards the logon-task definition. Every assertion here stands for a way Task Scheduler used to
/// terminate ThermoTray while it sat in the notification area, which looked like a random crash and
/// left nothing in the event log because the process was killed rather than faulted.
/// </summary>
public sealed class StartupServiceTests
{
    private const string ExecutablePath = @"C:\Users\test\AppData\Local\Programs\ThermoTray\ThermoTray.exe";
    private const string UserId = "S-1-5-21-1-2-3-1001";
    private static readonly XNamespace TaskNamespace = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    [Fact]
    public void BuildTaskDefinition_IsWellFormedXml() =>
        Assert.Equal("Task", Parse().Name.LocalName);

    /// <summary>schtasks reads the definition as UTF-16, so the declaration has to say so.</summary>
    [Fact]
    public void BuildTaskDefinition_DeclaresTheEncodingSchtasksExpects() =>
        Assert.StartsWith("""<?xml version="1.0" encoding="UTF-16"?>""", Definition(), StringComparison.Ordinal);

    /// <summary>A laptop that switches to battery must not lose its tray icons.</summary>
    [Theory]
    [InlineData("DisallowStartIfOnBatteries")]
    [InlineData("StopIfGoingOnBatteries")]
    [InlineData("AllowHardTerminate")]
    [InlineData("RunOnlyIfIdle")]
    public void BuildTaskDefinition_TurnsOffEverySettingThatCanStopTheTask(string setting) =>
        Assert.Equal("false", Setting(setting));

    /// <summary>Zero means no limit; the schtasks default stopped the task after three days of uptime.</summary>
    [Fact]
    public void BuildTaskDefinition_PlacesNoTimeLimitOnHowLongThermoTrayRuns() =>
        Assert.Equal("PT0S", Setting("ExecutionTimeLimit"));

    /// <summary>Idle settings only apply to an idle-only task, but they are set so no upgrade path can re-enable them.</summary>
    [Fact]
    public void BuildTaskDefinition_DoesNotStopTheTaskWhenTheMachineStopsBeingIdle() =>
        Assert.Equal("false", Parse().Descendants(TaskNamespace + "StopOnIdleEnd").Single().Value);

    [Fact]
    public void BuildTaskDefinition_RunsElevatedAsTheCurrentUserAtLogon()
    {
        var principal = Parse().Descendants(TaskNamespace + "Principal").Single();

        Assert.Equal("HighestAvailable", principal.Element(TaskNamespace + "RunLevel")?.Value);
        Assert.Equal("InteractiveToken", principal.Element(TaskNamespace + "LogonType")?.Value);
        Assert.Equal(UserId, principal.Element(TaskNamespace + "UserId")?.Value);
        Assert.Equal(UserId, Parse().Descendants(TaskNamespace + "LogonTrigger").Single().Element(TaskNamespace + "UserId")?.Value);
    }

    [Fact]
    public void BuildTaskDefinition_StartsTheExecutableMinimised()
    {
        var exec = Parse().Descendants(TaskNamespace + "Exec").Single();

        Assert.Equal(ExecutablePath, exec.Element(TaskNamespace + "Command")?.Value);
        Assert.Equal("--minimized", exec.Element(TaskNamespace + "Arguments")?.Value);
    }

    /// <summary>The marker is how an installation carrying an older definition is recognised and rewritten.</summary>
    [Fact]
    public void BuildTaskDefinition_CarriesTheDefinitionMarkerLaunchLooksFor() =>
        Assert.Equal(StartupService.DefinitionMarker, Parse().Descendants(TaskNamespace + "Source").Single().Value);

    /// <summary>An installation directory may legitimately contain XML metacharacters.</summary>
    [Fact]
    public void BuildTaskDefinition_EscapesThePathInsteadOfBreakingTheDocument()
    {
        const string awkwardPath = @"C:\Tools & <Apps>\ThermoTray""1"".exe";
        var definition = StartupService.BuildTaskDefinition(awkwardPath, UserId);

        Assert.Equal(awkwardPath, XDocument.Parse(definition).Descendants(TaskNamespace + "Command").Single().Value);
    }

    /// <summary>
    /// Hands the definition to Task Scheduler's own parser, which is the only thing that decides whether
    /// <c>schtasks /Create /XML</c> will accept it: well-formed XML is not enough, because the service
    /// also validates element order and values against its schema. Setting the text validates it and
    /// nothing else, so no task is registered by this test.
    /// </summary>
    [Fact]
    public void BuildTaskDefinition_IsAcceptedByTaskScheduler()
    {
        var serviceType = Type.GetTypeFromProgID("Schedule.Service");
        Assert.NotNull(serviceType);

        var service = Activator.CreateInstance(serviceType)!;
        Invoke(service, "Connect", BindingFlags.InvokeMethod, Type.Missing, Type.Missing, Type.Missing, Type.Missing);
        var definition = Invoke(service, "NewTask", BindingFlags.InvokeMethod, 0)!;

        Invoke(definition, "XmlText", BindingFlags.SetProperty, Definition());
    }

    private static object? Invoke(object target, string member, BindingFlags invokeAttribute, params object?[] arguments) =>
        target.GetType().InvokeMember(member, invokeAttribute, binder: null, target, arguments);

    private static string Definition() => StartupService.BuildTaskDefinition(ExecutablePath, UserId);

    private static XElement Parse() => XDocument.Parse(Definition()).Root!;

    private static string Setting(string name) => Parse()
        .Element(TaskNamespace + "Settings")!
        .Element(TaskNamespace + name)!
        .Value;
}
