using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace ThermoTray;

/// <summary>
/// Owns the single-instance handles and answers later launches. Only one instance may poll the
/// hardware and own the notification-area icons, so the guard is deliberately version independent;
/// what the versions decide is which of the two instances gets to be that one.
/// </summary>
internal sealed class InstanceCoordinator : IDisposable
{
    // Session-local names: every instance runs elevated in the same session, so a wider scope
    // would only invite name collisions with other sessions.
    private const string OwnershipMutexName = "Local\\ThermoTray.SingleInstance";

    /// <summary>
    /// Still created, and still honoured, because builds before the pipe existed know only this.
    /// </summary>
    private const string LegacyShowWindowEventName = "Local\\ThermoTray.ShowWindow";

    /// <summary>
    /// Read by the installer's <c>AppMutex</c>. It has to be machine wide and readable by an
    /// unelevated process, because the installer runs at the lowest privilege level.
    /// </summary>
    internal const string SetupMutexName = "Global\\ThermoTray.Setup";

    private static readonly TimeSpan ListenerShutdownTimeout = TimeSpan.FromSeconds(1);
    private const int OwnershipRetries = 20;
    private const int OwnershipRetryDelayMilliseconds = 100;

    private Mutex? _ownership;
    private Mutex? _setupMutex;
    private EventWaitHandle? _legacySignal;
    private RegisteredWaitHandle? _legacyRegistration;
    private InstanceServer? _server;

    private InstanceCoordinator(Mutex? ownership) => _ownership = ownership;

    /// <summary>Named per session because a pipe name, unlike a <c>Local\</c> object, is machine wide.</summary>
    internal static string PipeName { get; } = BuildPipeName();

    /// <summary>
    /// Returns the coordinator when this process may own the tray, or <see langword="null"/> when
    /// another instance already does.
    /// </summary>
    internal static InstanceCoordinator? TryClaim(Version? version, Action showWindow, Action requestExit)
    {
        Mutex? ownership;

        try
        {
            ownership = new Mutex(initiallyOwned: false, OwnershipMutexName, out var isFirstInstance);
            if (!isFirstInstance)
            {
                ownership.Dispose();
                return null;
            }
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException
            or IOException
            or WaitHandleCannotBeOpenedException)
        {
            // Without the guard a duplicate instance becomes possible, which beats refusing to start.
            ownership = null;
        }

        var coordinator = new InstanceCoordinator(ownership);
        coordinator.Start(version, showWindow, requestExit);
        return coordinator;
    }

    /// <summary>
    /// Claims ownership after the previous instance was asked to exit. The name survives for a
    /// moment after the process object signals, so a single attempt would lose a race it has
    /// already won.
    /// </summary>
    internal static InstanceCoordinator? ClaimAfterHandover(Version? version, Action showWindow, Action requestExit)
    {
        for (var attempt = 0; attempt < OwnershipRetries; attempt++)
        {
            var coordinator = TryClaim(version, showWindow, requestExit);
            if (coordinator is not null)
            {
                return coordinator;
            }

            Thread.Sleep(OwnershipRetryDelayMilliseconds);
        }

        return null;
    }

    /// <summary>
    /// Last resort for an instance that cannot be reached over the pipe, which means it predates it.
    /// Reports whether the running instance was actually told to show itself.
    /// </summary>
    internal static bool TrySignalLegacyInstance()
    {
        try
        {
            if (!EventWaitHandle.TryOpenExisting(LegacyShowWindowEventName, out var signal))
            {
                return false;
            }

            using (signal)
            {
                signal.Set();
            }

            return true;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException
            or IOException
            or WaitHandleCannotBeOpenedException)
        {
            return false;
        }
    }

