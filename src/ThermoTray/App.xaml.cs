using System.Windows;
using System.Text;
using System.IO;
using System.Threading;

namespace ThermoTray;

public partial class App : System.Windows.Application
{
    // Session-local names: every instance runs elevated in the same session, so a wider scope
    // would only invite name collisions with other sessions.
    private const string SingleInstanceMutexName = "Local\\ThermoTray.SingleInstance";
    private const string ShowWindowEventName = "Local\\ThermoTray.ShowWindow";

    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _showWindowSignal;
    private RegisteredWaitHandle? _showWindowRegistration;
    private TrayIconService? _trayIcon;
    private MainViewModel? _viewModel;
    private bool _isShuttingDown;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Contains("--diagnostics", StringComparer.OrdinalIgnoreCase))
        {
            WriteSensorDiagnostics();
            Shutdown();
            return;
        }

        if (!TryClaimSingleInstance())
        {
            // A second set of tray icons polling the same hardware helps nobody; hand over instead.
            SignalRunningInstance();
            Shutdown();
            return;
        }

        // The tray owns the lifetime, so hiding or closing the window must not end the process.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        _viewModel = new MainViewModel(new HardwareSensorService(), new SettingsService(), new StartupService());
        _trayIcon = new TrayIconService(_viewModel, ShowMainWindow, ExitApplication);

        var window = new MainWindow { DataContext = _viewModel };
        window.Closed += (_, _) => ExitApplication();
        MainWindow = window;
        _viewModel.Start();

        // Starting minimized never shows the window, which avoids a visible flash at logon.
        if (!e.Args.Contains("--minimized", StringComparer.OrdinalIgnoreCase))
        {
            window.Show();
        }
    }

    private bool TryClaimSingleInstance()
    {
        try
        {
            _singleInstanceMutex = new Mutex(initiallyOwned: false, SingleInstanceMutexName, out var isFirstInstance);
            if (isFirstInstance)
            {
                RegisterShowWindowSignal();
                return true;
            }

            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            return false;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException
            or IOException
            or WaitHandleCannotBeOpenedException)
        {
            // Without the guard a duplicate instance becomes possible, which beats refusing to start.
            return true;
        }
    }

    /// <summary>Lets a later launch bring this instance's window back instead of doing nothing.</summary>
    private void RegisterShowWindowSignal()
    {
        try
        {
            _showWindowSignal = new EventWaitHandle(initialState: false, EventResetMode.AutoReset, ShowWindowEventName);
            _showWindowRegistration = ThreadPool.RegisterWaitForSingleObject(
                _showWindowSignal,
                (_, _) => Dispatcher.InvokeAsync(ShowMainWindow),
                state: null,
                Timeout.Infinite,
                executeOnlyOnce: false);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException
            or IOException
            or WaitHandleCannotBeOpenedException)
        {
            // Single-instance enforcement still works; only the hand-over gesture is lost.
        }
    }

    private static void SignalRunningInstance()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(ShowWindowEventName, out var signal))
            {
                using (signal)
                {
                    signal.Set();
                }
            }
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException
            or IOException
            or WaitHandleCannotBeOpenedException)
        {
            // The running instance cannot be reached; exiting quietly is still the right outcome.
        }
    }

    private void ShowMainWindow()
    {
        if (MainWindow is null)
        {
            return;
        }

        MainWindow.Show();
        MainWindow.WindowState = WindowState.Normal;
        MainWindow.Activate();
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
            foreach (var sensor in sensorService.ReadRawTemperatureSensors())
            {
                report.AppendLine($"{sensor.HardwareType} | {sensor.HardwareName} | {sensor.SensorName} | {sensor.Celsius?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null"}");
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

        _showWindowRegistration?.Unregister(waitObject: null);
        _showWindowRegistration = null;
        _showWindowSignal?.Dispose();
        _showWindowSignal = null;
        _singleInstanceMutex?.Dispose();
        _singleInstanceMutex = null;
    }
}
