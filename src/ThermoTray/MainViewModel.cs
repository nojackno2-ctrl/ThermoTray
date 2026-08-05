using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Threading;

namespace ThermoTray;

/// <summary>
/// 主視窗與系統匣圖示的主要 View Model (MVVM 架構)。
/// 負責感測器定期輪詢、視窗顯示與隱藏狀態切換、開機啟動設定同步及多語系處理。
/// </summary>
public sealed class MainViewModel : INotifyPropertyChanged
{
    /// <summary>
    /// 當主視窗顯示時的採樣週期（1 秒）。
    /// </summary>
    private static readonly TimeSpan VisiblePollingInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// 當主視窗隱藏至系統匣時的採樣週期（2 秒）。
    /// 在背景執行時降低採樣頻率可有效減半 CPU 與系統資源開銷。
    /// </summary>
    private static readonly TimeSpan HiddenPollingInterval = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(5);
    private const long DriverProbeIntervalMilliseconds = 30_000;

    /// <summary>
    /// 判定 GPU 確實不存在前允許的連續無讀值次數（緩衝次數）。
    /// </summary>
    private const int MissingGpuGraceSamples = 5;

    private readonly HardwareSensorService _sensorService;
    private readonly SettingsService _settingsService;
    private readonly StartupService _startupService;
    private readonly AppSettings _settings;
    private readonly Localizer _localizer;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly object _startupGate = new();
    private SensorDriverStatus _driverStatus = SensorDriverStatus.Query();
    private bool _isDriverActionVisible;
    private bool _isGpuPresent = true;
    private bool _gpuEverReported;
    private int _gpuMissingSamples;
    private bool _isStopped;
    private int _startupRequestVersion;

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

    /// <summary>
    /// 初始化 MainViewModel 的新實例。
    /// </summary>
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

    /// <summary>
    /// 本地化服務實例。
    /// </summary>
    public Localizer T => _localizer;

    /// <summary>
    /// 格式化後顯示於視窗標題列的產品版本號字串 (如 "v1.1.3")。
    /// </summary>
    public string Version { get; } = FormatVersion(typeof(MainViewModel).Assembly.GetName().Version);

    /// <summary>
    /// 格式化後的 CPU 溫度顯示字串（例如 "45.2 °C"）。
    /// </summary>
    public string CpuTemperature
    {
        get => _cpuTemperature;
        private set => SetField(ref _cpuTemperature, value);
    }

    /// <summary>
    /// 格式化後的 GPU 溫度顯示字串（例如 "55.0 °C"）。
    /// </summary>
    public string GpuTemperature
    {
        get => _gpuTemperature;
        private set => SetField(ref _gpuTemperature, value);
    }

    /// <summary>
    /// 格式化後的 CPU 使用率顯示字串（例如 "12.5%"）。
    /// </summary>
    public string CpuUsage
    {
        get => _cpuUsage;
        private set => SetField(ref _cpuUsage, value);
    }

    /// <summary>
    /// 格式化後的 GPU 使用率顯示字串（例如 "3.0%"）。
    /// </summary>
    public string GpuUsage
    {
        get => _gpuUsage;
        private set => SetField(ref _gpuUsage, value);
    }

    /// <summary>
    /// CPU 系統匣圖示顯示的整數位元數字。
    /// </summary>
    public string CpuTrayDigits
    {
        get => _cpuTrayDigits;
        private set => SetField(ref _cpuTrayDigits, value);
    }

    /// <summary>
    /// GPU 系統匣圖示顯示的整數位元數字。
    /// </summary>
    public string GpuTrayDigits
    {
        get => _gpuTrayDigits;
        private set => SetField(ref _gpuTrayDigits, value);
    }

    /// <summary>
    /// CPU 使用率系統匣圖示顯示的整數位元數字。
    /// </summary>
    public string CpuUsageTrayDigits
    {
        get => _cpuUsageTrayDigits;
        private set => SetField(ref _cpuUsageTrayDigits, value);
    }

    /// <summary>
    /// GPU 使用率系統匣圖示顯示的整數位元數字。
    /// </summary>
    public string GpuUsageTrayDigits
    {
        get => _gpuUsageTrayDigits;
        private set => SetField(ref _gpuUsageTrayDigits, value);
    }

    /// <summary>
    /// CPU 感測器來源名稱。
    /// </summary>
    public string CpuSource
    {
        get => _cpuSource;
        private set => SetField(ref _cpuSource, value);
    }

    /// <summary>
    /// GPU 感測器來源名稱。
    /// </summary>
    public string GpuSource
    {
        get => _gpuSource;
        private set => SetField(ref _gpuSource, value);
    }

