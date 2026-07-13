namespace CodexTray.Core;

public sealed class UsageCache
{
    private readonly object m_Lock = new();
    private UsageResponse? m_Response;

    /// <summary>
    /// Stores the latest collected usage response.
    /// </summary>
    public void Update(UsageResponse response)
    {
        lock (m_Lock)
        {
            m_Response = response;
        }
    }

    /// <summary>
    /// Gets the latest collected usage response if one is available.
    /// </summary>
    public UsageResponse? Get()
    {
        lock (m_Lock)
        {
            return m_Response;
        }
    }
}
