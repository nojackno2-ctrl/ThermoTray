using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Threading;

namespace ThermoTray;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private static readonly TimeSpan VisiblePollingInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Used while the window is hidden in the tray, which is where this app spends nearly all of its
    /// life. Only the tray icons are readable then, and reading hardware sensors is by far the most
    /// expensive thing ThermoTray does, so halving the sample rate halves its idle CPU cost.
    /// </summary>
    private static readonly TimeSpan HiddenPollingInterval = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(5);
    private const long DriverProbeIntervalMilliseconds = 30_000;

    /// <summary>Samples a missing GPU is given before it is treated as absent rather than faulty.</summary>
    private const int MissingGpuGraceSamples = 5;

    private readonly HardwareSensorService _sensorService;
    private readonly SettingsService _settingsService;
    private readonly StartupService _startupService;
    private readonly AppSettings _settings;
    private readonly Localizer _localizer;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private SensorDriverStatus _driverStatus = SensorDriverStatus.Query();
    private bool _isDriverActionVisible;
    private bool _isGpuPresent = true;
    private bool _gpuEverReported;
    private int _gpuMissingSamples;
    private bool _isStopped;

    // Read by the sampling thread and written by the UI thread, so it must not be cached in a register.
    private volatile bool _isWindowVisible;
    private CancellationTokenSource? _pollingCancellation;
    private Task? _pollingTask;
    private long _nextDriverProbeTick = Environment.TickCount64 + DriverProbeIntervalMilliseconds;
    private TemperatureReading? _lastCpuReading;
    private TemperatureReading? _lastGpuReading;
    private UtilizationReading? _lastCpuUsage;
    private UtilizationReading? _lastGpuUsage;
    private string _cpuTemperature = "…";
    private string _gpuTemperature = "…";
    private string _cpuUsage = "…";
    private string _gpuUsage = "…";
    private string _cpuTrayDigits = TemperatureFormatter.TrayPlaceholder;
    private string _gpuTrayDigits = TemperatureFormatter.TrayPlaceholder;
    private string _cpuUsageTrayDigits = UtilizationFormatter.TrayPlaceholder;
    private string _gpuUsageTrayDigits = UtilizationFormatter.TrayPlaceholder;
    private string _cpuSource = string.Empty;
    private string _gpuSource = string.Empty;
    private string _statusMessage;

    public MainViewModel(HardwareSensorService sensorService, SettingsService settingsService, StartupService startupService)
    {
        _sensorService = sensorService;
        _settingsService = settingsService;
        _startupService = startupService;
        _settings = settingsService.Load();
        _localizer = new Localizer(_settings.Language);
        _settings.Language = _localizer.Language;
        _statusMessage = _localizer["WaitingForSensors"];
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public Localizer T => _localizer;

    public string CpuTemperature
    {
        get => _cpuTemperature;
        private set => SetField(ref _cpuTemperature, value);
    }

    public string GpuTemperature
    {
        get => _gpuTemperature;
        private set => SetField(ref _gpuTemperature, value);
    }

    public string CpuUsage
    {
        get => _cpuUsage;
        private set => SetField(ref _cpuUsage, value);
    }

    public string GpuUsage
    {
        get => _gpuUsage;
        private set => SetField(ref _gpuUsage, value);
    }

    /// <summary>Whole degrees for the tray icon, independent of the current culture's number format.</summary>
    public string CpuTrayDigits
    {
        get => _cpuTrayDigits;
        private set => SetField(ref _cpuTrayDigits, value);
    }

    public string GpuTrayDigits
    {
        get => _gpuTrayDigits;
        private set => SetField(ref _gpuTrayDigits, value);
    }

    /// <summary>Whole percentage points for the tray icon, independent of the current culture.</summary>
    public string CpuUsageTrayDigits
    {
        get => _cpuUsageTrayDigits;
        private set => SetField(ref _cpuUsageTrayDigits, value);
    }

    public string GpuUsageTrayDigits
    {
        get => _gpuUsageTrayDigits;
        private set => SetField(ref _gpuUsageTrayDigits, value);
    }

    public string CpuSource
    {
        get => _cpuSource;
        private set => SetField(ref _cpuSource, value);
    }

    public string GpuSource
    {
        get => _gpuSource;
        private set => SetField(ref _gpuSource, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetField(ref _statusMessage, value);
    }

    /// <summary>True when the missing kernel driver is the reason a reading is unavailable.</summary>
    public bool IsDriverActionVisible
    {
        get => _isDriverActionVisible;
        private set => SetField(ref _isDriverActionVisible, value);
    }

    /// <summary>
    /// False once this machine has gone long enough without a single GPU temperature or usage
    /// reading to conclude that it has no readable GPU telemetry. Its tray icon and card are then
    /// hidden instead of showing a warning that can never be resolved.
    /// </summary>
    public bool IsGpuPresent
    {
        get => _isGpuPresent;
        private set => SetField(ref _isGpuPresent, value);
    }

    public string DriverDownloadUrl => SensorDriverStatus.DownloadUrl;

    public bool StartWithWindows
    {
        get => _settings.StartWithWindows;
        set
        {
            if (_settings.StartWithWindows == value)
            {
                return;
            }

            if (!TrySetStartupEnabled(value))
            {
                StatusMessage = T["StartupError"];

                // Push the unchanged value back so the checkbox does not claim a state that was not applied.
                OnPropertyChanged();
                return;
            }

            _settings.StartWithWindows = value;
            SaveSettings();
            OnPropertyChanged();
        }
    }

    public bool HideWhenClosed
    {
        get => _settings.HideWhenClosed;
        set
        {
            if (_settings.HideWhenClosed == value)
            {
                return;
            }

            _settings.HideWhenClosed = value;
            SaveSettings();
            OnPropertyChanged();
        }
    }

    public string Language
    {
        get => _localizer.Language;
        set
        {
            if (value == _localizer.Language)
            {
                return;
            }

            _localizer.SetLanguage(value);
            _settings.Language = _localizer.Language;
            SaveSettings();
            _lastCpuReading = null;
            _lastGpuReading = null;
            _lastCpuUsage = null;
            _lastGpuUsage = null;
            StatusMessage = T["WaitingForSensors"];
            OnPropertyChanged(string.Empty);
        }
    }

    public void Start()
    {
        if (_isStopped || _pollingCancellation is not null)
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        _pollingCancellation = cancellation;

        // The whole loop runs on the thread pool, so a sample never needs its own dispatch back onto it.
        _pollingTask = Task.Run(() => PollAsync(cancellation.Token), CancellationToken.None);

        // schtasks can block for seconds, so the startup preference is reconciled off the UI thread.
        _ = Task.Run(ReconcileStartupSetting);
    }

    /// <summary>
    /// Tells the sampling loop whether anything other than the tray icons is on screen. The new rate
    /// takes effect on the next tick rather than immediately, which keeps the loop free of extra
    /// cross-thread signalling for a change worth at most one interval.
    /// </summary>
    public void SetWindowVisible(bool isVisible) => _isWindowVisible = isVisible;

    public void Stop()
    {
        _isStopped = true;
        var cancellation = _pollingCancellation;
        var pollingTask = _pollingTask;
        _pollingCancellation = null;
        _pollingTask = null;

        if (cancellation is null)
        {
            _sensorService.Dispose();
            return;
        }

        cancellation.Cancel();

        if (!WaitForPollingToStop(pollingTask))
        {
            // A sampling pass is still running; closing the sensor stack underneath it would be worse
            // than leaving it to process teardown.
            return;
        }

        cancellation.Dispose();
        _sensorService.Dispose();
    }

    private static bool WaitForPollingToStop(Task? pollingTask)
    {
        if (pollingTask is null)
        {
            return true;
        }

        try
        {
            return pollingTask.Wait(ShutdownTimeout);
        }
        catch (AggregateException)
        {
            // A faulted polling task has still finished, which is all this wait needs to establish.
            return true;
        }
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        var interval = CurrentPollingInterval;
        using var timer = new PeriodicTimer(interval);

        try
        {
            do
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                try
                {
                    var snapshot = _sensorService.Read();
                    Post(() => ApplySnapshot(snapshot));
                }
                catch (Exception)
                {
                    Post(() => StatusMessage = T["SensorError"]);
                }

                var desiredInterval = CurrentPollingInterval;
                if (desiredInterval != interval)
                {
                    interval = desiredInterval;
                    timer.Period = interval;
                }
            }
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    private TimeSpan CurrentPollingInterval => GetPollingInterval(_isWindowVisible);

    internal static TimeSpan GetPollingInterval(bool isWindowVisible) =>
        isWindowVisible ? VisiblePollingInterval : HiddenPollingInterval;

    /// <summary>
    /// Queues UI work without awaiting it. Awaiting would make the polling loop depend on the UI
    /// thread, which then could not block on that loop during shutdown.
    /// </summary>
    private void Post(Action action)
    {
        if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
        {
            return;
        }

        try
        {
            _dispatcher.InvokeAsync(action);
        }
        catch (InvalidOperationException)
        {
            // The dispatcher shut down after the check above; the update is no longer needed.
        }
    }

    private void ApplySnapshot(HardwareSnapshot snapshot)
    {
        if (_lastCpuReading != snapshot.CpuTemperature)
        {
            _lastCpuReading = snapshot.CpuTemperature;
            CpuTemperature = Format(snapshot.CpuTemperature);
            CpuTrayDigits = TemperatureFormatter.ToTrayDigits(snapshot.CpuTemperature);
            CpuSource = snapshot.CpuTemperature.Source;
        }

        if (_lastGpuReading != snapshot.GpuTemperature)
        {
            _lastGpuReading = snapshot.GpuTemperature;
            GpuTemperature = Format(snapshot.GpuTemperature);
            GpuTrayDigits = TemperatureFormatter.ToTrayDigits(snapshot.GpuTemperature);
            GpuSource = snapshot.GpuTemperature.Source;
        }

        if (_lastCpuUsage != snapshot.CpuUsage)
        {
            _lastCpuUsage = snapshot.CpuUsage;
            CpuUsage = FormatUsage(snapshot.CpuUsage);
            CpuUsageTrayDigits = UtilizationFormatter.ToTrayDigits(snapshot.CpuUsage);
        }

        if (_lastGpuUsage != snapshot.GpuUsage)
        {
            _lastGpuUsage = snapshot.GpuUsage;
            GpuUsage = FormatUsage(snapshot.GpuUsage);
            GpuUsageTrayDigits = UtilizationFormatter.ToTrayDigits(snapshot.GpuUsage);
        }

        UpdateGpuPresence(snapshot.GpuTemperature, snapshot.GpuUsage);
        StatusMessage = GetAvailabilityMessage(snapshot);
    }

    private void UpdateGpuPresence(TemperatureReading gpuTemperature, UtilizationReading gpuUsage)
    {
        if (gpuTemperature.IsAvailable || gpuUsage.IsAvailable)
        {
            _gpuEverReported = true;
            _gpuMissingSamples = 0;
            IsGpuPresent = true;
            return;
        }

        if (_gpuEverReported)
        {
            // A GPU that reported once and then stopped is a real fault, so keep surfacing it.
            return;
        }

        if (_gpuMissingSamples < MissingGpuGraceSamples)
        {
            _gpuMissingSamples++;
        }

        IsGpuPresent = _gpuMissingSamples < MissingGpuGraceSamples;
    }

    private string GetAvailabilityMessage(HardwareSnapshot snapshot)
    {
        if (!snapshot.CpuTemperature.IsAvailable)
        {
            // The driver can be installed while ThermoTray runs, so re-probe instead of trusting the startup value.
            if (!_driverStatus.IsInstalled && Environment.TickCount64 >= _nextDriverProbeTick)
            {
                _driverStatus = SensorDriverStatus.Query();
                _nextDriverProbeTick = Environment.TickCount64 + DriverProbeIntervalMilliseconds;
            }

            IsDriverActionVisible = !_driverStatus.IsInstalled;
            if (IsDriverActionVisible)
            {
                return T["DriverMissing"];
            }

            return snapshot.GpuTemperature.IsAvailable || snapshot.GpuUsage.IsAvailable
                ? T["CpuSensorUnavailable"]
                : T["NoSensor"];
        }

        IsDriverActionVisible = false;
        if (!snapshot.CpuUsage.IsAvailable)
        {
            return T["CpuUsageUnavailable"];
        }

        // A machine with no readable GPU telemetry is not a fault and must not raise a permanent warning.
        if (!IsGpuPresent)
        {
            return string.Empty;
        }

        if (!snapshot.GpuTemperature.IsAvailable)
        {
            return T["GpuSensorUnavailable"];
        }

        return snapshot.GpuUsage.IsAvailable ? string.Empty : T["GpuUsageUnavailable"];
    }

    private string Format(TemperatureReading reading) => reading.Celsius is decimal celsius
        ? $"{celsius:0.#} °C"
        : T["Unavailable"];

    private string FormatUsage(UtilizationReading reading) => reading.Percent is decimal percent
        ? $"{percent:0.#}%"
        : T["Unavailable"];

    private void SaveSettings()
    {
        try
        {
            _settingsService.Save(_settings);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Preferences are non-critical; avoid a user-visible crash if a local profile is locked.
        }
    }

    private bool TrySetStartupEnabled(bool enabled)
    {
        try
        {
            _startupService.SetEnabled(enabled);
            return true;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException
            or System.Security.SecurityException
            or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Recreates the logon task when the saved preference says startup is enabled but the task is
    /// gone, which is what an upgrade from the old <c>HKCU\Run</c> entry leaves behind.
    /// </summary>
    private void ReconcileStartupSetting()
    {
        if (!_settings.StartWithWindows || _startupService.IsEnabled() || TrySetStartupEnabled(true))
        {
            return;
        }

        Post(() =>
        {
            _settings.StartWithWindows = false;
            SaveSettings();
            OnPropertyChanged(nameof(StartWithWindows));
            StatusMessage = T["StartupError"];
        });
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private bool SetField<TValue>(ref TValue field, TValue value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<TValue>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
