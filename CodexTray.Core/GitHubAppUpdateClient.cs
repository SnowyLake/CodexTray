using System.Net;

namespace CodexTray.Core;

/// <summary>
/// Downloads the latest public GitHub release for this repository.
/// </summary>
public sealed class GitHubAppUpdateClient
{
    private static readonly HttpClient s_HttpClient = new()
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    private readonly HttpClient m_HttpClient;

    /// <summary>
    /// Creates a client that uses the shared HTTP handler.
    /// </summary>
    public GitHubAppUpdateClient()
        : this(s_HttpClient)
    {
    }

    /// <summary>
    /// Creates a client that uses a caller-supplied HTTP handler.
    /// </summary>
    public GitHubAppUpdateClient(HttpClient httpClient)
    {
        m_HttpClient = httpClient;
    }

    /// <summary>
    /// Reads the latest stable release. The caller supplies the timeout through <paramref name="cancellationToken"/>.
    /// </summary>
    public async Task<AppUpdateRelease> GetLatestReleaseAsync(CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = CreateRequest(HttpMethod.Get, new Uri(CodexTrayDefaults.GitHubLatestReleaseUrl));
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using HttpResponseMessage response = await m_HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return AppUpdateReleaseParser.ParseLatest(json);
    }

    /// <summary>
    /// Downloads the release zip and rejects a body whose length differs from the release.
    /// </summary>
    public async Task DownloadPackageAsync(AppUpdateRelease release, string destinationPath, CancellationToken cancellationToken)
    {
        if (release.PackageUrl.Scheme != Uri.UriSchemeHttps
            || !string.Equals(release.PackageUrl.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            throw new AppUpdateException("The update package URL is not the GitHub release asset.");
        }

        if (release.PackageSize <= 0 || release.PackageSize > CodexTrayDefaults.MaximumReleasePackageBytes)
        {
            throw new AppUpdateException("The update package size is not supported.");
        }

        using HttpRequestMessage request = CreateRequest(HttpMethod.Get, release.PackageUrl);
        using HttpResponseMessage response = await m_HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        string? parent = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        await using Stream httpStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using FileStream file = new(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
        byte[] buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            int read = await httpStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
            if (total > release.PackageSize)
            {
                throw new AppUpdateException("The update package size does not match the release.");
            }

            await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        if (total != release.PackageSize)
        {
            throw new AppUpdateException("The update package size does not match the release.");
        }
    }

    /// <summary>
    /// Creates a GitHub request with the required user agent.
    /// </summary>
    private static HttpRequestMessage CreateRequest(HttpMethod method, Uri uri)
    {
        HttpRequestMessage request = new(method, uri);
        request.Headers.TryAddWithoutValidation("User-Agent", CodexTrayDefaults.AppName);
        return request;
    }

    /// <summary>
    /// Maps a failed GitHub response to a message safe to show in the UI.
    /// </summary>
    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string body = string.Empty;
        if (response.Content != null)
        {
            try
            {
                body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or HttpRequestException)
            {
            }
        }

        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests
            || body.Contains("rate limit", StringComparison.OrdinalIgnoreCase))
        {
            throw new AppUpdateException("GitHub rate limit was reached. Try again later.");
        }

        throw new AppUpdateException("Could not reach GitHub. Check the network and try again.");
    }
}

/// <summary>
/// Checks for a release and prepares the files used to apply it.
/// </summary>
public sealed class AppUpdateSession
{
    private readonly GitHubAppUpdateClient m_Client;

    /// <summary>
    /// Creates a session that uses the shared GitHub client.
    /// </summary>
    public AppUpdateSession()
        : this(new GitHubAppUpdateClient())
    {
    }

    /// <summary>
    /// Creates a session that uses a caller-supplied GitHub client.
    /// </summary>
    public AppUpdateSession(GitHubAppUpdateClient client)
    {
        m_Client = client;
    }

    /// <summary>
    /// Compares the running version with the latest stable release.
    /// </summary>
    public async Task<AppUpdateCheckResult> CheckAsync(Version currentVersion, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(currentVersion);
        AppUpdateRelease latest = await CallAsync(
            CodexTrayDefaults.UpdateCheckTimeoutSeconds,
            cancellationToken,
            token => m_Client.GetLatestReleaseAsync(token)).ConfigureAwait(false);
        return new AppUpdateCheckResult(AppUpdateVersion.Compare(currentVersion, latest.Version), latest);
    }

    /// <summary>
    /// Downloads, verifies, and stages a release so a temporary executable can apply it.
    /// </summary>
    public async Task<AppUpdateHandoff> PrepareAsync(AppUpdateRelease release, string currentExecutablePath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(release);
        AppUpdateWork.CleanupStaleDirectories();
        string workDirectory = AppUpdateWork.CreateDirectory();
        try
        {
            return await CallAsync(
                CodexTrayDefaults.UpdateDownloadTimeoutSeconds,
                cancellationToken,
                async token =>
                {
                    string zipPath = Path.Combine(workDirectory, release.PackageName);
                    await m_Client.DownloadPackageAsync(release, zipPath, token).ConfigureAwait(false);
                    AppUpdatePackage.Verify(zipPath, release);
                    string extractedDirectory = Path.Combine(workDirectory, "package");
                    AppUpdatePackage.Extract(zipPath, extractedDirectory);
                    return AppUpdateWork.PrepareHandoff(extractedDirectory, workDirectory, currentExecutablePath);
                }).ConfigureAwait(false);
        }
        catch
        {
            AppUpdateWork.DeleteQuietly(workDirectory);
            throw;
        }
    }

    /// <summary>
    /// Runs a GitHub call with a timeout and maps network failures to an update error.
    /// </summary>
    private static async Task<T> CallAsync<T>(int timeoutSeconds, CancellationToken cancellationToken, Func<CancellationToken, Task<T>> action)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            return await action(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            throw new AppUpdateException("Could not reach GitHub. Check the network and try again.", innerException: exception);
        }
        catch (HttpRequestException exception)
        {
            throw new AppUpdateException("Could not reach GitHub. Check the network and try again.", innerException: exception);
        }
    }
}
