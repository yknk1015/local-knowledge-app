using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace KnowledgeApp.CSharp;

// The owner must acquire and dispose on the same thread (the WPF UI thread).
// The mutex is held until App.OnExit, after MainWindow's guarded shutdown finishes.
public sealed class SingleInstanceCoordinator : IDisposable
{
    private readonly Mutex _mutex;
    private readonly int _ownerThread = Environment.CurrentManagedThreadId;
    private readonly NamedPipeServerStream _server;
    private readonly CancellationTokenSource _stop = new();
    private readonly Func<CancellationToken, Task<SingleInstanceReply>> _activate;
    private readonly Task _listener;
    private bool _disposed;

    private SingleInstanceCoordinator(Mutex mutex, SingleInstanceIdentity identity,
        Func<CancellationToken, Task<SingleInstanceReply>> activate)
    {
        _mutex = mutex;
        _activate = activate;
        // Keep this one server handle for the entire lease. No close/recreate gap
        // allows a different process to substitute a server between requests.
        _server = new NamedPipeServerStream(identity.PipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Message,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance,
            256, 256);
        _listener = ListenAsync();
    }

    public static SingleInstanceCoordinator? TryAcquire(SingleInstanceIdentity identity,
        Func<CancellationToken, Task<SingleInstanceReply>> activate)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(activate);
        var mutex = new Mutex(false, identity.MutexName,
            new NamedWaitHandleOptions { CurrentUserOnly = true, CurrentSessionOnly = false });
        var acquired = false;
        try
        {
            try { acquired = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired)
            {
                mutex.Dispose();
                return null;
            }
            return new SingleInstanceCoordinator(mutex, identity, activate);
        }
        catch
        {
            if (acquired) mutex.ReleaseMutex();
            mutex.Dispose();
            throw;
        }
    }

    public static async Task<SingleInstanceReply> RequestActivationAsync(
        SingleInstanceIdentity identity, bool grantForeground = false)
    {
        using var deadline = new CancellationTokenSource(SingleInstancePolicy.ActivationWaitLimit);
        try
        {
            while (!deadline.IsCancellationRequested)
            {
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                attempt.CancelAfter(SingleInstancePolicy.ConnectionWaitLimit);
                try
                {
                    using var client = new NamedPipeClientStream(".", identity.PipeName, PipeDirection.InOut,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await client.ConnectAsync(attempt.Token).ConfigureAwait(false);
                    client.ReadMode = PipeTransmissionMode.Message;
                    // The PID comes from the OS pipe handle, never from IPC input.
                    if (!GetNamedPipeServerProcessId(client.SafePipeHandle, out var processId) ||
                        !ProcessIdToSessionId(processId, out var serverSession) ||
                        !ProcessIdToSessionId((uint)Environment.ProcessId, out var clientSession) ||
                        serverSession != clientSession) return SingleInstanceReply.Unavailable;
                    if (grantForeground)
                    {
                        _ = AllowSetForegroundWindow(processId);
                    }
                    await client.WriteAsync(SingleInstancePolicy.ActivationRequest.ToArray(), attempt.Token).ConfigureAwait(false);
                    var response = new byte[2];
                    var count = await client.ReadAsync(response, attempt.Token).ConfigureAwait(false);
                    if (count != 1 || !client.IsMessageComplete) return SingleInstanceReply.Unavailable;
                    await client.WriteAsync(new byte[] { 6 }, attempt.Token).ConfigureAwait(false);
                    var reply = (SingleInstanceReply)response[0];
                    if (reply is SingleInstanceReply.Activated or SingleInstanceReply.Closing) return reply;
                    if (reply != SingleInstanceReply.Starting) return SingleInstanceReply.Unavailable;
                }
                catch (OperationCanceledException) when (!deadline.IsCancellationRequested) { }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { return SingleInstanceReply.Unavailable; }
                await Task.Delay(100, deadline.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        return SingleInstanceReply.Unavailable;
    }

    private async Task ListenAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                await _server.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                using var request = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                request.CancelAfter(SingleInstancePolicy.ConnectionWaitLimit);
                try
                {
                    var buffer = new byte[SingleInstancePolicy.ActivationRequest.Length + 1];
                    var length = 0;
                    do
                    {
                        var count = await _server.ReadAsync(buffer.AsMemory(length), request.Token).ConfigureAwait(false);
                        if (count == 0) break;
                        length += count;
                    }
                    while (!_server.IsMessageComplete && length < buffer.Length);

                    // Unknown or oversized frames are closed, not drained into an
                    // unbounded allocation or passed through to application code.
                    if (!IsCurrentSessionClient() ||
                        !SingleInstancePolicy.IsActivationRequest(buffer.AsSpan(0, length), _server.IsMessageComplete)) continue;
                    var reply = await _activate(request.Token).WaitAsync(request.Token).ConfigureAwait(false);
                    if (reply is not (SingleInstanceReply.Activated or SingleInstanceReply.Starting or SingleInstanceReply.Closing))
                    {
                        reply = SingleInstanceReply.Rejected;
                    }
                    await _server.WriteAsync(new[] { (byte)reply }, request.Token).ConfigureAwait(false);
                    // Do not discard the reply with Disconnect before the client
                    // reads it. The receipt is fixed and shares the one-second limit.
                    var receipt = new byte[2];
                    _ = await _server.ReadAsync(receipt, request.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { }
                catch (IOException) { }
                catch (InvalidOperationException) { }
                finally
                {
                    if (!_stop.IsCancellationRequested && _server.IsConnected) _server.Disconnect();
                }
            }
        }
        // A failed listener is fail-closed: keep the mutex, and second launches
        // time out without constructing a second MainWindow or touching any DB.
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (IOException) { }
        catch (InvalidOperationException) { }
    }

    private bool IsCurrentSessionClient() =>
        GetNamedPipeClientProcessId(_server.SafePipeHandle, out var clientProcessId) &&
        ProcessIdToSessionId(clientProcessId, out var clientSession) &&
        ProcessIdToSessionId((uint)Environment.ProcessId, out var serverSession) &&
        clientSession == serverSession;

    public void Dispose()
    {
        if (_disposed) return;
        if (Environment.CurrentManagedThreadId != _ownerThread)
            throw new InvalidOperationException("The instance lease must be released by its owner thread.");
        _disposed = true;
        _stop.Cancel();
        _server.Dispose();
        // No callback owns application data. Cancellation removes queued dispatcher
        // work; shutdown must never wait synchronously for the UI dispatcher here.
        _ = _listener.ContinueWith(_ => _stop.Dispose(), TaskScheduler.Default);
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);
}
