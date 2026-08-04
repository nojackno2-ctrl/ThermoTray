using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Xunit;

namespace ThermoTray.Tests;

/// <summary>
/// A card is a rounded <see cref="Border"/>, and a rounded border clips whatever does not fit inside
/// it. The window therefore has to take its height from its content: any fixed height silently cuts
/// the last line off the cards on the first system font, display scale, or translation needing more room.
/// </summary>
public sealed class MainWindowLayoutTests : IClassFixture<MainWindowFixture>
{
    private readonly MainWindowFixture _fixture;

    public MainWindowLayoutTests(MainWindowFixture fixture) => _fixture = fixture;

    [Fact]
    public void TheCardsShowTheirWholeContents() =>
        _fixture.Invoke(window =>
        {
            AssertContentFits(window.CpuCard, "CPU");
            AssertContentFits(window.GpuCard, "GPU");
        });

    /// <summary>
    /// Growing the cards must not push the settings underneath them out of the window, which is the
    /// other way a content-sized layout can lose something without any visible sign that it did.
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
    /// The height has to follow the content. Without this the window keeps whatever height it was given
    /// and the cards absorb the shortfall by clipping, which is how a device name lost its last line.
    /// </summary>
    [Fact]
    public void TheWindowTakesItsHeightFromItsContent() =>
        _fixture.Invoke(window => Assert.Equal(SizeToContent.Height, window.SizeToContent));

    /// <summary>
    /// The last child is the device name, the longest and least predictable text on the card. Where it
    /// ends up is measured against the card rather than eyeballed, because a clipped card still reports
    /// a sensible size; only the text's own position gives it away.
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
/// Holds the one WPF <see cref="Application"/> an AppDomain is allowed, plus the real window built on
/// its own UI thread, so every layout test shares them.
/// </summary>
public sealed class MainWindowFixture : IDisposable
{
    /// <summary>As long as any real processor reports, so the cards have to cope with the longest case.</summary>
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
                // Set from a finally so a failure up there cannot leave the constructor waiting forever.
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
            // Far enough off screen that the test never flashes a window at whoever is watching.
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
