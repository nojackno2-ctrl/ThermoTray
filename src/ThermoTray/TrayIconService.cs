using System.ComponentModel;
using System.Collections.Specialized;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace ThermoTray;

/// <summary>
/// 負責管理 Windows 系統工作列圖示 (NotifyIcon) 之繪製、更新、選單與事件處理的服務類別。
/// 為 CPU 與 GPU 分別提供獨立的雙行（使用率與溫度）圖示。
/// </summary>
public sealed class TrayIconService : IDisposable
{
    /// <summary>
    /// 字型向量外框的 Em 大小。外框會被自動縮放以精確填滿文字繪製區域。
    /// </summary>
    private const float OutlineEmSize = 64f;

    private static readonly FontFamily IconFontFamily = new("Segoe UI");

    /// <summary>
    /// 使用 GenericTypographic 格式，取消字體預設內距，以真實字形外框作為繪製邊界。
    /// </summary>
    private static readonly StringFormat OutlineFormat = StringFormat.GenericTypographic;

    // GDI+ 畫筆快取物件，避免每次紅繪時重複建立與銷毀造成控制代碼浪費
    private static readonly SolidBrush UsageBrush = new(Color.FromArgb(245, 247, 250));
    private static readonly SolidBrush CpuBrush = new(Color.FromArgb(85, 214, 190));
    private static readonly SolidBrush GpuBrush = new(Color.FromArgb(116, 176, 255));

    private readonly MainViewModel _viewModel;
    private readonly Action _showMainWindow;
    private readonly Action _exitApplication;
    private Forms.NotifyIcon _cpuNotifyIcon;
    private readonly List<GpuTrayIcon> _gpuIcons = [];
    private string? _cpuIconKey;
    private bool _disposed;

    /// <summary>
    /// 初始化 TrayIconService 的新實例。
    /// </summary>
    /// <param name="viewModel">提供狀態資料的 View Model。</param>
    /// <param name="showMainWindow">顯示主視窗的回調委派。</param>
    /// <param name="exitApplication">結束應用程式的回調委派。</param>
    public TrayIconService(MainViewModel viewModel, Action showMainWindow, Action exitApplication)
    {
        _viewModel = viewModel;
        _showMainWindow = showMainWindow;
        _exitApplication = exitApplication;

        _cpuNotifyIcon = CreateNotifyIcon();
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        ((INotifyCollectionChanged)_viewModel.Gpus).CollectionChanged += OnGpuCollectionChanged;
        RebuildGpuIcons(restoreCpuOrder: false);
        UpdateCpuIcon();
        UpdateGpuIcons();
    }

    /// <summary>
    /// 建立 NotifyIcon 控制項並綁定雙擊事件與快顯功能表。
    /// </summary>
    private Forms.NotifyIcon CreateNotifyIcon()
    {
        var icon = new Forms.NotifyIcon
        {
            Visible = true,
            ContextMenuStrip = BuildMenu(),
        };
        icon.DoubleClick += (_, _) => _showMainWindow();
        return icon;
    }

