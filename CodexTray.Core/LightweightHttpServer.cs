using System.Net;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;

namespace CodexTray.Core;

public sealed class LightweightHttpServer : IDisposable
{
    private static readonly JsonSerializerOptions s_JsonOptions = new()
    {
        WriteIndented = false,
    };

    private readonly UsageCache m_UsageCache;
    private readonly Lock m_LifecycleLock = new();
    private readonly HashSet<Task> m_ClientTasks = [];
    private CancellationTokenSource? m_Cancellation;
    private TcpListener? m_Listener;
    private Task? m_AcceptTask;
    private Task? m_StopTask;
    private bool m_Disposed;
    private bool m_IsRunning;
    private string? m_LastError;

    public int Port { get; private set; }

    public bool IsRunning
    {
        get
        {
            lock (m_LifecycleLock)
            {
                return m_IsRunning;
            }
        }
    }

    public string? LastError
    {
        get
        {
            lock (m_LifecycleLock)
            {
                return m_LastError;
            }
        }
    }

    /// <summary>
    /// Gets the number of currently tracked client operations for diagnostics.
    /// </summary>
    private int ActiveClientCount
    {
        get
        {
            lock (m_LifecycleLock)
            {
                return m_ClientTasks.Count;
            }
        }
    }

    /// <summary>
    /// Creates a loopback HTTP server for Codex usage data.
    /// </summary>
    public LightweightHttpServer(UsageCache usageCache, int port)
    {
        m_UsageCache = usageCache;
        Port = port;
    }

    /// <summary>
    /// Starts the server on the configured port.
    /// </summary>
    public void Start()
    {
        lock (m_LifecycleLock)
        {
            ThrowIfDisposed();
            if (m_IsRunning)
            {
                return;
            }

            if (m_StopTask is { IsCompleted: false })
            {
                throw new InvalidOperationException("The server is still stopping.");
            }

            if (m_Cancellation != null || m_Listener != null || m_AcceptTask != null)
            {
                throw new InvalidOperationException("Call StopAsync before restarting a stopped server generation.");
            }

            CancellationTokenSource cancellation = new();
            TcpListener listener = new(IPAddress.Parse(CodexTrayDefaults.Host), Port);
            try
            {
                listener.Start();
                Port = ((IPEndPoint)listener.LocalEndpoint).Port;
                m_Cancellation = cancellation;
                m_Listener = listener;
                m_IsRunning = true;
                m_LastError = null;
                m_StopTask = null;
                m_AcceptTask = AcceptLoopAsync(listener, cancellation.Token);
            }
            catch
            {
                listener.Stop();
                cancellation.Dispose();
                throw;
            }
        }
    }

    /// <summary>
    /// Stops the server and drains all active clients.
    /// </summary>
    public Task StopAsync()
    {
        lock (m_LifecycleLock)
        {
            if (m_StopTask is { IsCompleted: false })
            {
                return m_StopTask;
            }

            if (m_Cancellation == null || m_Listener == null || m_AcceptTask == null)
            {
                return m_StopTask ?? Task.CompletedTask;
            }

            m_IsRunning = false;
            m_StopTask = StopCoreAsync(m_Cancellation, m_Listener, m_AcceptTask);
            return m_StopTask;
        }
    }

    /// <summary>
    /// Disposes the server without synchronously blocking the caller.
    /// </summary>
    public void Dispose()
    {
        lock (m_LifecycleLock)
        {
            if (m_Disposed)
            {
                return;
            }

            m_Disposed = true;
        }

        _ = ObserveStopAsync(StopAsync());
    }

    /// <summary>
    /// Cancels the listener and releases one generation of server resources.
    /// </summary>
    private async Task StopCoreAsync(CancellationTokenSource cancellation, TcpListener listener, Task acceptTask)
    {
        await Task.Yield();
        Exception? failure = null;
        try
        {
            failure = CaptureFailure(failure, cancellation.Cancel);
            failure = CaptureFailure(failure, listener.Stop);
            failure = await CaptureFailureAsync(failure, acceptTask).ConfigureAwait(false);

            Task[] clients;
            lock (m_LifecycleLock)
            {
                clients = [.. m_ClientTasks];
            }
            failure = await CaptureFailureAsync(failure, Task.WhenAll(clients)).ConfigureAwait(false);
        }
        finally
        {
            failure = CaptureFailure(failure, listener.Stop);
            failure = CaptureFailure(failure, cancellation.Dispose);

            lock (m_LifecycleLock)
            {
                if (ReferenceEquals(m_Cancellation, cancellation))
                {
                    m_Cancellation = null;
                    m_Listener = null;
                    m_AcceptTask = null;
                    m_IsRunning = false;
                }
            }
        }

        if (failure != null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    /// <summary>
    /// Runs one synchronous cleanup step while retaining the first failure.
    /// </summary>
    private static Exception? CaptureFailure(Exception? failure, Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            return failure ?? exception;
        }

        return failure;
    }

    /// <summary>
    /// Awaits one cleanup step while retaining the first failure.
    /// </summary>
    private static async Task<Exception?> CaptureFailureAsync(Exception? failure, Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            return failure ?? exception;
        }

        return failure;
    }

