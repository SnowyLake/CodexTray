using CodexTray.Core;

namespace CodexTray.App;

internal sealed record RefreshRequest(PageItem Pages, string[] ApiIds, bool AllApiCards);

/// <summary>
/// Merges pending retry targets while one owner drains the previous collection.
/// </summary>
internal sealed class RefreshRequests
{
    private PageItem m_Pages;
    private bool m_AllApiCards;
    private readonly HashSet<string> m_ApiIds = [];

    /// <summary>
    /// Adds source or card targets, allowing a full API refresh to subsume card retries.
    /// </summary>
    internal void Add(PageItem pages, string? apiId = null)
    {
        m_Pages |= pages;
        if ((pages & PageItem.Apis) != 0)
        {
            m_AllApiCards |= apiId == null;
            if (apiId != null)
            {
                m_ApiIds.Add(apiId);
            }
        }
    }

    /// <summary>
    /// Takes and clears the merged pending batch.
    /// </summary>
    internal RefreshRequest? Take()
    {
        if (m_Pages == PageItem.None)
        {
            return null;
        }

        RefreshRequest request = new(m_Pages, m_ApiIds.ToArray(), m_AllApiCards);
        m_Pages = PageItem.None;
        m_AllApiCards = false;
        m_ApiIds.Clear();
        return request;
    }
}

/// <summary>
/// Retains independent card results and rejects late results for edited or removed configurations.
/// </summary>
internal sealed class ApiUsageSnapshots
{
    private readonly Dictionary<string, (ApiMonitorSettings Settings, ApiUsageResult Result)> m_Results = [];

    /// <summary>
    /// Merges a selected refresh and returns snapshots in the current card order.
    /// </summary>
    internal IReadOnlyList<ApiUsageResult> Merge(IReadOnlyList<ApiMonitorSettings> current, IReadOnlyList<ApiMonitorSettings> queried, IReadOnlyList<ApiUsageResult> results)
    {
        foreach (ApiUsageResult result in results)
        {
            ApiMonitorSettings? query = queried.FirstOrDefault(card => card.Id == result.MonitorId);
            ApiMonitorSettings? live = current.FirstOrDefault(card => card.Id == result.MonitorId);
            if (query != null && live != null && Matches(query, live))
            {
                m_Results[live.Id] = (query, result);
            }
        }

        foreach (string id in m_Results.Keys.ToArray())
        {
            ApiMonitorSettings? live = current.FirstOrDefault(card => card.Id == id);
            if (live == null || !Matches(m_Results[id].Settings, live))
            {
                m_Results.Remove(id);
            }
        }

        return current.Select(card => m_Results.TryGetValue(card.Id, out var stored) ? stored.Result
            : new ApiUsageResult(card.Id, false, "N/A", "N/A", "Not refreshed with this configuration", DateTimeOffset.Now, Provider: card.Provider)).ToArray();
    }

    /// <summary>
    /// Compares the fields that affect an API request without logging credential values.
    /// </summary>
    private static bool Matches(ApiMonitorSettings first, ApiMonitorSettings second) => first.Provider == second.Provider && first.BaseUrl == second.BaseUrl
        && first.ApiKey == second.ApiKey && first.UserId == second.UserId;
}