    internal static bool WaitForProcessExit(int processId, TimeSpan timeout)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.WaitForExit((int)timeout.TotalMilliseconds);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            // The process is already gone, which is exactly what the caller was waiting for.
            return true;
        }
    }

    public void Dispose()
    {
        _server?.Dispose();
        _server = null;

        _legacyRegistration?.Unregister(waitObject: null);
        _legacyRegistration = null;
        _legacySignal?.Dispose();
        _legacySignal = null;

        _setupMutex?.Dispose();
        _setupMutex = null;
        _ownership?.Dispose();
        _ownership = null;
    }

    private void Start(Version? version, Action showWindow, Action requestExit)
    {
        _setupMutex = TryCreateSetupMutex();
        RegisterLegacySignal(showWindow);

        _server = new InstanceServer(PipeName, version, showWindow, requestExit);
        _server.Start();
    }

    /// <summary>Lets a launch that speaks the older protocol bring this instance's window back.</summary>
    private void RegisterLegacySignal(Action showWindow)
    {
        try
        {
            _legacySignal = new EventWaitHandle(initialState: false, EventResetMode.AutoReset, LegacyShowWindowEventName);
            _legacyRegistration = ThreadPool.RegisterWaitForSingleObject(
                _legacySignal,
                (_, _) => showWindow(),
                state: null,
                Timeout.Infinite,
                executeOnlyOnce: false);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException
            or IOException
            or WaitHandleCannotBeOpenedException)
        {
            // Single-instance enforcement still works; only the older hand-over gesture is lost.
        }
    }

    /// <summary>
    /// Exists only to be seen. The installer opens it with SYNCHRONIZE to find out whether ThermoTray
    /// is running, which it cannot learn any other way: it runs unelevated and so can neither read
    /// this process nor replace the executable image while it is loaded.
    /// </summary>
    private static Mutex? TryCreateSetupMutex()
    {
        try
        {
            var security = new MutexSecurity();
            using var identity = WindowsIdentity.GetCurrent();
            if (identity.User is not null)
            {
                security.AddAccessRule(new MutexAccessRule(identity.User, MutexRights.FullControl, AccessControlType.Allow));
            }

            // Read-only for everyone else: an unelevated installer must be able to see it, and
            // SYNCHRONIZE is all it needs. Mandatory integrity policy blocks writing up, not reading.
            security.AddAccessRule(new MutexAccessRule(
                new SecurityIdentifier(WellKnownSidType.WorldSid, domainSid: null),
                MutexRights.Synchronize | MutexRights.ReadPermissions,
                AccessControlType.Allow));

            return MutexAcl.Create(initiallyOwned: false, SetupMutexName, out _, security);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException
            or IOException
            or WaitHandleCannotBeOpenedException
            or PlatformNotSupportedException)
        {
            // The installer then falls back to its own files-in-use handling; monitoring is unaffected.
            return null;
        }
    }

    private static string BuildPipeName()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            return FormattableString.Invariant($"ThermoTray.Instance.{process.SessionId}");
        }
        catch (Exception exception) when (exception is InvalidOperationException or PlatformNotSupportedException)
        {
            return "ThermoTray.Instance";
        }
    }
}

/// <summary>
/// Answers later launches on behalf of the instance that owns the tray. One client at a time is
/// enough: a launch asks who is running and then either hands over or asks this instance to exit.
/// </summary>
internal sealed class InstanceServer : IDisposable
{
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(1);
    private const int BufferSize = 256;

    private readonly string _pipeName;
    private readonly Version? _version;
    private readonly Action _showWindow;
    private readonly Action _requestExit;
    private readonly CancellationTokenSource _cancellation = new();
    private Task? _listener;

    internal InstanceServer(string pipeName, Version? version, Action showWindow, Action requestExit)
    {
        _pipeName = pipeName;
        _version = version;
        _showWindow = showWindow;
        _requestExit = requestExit;
    }

    internal void Start() => _listener = Task.Run(() => ListenAsync(_cancellation.Token));

    public void Dispose()
    {
        _cancellation.Cancel();

        try
        {
            _listener?.Wait(ShutdownTimeout);
        }
        catch (AggregateException)
        {
            // The listener only ever fails on a broken client, which no longer matters here.
        }

        _listener = null;
        _cancellation.Dispose();
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                await ServeAsync(pipe, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or ObjectDisposedException)
            {
                // One client that disconnects mid-exchange must not stop the next launch being answered.
            }
        }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(pipe, InstanceWire.Encoding, detectEncodingFromByteOrderMarks: false, BufferSize, leaveOpen: true);
        await using var writer = new StreamWriter(pipe, InstanceWire.Encoding, BufferSize, leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = InstanceWire.NewLine,
        };

        while (!cancellationToken.IsCancellationRequested)
        {
            var request = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);

