using System.Net;

namespace CodexTray.Core;

/// <summary>
/// Supplies fixed diagnostic categories without copying untrusted exception or response content.
/// </summary>
public static class UsageDiagnostics
{
    /// <summary>
    /// Describes an HTTP status without including a server reason phrase.
    /// </summary>
    public static string HttpError(HttpStatusCode status) => $"{status switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "Authentication failed; sign in or check credentials",
        HttpStatusCode.TooManyRequests => "Rate limited; retry later",
        _ when (int)status >= 500 => "Service unavailable",
        _ => "Request rejected",
    }} (HTTP {(int)status})";

    /// <summary>
    /// Converts failures to a safe message without echoing exception messages.
    /// </summary>
    public static string Error(Exception exception) => exception switch
    {
        UsageHttpException => exception.Message,
        TaskCanceledException => "Request timed out",
        HttpRequestException => "Network request failed",
        IOException or UnauthorizedAccessException => "Local data could not be read",
        _ => "Invalid response format",
    };

    /// <summary>
    /// Classifies already sanitized display messages for copyable diagnostics.
    /// </summary>
    public static string Category(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            return "Available";
        }
        if (error.Contains("HTTP 429", StringComparison.OrdinalIgnoreCase) || error.Contains("rate limited", StringComparison.OrdinalIgnoreCase))
        {
            return "Rate limit";
        }
        if (error.Contains("timed out", StringComparison.OrdinalIgnoreCase))
        {
            return "Timeout";
        }
        if (error.Contains("network", StringComparison.OrdinalIgnoreCase))
        {
            return "Network";
        }
        if (error.Contains("OAuth", StringComparison.OrdinalIgnoreCase) || error.Contains("Authentication", StringComparison.OrdinalIgnoreCase) || error.Contains("HTTP 401") || error.Contains("HTTP 403"))
        {
            return "Authentication";
        }
        if (error.Contains("Enter ", StringComparison.OrdinalIgnoreCase) || error.Contains("Not refreshed", StringComparison.OrdinalIgnoreCase))
        {
            return "Configuration / pending";
        }
        if (error.Contains("HTTP 5", StringComparison.OrdinalIgnoreCase))
        {
            return "Service unavailable";
        }
        if (error.Contains("Local ", StringComparison.OrdinalIgnoreCase))
        {
            return "Local data";
        }
        return "Response format / request rejected";
    }
}
