using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace ThermoTray;

public sealed class TrayIconService : IDisposable
{
    private const int IconSize = 64;
    private const float DigitsFontSize = 36f;
    private const float PlaceholderFontSize = 30f;
    private const float MinimumFontSize = 22f;
    private const float FontSizeStep = 2f;
    private static readonly Color IconBackground = Color.FromArgb(32, 36, 44);
    private static readonly Color CpuForeground = Color.FromArgb(85, 214, 190);
    private static readonly Color GpuForeground = Color.FromArgb(116, 176, 255);

    private readonly MainViewModel _viewModel;
    private readonly Action _showMainWindow;
    private readonly Action _exitApplication;
    private readonly Forms.NotifyIcon _cpuNotifyIcon;
    private readonly Forms.NotifyIcon _gpuNotifyIcon;
    private string? _cpuIconDigits;
    private string? _gpuIconDigits;
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

        if (everythingChanged || e.PropertyName is nameof(MainViewModel.CpuTemperature) or nameof(MainViewModel.CpuTrayDigits))
        {
            UpdateCpuIcon();
        }

        if (everythingChanged || e.PropertyName is nameof(MainViewModel.GpuTemperature)
            or nameof(MainViewModel.GpuTrayDigits)
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

        var digits = _viewModel.CpuTrayDigits;
        if (!string.Equals(digits, _cpuIconDigits, StringComparison.Ordinal))
        {
            ReplaceIcon(_cpuNotifyIcon, digits, CpuForeground);
            _cpuIconDigits = digits;
        }

        _cpuNotifyIcon.Text = $"CPU: {_viewModel.CpuTemperature}";
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

        var digits = _viewModel.GpuTrayDigits;
        if (!string.Equals(digits, _gpuIconDigits, StringComparison.Ordinal))
        {
            ReplaceIcon(_gpuNotifyIcon, digits, GpuForeground);
            _gpuIconDigits = digits;
        }

        _gpuNotifyIcon.Text = $"GPU: {_viewModel.GpuTemperature}";
    }

    private static void ReplaceIcon(Forms.NotifyIcon notifyIcon, string temperature, Color color)
    {
        var oldIcon = notifyIcon.Icon;
        notifyIcon.Icon = CreateTemperatureIcon(temperature, color);
        oldIcon?.Dispose();
    }

    private static Icon CreateTemperatureIcon(string temperature, Color foreground)
    {
        using var bitmap = new Bitmap(IconSize, IconSize);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(IconBackground);
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        using var font = CreateFittingFont(graphics, temperature, format);
        using var brush = new SolidBrush(foreground);
        graphics.DrawString(temperature, font, brush, new RectangleF(0, 0, IconSize, IconSize), format);

        var iconHandle = bitmap.GetHicon();
        using var temporaryIcon = Icon.FromHandle(iconHandle);
        var icon = (Icon)temporaryIcon.Clone();
        _ = DestroyIcon(iconHandle);
        return icon;
    }

    /// <summary>Picks the largest font that keeps the text inside the icon, so 100 °C is not clipped.</summary>
    private static Font CreateFittingFont(Graphics graphics, string temperature, StringFormat format)
    {
        var startingSize = temperature == TemperatureFormatter.TrayPlaceholder ? PlaceholderFontSize : DigitsFontSize;

        for (var size = startingSize; size > MinimumFontSize; size -= FontSizeStep)
        {
            var font = new Font("Segoe UI", size, FontStyle.Bold, GraphicsUnit.Pixel);
            if (graphics.MeasureString(temperature, font, new SizeF(IconSize * 4f, IconSize * 4f), format).Width <= IconSize)
            {
                return font;
            }

            font.Dispose();
        }

        return new Font("Segoe UI", MinimumFontSize, FontStyle.Bold, GraphicsUnit.Pixel);
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
