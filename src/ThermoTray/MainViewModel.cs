using System.ComponentModel;
using System.Collections.ObjectModel;
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
    private readonly ObservableCollection<GpuViewModel> _gpuItems = [];
    private readonly ReadOnlyObservableCollection<GpuViewModel> _gpus;
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
    private UtilizationReading? _lastCpuUsage;
    private string _cpuTemperature = "…";
    private string _cpuUsage = "…";
    private string _cpuTrayDigits = TemperatureFormatter.TrayPlaceholder;
    private string _cpuUsageTrayDigits = UtilizationFormatter.TrayPlaceholder;
    private string _cpuDeviceName = string.Empty;
    private string _cpuSource = string.Empty;
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
        _settings.GpuTraySettings ??= new();
        _localizer = new Localizer(_settings.Language);
        _settings.Language = _localizer.Language;
        _gpus = new ReadOnlyObservableCollection<GpuViewModel>(_gpuItems);
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
    /// 格式化後的 CPU 使用率顯示字串（例如 "12.5%"）。
    /// </summary>
    public string CpuUsage
    {
        get => _cpuUsage;
        private set => SetField(ref _cpuUsage, value);
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
    /// CPU 使用率系統匣圖示顯示的整數位元數字。
    /// </summary>
    public string CpuUsageTrayDigits
    {
        get => _cpuUsageTrayDigits;
        private set => SetField(ref _cpuUsageTrayDigits, value);
    }

    public string CpuDeviceName
    {
        get => _cpuDeviceName;
        private set => SetField(ref _cpuDeviceName, value);
    }

    /// <summary>
    /// CPU 感測器來源名稱。
    /// </summary>
    public string CpuSource
    {
        get => _cpuSource;
        private set => SetField(ref _cpuSource, value);
    }

    public bool ShowCpuUsageInTray
    {
        get => _settings.ShowCpuUsageInTray;
        set
        {
            if (_settings.ShowCpuUsageInTray == value)
            {
                return;
            }

            _settings.ShowCpuUsageInTray = value;
            SaveSettings();
            OnPropertyChanged();
        }
    }

    public bool ShowCpuTemperatureInTray
    {
        get => _settings.ShowCpuTemperatureInTray;
        set
        {
            if (_settings.ShowCpuTemperatureInTray == value)
            {
                return;
            }

            _settings.ShowCpuTemperatureInTray = value;
            SaveSettings();
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 當前所有 GPU 的獨立顯示狀態。每個項目對應一張實體顯示卡。
    /// </summary>
    public ReadOnlyObservableCollection<GpuViewModel> Gpus => _gpus;

    /// <summary>
    /// 供 UI 測試與 ViewModel 內部同步使用的可變 GPU 集合。
    /// </summary>
    internal ObservableCollection<GpuViewModel> GpuItems => _gpuItems;

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
            _lastCpuUsage = null;
            foreach (var gpu in _gpuItems)
            {
                gpu.UpdateLabels(T["GpuUsage"], T["GpuTemperature"]);
            }
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
            CpuDeviceName = snapshot.CpuTemperature.IsAvailable
                ? snapshot.CpuTemperature.DeviceName
                : snapshot.CpuUsage.DeviceName;
            CpuSource = GetSensorName(snapshot.CpuTemperature.Source);
        }

        if (_lastCpuUsage != snapshot.CpuUsage)
        {
            _lastCpuUsage = snapshot.CpuUsage;
            CpuUsage = FormatUsage(snapshot.CpuUsage);
            CpuUsageTrayDigits = UtilizationFormatter.ToTrayDigits(snapshot.CpuUsage);
            if (!snapshot.CpuTemperature.IsAvailable)
            {
                CpuDeviceName = snapshot.CpuUsage.DeviceName;
                CpuSource = GetSensorName(snapshot.CpuUsage.Source);
            }
        }

        ApplyGpuReadings(snapshot.Gpus);
        UpdateGpuPresence(snapshot.Gpus);
        StatusMessage = GetAvailabilityMessage(snapshot);
    }

    /// <summary>
    /// 將快照中的每張 GPU 同步至對應的 ViewModel，並移除已離開拓撲的裝置。
    /// </summary>
    private void ApplyGpuReadings(IReadOnlyList<GpuReading> readings)
    {
        for (var index = 0; index < readings.Count; index++)
        {
            var reading = readings[index];
            var gpu = FindGpu(reading.Id);
            if (gpu is null)
            {
                gpu = new GpuViewModel(reading.Id);
                gpu.ConfigureTraySettings(GetGpuTraySettings(reading.Id), SaveSettings);
                _gpuItems.Add(gpu);
            }

            gpu.Apply(
                reading,
                index,
                T["GpuUsage"],
                T["GpuTemperature"],
                Format,
                FormatUsage);
        }

        for (var index = _gpuItems.Count - 1; index >= 0; index--)
        {
            if (!ContainsGpu(readings, _gpuItems[index].Id))
            {
                _gpuItems.RemoveAt(index);
            }
        }
    }

    private TrayDisplaySettings GetGpuTraySettings(string id)
    {
        if (!_settings.GpuTraySettings.TryGetValue(id, out var settings) || settings is null)
        {
            settings = new TrayDisplaySettings();
            _settings.GpuTraySettings[id] = settings;
        }

        return settings;
    }

    private static string GetSensorName(string source)
    {
        var separator = source.IndexOf('\u2022');
        return separator >= 0 ? source[(separator + 1)..].Trim() : source;
    }

    /// <summary>
    /// 依拓撲識別碼尋找既有 GPU ViewModel。
    /// </summary>
    private GpuViewModel? FindGpu(string id)
    {
        foreach (var gpu in _gpuItems)
        {
            if (string.Equals(gpu.Id, id, StringComparison.Ordinal))
            {
                return gpu;
            }
        }

        return null;
    }

    /// <summary>
    /// 判斷最新快照是否仍包含指定 GPU。
    /// </summary>
    private static bool ContainsGpu(IReadOnlyList<GpuReading> readings, string id)
    {
        foreach (var reading in readings)
        {
            if (string.Equals(reading.Id, id, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 依據 GPU 讀值動態判斷系統中是否配備可讀取的 GPU。
    /// </summary>
    private void UpdateGpuPresence(IReadOnlyList<GpuReading> readings)
    {
        var anyGpuReading = false;
        foreach (var reading in readings)
        {
            if (reading.IsAvailable)
            {
                anyGpuReading = true;
                break;
            }
        }

        if (anyGpuReading)
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

            return HasAvailableGpu(snapshot.Gpus)
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

        foreach (var gpu in snapshot.Gpus)
        {
            if (!gpu.Temperature.IsAvailable)
            {
                return T["GpuSensorUnavailable"];
            }

            if (!gpu.Usage.IsAvailable)
            {
                return T["GpuUsageUnavailable"];
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// 判斷快照中是否至少有一張 GPU 提供可信的即時讀值。
    /// </summary>
    private static bool HasAvailableGpu(IReadOnlyList<GpuReading> readings)
    {
        foreach (var reading in readings)
        {
            if (reading.IsAvailable)
            {
                return true;
            }
        }

        return false;
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
