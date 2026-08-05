using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ThermoTray;

/// <summary>
/// 主視窗與系統匣使用的單一 GPU 顯示狀態。
/// 每個實例只代表一張實體 GPU，避免雙 GPU 時把不同裝置的讀值混在一起。
/// </summary>
public sealed class GpuViewModel : INotifyPropertyChanged
{
    private string _displayName = string.Empty;
    private string _deviceName = string.Empty;
    private string _usageLabel = string.Empty;
    private string _usage = UtilizationFormatter.TrayPlaceholder;
    private string _temperatureLabel = string.Empty;
    private string _temperature = TemperatureFormatter.TrayPlaceholder;
    private string _source = string.Empty;
    private string _usageTrayDigits = UtilizationFormatter.TrayPlaceholder;
    private string _temperatureTrayDigits = TemperatureFormatter.TrayPlaceholder;
    private TrayDisplaySettings _traySettings = new();
    private Action _saveSettings = static () => { };

    /// <summary>
    /// 建立指定識別碼的 GPU 顯示狀態。
    /// </summary>
    /// <param name="id">拓撲掃描時產生的 GPU 識別碼。</param>
    internal GpuViewModel(string id) => Id = id;

    /// <summary>
    /// GPU 的拓撲識別碼。
    /// </summary>
    public string Id { get; }

    /// <summary>
    /// 顯示用的 GPU 序號標題，例如「GPU 0」或「GPU 1」。
    /// </summary>
    public string DisplayName
    {
        get => _displayName;
        private set => SetField(ref _displayName, value);
    }

    /// <summary>
    /// GPU 裝置名稱，例如 NVIDIA 或 AMD 的完整型號名稱。
    /// </summary>
    public string DeviceName
    {
        get => _deviceName;
        private set => SetField(ref _deviceName, value);
    }

    /// <summary>
    /// GPU 使用率欄位標籤。
    /// </summary>
    public string UsageLabel
    {
        get => _usageLabel;
        private set => SetField(ref _usageLabel, value);
    }

    /// <summary>
    /// GPU 使用率格式化文字。
    /// </summary>
    public string Usage
    {
        get => _usage;
        private set => SetField(ref _usage, value);
    }

    /// <summary>
    /// GPU 溫度欄位標籤。
    /// </summary>
    public string TemperatureLabel
    {
        get => _temperatureLabel;
        private set => SetField(ref _temperatureLabel, value);
    }

    /// <summary>
    /// GPU 溫度格式化文字。
    /// </summary>
    public string Temperature
    {
        get => _temperature;
        private set => SetField(ref _temperature, value);
    }

    /// <summary>
    /// GPU 感測器來源文字，用於卡片底部辨識實際感測器。
    /// </summary>
    public string Source
    {
        get => _source;
        private set => SetField(ref _source, value);
    }

    /// <summary>
    /// 工具列圖示上方顯示的整數使用率。
    /// </summary>
    public string UsageTrayDigits
    {
        get => _usageTrayDigits;
        private set => SetField(ref _usageTrayDigits, value);
    }

    /// <summary>
    /// 工具列圖示下方顯示的整數溫度。
    /// </summary>
    public string TemperatureTrayDigits
    {
        get => _temperatureTrayDigits;
        private set => SetField(ref _temperatureTrayDigits, value);
    }

    public bool ShowUsageInTray
    {
        get => _traySettings.ShowUsage;
        set
        {
            if (_traySettings.ShowUsage == value)
            {
                return;
            }

            _traySettings.ShowUsage = value;
            _saveSettings();
            OnPropertyChanged();
        }
    }

    public bool ShowTemperatureInTray
    {
        get => _traySettings.ShowTemperature;
        set
        {
            if (_traySettings.ShowTemperature == value)
            {
                return;
            }

            _traySettings.ShowTemperature = value;
            _saveSettings();
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// GPU 讀值或語言設定改變時觸發的屬性通知。
    /// </summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// 套用單一 GPU 的讀值與本地化欄位標籤。
    /// </summary>
    /// <param name="reading">同一張 GPU 配對後的溫度與使用率。</param>
    /// <param name="index">目前拓撲中的 GPU 序號。</param>
    /// <param name="usageLabel">本地化使用率標籤。</param>
    /// <param name="temperatureLabel">本地化溫度標籤。</param>
    /// <param name="formatTemperature">溫度格式化委派。</param>
    /// <param name="formatUsage">使用率格式化委派。</param>
    internal void Apply(
        GpuReading reading,
        int index,
        string usageLabel,
        string temperatureLabel,
        Func<TemperatureReading, string> formatTemperature,
        Func<UtilizationReading, string> formatUsage)
    {
        DisplayName = $"GPU {index}";
        DeviceName = reading.Name;
        UsageLabel = usageLabel;
        Usage = formatUsage(reading.Usage);
        TemperatureLabel = temperatureLabel;
        Temperature = formatTemperature(reading.Temperature);
        Source = GetSensorName(reading.Temperature.IsAvailable
            ? reading.Temperature.Source
            : reading.Usage.Source);
        UsageTrayDigits = UtilizationFormatter.ToTrayDigits(reading.Usage);
        TemperatureTrayDigits = TemperatureFormatter.ToTrayDigits(reading.Temperature);
    }

    private static string GetSensorName(string source)
    {
        var separator = source.IndexOf('\u2022');
        return separator >= 0 ? source[(separator + 1)..].Trim() : source;
    }

    internal void ConfigureTraySettings(TrayDisplaySettings settings, Action saveSettings)
    {
        _traySettings = settings;
        _saveSettings = saveSettings;
        OnPropertyChanged(nameof(ShowUsageInTray));
        OnPropertyChanged(nameof(ShowTemperatureInTray));
    }

    /// <summary>
    /// 只更新切換語言後的欄位標籤，保留最近一次硬體讀值。
    /// </summary>
    internal void UpdateLabels(string usageLabel, string temperatureLabel)
    {
        UsageLabel = usageLabel;
        TemperatureLabel = temperatureLabel;
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
