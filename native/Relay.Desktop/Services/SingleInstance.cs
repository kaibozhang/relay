using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Relay.Desktop.Services;

/// <summary>
/// One process per Windows user and caller-supplied vault key. Keep this object alive until exit.
/// The first process handles its own initial arguments; forwarded callbacks run off the UI thread.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    public const int MaxArguments = 32;
    public const int MaxArgumentLength = 32768;
    // JSON may encode each UTF-16 code unit as six ASCII bytes (\uXXXX).
    private const int MaxPayloadBytes = MaxArguments * MaxArgumentLength * 6 + 1024;
    private const int DeliveryTimeoutMilliseconds = 5000;
    private readonly object _gate = new();
    private readonly ManualResetEventSlim _releaseMutex = new(false);
    private readonly CancellationTokenSource _shutdown = new();
    private Thread? _mutexThread;
    private Task? _listener;
    private NamedPipeServerStream? _server;
    private bool _attempted;
    private bool _disposed;
    private string? _lastError;

    public bool IsOwner { get; private set; }
    public string? LastError => Volatile.Read(ref _lastError);

    /// <summary>
    /// Returns true for the owner, false only after the existing owner acknowledges delivery.
    /// Throws on invalid arguments or failed delivery (never silently starts a second owner).
    /// Supply a stable canonical vault key, e.g. a normalized full vault directory path.
    /// onArgs must enqueue UI work rather than synchronously waiting for the UI dispatcher.
    /// Each object permits exactly one Acquire call, and must be disposed even on failure.
    /// </summary>
    public bool Acquire(string key, string[] args, Action<string[]> onArgs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(onArgs);
        ValidateArguments(args);
        if (key.Length > 32768) throw new ArgumentException("Vault key is too long.", nameof(key));
        var forwardedArgs = (string[])args.Clone();
        using var identity = WindowsIdentity.GetCurrent();
        string sid = identity.User?.Value ?? throw new InvalidOperationException("Windows user identity is unavailable.");
        string scope = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sid + "\n" + key)));
        string pipeName = "Relay.Desktop." + scope;

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_attempted) throw new InvalidOperationException("Acquire may only be called once.");
            _attempted = true;
            var acquired = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            // A Mutex must be released by its acquiring thread, even when Dispose is called elsewhere.
            _mutexThread = new Thread(() => OwnMutex("Global\\" + pipeName, acquired))
            {
                IsBackground = true,
                Name = "Relay single-instance ownership"
            };
            _mutexThread.Start();
            IsOwner = acquired.Task.GetAwaiter().GetResult();
            if (IsOwner)
            {
                try
                {
                    var server = CreateServer(pipeName);
                    var stopping = _shutdown.Token;
                    _server = server;
                    _listener = Task.Run(() => ListenAsync(pipeName, server, onArgs, stopping));
                }
                catch
                {
                    IsOwner = false;
                    _releaseMutex.Set();
                    _mutexThread.Join();
                    throw;
                }
                return true;
            }
        }

        ForwardAsync(pipeName, forwardedArgs).GetAwaiter().GetResult();
        return false;
    }

    private void OwnMutex(string name, TaskCompletionSource<bool> acquired)
    {
        bool owns = false;
        try
        {
            // User-scoped ACL as well as a SID-hashed name; global means shared across login sessions.
            using var mutex = new Mutex(false, name, new NamedWaitHandleOptions
            {
                CurrentUserOnly = true,
                CurrentSessionOnly = false
            });
            try { owns = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { owns = true; }
            acquired.TrySetResult(owns);
            if (owns)
            {
                try { _releaseMutex.Wait(); }
                finally { mutex.ReleaseMutex(); }
            }
        }
        catch (Exception error)
        {
            acquired.TrySetException(error);
            Volatile.Write(ref _lastError, error.Message);
        }
    }

    private static NamedPipeServerStream CreateServer(string pipeName) => new(
        pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 4096, 4096);

    private async Task ListenAsync(string pipeName, NamedPipeServerStream firstServer,
        Action<string[]> onArgs, CancellationToken stopping)
    {
        NamedPipeServerStream? server = firstServer;
        try
        {
            while (!stopping.IsCancellationRequested)
            {
                try
                {
                    lock (_gate)
                    {
                        if (_disposed) break;
                        server ??= CreateServer(pipeName);
                        _server = server;
                    }
                    await server.WaitForConnectionAsync(stopping).ConfigureAwait(false);
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stopping);
                    deadline.CancelAfter(DeliveryTimeoutMilliseconds);
                    byte[] header = new byte[4];
                    await server.ReadExactlyAsync(header, deadline.Token).ConfigureAwait(false);
                    int length = BinaryPrimitives.ReadInt32LittleEndian(header);
                    if (length <= 0 || length > MaxPayloadBytes) throw new InvalidDataException("Invalid IPC payload length.");
                    byte[] payload = new byte[length];
                    await server.ReadExactlyAsync(payload, deadline.Token).ConfigureAwait(false);
                    string[] arguments = DecodeArguments(payload);
                    ValidateArguments(arguments);
                    // No execution or path opening here: the application owns action policy and dispatch.
                    onArgs(arguments);
                    await server.WriteAsync(new byte[] { 1 }, deadline.Token).ConfigureAwait(false);
                    await server.FlushAsync(deadline.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stopping.IsCancellationRequested) { break; }
                catch (ObjectDisposedException) when (stopping.IsCancellationRequested) { break; }
                catch (Exception error)
                {
                    // A malformed/stalled client or an application callback must not kill the listener.
                    Volatile.Write(ref _lastError, error.Message);
                    if (!stopping.IsCancellationRequested)
                    {
                        try { await Task.Delay(50, stopping).ConfigureAwait(false); }
                        catch (OperationCanceledException) { break; }
                    }
                }
                finally
                {
                    server?.Dispose();
                    server = null;
                    lock (_gate) { _server = null; }
                }
            }
        }
        finally { server?.Dispose(); }
    }

    private static string[] DecodeArguments(byte[] payload)
    {
        // Reject too many elements before allocating a potentially large attacker-controlled array.
        var reader = new Utf8JsonReader(payload, new JsonReaderOptions { MaxDepth = 2 });
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray)
            throw new InvalidDataException("An IPC argument array is required.");
        var arguments = new System.Collections.Generic.List<string>(MaxArguments);
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.String || arguments.Count == MaxArguments
                || reader.ValueSpan.Length > MaxArgumentLength * 6)
                throw new InvalidDataException("Invalid IPC argument.");
            string value = reader.GetString()!;
            if (value.Length > MaxArgumentLength) throw new InvalidDataException("IPC argument is too long.");
            arguments.Add(value);
        }
        if (reader.TokenType != JsonTokenType.EndArray || reader.Read())
            throw new InvalidDataException("Invalid IPC JSON framing.");
        return arguments.ToArray();
    }

    private static async Task ForwardAsync(string pipeName, string[] args)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(args);
        if (payload.Length > MaxPayloadBytes) throw new ArgumentException("IPC payload is too large.", nameof(args));
        using var deadline = new CancellationTokenSource(DeliveryTimeoutMilliseconds);
        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, TokenImpersonationLevel.Identification);
        try
        {
            await client.ConnectAsync(deadline.Token).ConfigureAwait(false);
            byte[] header = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
            await client.WriteAsync(header, deadline.Token).ConfigureAwait(false);
            await client.WriteAsync(payload, deadline.Token).ConfigureAwait(false);
            await client.FlushAsync(deadline.Token).ConfigureAwait(false);
            byte[] acknowledgement = new byte[1];
            await client.ReadExactlyAsync(acknowledgement, deadline.Token).ConfigureAwait(false);
            if (acknowledgement[0] != 1) throw new IOException("The existing Relay instance rejected the request.");
        }
        catch (OperationCanceledException error)
        {
            throw new TimeoutException("The existing Relay instance did not acknowledge the request within five seconds.", error);
        }
    }

    /// <summary>Only known action flags and bounded opaque resource values cross the IPC boundary.</summary>
    public static void ValidateArguments(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length > MaxArguments) throw new ArgumentException("At most 32 arguments are allowed.", nameof(args));
        foreach (string? arg in args)
        {
            if (arg is null || arg.Length > MaxArgumentLength || arg.IndexOf('\0') >= 0)
                throw new ArgumentException("An argument is null, oversized, or contains a NUL character.", nameof(args));
        }
        string? action = null;
        bool hasResource = false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--capture":
                case "--resume":
                    if (action is not null) throw new ArgumentException("Specify at most one action.", nameof(args));
                    action = args[i];
                    break;
                case "--resource":
                    if (++i >= args.Length || string.IsNullOrWhiteSpace(args[i]) || args[i].StartsWith("--", StringComparison.Ordinal))
                        throw new ArgumentException("--resource requires a resource value.", nameof(args));
                    hasResource = true;
                    break;
                default:
                    throw new ArgumentException("Unsupported Relay argument.", nameof(args));
            }
        }
        if (action == "--resume" && hasResource)
            throw new ArgumentException("Resources cannot be combined with --resume.", nameof(args));
    }

    public void Dispose()
    {
        Thread? ownershipThread;
        Task? listener;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _shutdown.Cancel();
            _server?.Dispose();
            _releaseMutex.Set();
            ownershipThread = _mutexThread;
            listener = _listener;
            IsOwner = false;
        }
        ownershipThread?.Join();
        _releaseMutex.Dispose();
        // Never wait on an application callback: it may be dispatching to the disposing UI thread.
        if (listener is null) _shutdown.Dispose();
        else _ = listener.ContinueWith(_ => _shutdown.Dispose(), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}
