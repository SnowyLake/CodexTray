namespace CodexTray.Core;

internal sealed class TokenCostPeriodAccumulator
{
    private readonly DateTime m_Today;
    private readonly DateTime m_LastSevenDaysStart;
    private readonly PeriodAccumulator[] m_LastSevenDaysDailyPeriods;
    private readonly PeriodAccumulator m_TodayPeriod;
    private readonly PeriodAccumulator m_LastSevenDaysPeriod;
    private readonly PeriodAccumulator m_LastThirtyDaysPeriod;
    private readonly PeriodAccumulator m_LifetimePeriod;

    /// <summary>
    /// Creates calendar periods relative to one local timestamp with optional complete-cost enforcement.
    /// </summary>
    public TokenCostPeriodAccumulator(DateTimeOffset asOf, bool requireCompleteCosts = false)
    {
        m_Today = asOf.LocalDateTime.Date;
        m_LastSevenDaysStart = m_Today.AddDays(-6);
        m_TodayPeriod = new PeriodAccumulator(requireCompleteCosts);
        m_LastSevenDaysPeriod = new PeriodAccumulator(requireCompleteCosts);
        m_LastThirtyDaysPeriod = new PeriodAccumulator(requireCompleteCosts);
        m_LifetimePeriod = new PeriodAccumulator(requireCompleteCosts);
        m_LastSevenDaysDailyPeriods = Enumerable.Range(0, 7).Select(_ => new PeriodAccumulator(requireCompleteCosts)).ToArray();
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
        private readonly bool m_RequireCompleteCosts;
        private bool m_HasUnpricedUsage;
        private long m_TotalTokens;
        private decimal m_TotalCost;

        /// <summary>
        /// Creates one period accumulator with the requested missing-cost behavior.
        /// </summary>
        public PeriodAccumulator(bool requireCompleteCosts)
        {
            m_RequireCompleteCosts = requireCompleteCosts;
        }

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
            else if (tokens > 0)
            {
                m_HasUnpricedUsage = true;
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
                CostUsd = m_RequireCompleteCosts && m_HasUnpricedUsage ? null : m_TotalCost,
            };
        }
    }
}