    /// <summary>
    /// 狀態欄訊息文字。
    /// </summary>
    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetField(ref _statusMessage, value);
    }

    /// <summary>
    /// 是否顯示 PawnIO 驅動程式下載與提醒提示按鈕。
    /// </summary>
    public bool IsDriverActionVisible
    {
        get => _isDriverActionVisible;
        private set => SetField(ref _isDriverActionVisible, value);
    }

    /// <summary>
    /// 取得一個值，表示當前系統是否存在可讀取的 GPU。若經過多次採樣均無 GPU 感測器，則隱藏 GPU 圖示。
    /// </summary>
    public bool IsGpuPresent
    {
        get => _isGpuPresent;
        private set => SetField(ref _isGpuPresent, value);
    }

    /// <summary>
    /// PawnIO 驅動程式下載連結。
    /// </summary>
    public string DriverDownloadUrl => SensorDriverStatus.DownloadUrl;

    /// <summary>
    /// 取得或設定是否隨 Windows 啟動。
    /// </summary>
    public bool StartWithWindows
    {
        get => _settings.StartWithWindows;
        set
        {
            if (_settings.StartWithWindows == value)
            {
                return;
            }

            Interlocked.Increment(ref _startupRequestVersion);
            if (!TrySetStartupEnabled(value))
            {
                StatusMessage = T["StartupError"];
                OnPropertyChanged();
                return;
            }

            _settings.StartWithWindows = value;
            SaveSettings();
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 取得或設定關閉主視窗時是否縮小至系統匣。
    /// </summary>
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

    /// <summary>
    /// 取得或設定當前介面語言。
    /// </summary>
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

    /// <summary>
    /// 啟動感測器背景輪詢與開機啟動設定校驗。
    /// </summary>
    public void Start()
    {
        if (_isStopped || _pollingCancellation is not null)
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        _pollingCancellation = cancellation;

        _pollingTask = Task.Run(() => PollAsync(cancellation.Token), CancellationToken.None);
        _ = Task.Run(ReconcileStartupSetting);
    }

    /// <summary>
    /// 設定當前主視窗是否顯示，據此動態調整輪詢頻率（顯示時 1s，隱藏至系統匣時 2s）。
    /// </summary>
    public void SetWindowVisible(bool isVisible) => _isWindowVisible = isVisible;

    /// <summary>
    /// 停止輪詢並處置感測器服務。
    /// </summary>
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
            return;
        }

        cancellation.Dispose();
        _sensorService.Dispose();
    }

    /// <summary>
    /// 等待輪詢工作退出。
    /// </summary>
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
            return true;
        }
    }

    /// <summary>
    /// 背景輪詢迴圈，依指定頻率呼叫感測器服務並更新 UI。
    /// </summary>
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
        }
    }

    private TimeSpan CurrentPollingInterval => GetPollingInterval(_isWindowVisible);

    /// <summary>
    /// 將 Version 物件格式化為 "v1.1.3" 格式字串。
    /// </summary>
    internal static string FormatVersion(Version? version) => version is null
        ? string.Empty
        : $"v{version.Major}.{version.Minor}.{Math.Max(version.Build, 0)}";

    /// <summary>
    /// 依據視窗顯示狀態取得採樣間隔（可測試函數）。
    /// </summary>
    internal static TimeSpan GetPollingInterval(bool isWindowVisible) =>
        isWindowVisible ? VisiblePollingInterval : HiddenPollingInterval;

    /// <summary>
    /// 非同步分派 UI 委派動作至 Dispatcher。
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
        }
    }

    /// <summary>
    /// 將讀取到的硬體快照套用至各 View Model 屬性與系統匣字串。
    /// </summary>
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

    /// <summary>
    /// 依據 GPU 讀值動態判斷系統中是否配備可讀取的 GPU。
    /// </summary>
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
            return;
        }

        if (_gpuMissingSamples < MissingGpuGraceSamples)
        {
            _gpuMissingSamples++;
        }

        IsGpuPresent = _gpuMissingSamples < MissingGpuGraceSamples;
    }

    /// <summary>
    /// 依據硬體快照回報適當狀態與錯誤提醒訊息。
    /// </summary>
    private string GetAvailabilityMessage(HardwareSnapshot snapshot)
    {
        if (!snapshot.CpuTemperature.IsAvailable)
        {
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
        }
    }

    private bool TrySetStartupEnabled(bool enabled)
    {
        lock (_startupGate)
        {
            try
            {
                _startupService.SetEnabled(enabled);
                return true;
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException
                or System.Security.SecurityException
                or IOException
                or InvalidOperationException)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// 校驗並修復開機啟動設定。
    /// </summary>
    private void ReconcileStartupSetting()
    {
        if (!_settings.StartWithWindows)
        {
            return;
        }

        var requestVersion = Volatile.Read(ref _startupRequestVersion);
        var succeeded = false;
        lock (_startupGate)
        {
            if (requestVersion != Volatile.Read(ref _startupRequestVersion) || !_settings.StartWithWindows)
            {
                return;
            }

            if (_startupService.IsUpToDate())
            {
                return;
            }

            succeeded = TrySetStartupEnabledCore(true);
        }

        if (succeeded || requestVersion != Volatile.Read(ref _startupRequestVersion))
        {
            return;
        }

        Post(() =>
        {
            if (requestVersion != Volatile.Read(ref _startupRequestVersion) || !_settings.StartWithWindows)
            {
                return;
            }

            _settings.StartWithWindows = false;
            SaveSettings();
            OnPropertyChanged(nameof(StartWithWindows));
            StatusMessage = T["StartupError"];
        });
    }

    private bool TrySetStartupEnabledCore(bool enabled)
    {
        try
        {
            _startupService.SetEnabled(enabled);
            return true;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException
            or System.Security.SecurityException
            or IOException
            or InvalidOperationException)
        {
            return false;
        }
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
