using System.Net;

namespace CodexTray.Core;

/// <summary>
/// Retries read-only usage queries without replaying rotating OAuth token requests.
/// </summary>
internal static class UsageHttp
{
    /// <summary>
    /// Sends up to three fresh requests, respecting server delays within a ten-second wait budget.
    /// </summary>
    internal static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpRequestMessage request, CancellationToken cancellationToken,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        delay ??= Task.Delay;
        byte[]? body = request.Content == null ? null : await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        TimeSpan waited = TimeSpan.Zero;
        for (int attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using HttpRequestMessage copy = new(request.Method, request.RequestUri) { Version = request.Version, VersionPolicy = request.VersionPolicy };
            foreach (var header in request.Headers)
            {
                copy.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            if (body != null)
            {
                copy.Content = new ByteArrayContent(body);
                foreach (var header in request.Content!.Headers)
                {
                    copy.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }

            TimeSpan wait = TimeSpan.FromMilliseconds(500 * (attempt + 1));
            HttpResponseMessage? response = null;
            try
            {
                response = await client.SendAsync(copy, cancellationToken).ConfigureAwait(false);
                if (attempt == 2 || !IsTransient(response.StatusCode))
                {
                    return response;
                }

                if (response.Headers.RetryAfter?.Delta is TimeSpan delta)
                {
                    wait = delta > TimeSpan.Zero ? delta : TimeSpan.Zero;
                }
                else if (response.Headers.RetryAfter?.Date is DateTimeOffset date)
                {
                    wait = date > DateTimeOffset.UtcNow ? date - DateTimeOffset.UtcNow : TimeSpan.Zero;
                }

                if (wait > TimeSpan.FromSeconds(10) - waited)
                {
                    return response;
                }
            }
            catch (Exception exception) when (attempt < 2 && !cancellationToken.IsCancellationRequested && exception is HttpRequestException or TaskCanceledException)
            {
                if (wait > TimeSpan.FromSeconds(10) - waited)
                {
                    throw;
                }
            }
            response?.Dispose();
            await delay(wait, cancellationToken).ConfigureAwait(false);
            waited += wait;
        }
    }

    /// <summary>
    /// Identifies retryable HTTP failures from usage endpoints.
    /// </summary>
    internal static bool IsTransient(HttpStatusCode status) => status == HttpStatusCode.TooManyRequests || (int)status >= 500;
}

/// <summary>
/// Carries only a status code and a fixed safe description, never response text or request credentials.
/// </summary>
internal sealed class UsageHttpException(HttpStatusCode status) : InvalidOperationException(UsageDiagnostics.HttpError(status))
{
    internal HttpStatusCode Status { get; } = status;
}
