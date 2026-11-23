namespace CodexTray.Core;

public sealed class UsageCache
{
    private volatile UsageResponse? m_CodexResponse;
    private volatile GrokPluginUsage? m_GrokUsage;
    private volatile GeminiPluginUsage? m_GeminiUsage;
    private volatile OpenCodePluginUsage? m_OpenCodeUsage;

    /// <summary>
    /// Stores the latest collected Codex usage response.
    /// </summary>
    public void UpdateCodex(UsageResponse response)
    {
        m_CodexResponse = response;
    }

    /// <summary>
    /// Stores the latest collected Grok usage response.
    /// </summary>
    public void UpdateGrok(GrokPluginUsage usage)
    {
        m_GrokUsage = usage;
    }

    /// <summary>
    /// Stores the latest collected Gemini plugin snapshot.
    /// </summary>
    public void UpdateGemini(GeminiPluginUsage usage)
    {
        m_GeminiUsage = usage;
    }

    /// <summary>
    /// Stores the latest collected OpenCode plugin snapshot.
    /// </summary>
    public void UpdateOpenCode(OpenCodePluginUsage usage)
    {
        m_OpenCodeUsage = usage;
    }

    /// <summary>
    /// Clears the cached Codex usage response.
    /// </summary>
    public void ClearCodex()
    {
        m_CodexResponse = null;
    }

    /// <summary>
    /// Clears the cached Grok usage response.
    /// </summary>
    public void ClearGrok()
    {
        m_GrokUsage = null;
    }

    /// <summary>
    /// Clears the cached Gemini plugin snapshot.
    /// </summary>
    public void ClearGemini()
    {
        m_GeminiUsage = null;
    }

    /// <summary>
    /// Clears the cached OpenCode plugin snapshot.
    /// </summary>
    public void ClearOpenCode()
    {
        m_OpenCodeUsage = null;
    }

    /// <summary>
    /// Gets the latest Codex plugin snapshot without synthesizing a missing Codex failure.
    /// </summary>
    public UsageResponse? GetCodex()
    {
        return m_CodexResponse;
    }

    /// <summary>
    /// Gets a merged plugin response from the latest Codex, Grok, Gemini, and OpenCode values.
    /// </summary>
    public UsageResponse? Get()
    {
        UsageResponse? codex = m_CodexResponse;
        GrokPluginUsage? grok = m_GrokUsage;
        GeminiPluginUsage? gemini = m_GeminiUsage;
        OpenCodePluginUsage? openCode = m_OpenCodeUsage;
        if (codex == null && grok == null && gemini == null && openCode == null)
        {
            return null;
        }

        codex ??= new UsageResponse
        {
            Available = false,
            Error = "Codex usage has not been collected yet",
            Source = "cache",
        };
        UsageLimit grokWeekly = grok?.Weekly ?? new UsageLimit
        {
            Name = "weekly",
            UsedPercent = 100,
            RemainingPercent = 0,
        };
        UsageLimit geminiSession = gemini?.Session ?? new UsageLimit
        {
            Name = "session",
            UsedPercent = 100,
            RemainingPercent = 0,
        };
        UsageLimit openCodeSession = openCode?.Session ?? new UsageLimit
        {
            Name = "session",
            UsedPercent = 100,
            RemainingPercent = 0,
        };
        string grokDisplay = grok?.Display ?? CodexTrayDefaults.UnavailableDisplay;
        string geminiDisplay = gemini?.Display ?? CodexTrayDefaults.UnavailableDisplay;
        string openCodeDisplay = openCode?.Display ?? CodexTrayDefaults.UnavailableDisplay;
        return new UsageResponse
        {
            Available = codex.Available,
            Error = codex.Error,
            Source = codex.Source,
            PlanType = codex.PlanType,
            UpdatedAt = codex.UpdatedAt,
            Limits = new UsageLimits
            {
                Session = codex.Limits.Session,
                Weekly = codex.Limits.Weekly,
                GrokWeekly = grokWeekly,
                GeminiSession = geminiSession,
                OpenCodeSession = openCodeSession,
            },
            ResetCredits = codex.ResetCredits,
            Display = new UsageDisplay
            {
                Weekly = codex.Display.Weekly,
                GrokWeekly = grokDisplay,
                GeminiSession = geminiDisplay,
                OpenCodeSession = openCodeDisplay,
            },
        };
    }
}
