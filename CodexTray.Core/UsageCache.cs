namespace CodexTray.Core;

public sealed class UsageCache
{
    private volatile UsageResponse? m_Response;

    /// <summary>
    /// Stores the latest collected usage response.
    /// </summary>
    public void Update(UsageResponse response)
    {
        m_Response = response;
    }

    /// <summary>
    /// Gets the latest collected usage response if one is available.
    /// </summary>
    public UsageResponse? Get()
    {
        return m_Response;
    }
}
