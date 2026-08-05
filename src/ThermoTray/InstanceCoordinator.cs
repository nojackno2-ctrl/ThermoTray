using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace ThermoTray;

/// <summary>
/// 負責管理單一執行體互斥鎖 (Mutex) 與跨實體通訊的協調器。
/// 確保系統中同時只有一個 ThermoTray 實體持有系統匣圖示並輪詢硬體。
/// </summary>
internal sealed class InstanceCoordinator : IDisposable
{
    // Session 範圍的互斥鎖名稱：所有實體均在相同 Session 內提升權限執行
    private const string OwnershipMutexName = "Local\\ThermoTray.SingleInstance";

    /// <summary>
    /// 舊版本 (1.1.3 之前) 使用的視窗顯示訊號事件名稱，保留用於向下相容。
    /// </summary>
    private const string LegacyShowWindowEventName = "Local\\ThermoTray.ShowWindow";

    /// <summary>
    /// 供 Inno Setup 安裝程式讀取的全域互斥鎖名稱。安裝程式以一般權限執行，透過此鎖判斷 ThermoTray 是否正在執行。
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

    /// <summary>
    /// 依據 Session ID 產生的管道名稱。
    /// </summary>
    internal static string PipeName { get; } = BuildPipeName();

    /// <summary>
    /// 嘗試取得系統匣單一執行體的持有權。
    /// </summary>
    /// <param name="version">當前實體的版本號。</param>
    /// <param name="showWindow">顯示主視窗的回調委派。</param>
    /// <param name="requestExit">請求結束程式的回調委派。</param>
    /// <returns>若成功取得持有權傳回 <see cref="InstanceCoordinator"/> 實例，否則傳回 null。</returns>
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
            ownership = null;
        }

        var coordinator = new InstanceCoordinator(ownership);
        coordinator.Start(version, showWindow, requestExit);
        return coordinator;
    }

    /// <summary>
    /// 在要求前一個實體退出後，帶重試機制地嘗試重新取得持有權。
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
    /// 當無法透過具名管道連接時，嘗試發送舊版 EventWaitHandle 訊號要求對方顯示視窗。
    /// </summary>
    /// <returns>若成功發送訊號傳回 true，否則傳回 false。</returns>
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

    /// <summary>
    /// 等待指定處理程序 exit 結束。
    /// </summary>
    internal static bool WaitForProcessExit(int processId, TimeSpan timeout)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.WaitForExit((int)timeout.TotalMilliseconds);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            // 處理程序已不存在，視為已順利退出
            return true;
        }
    }

    /// <summary>
    /// 釋放所有控制代碼與管道伺服器。
    /// </summary>
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

    /// <summary>
    /// 啟動通訊伺服器與舊版訊號監聽。
    /// </summary>
    private void Start(Version? version, Action showWindow, Action requestExit)
    {
        _setupMutex = TryCreateSetupMutex();
        RegisterLegacySignal(showWindow);

        _server = new InstanceServer(PipeName, version, showWindow, requestExit);
        _server.Start();
    }

    /// <summary>
    /// 註冊舊版訊號觸發監聽。
    /// </summary>
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
        }
    }

    /// <summary>
    /// 建立供 Inno Setup 探測的全域互斥鎖，賦予 Everyone 讀取與 SYNCHRONIZE 權限。
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
            return null;
        }
    }

    /// <summary>
    /// 依據 Process SessionId 建立管道名稱。
    /// </summary>
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
/// 具名管道伺服器，負責監聽後續啟動的 ThermoTray 傳來的命令與版本查詢。
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
        }

        _listener = null;
        _cancellation.Dispose();
    }

    /// <summary>
    /// 非同步持續監聽管道連線。
    /// </summary>
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
            }
        }
    }

    /// <summary>
    /// 處理單次管道連線中的命令（WHO / SHOW / EXIT）。
    /// </summary>
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
                    await AcknowledgeAsync(writer, pipe, cancellationToken).ConfigureAwait(false);
                    _requestExit();
                    return;

                default:
                    return;
            }
        }
    }

    /// <summary>
    /// 回應 OK 確認訊息並排空管道。
    /// </summary>
    private static async Task AcknowledgeAsync(StreamWriter writer, NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        await writer.WriteLineAsync(InstanceProtocol.Acknowledgement.AsMemory(), cancellationToken).ConfigureAwait(false);

        try
        {
            pipe.WaitForPipeDrain();
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
        }
    }
}

/// <summary>
/// 新啟動實體發起管道連線的用戶端。
/// </summary>
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
    /// 嘗試連接正在執行的管道伺服器。
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

    /// <summary>
    /// 發送 SHOW 命令。
    /// </summary>
    internal bool RequestShow() => Exchange(InstanceProtocol.ShowRequest) == InstanceProtocol.Acknowledgement;

    /// <summary>
    /// 發送 EXIT 命令。
    /// </summary>
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

    /// <summary>
    /// 發送 WHO 命令查詢對方版本號與 PID。
    /// </summary>
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

    /// <summary>
    /// 發送管道請求並接收單行回應。
    /// </summary>
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
    /// 在背景 ThreadPool 執行非同步工作並同步等待，防止 UI 執行緒死鎖。
    /// </summary>
    private static void RunSync(Func<Task> operation) =>
        Task.Run(() => operation().WaitAsync(ExchangeTimeout)).GetAwaiter().GetResult();

    private static T RunSync<T>(Func<Task<T>> operation) =>
        Task.Run(() => operation().WaitAsync(ExchangeTimeout)).GetAwaiter().GetResult();
}

/// <summary>
/// 具名管道編碼與換行格式常數。
/// </summary>
internal static class InstanceWire
{
    /// <summary>
    /// 使用無 BOM 標記的 UTF-8 編碼。
    /// </summary>
    internal static Encoding Encoding { get; } = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    internal const string NewLine = "\n";
}
