using System.Net;
using System.Net.Sockets;
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
    private CancellationTokenSource? m_Cancellation;
    private TcpListener? m_Listener;
    private Task? m_AcceptTask;
    private bool m_Disposed;

    public int Port { get; private set; }

    public bool IsRunning { get; private set; }

    public string? LastError { get; private set; }

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
        ThrowIfDisposed();
        if (IsRunning)
        {
            return;
        }

        m_Cancellation = new CancellationTokenSource();
        m_Listener = new TcpListener(IPAddress.Parse(CodexTrayDefaults.Host), Port);
        m_Listener.Start();
        Port = ((IPEndPoint)m_Listener.LocalEndpoint).Port;
        IsRunning = true;
        LastError = null;
        m_AcceptTask = Task.Run(() => AcceptLoopAsync(m_Cancellation.Token));
    }

    /// <summary>
    /// Stops the server and closes active listener resources.
    /// </summary>
    public void Stop()
    {
        if (!IsRunning)
        {
            return;
        }

        m_Cancellation?.Cancel();
        m_Listener?.Stop();
        try
        {
            m_AcceptTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }

        m_AcceptTask = null;
        m_Listener = null;
        m_Cancellation?.Dispose();
        m_Cancellation = null;
        IsRunning = false;
    }

    /// <summary>
    /// Disposes the server resources.
    /// </summary>
    public void Dispose()
    {
        if (m_Disposed)
        {
            return;
        }

        Stop();
        m_Disposed = true;
    }

    /// <summary>
    /// Runs the listener accept loop.
    /// </summary>
    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && m_Listener != null)
        {
            TcpClient client;
            try
            {
                client = await m_Listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException exception)
            {
                LastError = exception.Message;
                break;
            }

            _ = Task.Run(() => HandleClientAsync(client, cancellationToken), cancellationToken);
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
            LastError = null;
            await WriteUsageTextAsync(stream, textResponse, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!path.StartsWith(CodexTrayDefaults.UsageEndpointPath, StringComparison.OrdinalIgnoreCase))
        {
            await WriteResponseAsync(stream, 404, "Not Found", "application/json; charset=utf-8", "{\"error\":\"not_found\"}", cancellationToken).ConfigureAwait(false);
            return;
        }

        UsageResponse response = m_UsageCache.Get() ?? CreatePendingResponse();
        LastError = null;

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
        string body = string.Join(Environment.NewLine, response.Display.Codex5H, response.Display.Codex7D);
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
