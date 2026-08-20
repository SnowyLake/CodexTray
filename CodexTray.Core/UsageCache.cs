namespace CodexTray.Core;

public sealed class UsageCache
{
    private volatile UsageResponse? m_CodexResponse;
    private volatile GrokPluginUsage? m_GrokUsage;
    private volatile CursorPluginUsage? m_CursorUsage;

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
    /// Stores the latest collected Cursor usage response.
    /// </summary>
    public void UpdateCursor(CursorPluginUsage usage)
    {
        m_CursorUsage = usage;
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
    /// Clears the cached Cursor usage response.
    /// </summary>
    public void ClearCursor()
    {
        m_CursorUsage = null;
    }

    /// <summary>
    /// Gets a merged plugin response from the latest Codex, Cursor, and Grok values.
    /// </summary>
    public UsageResponse? Get()
    {
        UsageResponse? codex = m_CodexResponse;
        GrokPluginUsage? grok = m_GrokUsage;
        CursorPluginUsage? cursor = m_CursorUsage;
        if (codex == null && grok == null && cursor == null)
        {
            return null;
        }

        codex ??= new UsageResponse
        {
            Available = false,
            Error = "Codex usage has not been collected yet",
            Source = "cache",
        };
        UsageLimit cursorMonthly = cursor?.Monthly ?? new UsageLimit
        {
            Name = "monthly",
            UsedPercent = 100,
            RemainingPercent = 0,
        };
        UsageLimit grokWeekly = grok?.Weekly ?? new UsageLimit
        {
            Name = "weekly",
            UsedPercent = 100,
            RemainingPercent = 0,
        };
        string grokDisplay = grok?.Display ?? CodexTrayDefaults.UnavailableDisplay;
        string cursorDisplay = cursor?.Display ?? CodexTrayDefaults.UnavailableDisplay;
        return new UsageResponse
        {
            Available = codex.Available,
            Error = codex.Error,
            CodexDir = codex.CodexDir,
            SourceFile = codex.SourceFile,
            Source = codex.Source,
            PlanType = codex.PlanType,
            UpdatedAt = codex.UpdatedAt,
            Limits = new UsageLimits
            {
                Session = codex.Limits.Session,
                Weekly = codex.Limits.Weekly,
                CursorMonthly = cursorMonthly,
                GrokWeekly = grokWeekly,
            },
            ResetCredits = codex.ResetCredits,
            Display = new UsageDisplay
            {
                Weekly = codex.Display.Weekly,
                CursorMonthly = cursorDisplay,
                GrokWeekly = grokDisplay,
                Summary = $"Codex: {codex.Display.Weekly} | Cursor: {cursorDisplay} | Grok: {grokDisplay}",
            },
        };
    }
}
