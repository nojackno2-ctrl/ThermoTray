using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace ThermoTray;

/// <summary>
/// 負責設定 Windows 自動啟動的服務類別。
/// 由於 ThermoTray 要求最高管理員權限 (requireAdministrator)，
/// 一般的 <c>HKCU\Run</c> 登錄檔寫法會在開機時被 Windows 阻擋而無法執行，
/// 因此本服務透過工作排程器 (Task Scheduler) 建立具備最高權限 (<c>HighestAvailable</c>) 的登入觸發工作。
/// </summary>
public sealed class StartupService
{
    private const string RunKeyPath = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
    private const string ValueName = "ThermoTray";
    private const string TaskName = "ThermoTray";
    private const int TimeoutMilliseconds = 10_000;

    /// <summary>
    /// 寫入排程工作 Source 欄位中的識別標記。每當 <see cref="BuildTaskDefinition"/>XML 結構有重大更新時即遞增，
    /// 應用程式開機或啟動時會檢查此標記，若舊版本排程缺少此標記則自動重建更新。
    /// </summary>
    internal const string DefinitionMarker = "ThermoTray startup task (definition 2)";

    /// <summary>
    /// 檢查開機啟動排程工作是否存在且為最新版本定義。
    /// 舊版本透過 `schtasks` 命令列產生的預設排程會在改用電池或運行 3 天後強制關閉 ThermoTray，因此需判斷並自動更新。
    /// </summary>
    /// <returns>若排程存在且包含最新標記傳回 true，否則傳回 false。</returns>
    public bool IsUpToDate() =>
        RunSchtasks($"/Query /TN {TaskName} /XML", out var definition)
        && definition.Contains(DefinitionMarker, StringComparison.Ordinal);

    /// <summary>
    /// 設定或取消開機自動啟動。
    /// </summary>
    /// <param name="enabled">True 表示開啟自動啟動，False 表示關閉自動啟動。</param>
    /// <exception cref="InvalidOperationException">當無法取得執行檔路徑時擲出。</exception>
    /// <exception cref="UnauthorizedAccessException">當未具備管理員權限或無法建立排程時擲出。</exception>
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

    /// <summary>
    /// 嘗試建立高權限開機排程工作。將 XML 定義檔寫入臨時路徑後透過 `schtasks /Create /XML` 匯入。
    /// </summary>
    /// <param name="executablePath">ThermoTray 執行檔路徑。</param>
    /// <returns>若建立成功傳回 true，否則傳回 false。</returns>
    private static bool TryCreateElevatedTask(string executablePath)
    {
        // 使用隨機檔名建立臨時 XML 定義檔，防止預測性路徑替換漏洞
        var definitionPath = Path.Combine(Path.GetTempPath(), $"ThermoTray-{Path.GetRandomFileName()}.xml");

        try
        {
            // schtasks 要求 XML 定義檔必須為 UTF-16 編碼，否則拒絕解析
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
    /// 建立完整且正確的排程工作 XML 定義字串。
    /// 明確將 `DisallowStartIfOnBatteries`、`StopIfGoingOnBatteries` 與 `ExecutionTimeLimit` 設定為無限制/不中斷，
    /// 避免預設行為導致筆記型電腦在拔除電源或執行超過 72 小時後被 Task Scheduler 強制結束處理程序。
    /// </summary>
    /// <param name="executablePath">執行檔路徑。</param>
    /// <param name="userId">當前使用者 SID 或帳戶名稱。</param>
    /// <returns>UTF-16 格式之 XML 排程定義內文。</returns>
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
    /// 取得當前使用者的 SID 字串（如 S-1-5-21...）。優先使用 SID 可防止使用者變更帳戶名稱後排程失效。
    /// </summary>
    /// <returns>SID 字串或 DOMAIN\User 格式字串。</returns>
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
            // 退回使用 Domain\User 名稱
        }

        return $"{Environment.UserDomainName}\\{Environment.UserName}";
    }

    /// <summary>
    /// 對 XML 敏感字元進行安全轉義。
    /// </summary>
    private static string Escape(string value) => SecurityElement.Escape(value) ?? string.Empty;

    /// <summary>
    /// 清理產生的臨時 XML 定義檔。
    /// </summary>
    private static void TryDeleteDefinition(string definitionPath)
    {
        try
        {
            File.Delete(definitionPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 臨時檔殘留不會造成致命影響，忽略刪除失敗
        }
    }

    /// <summary>
    /// 刪除既有的 ThermoTray 排程工作。
    /// </summary>
    private static void RemoveScheduledTask() => RunSchtasks($"/Delete /TN {TaskName} /F", out _);

    /// <summary>
    /// 呼叫系統 `schtasks.exe` 命令，並同步清空標準輸出與錯誤串流，防止管線緩衝區滿載造成死鎖。
    /// </summary>
    /// <param name="arguments">命令列引數。</param>
    /// <param name="standardOutput">輸出的標準文字內容。</param>
    /// <returns>若命令成功執行且 ExitCode 為 0 傳回 true，否則傳回 false。</returns>
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

            // 同步讀取 StandardOutput 與 StandardError，避免管線堵塞
            var outputRead = process.StandardOutput.ReadToEndAsync();
            var errorRead = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(TimeoutMilliseconds))
            {
                Terminate(process);
                return false;
            }

            // 等待重定向串流讀取完畢
            process.WaitForExit();

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

    /// <summary>
    /// 強制結束超時的 schtasks 處理程序。
    /// </summary>
    private static void Terminate(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            // 處理程序已退出或無法中止
        }
    }

    /// <summary>
    /// 清除舊有的 `HKCU\Run` 登錄檔數值（移轉至 Task Scheduler 排程工作）。
    /// </summary>
    private static void RemoveRunValue()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
