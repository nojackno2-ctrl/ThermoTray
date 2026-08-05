using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;

namespace ThermoTray;

/// <summary>
/// ThermoTray 主視窗後端程式碼。
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>
    /// 標記是否為真正的關閉請求（由選單「結束」觸發），而非點擊右上角 X 按鈕隱藏至系統匣。
    /// </summary>
    private bool _reallyClosing;

    /// <summary>
    /// 初始化 MainWindow 的新實例。
    /// </summary>
    public MainWindow()
    {
        InitializeComponent();
        StateChanged += OnStateChanged;
    }

    /// <summary>
    /// 處理視窗關閉事件。若設定為「關閉時縮小至系統匣」，則攔截關閉並將視窗隱藏。
    /// </summary>
    /// <param name="e">包含取消選項的關閉事件引數。</param>
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

    /// <summary>
    /// 處理 PawnIO 驅動程式下載超連結點擊事件，使用系統預設瀏覽器開啟連結。
    /// </summary>
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
            // 若系統未關聯預設瀏覽器，彈出提示對話方塊並顯示網址供使用者手動複製
            var message = (DataContext as MainViewModel)?.T["OpenLinkError"] ?? string.Empty;
            System.Windows.MessageBox.Show(this, $"{message}{Environment.NewLine}{address}", Title);
        }
    }

    /// <summary>
    /// 處理視窗狀態改變事件。當使用者將視窗最小化時，自動將其隱藏至系統匣。
    /// </summary>
    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            Hide();
        }
    }
}