            switch (request)
            {
                case InstanceProtocol.IdentifyRequest:
                    await writer.WriteLineAsync(
                        InstanceProtocol.FormatIdentity(_version, Environment.ProcessId).AsMemory(),
                        cancellationToken).ConfigureAwait(false);
                    break;

                case InstanceProtocol.ShowRequest:
                    await AcknowledgeAsync(writer, pipe, cancellationToken).ConfigureAwait(false);
                    _showWindow();
                    return;

                case InstanceProtocol.ExitRequest:
                    // Acknowledged before shutting down, or the caller could not tell a refusal from a
                    // crash. The callback must not run inline, or shutdown would wait on this loop.
                    await AcknowledgeAsync(writer, pipe, cancellationToken).ConfigureAwait(false);
                    _requestExit();
                    return;

                default:
                    // Includes null, which is a disconnected client, and anything that is not ours.
                    return;
            }
        }
    }

    private static async Task AcknowledgeAsync(StreamWriter writer, NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        await writer.WriteLineAsync(InstanceProtocol.Acknowledgement.AsMemory(), cancellationToken).ConfigureAwait(false);

        try
        {
            // Closing the pipe can discard what the client has not read yet, and on the exit path
            // this process is about to disappear.
            pipe.WaitForPipeDrain();
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            // The client left without reading; nothing more can be delivered to it.
        }
    }
}

/// <summary>The launching side of the same exchange. Every call is synchronous by design: it runs
/// during startup, before there is a window or a message loop to keep responsive.</summary>
internal sealed class InstanceClient : IDisposable
{
    private static readonly TimeSpan ExchangeTimeout = TimeSpan.FromSeconds(5);
    private const int BufferSize = 256;

    private readonly NamedPipeClientStream _pipe;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private bool _isDisposed;

    private InstanceClient(NamedPipeClientStream pipe)
    {
        _pipe = pipe;
        _reader = new StreamReader(pipe, InstanceWire.Encoding, detectEncodingFromByteOrderMarks: false, BufferSize, leaveOpen: true);
        _writer = new StreamWriter(pipe, InstanceWire.Encoding, BufferSize, leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = InstanceWire.NewLine,
        };
    }

    internal Version? RunningVersion { get; private set; }

    internal int RunningProcessId { get; private set; }

    /// <summary>
    /// Returns a connected client that has already identified its peer, or <see langword="null"/>
    /// when nothing on the other end speaks this protocol.
    /// </summary>
    internal static InstanceClient? TryConnect(string pipeName, TimeSpan timeout)
    {
        NamedPipeClientStream? pipe = null;

        try
        {
            pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            RunSync(() => pipe.ConnectAsync((int)timeout.TotalMilliseconds));
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            pipe?.Dispose();
            return null;
        }

        var client = new InstanceClient(pipe);
        if (client.TryIdentify())
        {
            return client;
        }

        client.Dispose();
        return null;
    }

    internal bool RequestShow() => Exchange(InstanceProtocol.ShowRequest) == InstanceProtocol.Acknowledgement;

    internal bool RequestExit() => Exchange(InstanceProtocol.ExitRequest) == InstanceProtocol.Acknowledgement;

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _writer.Dispose();
        _reader.Dispose();
        _pipe.Dispose();
    }

    private bool TryIdentify()
    {
        if (!InstanceProtocol.TryParseIdentity(Exchange(InstanceProtocol.IdentifyRequest), out var version, out var processId))
        {
            return false;
        }

        RunningVersion = version;
        RunningProcessId = processId;
        return true;
    }

    private string? Exchange(string request)
    {
        try
        {
            return RunSync(async () =>
            {
                await _writer.WriteLineAsync(request.AsMemory()).ConfigureAwait(false);
                return await _reader.ReadLineAsync().ConfigureAwait(false);
            });
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            return null;
        }
    }

    private static bool IsExpected(Exception exception) => exception is IOException
        or TimeoutException
        or UnauthorizedAccessException
        or ObjectDisposedException
        or InvalidOperationException
        or OperationCanceledException;

    /// <summary>
    /// Runs the exchange off the calling thread. Blocking the UI thread on a continuation that the
    /// dispatcher would have to run is a deadlock, and startup does block on these calls.
    /// </summary>
    private static void RunSync(Func<Task> operation) =>
        Task.Run(() => operation().WaitAsync(ExchangeTimeout)).GetAwaiter().GetResult();

    private static T RunSync<T>(Func<Task<T>> operation) =>
        Task.Run(() => operation().WaitAsync(ExchangeTimeout)).GetAwaiter().GetResult();
}

internal static class InstanceWire
{
    /// <summary>Without the explicit constructor <see cref="StreamWriter"/> emits a byte order mark.</summary>
    internal static Encoding Encoding { get; } = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    internal const string NewLine = "\n";
}
