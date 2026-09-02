using System;
using System.IO;
using Xunit;

namespace ThermoTray.Tests;

/// <summary>
/// 驗證安裝器持續提供一致的開始功能表、升級、解除安裝與診斷體驗。
/// </summary>
public sealed class InstallerContractTests
{
    /// <summary>
    /// 驗證升級設定、應用程式與功能中繼資料，以及安裝紀錄不會被後續修改移除。
    /// </summary>
    [Fact]
    public void InstallerProvidesConsistentUpgradeAndDiagnosticsMetadata()
    {
        var script = ReadInstallerScript();

        foreach (var directive in new[]
        {
            "AllowNoIcons=no",
            "DisableProgramGroupPage=yes",
            "UsePreviousAppDir=yes",
            "UsePreviousGroup=yes",
            "UsePreviousTasks=yes",
            "CreateUninstallRegKey=yes",
            "SetupLogging=yes",
            "UninstallLogging=yes",
            "CloseApplications=no",
            "RestartApplications=no"
        })
        {
            Assert.Contains(directive, script.Lines, StringComparer.Ordinal);
        }

        Assert.Contains("AppPublisherURL={#AppURL}", script.Lines, StringComparer.Ordinal);
        Assert.Contains("AppSupportURL={#AppURL}/issues", script.Lines, StringComparer.Ordinal);
        Assert.Contains("AppUpdatesURL={#AppURL}/releases", script.Lines, StringComparer.Ordinal);
    }

    /// <summary>
    /// 驗證開始功能表固定建立啟動與解除安裝捷徑，而桌面捷徑仍由使用者自行選擇。
    /// </summary>
    [Fact]
    public void StartMenuAndOptionalShortcutPolicyIsPreserved()
    {
        var script = ReadInstallerScript();
        var startMenuShortcut = Assert.Single(script.Lines, line =>
            line.StartsWith("Name: \"{group}\\{#AppName}\";", StringComparison.Ordinal));
        var uninstallShortcut = Assert.Single(script.Lines, line =>
            line.Contains("{cm:UninstallProgram,{#AppName}}", StringComparison.Ordinal));
        var desktopTask = Assert.Single(script.Lines, line =>
            line.StartsWith("Name: \"desktopicon\";", StringComparison.Ordinal));

        Assert.DoesNotContain("Tasks:", startMenuShortcut, StringComparison.Ordinal);
        Assert.DoesNotContain("Tasks:", uninstallShortcut, StringComparison.Ordinal);
        Assert.Contains("WorkingDir: \"{app}\"", startMenuShortcut, StringComparison.Ordinal);
        Assert.Contains("AppUserModelID:", startMenuShortcut, StringComparison.Ordinal);
        Assert.Contains("Flags: unchecked", desktopTask, StringComparison.Ordinal);
    }

    private static (string Content, string[] Lines) ReadInstallerScript()
    {
        var repositoryRoot = FindRepositoryRoot();
        var content = File.ReadAllText(Path.Combine(repositoryRoot, "installer", "ThermoTray.iss"));
        return (content, content.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries));
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