    /// <summary>
    /// 建立圖示右鍵快顯功能表 (Open / Exit)。
    /// </summary>
    private Forms.ContextMenuStrip BuildMenu()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(new Forms.ToolStripMenuItem(_viewModel.T["Open"], null, (_, _) => _showMainWindow()));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(new Forms.ToolStripMenuItem(_viewModel.T["Exit"], null, (_, _) => _exitApplication()));
        return menu;
    }

    /// <summary>
    /// 監聽 ViewModel 屬性變更，當讀值或語言改變時更新對應圖示。
    /// </summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        var everythingChanged = string.IsNullOrEmpty(e.PropertyName);

        if (everythingChanged || e.PropertyName is nameof(MainViewModel.CpuTemperature)
            or nameof(MainViewModel.CpuTrayDigits)
            or nameof(MainViewModel.CpuUsage)
            or nameof(MainViewModel.CpuUsageTrayDigits)
            or nameof(MainViewModel.CpuDeviceName)
            or nameof(MainViewModel.ShowCpuUsageInTray)
            or nameof(MainViewModel.ShowCpuTemperatureInTray))
        {
            UpdateCpuIcon();
        }

        if (everythingChanged || e.PropertyName is nameof(MainViewModel.IsGpuPresent))
        {
            UpdateGpuIcons();
        }

        if (everythingChanged)
        {
            ReplaceMenu(_cpuNotifyIcon);
            foreach (var entry in _gpuIcons)
            {
                ReplaceMenu(entry.NotifyIcon);
            }
        }
    }

    /// <summary>
    /// 當 GPU 清單增減時重建對應的獨立系統匣圖示。
    /// </summary>
    private void OnGpuCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        RebuildGpuIcons(restoreCpuOrder: true);
    }

    /// <summary>
    /// 當單一 GPU 的使用率、溫度或語言標籤更新時刷新該 GPU 圖示。
    /// </summary>
    private void OnGpuPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_disposed || sender is not GpuViewModel gpu)
        {
            return;
        }

        foreach (var entry in _gpuIcons)
        {
            if (ReferenceEquals(entry.Gpu, gpu))
            {
                UpdateGpuIcon(entry);
                return;
            }
        }
    }

    /// <summary>
    /// 依目前 GPU 清單建立每張 GPU 的獨立圖示。
    /// </summary>
    private void RebuildGpuIcons(bool restoreCpuOrder)
    {
        foreach (var entry in _gpuIcons)
        {
            entry.Gpu.PropertyChanged -= OnGpuPropertyChanged;
            DisposeNotifyIcon(entry.NotifyIcon);
        }

        _gpuIcons.Clear();
        foreach (var gpu in _viewModel.Gpus)
        {
            gpu.PropertyChanged += OnGpuPropertyChanged;
            _gpuIcons.Add(new GpuTrayIcon(gpu, CreateNotifyIcon()));
        }

        if (restoreCpuOrder && _gpuIcons.Count > 0)
        {
            // Windows 通常把較晚註冊的圖示放在左側；GPU 清單初次出現後重新註冊 CPU，
            // 讓預設順序維持 CPU 在左、各張 GPU 依序在右（使用者手動排列仍由 Windows 記憶）。
            var oldCpuIcon = _cpuNotifyIcon;
            _cpuNotifyIcon = CreateNotifyIcon();
            DisposeNotifyIcon(oldCpuIcon);
        }

        UpdateCpuIcon();
        UpdateGpuIcons();
    }

    /// <summary>
    /// 更新右鍵功能表項目（如切換語言時更新文字）。
    /// </summary>
    private void ReplaceMenu(Forms.NotifyIcon notifyIcon)
    {
        var oldMenu = notifyIcon.ContextMenuStrip;
        notifyIcon.ContextMenuStrip = BuildMenu();
        oldMenu?.Dispose();
    }

    /// <summary>
    /// 更新 CPU 圖示與 Tooltip 提示文字。
    /// </summary>
    private void UpdateCpuIcon()
    {
        if (_disposed)
        {
            return;
        }

        var showUsage = _viewModel.ShowCpuUsageInTray;
        var showTemperature = _viewModel.ShowCpuTemperatureInTray;
        _cpuNotifyIcon.Visible = showUsage || showTemperature;
        if (!_cpuNotifyIcon.Visible)
        {
            return;
        }

        var iconSize = GetTrayIconSize();
        var usageDigits = showUsage ? _viewModel.CpuUsageTrayDigits : string.Empty;
        var temperatureDigits = showTemperature ? _viewModel.CpuTrayDigits : string.Empty;

        var iconKey = $"{iconSize}|{showUsage}|{usageDigits}|{showTemperature}|{temperatureDigits}";
        if (!string.Equals(iconKey, _cpuIconKey, StringComparison.Ordinal))
        {
            ReplaceIcon(_cpuNotifyIcon, usageDigits, temperatureDigits, CpuBrush, iconSize);
            _cpuIconKey = iconKey;
        }

        _cpuNotifyIcon.Text = BuildTooltip(
            _viewModel.CpuDeviceName,
            showUsage,
            _viewModel.T["CpuUsage"],
            _viewModel.CpuUsage,
            showTemperature,
            _viewModel.T["CpuTemperature"],
            _viewModel.CpuTemperature);
    }

    /// <summary>
    /// 更新所有 GPU 圖示與各自的 Tooltip 提示文字。
    /// </summary>
    private void UpdateGpuIcons()
    {
        if (_disposed)
        {
            return;
        }

        foreach (var entry in _gpuIcons)
        {
            UpdateGpuIcon(entry);
        }
    }

    /// <summary>
    /// 更新單一 GPU 圖示與 Tooltip 提示文字。
    /// </summary>
    private void UpdateGpuIcon(GpuTrayIcon entry)
    {
        var showUsage = entry.Gpu.ShowUsageInTray;
        var showTemperature = entry.Gpu.ShowTemperatureInTray;
        entry.NotifyIcon.Visible = _viewModel.IsGpuPresent && (showUsage || showTemperature);
        if (!entry.NotifyIcon.Visible)
        {
            return;
        }

        var iconSize = GetTrayIconSize();
        var usageDigits = showUsage ? entry.Gpu.UsageTrayDigits : string.Empty;
        var temperatureDigits = showTemperature ? entry.Gpu.TemperatureTrayDigits : string.Empty;

        var iconKey = $"{iconSize}|{showUsage}|{usageDigits}|{showTemperature}|{temperatureDigits}";
        if (!string.Equals(iconKey, entry.IconKey, StringComparison.Ordinal))
        {
            ReplaceIcon(entry.NotifyIcon, usageDigits, temperatureDigits, GpuBrush, iconSize);
            entry.IconKey = iconKey;
        }

        entry.NotifyIcon.Text = BuildGpuTooltip(entry.Gpu, showUsage, showTemperature);
    }

    /// <summary>
    /// 組合圖示提示（Tooltip）文字。
    /// </summary>
    private static string BuildTooltip(
        string deviceName,
        bool showUsage,
        string usageLabel,
        string usage,
        bool showTemperature,
        string temperatureLabel,
        string temperature)
    {
        var tooltip = string.IsNullOrWhiteSpace(deviceName) ? "CPU" : deviceName;
        if (showUsage)
        {
            tooltip += $" | {usageLabel}: {usage}";
        }

        if (showTemperature)
        {
            tooltip += $" | {temperatureLabel}: {temperature}";
        }

        return TrimTooltip(tooltip);
    }

    /// <summary>
    /// 組合包含 GPU 序號與裝置名稱的獨立 Tooltip，避免雙 GPU 時無法辨識圖示所屬裝置。
    /// </summary>
    private static string BuildGpuTooltip(GpuViewModel gpu, bool showUsage, bool showTemperature)
    {
        return BuildTooltip(
            $"{gpu.DisplayName}: {gpu.DeviceName}",
            showUsage,
            gpu.UsageLabel,
            gpu.Usage,
            showTemperature,
            gpu.TemperatureLabel,
            gpu.Temperature);
    }

    private static string TrimTooltip(string tooltip) =>
        tooltip.Length <= 127 ? tooltip : $"{tooltip[..124]}...";

    /// <summary>
    /// 依據系統縮放比例取得工作列圖示的目標像素尺寸。
    /// </summary>
    private static int GetTrayIconSize()
    {
        var reported = Forms.SystemInformation.SmallIconSize;
        var side = Math.Min(reported.Width, reported.Height);
        return side >= TrayIconLayout.MinimumIconSize ? side : TrayIconLayout.MinimumIconSize;
    }

    /// <summary>
    /// 重新生成 Icon 並替換既有圖示，並妥善釋放舊 Icon 物件。
    /// </summary>
    private static void ReplaceIcon(
        Forms.NotifyIcon notifyIcon,
        string usageDigits,
        string temperatureDigits,
        Brush temperatureBrush,
        int iconSize)
    {
        var oldIcon = notifyIcon.Icon;
        notifyIcon.Icon = CreateHardwareIcon(usageDigits, temperatureDigits, temperatureBrush, iconSize);
        oldIcon?.Dispose();
    }

    /// <summary>
    /// 建立包含位元圖與 Win32 Icon 控制代碼的硬體圖示，確保原生 HICON 被確實銷毀。
    /// </summary>
    private static Icon CreateHardwareIcon(string usageDigits, string temperatureDigits, Brush temperatureBrush, int iconSize)
    {
        using var bitmap = CreateIconBitmap(usageDigits, temperatureDigits, temperatureBrush, iconSize);
        var iconHandle = bitmap.GetHicon();

        try
        {
            using var temporaryIcon = Icon.FromHandle(iconHandle);
            return (Icon)temporaryIcon.Clone();
        }
        finally
        {
            // Clone 建立獨立 Icon 物件後，必須強制銷毀原生的 Win32 HICON控制代碼，防止記憶體與控制代碼洩漏
            _ = DestroyIcon(iconHandle);
        }
    }

    /// <summary>
    /// 繪製包含使用率（上方）與溫度（下方）的雙行文字 Icon 位元圖。
    /// </summary>
    internal static Bitmap CreateIconBitmap(string usageDigits, string temperatureDigits, Brush temperatureBrush, int iconSize)
    {
        var canvasSize = TrayIconLayout.GetCanvasSize(iconSize);
        var (usageLine, temperatureLine) = TrayIconLayout.GetLines(canvasSize);

        using var canvas = new Bitmap(canvasSize, canvasSize);
        using (var graphics = Graphics.FromImage(canvas))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);
            DrawDigits(graphics, usageDigits, UsageBrush, usageLine);
            DrawDigits(graphics, temperatureDigits, temperatureBrush, temperatureLine);
        }

        var bitmap = new Bitmap(iconSize, iconSize);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.Clear(Color.Transparent);
            graphics.DrawImage(canvas, new Rectangle(0, 0, iconSize, iconSize));
        }

        return bitmap;
    }

    /// <summary>
    /// 精確縮放向量字形路徑 (GraphicsPath)，使其完美填滿目標行矩形範圍，防止數字超出邊界或被裁切。
    /// </summary>
    private static void DrawDigits(Graphics graphics, string digits, Brush brush, RectangleF line)
    {
        using var path = new GraphicsPath();
        path.AddString(digits, IconFontFamily, (int)FontStyle.Bold, OutlineEmSize, PointF.Empty, OutlineFormat);

        var ink = path.GetBounds();
        if (ink.Width <= 0f || ink.Height <= 0f)
        {
            return;
        }

        var scale = Math.Min(line.Width / ink.Width, line.Height / ink.Height);
        using var transform = new Matrix();
        transform.Translate(line.X + (line.Width / 2f), line.Y + (line.Height / 2f));
        transform.Scale(scale, scale);
        transform.Translate(-(ink.X + (ink.Width / 2f)), -(ink.Y + (ink.Height / 2f)));
        path.Transform(transform);

        graphics.FillPath(brush, path);
    }

    /// <summary>
    /// 釋放系統匣圖示資源與解綁事件。
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        ((INotifyCollectionChanged)_viewModel.Gpus).CollectionChanged -= OnGpuCollectionChanged;
        DisposeNotifyIcon(_cpuNotifyIcon);
        foreach (var entry in _gpuIcons)
        {
            entry.Gpu.PropertyChanged -= OnGpuPropertyChanged;
            DisposeNotifyIcon(entry.NotifyIcon);
        }

        _gpuIcons.Clear();
    }

    /// <summary>
    /// 隱藏並處置指定的 NotifyIcon 物件。
    /// </summary>
    private static void DisposeNotifyIcon(Forms.NotifyIcon notifyIcon)
    {
        notifyIcon.Visible = false;
        notifyIcon.Icon?.Dispose();
        notifyIcon.ContextMenuStrip?.Dispose();
        notifyIcon.Dispose();
    }

    /// <summary>
    /// 保存單一 GPU 的 ViewModel、NotifyIcon 與目前圖示快取鍵。
    /// </summary>
    private sealed class GpuTrayIcon(GpuViewModel gpu, Forms.NotifyIcon notifyIcon)
    {
        public GpuViewModel Gpu { get; } = gpu;

        public Forms.NotifyIcon NotifyIcon { get; } = notifyIcon;

        public string? IconKey { get; set; }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
