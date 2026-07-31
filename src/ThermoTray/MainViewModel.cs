using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Threading;

namespace ThermoTray;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private static readonly TimeSpan PollingInterval = TimeSpan.FromSeconds(1);
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
    private CancellationTokenSource? _pollingCancellation;
    private Task? _pollingTask;
    private long _nextDriverProbeTick = Environment.TickCount64 + DriverProbeIntervalMilliseconds;
    private TemperatureReading? _lastCpuReading;
    private TemperatureReading? _lastGpuReading;
    private string _cpuTemperature = "…";
    private string _gpuTemperature = "…";
    private string _cpuTrayDigits = TemperatureFormatter.TrayPlaceholder;
    private string _gpuTrayDigits = TemperatureFormatter.TrayPlaceholder;
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
    /// False once this machine has gone long enough without a single GPU reading to conclude that
    /// it has no readable GPU sensor. Its tray icon and card are then hidden instead of showing a
    /// warning that can never be resolved.
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
        _pollingTask = PollAsync(cancellation.Token);

        // schtasks can block for seconds, so the startup preference is reconciled off the UI thread.
        _ = Task.Run(ReconcileStartupSetting);
    }

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
        using var timer = new PeriodicTimer(PollingInterval);

        try
        {
            do
            {
                try
                {
                    var snapshot = await Task.Run(_sensorService.Read, cancellationToken).ConfigureAwait(false);
                    Post(() => ApplySnapshot(snapshot));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception)
                {
                    Post(() => StatusMessage = T["SensorError"]);
                }
            }
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

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

    private void ApplySnapshot(TemperatureSnapshot snapshot)
    {
        if (_lastCpuReading != snapshot.Cpu)
        {
            _lastCpuReading = snapshot.Cpu;
            CpuTemperature = Format(snapshot.Cpu);
            CpuTrayDigits = TemperatureFormatter.ToTrayDigits(snapshot.Cpu);
            CpuSource = snapshot.Cpu.Source;
        }

        if (_lastGpuReading != snapshot.Gpu)
        {
            _lastGpuReading = snapshot.Gpu;
            GpuTemperature = Format(snapshot.Gpu);
            GpuTrayDigits = TemperatureFormatter.ToTrayDigits(snapshot.Gpu);
            GpuSource = snapshot.Gpu.Source;
        }

        UpdateGpuPresence(snapshot.Gpu);
        StatusMessage = GetAvailabilityMessage(snapshot);
    }

    private void UpdateGpuPresence(TemperatureReading gpu)
    {
        if (gpu.IsAvailable)
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

    private string GetAvailabilityMessage(TemperatureSnapshot snapshot)
    {
        // A machine with no readable GPU is not a fault and must not raise a permanent warning.
        var gpuReported = snapshot.Gpu.IsAvailable || !IsGpuPresent;

        if (snapshot.Cpu.IsAvailable)
        {
            IsDriverActionVisible = false;
            return gpuReported ? string.Empty : T["GpuSensorUnavailable"];
        }

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

        return snapshot.Gpu.IsAvailable ? T["CpuSensorUnavailable"] : T["NoSensor"];
    }

    private string Format(TemperatureReading reading) => reading.Celsius is decimal celsius
        ? $"{celsius:0.#} °C"
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
