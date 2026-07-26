namespace CodexTray.Core;

internal sealed class TokenCostPeriodAccumulator
{
    private readonly DateTime m_Today;
    private readonly DateTime m_WeekStart;
    private readonly DateTime m_MonthStart;
    private readonly PeriodAccumulator m_TodayPeriod = new();
    private readonly PeriodAccumulator m_YesterdayPeriod = new();
    private readonly PeriodAccumulator m_WeekPeriod = new();
    private readonly PeriodAccumulator m_MonthPeriod = new();
    private readonly PeriodAccumulator m_SevenDayPeriod = new();
    private readonly PeriodAccumulator m_ThirtyDayPeriod = new();
    private readonly PeriodAccumulator m_TotalPeriod = new();

    /// <summary>
    /// Creates calendar periods relative to one local timestamp.
    /// </summary>
    public TokenCostPeriodAccumulator(DateTimeOffset asOf)
    {
        m_Today = asOf.LocalDateTime.Date;
        m_WeekStart = m_Today.AddDays(-((int)m_Today.DayOfWeek + 6) % 7);
        m_MonthStart = new DateTime(m_Today.Year, m_Today.Month, 1);
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

        if (eventDate == m_Today.AddDays(-1))
        {
            m_YesterdayPeriod.Add(tokens, costUsd);
        }

        if (eventDate >= m_WeekStart)
        {
            m_WeekPeriod.Add(tokens, costUsd);
        }

        if (eventDate >= m_MonthStart)
        {
            m_MonthPeriod.Add(tokens, costUsd);
        }

        if (eventDate >= m_Today.AddDays(-6))
        {
            m_SevenDayPeriod.Add(tokens, costUsd);
        }

        if (eventDate >= m_Today.AddDays(-29))
        {
            m_ThirtyDayPeriod.Add(tokens, costUsd);
        }

        m_TotalPeriod.Add(tokens, costUsd);
    }

    /// <summary>
    /// Converts the accumulated calendar periods into token-cost statistics.
    /// </summary>
    public TokenCostStatistics ToStatistics()
    {
        return new TokenCostStatistics
        {
            Today = m_TodayPeriod.ToSummary(),
            Yesterday = m_YesterdayPeriod.ToSummary(),
            Week = m_WeekPeriod.ToSummary(),
            Month = m_MonthPeriod.ToSummary(),
            SevenDay = m_SevenDayPeriod.ToSummary(),
            ThirtyDay = m_ThirtyDayPeriod.ToSummary(),
            Total = m_TotalPeriod.ToSummary(),
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
