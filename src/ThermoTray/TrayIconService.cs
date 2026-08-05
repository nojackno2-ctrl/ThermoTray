using System.ComponentModel;
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
    private readonly Forms.NotifyIcon _cpuNotifyIcon;
    private readonly Forms.NotifyIcon _gpuNotifyIcon;
    private string? _cpuIconKey;
    private string? _gpuIconKey;
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

        // 系統匣區域會將最新註冊的圖示放在最左側，因此先註冊 GPU 再註冊 CPU，
        // 最終排版呈現為 CPU 在左、GPU 在右。
        _gpuNotifyIcon = CreateNotifyIcon();
        _cpuNotifyIcon = CreateNotifyIcon();
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        UpdateCpuIcon();
        UpdateGpuIcon();
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
            or nameof(MainViewModel.CpuUsageTrayDigits))
        {
            UpdateCpuIcon();
        }

        if (everythingChanged || e.PropertyName is nameof(MainViewModel.GpuTemperature)
            or nameof(MainViewModel.GpuTrayDigits)
            or nameof(MainViewModel.GpuUsage)
            or nameof(MainViewModel.GpuUsageTrayDigits)
            or nameof(MainViewModel.IsGpuPresent))
        {
            UpdateGpuIcon();
        }

        if (everythingChanged)
        {
            ReplaceMenu(_cpuNotifyIcon);
            ReplaceMenu(_gpuNotifyIcon);
        }
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

        var iconSize = GetTrayIconSize();
        var usageDigits = _viewModel.CpuUsageTrayDigits;
        var temperatureDigits = _viewModel.CpuTrayDigits;

        var iconKey = $"{iconSize}|{usageDigits}|{temperatureDigits}";
        if (!string.Equals(iconKey, _cpuIconKey, StringComparison.Ordinal))
        {
            ReplaceIcon(_cpuNotifyIcon, usageDigits, temperatureDigits, CpuBrush, iconSize);
            _cpuIconKey = iconKey;
        }

        _cpuNotifyIcon.Text = BuildTooltip("CpuUsage", _viewModel.CpuUsage, "CpuTemperature", _viewModel.CpuTemperature);
    }

    /// <summary>
    /// 更新 GPU 圖示與 Tooltip 提示文字。
    /// </summary>
    private void UpdateGpuIcon()
    {
        if (_disposed)
        {
            return;
        }

        _gpuNotifyIcon.Visible = _viewModel.IsGpuPresent;
        if (!_viewModel.IsGpuPresent)
        {
            return;
        }

        var iconSize = GetTrayIconSize();
        var usageDigits = _viewModel.GpuUsageTrayDigits;
        var temperatureDigits = _viewModel.GpuTrayDigits;

        var iconKey = $"{iconSize}|{usageDigits}|{temperatureDigits}";
        if (!string.Equals(iconKey, _gpuIconKey, StringComparison.Ordinal))
        {
            ReplaceIcon(_gpuNotifyIcon, usageDigits, temperatureDigits, GpuBrush, iconSize);
            _gpuIconKey = iconKey;
        }

        _gpuNotifyIcon.Text = BuildTooltip("GpuUsage", _viewModel.GpuUsage, "GpuTemperature", _viewModel.GpuTemperature);
    }

    /// <summary>
    /// 組合圖示提示（Tooltip）文字。
    /// </summary>
    private string BuildTooltip(string usageLabel, string usage, string temperatureLabel, string temperature) =>
        $"{_viewModel.T[usageLabel]}: {usage} | {_viewModel.T[temperatureLabel]}: {temperature}";

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
        DisposeNotifyIcon(_cpuNotifyIcon);
        DisposeNotifyIcon(_gpuNotifyIcon);
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

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
