using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace ThermoTray;

public sealed class TrayIconService : IDisposable
{
    /// <summary>
    /// Em size for the glyph outlines. The outline is scaled to whatever line it has to fill, so this
    /// only decides how much detail that outline carries into the scaling.
    /// </summary>
    private const float OutlineEmSize = 64f;

    private static readonly FontFamily IconFontFamily = new("Segoe UI");

    /// <summary>Typographic layout adds no padding around the glyphs, so the outline is the ink itself.</summary>
    private static readonly StringFormat OutlineFormat = StringFormat.GenericTypographic;

    // Every drawing object above and below owns a GDI+ handle and outlives each icon drawn with it. An
    // icon is redrawn whenever its digits change, so creating them per redraw would churn handles all
    // day for a fixed and very small set of objects. Only the UI thread touches them.
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

    public TrayIconService(MainViewModel viewModel, Action showMainWindow, Action exitApplication)
    {
        _viewModel = viewModel;
        _showMainWindow = showMainWindow;
        _exitApplication = exitApplication;
        // The notification area puts the most recently registered icon leftmost, so register
        // GPU first to end up with CPU on the left and GPU on the right.
        _gpuNotifyIcon = CreateNotifyIcon();
        _cpuNotifyIcon = CreateNotifyIcon();
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        UpdateCpuIcon();
        UpdateGpuIcon();
    }

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

    private Forms.ContextMenuStrip BuildMenu()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(new Forms.ToolStripMenuItem(_viewModel.T["Open"], null, (_, _) => _showMainWindow()));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(new Forms.ToolStripMenuItem(_viewModel.T["Exit"], null, (_, _) => _exitApplication()));
        return menu;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // A null or empty name is the conventional "everything changed" signal.
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

    private void ReplaceMenu(Forms.NotifyIcon notifyIcon)
    {
        var oldMenu = notifyIcon.ContextMenuStrip;
        notifyIcon.ContextMenuStrip = BuildMenu();
        oldMenu?.Dispose();
    }

    private void UpdateCpuIcon()
    {
        if (_disposed)
        {
            return;
        }

        var iconSize = GetTrayIconSize();
        var usageDigits = _viewModel.CpuUsageTrayDigits;
        var temperatureDigits = _viewModel.CpuTrayDigits;

        // The size belongs in the key so that a change of display scale redraws at the new size.
        var iconKey = $"{iconSize}|{usageDigits}|{temperatureDigits}";
        if (!string.Equals(iconKey, _cpuIconKey, StringComparison.Ordinal))
        {
            ReplaceIcon(_cpuNotifyIcon, usageDigits, temperatureDigits, CpuBrush, iconSize);
            _cpuIconKey = iconKey;
        }

        _cpuNotifyIcon.Text = BuildTooltip("CpuUsage", _viewModel.CpuUsage, "CpuTemperature", _viewModel.CpuTemperature);
    }

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

    private string BuildTooltip(string usageLabel, string usage, string temperatureLabel, string temperature) =>
        $"{_viewModel.T[usageLabel]}: {usage} | {_viewModel.T[temperatureLabel]}: {temperature}";

    /// <summary>
    /// The notification area's own icon metric, which follows the display scale. Drawing for this size
    /// rather than a fixed one is what keeps Windows from resampling the finished icon a second time.
    /// </summary>
    private static int GetTrayIconSize()
    {
        var reported = Forms.SystemInformation.SmallIconSize;
        var side = Math.Min(reported.Width, reported.Height);
        return side >= TrayIconLayout.MinimumIconSize ? side : TrayIconLayout.MinimumIconSize;
    }

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

    private static Icon CreateHardwareIcon(string usageDigits, string temperatureDigits, Brush temperatureBrush, int iconSize)
    {
        using var bitmap = CreateIconBitmap(usageDigits, temperatureDigits, temperatureBrush, iconSize);
        var iconHandle = bitmap.GetHicon();
        using var temporaryIcon = Icon.FromHandle(iconHandle);
        var icon = (Icon)temporaryIcon.Clone();
        _ = DestroyIcon(iconHandle);
        return icon;
    }

    /// <summary>Draws unitless digits, utilization above temperature, as large as the icon allows.</summary>
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
    /// Scales the glyph outline itself so the digits fill their line exactly. Sizing a font instead can
    /// only bound the advance width, which says nothing about how tall the digits are or whether they
    /// still fit once the smallest allowed size is reached, and anything that does not fit is cut off
    /// rather than shrunk: a reading of 100 was drawn, and read, as 10.
    /// </summary>
    private static void DrawDigits(Graphics graphics, string digits, Brush brush, RectangleF line)
    {
        using var path = new GraphicsPath();
        path.AddString(digits, IconFontFamily, (int)FontStyle.Bold, OutlineEmSize, PointF.Empty, OutlineFormat);

        // A string of nothing but spaces, and a font missing every glyph, both produce an empty outline.
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
