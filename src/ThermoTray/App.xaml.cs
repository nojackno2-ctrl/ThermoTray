using System.Globalization;
using System.Windows;
using System.Windows.Interop;
using System.Text;
using System.IO;

namespace ThermoTray;

/// <summary>
/// ThermoTray 的應用程式進入點與生命週期管理類別 (Inherits <see cref="System.Windows.Application"/>)。
/// </summary>
public partial class App : System.Windows.Application
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan HandoverTimeout = TimeSpan.FromSeconds(10);

    private InstanceCoordinator? _coordinator;
    private Localizer? _messages;
    private TrayIconService? _trayIcon;
    private MainViewModel? _viewModel;
    private bool _isShuttingDown;

    /// <summary>
    /// 取得當前組件的版本號資訊。
    /// </summary>
    private static Version? ProductVersion => typeof(App).Assembly.GetName().Version;

    /// <summary>
    /// 依需求延遲載入的本地化訊息實例。一般啟動路徑不需載入此對話方塊字串。
    /// </summary>
    private Localizer Messages => _messages ??= new Localizer(new SettingsService().Load().Language);

    /// <summary>
    /// 處理應用程式啟動邏輯，包含命令列參數判斷、單一執行體協調、MVVM 與系統匣服務初始化。
    /// </summary>
    /// <param name="e">啟動事件引數。</param>
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 檢查是否帶有 --diagnostics 診斷參數
        if (e.Args.Contains("--diagnostics", StringComparer.OrdinalIgnoreCase))
        {
            WriteSensorDiagnostics();
            Shutdown();
            return;
        }

        // 開機最小化啟動不顯示主視窗，避免登入時視窗閃爍
        var startedMinimized = e.Args.Contains("--minimized", StringComparer.OrdinalIgnoreCase);

        // 嘗試取得單一執行體控制權，若已有舊實體執行且無法接手則結束
        if (!TryBecomeTheRunningInstance(startedMinimized))
        {
            Shutdown();
            return;
        }

        // 系統匣圖示擁有生命週期，因此關閉或隱藏主視窗不會自動終止處理程序
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var viewModel = new MainViewModel(new HardwareSensorService(), new SettingsService(), new StartupService());
        _viewModel = viewModel;
        _trayIcon = new TrayIconService(viewModel, ShowMainWindow, ExitApplication);

        var window = new MainWindow { DataContext = viewModel };
        window.Closed += (_, _) => ExitApplication();

        // 隱藏至系統匣為常態，主視窗回報顯示狀態以調整輪詢頻率
        window.IsVisibleChanged += (_, _) => viewModel.SetWindowVisible(window.IsVisible);
        MainWindow = window;
        viewModel.Start();

        if (!startedMinimized)
        {
            window.Show();
        }
    }

    /// <summary>
    /// 嘗試取得系統匣單一執行體的持有權。防止重複啟動導致兩個圖示同時輪詢硬體。
    /// </summary>
    /// <param name="startedMinimized">是否以最小化模式啟動。</param>
    /// <returns>若本實體為繼續執行的實體傳回 true，否則傳回 false。</returns>
    private bool TryBecomeTheRunningInstance(bool startedMinimized)
    {
        _coordinator = InstanceCoordinator.TryClaim(ProductVersion, PostShowMainWindow, PostExitApplication);
        return _coordinator is not null || TryTakeOverFromRunningInstance(startedMinimized);
    }

    /// <summary>
    /// 當已存在執行中的 ThermoTray 時，嘗試透過管道與其通訊並協調接手或顯示視窗。
    /// </summary>
    private bool TryTakeOverFromRunningInstance(bool startedMinimized)
    {
        using var client = InstanceClient.TryConnect(InstanceCoordinator.PipeName, ConnectTimeout);

        if (client is null)
        {
            // 無法透過具名管道連接，代表對方為舊版本。觸發舊版互斥鎖/事件讓其顯示視窗
            var raised = InstanceCoordinator.TrySignalLegacyInstance();

            if (!startedMinimized)
            {
                ShowMessage(
                    raised ? Format("LegacyInstanceRunning", runningVersion: null) : Messages["AlreadyRunning"],
                    MessageBoxImage.Information);
            }

            return false;
        }

        // 若為開機隨 Windows 啟動（--minimized），絕不跳出提示訊息阻礙使用者，直接進行無聲 Handover
        var action = startedMinimized
            ? InstanceAction.ShowRunning
            : InstanceProtocol.Decide(client.RunningVersion, ProductVersion);

        switch (action)
        {
            case InstanceAction.ReplaceRunning when ConfirmReplacement(client.RunningVersion):
                return TryReplace(client);

            case InstanceAction.ShowNewerRunning:
                ShowMessage(Format("NewerInstanceRunning", client.RunningVersion), MessageBoxImage.Information);
                break;
        }

        HandOver(client);
        return false;
    }

    /// <summary>
    /// 將前景切換權限授予執行中的舊實體，並要求其顯示主視窗。
    /// </summary>
    private static void HandOver(InstanceClient client)
    {
        NativeMethods.AllowSetForegroundWindow(client.RunningProcessId);
        client.RequestShow();
    }

    /// <summary>
    /// 請求執行中的舊實體結束，並在舊實體退出後由當前實體接手系統匣。
    /// </summary>
    private bool TryReplace(InstanceClient client)
    {
        var runningVersion = client.RunningVersion;
        var runningProcessId = client.RunningProcessId;
        var accepted = client.RequestExit();
        client.Dispose();

        if (accepted && InstanceCoordinator.WaitForProcessExit(runningProcessId, HandoverTimeout))
        {
            _coordinator = InstanceCoordinator.ClaimAfterHandover(ProductVersion, PostShowMainWindow, PostExitApplication);
            if (_coordinator is not null)
            {
                return true;
            }
        }

        ShowMessage(Format("ReplaceFailed", runningVersion), MessageBoxImage.Warning);
        return false;
    }

    /// <summary>
    /// 彈出對話方塊詢問使用者是否關閉舊版本改用新版本。
    /// </summary>
    private bool ConfirmReplacement(Version? runningVersion) =>
        System.Windows.MessageBox.Show(
            Format("ReplaceRunningInstance", runningVersion),
            "ThermoTray",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question) == MessageBoxResult.Yes;

    /// <summary>
    /// 格式化本地化訊息中的版本資訊。
    /// </summary>
    private string Format(string key, Version? runningVersion) => string.Format(
        CultureInfo.CurrentCulture,
        Messages[key],
        MainViewModel.FormatVersion(runningVersion),
        MainViewModel.FormatVersion(ProductVersion));

    /// <summary>
    /// 顯示系統訊息對話方塊。
    /// </summary>
    private static void ShowMessage(string message, MessageBoxImage icon) =>
        System.Windows.MessageBox.Show(message, "ThermoTray", MessageBoxButton.OK, icon);

    /// <summary>
    /// 在 UI 執行緒上非同步分送顯示主視窗請求。
    /// </summary>
    private void PostShowMainWindow() => Dispatcher.InvokeAsync(ShowMainWindow);

    /// <summary>
    /// 在 UI 執行緒上非同步分送結束應用程式請求。
    /// </summary>
    private void PostExitApplication() => Dispatcher.InvokeAsync(ExitApplication);

    /// <summary>
    /// 顯示並啟動主視窗，將其帶入系統最前景。
    /// </summary>
    private void ShowMainWindow()
    {
        if (MainWindow is null)
        {
            return;
        }

        MainWindow.Show();
        MainWindow.WindowState = WindowState.Normal;
        MainWindow.Activate();

        var handle = new WindowInteropHelper(MainWindow).Handle;
        if (handle != IntPtr.Zero)
        {
            NativeMethods.SetForegroundWindow(handle);
        }
    }

    /// <summary>
    /// 結束應用程式並釋放所有服務資源。
    /// </summary>
    private void ExitApplication()
    {
        if (_isShuttingDown)
        {
            return;
        }

        _isShuttingDown = true;
        ReleaseServices();
        Shutdown();
    }

    /// <summary>
    /// 執行感測器診斷，將原始 LibreHardwareMonitor 感測器數據寫入檔案以利排除故障。
    /// </summary>
    private static void WriteSensorDiagnostics()
    {
        var outputPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ThermoTray",
            "sensor-diagnostics.txt");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

            using var sensorService = new HardwareSensorService();
            var report = new StringBuilder();
            report.AppendLine("ThermoTray raw temperature sensor diagnostics");
            report.AppendLine($"Generated: {DateTimeOffset.Now:O}");
            var driver = SensorDriverStatus.Query();
            report.AppendLine($"PawnIO kernel driver installed: {driver.IsInstalled}"
                + (driver.IsInstalled ? $" (version {driver.Version})" : $" - install from {SensorDriverStatus.DownloadUrl}"));
            report.AppendLine($"Process elevated: {ElevationService.IsElevated} (PawnIO grants its device to elevated processes only)");
            foreach (var sensor in sensorService.ReadRawSensors())
            {
                report.AppendLine($"{sensor.HardwareType} | {sensor.HardwareName} | {sensor.SensorType} | {sensor.SensorName} | {sensor.Value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null"}");
            }

            File.WriteAllText(outputPath, report.ToString());
        }
        catch (Exception exception)
        {
            TryReportDiagnosticsFailure(outputPath, exception);
        }
    }

    /// <summary>
    /// 寫入診斷失敗紀錄。
    /// </summary>
    private static void TryReportDiagnosticsFailure(string outputPath, Exception exception)
    {
        try
        {
            File.WriteAllText(outputPath, $"Diagnostics failed: {exception}");
        }
        catch (Exception)
        {
            // 無主視窗應用程式無 Console，寫入失敗時忽略
        }
    }

    /// <summary>
    /// 處理 WPF OnExit 事件，清理內部資源。
    /// </summary>
    protected override void OnExit(ExitEventArgs e)
    {
        _isShuttingDown = true;
        ReleaseServices();
        base.OnExit(e);
    }

    /// <summary>
    /// 停止感測器輪詢並釋放系統匣圖示與互斥鎖。
    /// </summary>
    private void ReleaseServices()
    {
        _viewModel?.Stop();
        _trayIcon?.Dispose();

        _coordinator?.Dispose();
        _coordinator = null;
    }
}
