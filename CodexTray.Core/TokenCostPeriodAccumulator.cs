namespace CodexTray.Core;

internal sealed class TokenCostPeriodAccumulator
{
    private readonly DateTime m_Today;
    private readonly DateTime m_LastSevenDaysStart;
    private readonly PeriodAccumulator[] m_LastSevenDaysDailyPeriods;
    private readonly PeriodAccumulator m_TodayPeriod = new();
    private readonly PeriodAccumulator m_LastSevenDaysPeriod = new();
    private readonly PeriodAccumulator m_LastThirtyDaysPeriod = new();
    private readonly PeriodAccumulator m_LifetimePeriod = new();

    /// <summary>
    /// Creates calendar periods relative to one local timestamp.
    /// </summary>
    public TokenCostPeriodAccumulator(DateTimeOffset asOf)
    {
        m_Today = asOf.LocalDateTime.Date;
        m_LastSevenDaysStart = m_Today.AddDays(-6);
        m_LastSevenDaysDailyPeriods = Enumerable.Range(0, 7).Select(_ => new PeriodAccumulator()).ToArray();
    }

    /// <summary>
    /// Adds one event to every matching calendar period.
    /// </summary>
    public void Add(DateTimeOffset timestamp, long tokens, decimal? costUsd)
    {
        DateTime eventDate = timestamp.LocalDateTime.Date;
        if (eventDate > m_Today)
        {
            return;
        }

        if (eventDate == m_Today)
        {
            m_TodayPeriod.Add(tokens, costUsd);
        }

        if (eventDate >= m_LastSevenDaysStart)
        {
            m_LastSevenDaysPeriod.Add(tokens, costUsd);
            m_LastSevenDaysDailyPeriods[(eventDate - m_LastSevenDaysStart).Days].Add(tokens, costUsd);
        }

        if (eventDate >= m_Today.AddDays(-29))
        {
            m_LastThirtyDaysPeriod.Add(tokens, costUsd);
        }

        m_LifetimePeriod.Add(tokens, costUsd);
    }

    /// <summary>
    /// Converts the accumulated calendar periods into token-cost statistics.
    /// </summary>
    public TokenCostStatistics ToStatistics()
    {
        return new TokenCostStatistics
        {
            Today = m_TodayPeriod.ToSummary(),
            LastSevenDays = m_LastSevenDaysPeriod.ToSummary(),
            LastThirtyDays = m_LastThirtyDaysPeriod.ToSummary(),
            Lifetime = m_LifetimePeriod.ToSummary(),
            LastSevenDaysDaily = m_LastSevenDaysDailyPeriods
                .Select((period, index) => new TokenCostDailySummary
                {
                    Date = m_LastSevenDaysStart.AddDays(index),
                    Summary = period.ToSummary(),
                })
                .ToArray(),
        };
    }

    private sealed class PeriodAccumulator
    {
        private long m_TotalTokens;
        private decimal m_TotalCost;

        /// <summary>
        /// Adds one usage value to this period.
        /// </summary>
        public void Add(long tokens, decimal? costUsd)
        {
            m_TotalTokens = checked(m_TotalTokens + tokens);
            if (costUsd.HasValue)
            {
                m_TotalCost += costUsd.Value;
            }
        }

        /// <summary>
        /// Converts accumulated values into a token-cost summary.
        /// </summary>
        public TokenCostSummary ToSummary()
        {
            return new TokenCostSummary
            {
                TotalTokens = m_TotalTokens,
                CostUsd = m_TotalCost,
            };
        }
    }
}
