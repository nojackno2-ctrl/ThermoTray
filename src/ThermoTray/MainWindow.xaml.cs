using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;

namespace ThermoTray;

public partial class MainWindow : Window
{
    private bool _reallyClosing;

    public MainWindow()
    {
        InitializeComponent();
        StateChanged += OnStateChanged;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_reallyClosing && DataContext is MainViewModel { HideWhenClosed: true })
        {
            e.Cancel = true;
            Hide();
        }
        else
        {
            _reallyClosing = true;
        }

        base.OnClosing(e);
    }

    private void OnDriverLinkRequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        e.Handled = true;
        var address = e.Uri.AbsoluteUri;

        try
        {
            using var browser = Process.Start(new ProcessStartInfo(address) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            // No browser is registered, so show the address for the user to copy instead of crashing.
            var message = (DataContext as MainViewModel)?.T["OpenLinkError"] ?? string.Empty;
            System.Windows.MessageBox.Show(this, $"{message}{Environment.NewLine}{address}", Title);
        }
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            Hide();
        }
    }
}
