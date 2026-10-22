using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using LemonNeo.Hosting;

namespace LemonNeo.Desktop.Services;

/// <summary>
/// Enforces one Desktop process per data directory and forwards later launches to the running one.
/// </summary>
/// <remarks>
/// The owning process holds an exclusive lock on a file inside the data directory, which works the
/// same way on Windows (share modes) and on macOS/Linux (advisory locks). Later launches reach the
/// owner over a named pipe, which .NET implements with Unix domain sockets on macOS and Linux.
/// </remarks>
public sealed class SingleInstanceGuard : IDisposable
{
    private const int AsfwAny = -1;
    private static readonly byte[] ActivationMessage = [1];
    private static readonly TimeSpan ActivationTimeout = TimeSpan.FromSeconds(5);

    private readonly CancellationTokenSource _cancellation = new();
    private readonly object _gate = new();
    private readonly string? _lockPath;
    private readonly string? _pipeName;
    private Action? _activationHandler;
    private bool _activationPending;
    private bool _disposed;
    private FileStream? _lockFile;

    private SingleInstanceGuard(string? pipeName, string? lockPath)
    {
        _pipeName = pipeName;
        _lockPath = lockPath;
    }

    /// <summary>Creates a guard for the default data directory, or an unrestricted one if paths are unavailable.</summary>
    public static SingleInstanceGuard CreateDefault()
    {
        try
        {
            var dataDirectory = AppHost.CreateDefaultPaths().DataDirectory;
            return new SingleInstanceGuard(BuildPipeName(dataDirectory),
                Path.Combine(dataDirectory, "instance.lock"));
        }
        catch (Exception exception)
        {
            // Without a usable identity there is nothing to compare, so allow a normal launch.
            Trace.WriteLine($"Single-instance support unavailable: {exception}");
            return new SingleInstanceGuard(null, null);
        }
    }

    /// <summary>Registers the handler that shows the main window; earlier requests are replayed.</summary>
    public void SetActivationHandler(Action handler)
    {
        bool pending;
        lock (_gate)
        {
            _activationHandler = handler;
            pending = _activationPending;
            _activationPending = false;
        }

        if (pending) handler();
    }

    /// <summary>
    /// Tries to become the single running instance. Returns <see langword="true"/> when this process
    /// owns the instance (or when single-instance support is unavailable and must be skipped).
    /// </summary>
    public bool TryAcquire()
    {
        if (_lockFile is not null) return true;
        if (_lockPath is null) return true;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_lockPath)!);
            _lockFile = new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                FileShare.None);
            _lockFile.SetLength(0);
        }
        catch (IOException)
        {
            // Another process holds the lock (share violation / advisory lock).
            return false;
        }
        catch (Exception exception)
        {
            // A broken lock location must not prevent the application from starting.
            Trace.WriteLine($"Could not acquire the single-instance lock: {exception}");
            _lockFile?.Dispose();
            _lockFile = null;
            return true;
        }

        if (_pipeName is not null) _ = Task.Run(() => ListenAsync(_cancellation.Token));
        return true;
    }

    /// <summary>
    /// Asks the running instance to show its window. Returns <see langword="false"/> when no
    /// instance could be reached within the timeout.
    /// </summary>
    public async Task<bool> NotifyExistingInstanceAsync()
    {
        if (_pipeName is null) return false;

        // Windows may refuse the owner's focus request because this process currently holds the
        // foreground. Hand that permission over so the existing window can surface itself.
        if (OperatingSystem.IsWindows()) AllowSetForegroundWindow(AsfwAny);

        using var timeout = new CancellationTokenSource(ActivationTimeout);
        while (!timeout.IsCancellationRequested)
        {
            try
            {
                await using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out,
                    PipeOptions.Asynchronous);
                // Short attempts keep the caller responsive while the owner finishes starting up.
                await client.ConnectAsync(250, timeout.Token);
                await client.WriteAsync(ActivationMessage, timeout.Token);
                await client.FlushAsync(timeout.Token);
                return true;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception exception) when (exception is IOException or TimeoutException
                                                  or UnauthorizedAccessException
                                                  or PlatformNotSupportedException)
            {
                if (exception is PlatformNotSupportedException)
                {
                    Trace.WriteLine($"Single-instance activation is not supported: {exception}");
                    break;
                }

                try
                {
                    await Task.Delay(100, timeout.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        return false;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        _cancellation.Cancel();
        _lockFile?.Dispose();
        _lockFile = null;
        // The cancellation source is intentionally not disposed: the listener task may still hold
        // its token, and the process is exiting anyway.
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        var pipeName = _pipeName;
        if (pipeName is null) return;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // A fresh instance per connection lets a disconnected client retry cleanly.
                await using var server = new NamedPipeServerStream(pipeName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(cancellationToken);

                var buffer = new byte[ActivationMessage.Length];
                using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                readTimeout.CancelAfter(TimeSpan.FromSeconds(2));
                try
                {
                    await server.ReadExactlyAsync(buffer, readTimeout.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // The client connected but stayed silent; the connection is the signal.
                }
                catch (IOException)
                {
                    // The client disconnected before sending; activation was still requested.
                }

                RequestActivation();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (PlatformNotSupportedException exception)
            {
                Trace.WriteLine($"Single-instance activation is not supported: {exception}");
                break;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Trace.WriteLine($"Single-instance listener error: {exception}");
                try
                {
                    await Task.Delay(250, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private void RequestActivation()
    {
        Action? handler;
        lock (_gate)
        {
            handler = _activationHandler;
            if (handler is null)
            {
                // The window is not ready yet; replay once it is.
                _activationPending = true;
                return;
            }
        }

        handler();
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int processId);

    private static string BuildPipeName(string dataDirectory)
    {
        var identity = Path.GetFullPath(dataDirectory);
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
            identity = identity.ToUpperInvariant();

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..16];
        return $"LemonNeo.Desktop.{hash}";
    }
}
