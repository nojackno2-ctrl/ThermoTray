using System.Reflection;
using System.Xml.Linq;
using Xunit;

namespace ThermoTray.Tests;

/// <summary>
/// <see cref="StartupService"/> 開機啟動工作排程器 (Task Scheduler) XML 定義檔單元測試。
/// 確保排程設定防範拔除電源或 uptime 超過 72 小時被 Task Scheduler 強制結束處理程序的情況。
/// </summary>
public sealed class StartupServiceTests
{
    private const string ExecutablePath = @"C:\Users\test\AppData\Local\Programs\ThermoTray\ThermoTray.exe";
    private const string UserId = "S-1-5-21-1-2-3-1001";
    private static readonly XNamespace TaskNamespace = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    /// <summary>
    /// 驗證生成的字串為格式合法的 XML。
    /// </summary>
    [Fact]
    public void BuildTaskDefinition_IsWellFormedXml() =>
        Assert.Equal("Task", Parse().Name.LocalName);

    /// <summary>
    /// 驗證 XML 開頭宣告包含 `encoding="UTF-16"`（schtasks 的必要規範）。
    /// </summary>
    [Fact]
    public void BuildTaskDefinition_DeclaresTheEncodingSchtasksExpects() =>
        Assert.StartsWith("""<?xml version="1.0" encoding="UTF-16"?>""", Definition(), StringComparison.Ordinal);

    /// <summary>
    /// 驗證關閉所有可能中斷排程執行的設定 (DisallowStartIfOnBatteries, StopIfGoingOnBatteries, AllowHardTerminate, RunOnlyIfIdle)。
    /// </summary>
    [Theory]
    [InlineData("DisallowStartIfOnBatteries")]
    [InlineData("StopIfGoingOnBatteries")]
    [InlineData("AllowHardTerminate")]
    [InlineData("RunOnlyIfIdle")]
    public void BuildTaskDefinition_TurnsOffEverySettingThatCanStopTheTask(string setting) =>
        Assert.Equal("false", Setting(setting));

    /// <summary>
    /// 驗證 ExecutionTimeLimit 設定為 "PT0S"（代表無時間限制），避免預設 72 小時後終止。
    /// </summary>
    [Fact]
    public void BuildTaskDefinition_PlacesNoTimeLimitOnHowLongThermoTrayRuns() =>
        Assert.Equal("PT0S", Setting("ExecutionTimeLimit"));

    /// <summary>
    /// 驗證 StopOnIdleEnd 設定為 false。
    /// </summary>
    [Fact]
    public void BuildTaskDefinition_DoesNotStopTheTaskWhenTheMachineStopsBeingIdle() =>
        Assert.Equal("false", Parse().Descendants(TaskNamespace + "StopOnIdleEnd").Single().Value);

    /// <summary>
    /// 驗證 Principal 設定為當前使用者互動登入時以 HighestAvailable 權限執行。
    /// </summary>
    [Fact]
    public void BuildTaskDefinition_RunsElevatedAsTheCurrentUserAtLogon()
    {
        var principal = Parse().Descendants(TaskNamespace + "Principal").Single();

        Assert.Equal("HighestAvailable", principal.Element(TaskNamespace + "RunLevel")?.Value);
        Assert.Equal("InteractiveToken", principal.Element(TaskNamespace + "LogonType")?.Value);
        Assert.Equal(UserId, principal.Element(TaskNamespace + "UserId")?.Value);
        Assert.Equal(UserId, Parse().Descendants(TaskNamespace + "LogonTrigger").Single().Element(TaskNamespace + "UserId")?.Value);
    }

    /// <summary>
    /// 驗證 Exec Action 帶有 `--minimized` 參數。
    /// </summary>
    [Fact]
    public void BuildTaskDefinition_StartsTheExecutableMinimised()
    {
        var exec = Parse().Descendants(TaskNamespace + "Exec").Single();

        Assert.Equal(ExecutablePath, exec.Element(TaskNamespace + "Command")?.Value);
        Assert.Equal("--minimized", exec.Element(TaskNamespace + "Arguments")?.Value);
    }

    /// <summary>
    /// 驗證 Source 包含版本標記字串。
    /// </summary>
    [Fact]
    public void BuildTaskDefinition_CarriesTheDefinitionMarkerLaunchLooksFor() =>
        Assert.Equal(StartupService.DefinitionMarker, Parse().Descendants(TaskNamespace + "Source").Single().Value);

    /// <summary>
    /// 驗證路徑包含特殊字元時有進行安全轉義。
    /// </summary>
    [Fact]
    public void BuildTaskDefinition_EscapesThePathInsteadOfBreakingTheDocument()
    {
        const string awkwardPath = @"C:\Tools & <Apps>\ThermoTray""1"".exe";
        var definition = StartupService.BuildTaskDefinition(awkwardPath, UserId);

        Assert.Equal(awkwardPath, XDocument.Parse(definition).Descendants(TaskNamespace + "Command").Single().Value);
    }

    /// <summary>
    /// 使用 Windows 原生 COM 物件 `Schedule.Service` 驗證排程 XML 格式可被 Task Scheduler 解構解析。
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