    /// <summary>
    /// Runs the listener accept loop.
    /// </summary>
    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException exception)
            {
                MarkGenerationFailed(listener, exception.Message);
                break;
            }
            catch (Exception exception)
            {
                MarkGenerationFailed(listener, exception.Message);
                break;
            }

            TaskCompletionSource start = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task? clientTask = null;
            clientTask = HandleClientTrackedAsync(client, cancellationToken, start.Task, () => RemoveClientTask(clientTask!));
            lock (m_LifecycleLock)
            {
                m_ClientTasks.Add(clientTask);
            }

            start.SetResult();
        }
    }

    /// <summary>
    /// Handles and observes one accepted client operation.
    /// </summary>
    private async Task HandleClientTrackedAsync(TcpClient client, CancellationToken cancellationToken, Task startTask, Action removeTask)
    {
        await startTask.ConfigureAwait(false);
        using (client)
        {
            try
            {
                await HandleClientAsync(client, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (cancellationToken.IsCancellationRequested && exception is OperationCanceledException or IOException or ObjectDisposedException)
            {
            }
            catch (Exception exception)
            {
                SetLastError(exception.Message);
            }
            finally
            {
                removeTask();
            }
        }
    }

    /// <summary>
    /// Removes a completed client operation from lifecycle tracking.
    /// </summary>
    private void RemoveClientTask(Task task)
    {
        lock (m_LifecycleLock)
        {
            m_ClientTasks.Remove(task);
        }
    }

    /// <summary>
    /// Records the latest server error.
    /// </summary>
    private void SetLastError(string? error)
    {
        lock (m_LifecycleLock)
        {
            m_LastError = error;
        }
    }

    /// <summary>
    /// Marks the current listener generation as failed without releasing its resources.
    /// </summary>
    private void MarkGenerationFailed(TcpListener listener, string error)
    {
        lock (m_LifecycleLock)
        {
            if (ReferenceEquals(m_Listener, listener))
            {
                m_IsRunning = false;
                m_LastError = error;
            }
        }
    }

    /// <summary>
    /// Observes a fallback stop failure and preserves it as the latest server error.
    /// </summary>
    private async Task ObserveStopAsync(Task stopTask)
    {
        try
        {
            await stopTask.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            SetLastError(exception.Message);
        }
    }

    /// <summary>
    /// Handles one HTTP client request.
    /// </summary>
    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        await using NetworkStream stream = client.GetStream();
        using StreamReader reader = new(stream, Encoding.ASCII, leaveOpen: true);
        string? requestLine = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(requestLine))
        {
            return;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            string? headerLine = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(headerLine))
            {
                break;
            }
        }

        string[] parts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string method = parts.Length > 0 ? parts[0] : string.Empty;
        string path = parts.Length > 1 ? parts[1] : string.Empty;
        if (!string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase))
        {
            await WriteResponseAsync(stream, 405, "Method Not Allowed", "application/json; charset=utf-8", "{\"error\":\"method_not_allowed\"}", cancellationToken).ConfigureAwait(false);
            return;
        }

        if (path.StartsWith(CodexTrayDefaults.HealthEndpointPath, StringComparison.OrdinalIgnoreCase))
        {
            await WriteJsonAsync(stream, 200, new { ok = true }, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (path.StartsWith(CodexTrayDefaults.UsageTextEndpointPath, StringComparison.OrdinalIgnoreCase))
        {
            UsageResponse textResponse = m_UsageCache.Get() ?? CreatePendingResponse();
            SetLastError(null);
            await WriteUsageTextAsync(stream, textResponse, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!path.StartsWith(CodexTrayDefaults.UsageEndpointPath, StringComparison.OrdinalIgnoreCase))
        {
            await WriteResponseAsync(stream, 404, "Not Found", "application/json; charset=utf-8", "{\"error\":\"not_found\"}", cancellationToken).ConfigureAwait(false);
            return;
        }

        UsageResponse response = m_UsageCache.Get() ?? CreatePendingResponse();
        SetLastError(null);
        await WriteJsonAsync(stream, 200, response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Creates a response for requests that arrive before the first collection finishes.
    /// </summary>
    private static UsageResponse CreatePendingResponse()
    {
        return new UsageResponse
        {
            Available = false,
            Error = "Usage has not been collected yet",
            Source = "cache",
            Display = new UsageDisplay(),
        };
    }

    /// <summary>
    /// Writes an object as a JSON HTTP response.
    /// </summary>
    private static Task WriteJsonAsync(NetworkStream stream, int statusCode, object value, CancellationToken cancellationToken)
    {
        string body = JsonSerializer.Serialize(value, s_JsonOptions);
        return WriteResponseAsync(stream, statusCode, "OK", "application/json; charset=utf-8", body, cancellationToken);
    }

    /// <summary>
    /// Writes the compact text response used by the native TrafficMonitor plugin.
    /// </summary>
    private static Task WriteUsageTextAsync(NetworkStream stream, UsageResponse response, CancellationToken cancellationToken)
    {
        string body = string.Join(Environment.NewLine, response.Display.Session, response.Display.Weekly, response.Display.CursorMonthly);
        return WriteResponseAsync(stream, 200, "OK", "text/plain; charset=utf-8", body, cancellationToken);
    }

    /// <summary>
    /// Writes a raw HTTP response.
    /// </summary>
    private static async Task WriteResponseAsync(NetworkStream stream, int statusCode, string reasonPhrase, string contentType, string body, CancellationToken cancellationToken)
    {
        byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
        string headers = $"HTTP/1.1 {statusCode} {reasonPhrase}\r\nContent-Type: {contentType}\r\nContent-Length: {bodyBytes.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n";
        byte[] headerBytes = Encoding.ASCII.GetBytes(headers);
        await stream.WriteAsync(headerBytes, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(bodyBytes, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Throws if the server has already been disposed.
    /// </summary>
    private void ThrowIfDisposed()
    {
        if (m_Disposed)
        {
            throw new ObjectDisposedException(nameof(LightweightHttpServer));
        }
    }
}
