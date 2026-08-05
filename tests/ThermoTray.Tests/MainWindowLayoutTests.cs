using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Xunit;

namespace ThermoTray.Tests;

/// <summary>
/// 主視窗 WPF UI 幾何佈局與元件視覺呈現單元測試。
/// 確保卡片內容文字、標題版本號與底部設定選項均正常顯示且未遭裁切。
/// </summary>
public sealed class MainWindowLayoutTests : IClassFixture<MainWindowFixture>
{
    private readonly MainWindowFixture _fixture;

    public MainWindowLayoutTests(MainWindowFixture fixture) => _fixture = fixture;

    /// <summary>
    /// 驗證 CPU 與 GPU 卡片元件內容完整顯示，末行文字未被 Border 裁切。
    /// </summary>
    [Fact]
    public void TheCardsShowTheirWholeContents() =>
        _fixture.Invoke(window =>
        {
            AssertContentFits(window.CpuCard, "CPU");
            AssertContentFits(window.GpuCard, "GPU");
        });

    /// <summary>
    /// 驗證卡片下方的設定選項面板完整留在視窗內部，未被擠出視窗底部邊界。
    /// </summary>
    [Fact]
    public void TheSettingsBelowTheCardsStayInTheWindow() =>
        _fixture.Invoke(window =>
        {
            var content = (FrameworkElement)window.Content;
            var settings = (FrameworkElement)((Grid)content).Children[^1];
            var bottom = settings.TransformToAncestor(content).TransformBounds(new Rect(settings.RenderSize)).Bottom;

            Assert.True(
                bottom <= content.ActualHeight + 0.5,
                $"the settings reach {bottom} in a window whose contents end at {content.ActualHeight}");
        });

    /// <summary>
    /// 驗證視窗 SizeToContent 設定為 Height（視窗高度隨內容動態調配）。
    /// </summary>
    [Fact]
    public void TheWindowTakesItsHeightFromItsContent() =>
        _fixture.Invoke(window => Assert.Equal(SizeToContent.Height, window.SizeToContent));

    /// <summary>
    /// 驗證標題列顯示正確的產品版本號，且渲染寬度大於 0 並未超出右側邊界。
    /// </summary>
    [Fact]
    public void TheHeaderShowsTheProductVersion() =>
        _fixture.Invoke(window =>
        {
            var content = (FrameworkElement)window.Content;
            var title = (StackPanel)((StackPanel)((Grid)content).Children[0]).Children[0];
            var version = (TextBlock)title.Children[1];

            Assert.Equal(MainViewModel.FormatVersion(typeof(MainViewModel).Assembly.GetName().Version), version.Text);
            Assert.True(version.ActualWidth > 0, "the version renders nothing");

            var edge = version.TransformToAncestor(content).TransformBounds(new Rect(version.RenderSize)).Right;
            Assert.True(edge <= content.ActualWidth + 0.5, $"the version reaches {edge} in contents {content.ActualWidth} wide");
        });

    /// <summary>
    /// 檢查卡片最後一行文字控制項是否完全落在 Border 內距允許範圍內。
    /// </summary>
    private static void AssertContentFits(Border card, string which)
    {
        var stack = (StackPanel)card.Child;
        var last = (FrameworkElement)stack.Children[^1];
        var bottom = last.TransformToAncestor(card).TransformBounds(new Rect(last.RenderSize)).Bottom;
        var available = card.ActualHeight - card.Padding.Bottom;

        Assert.True(
            bottom <= available + 0.5,
            $"the {which} card cuts its last line off: it reaches {bottom} inside a card that ends at {available}");
    }
}

/// <summary>
/// 為 WPF 視窗 UI 測試提供單一 AppDomain UI 執行緒 (STA) 與 MainWindow 實例的測試固件。
/// </summary>
public sealed class MainWindowFixture : IDisposable
{
    private const string LongCpuName = "AMD Ryzen 9 5900HS with Radeon Graphics • Core (Tctl/Tdie)";
    private const string LongGpuName = "NVIDIA GeForce RTX 3060 Laptop GPU • GPU Core";

    private readonly Dispatcher _dispatcher;
    private readonly MainWindow _window;
    private readonly App _application;

    public MainWindowFixture()
    {
        using var ready = new ManualResetEventSlim();
        Dispatcher? dispatcher = null;
        MainWindow? window = null;
        App? application = null;
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                application = new App();
                application.InitializeComponent();
                window = CreateWindow();
                dispatcher = Dispatcher.CurrentDispatcher;
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                ready.Set();
            }

            if (failure is null)
            {
                Dispatcher.Run();
            }
        })
        {
            IsBackground = true,
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();

        if (failure is not null)
        {
            throw new InvalidOperationException("Could not build the window under test.", failure);
        }

        _dispatcher = dispatcher!;
        _window = window!;
        _application = application!;
    }

    /// <summary>
    /// 在 UI 執行緒分派執行斷言。
    /// </summary>
    public void Invoke(Action<MainWindow> assert)
    {
        Exception? failure = null;
        _dispatcher.Invoke(() =>
        {
            try
            {
                assert(_window);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });

        if (failure is not null)
        {
            throw failure;
        }
    }

    public void Dispose()
    {
        _dispatcher.Invoke(() =>
        {
            _window.Close();
            _application.Shutdown();
        });
        _dispatcher.InvokeShutdown();
    }

    /// <summary>
    /// 建立填入長硬體名稱測試資料的隱藏測試視窗。
    /// </summary>
    private static MainWindow CreateWindow()
    {
        var viewModel = new MainViewModel(new HardwareSensorService(), new SettingsService(), new StartupService());
        SetField(viewModel, "_cpuUsage", "3.8%");
        SetField(viewModel, "_cpuTemperature", "61.8 °C");
        SetField(viewModel, "_cpuSource", LongCpuName);
        SetField(viewModel, "_gpuUsage", "16%");
        SetField(viewModel, "_gpuTemperature", "51 °C");
        SetField(viewModel, "_gpuSource", LongGpuName);

        var window = new MainWindow
        {
            DataContext = viewModel,
            ShowInTaskbar = false,
            Left = -20000,
            Top = -20000,
        };

        window.Show();
        window.UpdateLayout();
        return window;
    }

    private static void SetField(object target, string field, string value) =>
        target.GetType()
            .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(target, value);
}
