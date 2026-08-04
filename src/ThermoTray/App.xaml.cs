using System.Globalization;
using System.Windows;
using System.Windows.Interop;
using System.Text;
using System.IO;

namespace ThermoTray;

public partial class App : System.Windows.Application
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan HandoverTimeout = TimeSpan.FromSeconds(10);

    private InstanceCoordinator? _coordinator;
    private Localizer? _messages;
    private TrayIconService? _trayIcon;
    private MainViewModel? _viewModel;
    private bool _isShuttingDown;

    private static Version? ProductVersion => typeof(App).Assembly.GetName().Version;

    /// <summary>Loaded on demand: the common startup path never shows one of these messages.</summary>
    private Localizer Messages => _messages ??= new Localizer(new SettingsService().Load().Language);

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Contains("--diagnostics", StringComparer.OrdinalIgnoreCase))
        {
            WriteSensorDiagnostics();
            Shutdown();
            return;
        }

        // Starting minimized never shows the window, which avoids a visible flash at logon.
        var startedMinimized = e.Args.Contains("--minimized", StringComparer.OrdinalIgnoreCase);

        if (!TryBecomeTheRunningInstance(startedMinimized))
        {
            Shutdown();
            return;
        }

        // The tray owns the lifetime, so hiding or closing the window must not end the process.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var viewModel = new MainViewModel(new HardwareSensorService(), new SettingsService(), new StartupService());
        _viewModel = viewModel;
        _trayIcon = new TrayIconService(viewModel, ShowMainWindow, ExitApplication);

        var window = new MainWindow { DataContext = viewModel };
        window.Closed += (_, _) => ExitApplication();

        // Hiding to the tray is the app's normal state, and it is the sampling rate's only input, so
        // the window reports every visibility change rather than only the ones it initiates.
        window.IsVisibleChanged += (_, _) => viewModel.SetWindowVisible(window.IsVisible);
        MainWindow = window;
        viewModel.Start();

        if (!startedMinimized)
        {
            window.Show();
        }
    }

    /// <summary>
    /// A second set of tray icons polling the same hardware helps nobody, so exactly one instance
    /// runs. Reports whether this process is the one that continues.
    /// </summary>
    private bool TryBecomeTheRunningInstance(bool startedMinimized)
    {
        _coordinator = InstanceCoordinator.TryClaim(ProductVersion, PostShowMainWindow, PostExitApplication);
        return _coordinator is not null || TryTakeOverFromRunningInstance(startedMinimized);
    }

    private bool TryTakeOverFromRunningInstance(bool startedMinimized)
    {
        using var client = InstanceClient.TryConnect(InstanceCoordinator.PipeName, ConnectTimeout);

        if (client is null)
        {
            // An instance that cannot be reached over the pipe predates it. Raising its window is the
            // only hand-over such a build understands, so an upgrade cannot take the tray from it and
            // the user has to be told why their new build appeared to do nothing.
            var raised = InstanceCoordinator.TrySignalLegacyInstance();

            if (!startedMinimized)
            {
                ShowMessage(
                    raised ? Format("LegacyInstanceRunning", runningVersion: null) : Messages["AlreadyRunning"],
                    MessageBoxImage.Information);
            }

            return false;
        }

        // A logon launch must never stop at a modal prompt nobody is there to answer, whichever
        // version turns out to be running, so it always degrades to a silent hand-over.
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

    private static void HandOver(InstanceClient client)
    {
        // This process still holds the foreground right the user's launch gave it; the running
        // instance needs it to raise its own window.
        NativeMethods.AllowSetForegroundWindow(client.RunningProcessId);
        client.RequestShow();
    }

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

    private bool ConfirmReplacement(Version? runningVersion) =>
        System.Windows.MessageBox.Show(
            Format("ReplaceRunningInstance", runningVersion),
            "ThermoTray",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question) == MessageBoxResult.Yes;

    private string Format(string key, Version? runningVersion) => string.Format(
        CultureInfo.CurrentCulture,
        Messages[key],
        MainViewModel.FormatVersion(runningVersion),
        MainViewModel.FormatVersion(ProductVersion));

    private static void ShowMessage(string message, MessageBoxImage icon) =>
        System.Windows.MessageBox.Show(message, "ThermoTray", MessageBoxButton.OK, icon);

    private void PostShowMainWindow() => Dispatcher.InvokeAsync(ShowMainWindow);

    private void PostExitApplication() => Dispatcher.InvokeAsync(ExitApplication);

    private void ShowMainWindow()
    {
        if (MainWindow is null)
        {
            return;
        }

        MainWindow.Show();
        MainWindow.WindowState = WindowState.Normal;
        MainWindow.Activate();

        // Activate() alone is a request the window manager may answer with a flashing taskbar button;
        // the launching instance handed this process the right to take the foreground outright.
        var handle = new WindowInteropHelper(MainWindow).Handle;
        if (handle != IntPtr.Zero)
        {
            NativeMethods.SetForegroundWindow(handle);
        }
    }

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

    private static void TryReportDiagnosticsFailure(string outputPath, Exception exception)
    {
        try
        {
            File.WriteAllText(outputPath, $"Diagnostics failed: {exception}");
        }
        catch (Exception)
        {
            // A windowed application has no console, so an unwritable output path leaves nowhere to report.
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _isShuttingDown = true;
        ReleaseServices();
        base.OnExit(e);
    }

    private void ReleaseServices()
    {
        _viewModel?.Stop();
        _trayIcon?.Dispose();

        _coordinator?.Dispose();
        _coordinator = null;
    }
}
