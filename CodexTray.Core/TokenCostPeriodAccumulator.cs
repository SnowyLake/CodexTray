namespace CodexTray.Core;

internal sealed class TokenCostPeriodAccumulator
{
    private readonly bool m_RequireCompleteCosts;
    private readonly DateTime m_Today;
    private readonly DateTime m_LastSevenDaysStart;
    private readonly DateTime m_LastThirtyDaysStart;
    private readonly DateTime m_CurrentWeekStart;
    private readonly DateTime m_CurrentMonthStart;
    private readonly PeriodAccumulator[] m_LastThirtyDaysDailyPeriods;
    private readonly PeriodAccumulator[] m_CurrentMonthDailyPeriods;
    private readonly Dictionary<string, ModelPeriodAccumulator> m_ModelPeriods = new(StringComparer.OrdinalIgnoreCase);
    private readonly PeriodAccumulator m_TodayPeriod;
    private readonly PeriodAccumulator m_LastSevenDaysPeriod;
    private readonly PeriodAccumulator m_LastThirtyDaysPeriod;
    private readonly PeriodAccumulator m_CurrentWeekPeriod;
    private readonly PeriodAccumulator m_CurrentMonthPeriod;
    private readonly PeriodAccumulator m_LifetimePeriod;

    /// <summary>
    /// Creates calendar periods relative to one local timestamp with optional complete-cost enforcement.
    /// </summary>
    public TokenCostPeriodAccumulator(DateTimeOffset asOf, bool requireCompleteCosts = false)
    {
        m_RequireCompleteCosts = requireCompleteCosts;
        m_Today = asOf.LocalDateTime.Date;
        m_LastSevenDaysStart = m_Today.AddDays(-6);
        m_LastThirtyDaysStart = m_Today.AddDays(-29);
        m_CurrentWeekStart = m_Today.AddDays(-(((int)m_Today.DayOfWeek + 6) % 7));
        m_CurrentMonthStart = new DateTime(m_Today.Year, m_Today.Month, 1);
        m_TodayPeriod = new PeriodAccumulator(requireCompleteCosts);
        m_LastSevenDaysPeriod = new PeriodAccumulator(requireCompleteCosts);
        m_LastThirtyDaysPeriod = new PeriodAccumulator(requireCompleteCosts);
        m_CurrentWeekPeriod = new PeriodAccumulator(requireCompleteCosts);
        m_CurrentMonthPeriod = new PeriodAccumulator(requireCompleteCosts);
        m_LifetimePeriod = new PeriodAccumulator(requireCompleteCosts);
        m_LastThirtyDaysDailyPeriods = Enumerable.Range(0, 30).Select(_ => new PeriodAccumulator(requireCompleteCosts)).ToArray();
        m_CurrentMonthDailyPeriods = Enumerable.Range(0, m_Today.Day).Select(_ => new PeriodAccumulator(requireCompleteCosts)).ToArray();
    }

    /// <summary>
    /// Adds one event to every matching calendar period.
    /// </summary>
    public void Add(
        DateTimeOffset timestamp,
        long tokens,
        decimal? costUsd,
        string? model = null,
        long cacheReadTokens = 0,
        long cacheableInputTokens = 0)
    {
        DateTime eventDate = timestamp.LocalDateTime.Date;
        if (eventDate > m_Today)
        {
            return;
        }

        if (eventDate == m_Today)
        {
            m_TodayPeriod.Add(tokens, costUsd, cacheReadTokens, cacheableInputTokens);
        }

        if (eventDate >= m_LastSevenDaysStart)
        {
            m_LastSevenDaysPeriod.Add(tokens, costUsd, cacheReadTokens, cacheableInputTokens);
        }

        if (eventDate >= m_LastThirtyDaysStart)
        {
            m_LastThirtyDaysDailyPeriods[(eventDate - m_LastThirtyDaysStart).Days].Add(tokens, costUsd, cacheReadTokens, cacheableInputTokens);
            m_LastThirtyDaysPeriod.Add(tokens, costUsd, cacheReadTokens, cacheableInputTokens);
        }

        if (eventDate >= m_CurrentWeekStart)
        {
            m_CurrentWeekPeriod.Add(tokens, costUsd, cacheReadTokens, cacheableInputTokens);
        }

        if (eventDate >= m_CurrentMonthStart)
        {
            m_CurrentMonthDailyPeriods[(eventDate - m_CurrentMonthStart).Days].Add(tokens, costUsd, cacheReadTokens, cacheableInputTokens);
            m_CurrentMonthPeriod.Add(tokens, costUsd, cacheReadTokens, cacheableInputTokens);
        }

        m_LifetimePeriod.Add(tokens, costUsd, cacheReadTokens, cacheableInputTokens);
        if (!string.IsNullOrWhiteSpace(model))
        {
            if (!m_ModelPeriods.TryGetValue(model, out ModelPeriodAccumulator? modelPeriods))
            {
                modelPeriods = new ModelPeriodAccumulator(m_RequireCompleteCosts);
                m_ModelPeriods.Add(model, modelPeriods);
            }

            modelPeriods.Add(eventDate, m_Today, m_LastSevenDaysStart, m_CurrentWeekStart, m_CurrentMonthStart, tokens, costUsd, cacheReadTokens, cacheableInputTokens);
        }
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
            CurrentWeek = m_CurrentWeekPeriod.ToSummary(),
            CurrentMonth = m_CurrentMonthPeriod.ToSummary(),
            Lifetime = m_LifetimePeriod.ToSummary(),
            LastSevenDaysDaily = m_LastThirtyDaysDailyPeriods
                .Skip(23)
                .Select((period, index) => new TokenCostDailySummary
                {
                    Date = m_LastSevenDaysStart.AddDays(index),
                    Summary = period.ToSummary(),
                })
                .ToArray(),
            LastThirtyDaysDaily = m_LastThirtyDaysDailyPeriods
                .Select((period, index) => new TokenCostDailySummary
                {
                    Date = m_LastThirtyDaysStart.AddDays(index),
                    Summary = period.ToSummary(),
                })
                .ToArray(),
            CurrentMonthDaily = m_CurrentMonthDailyPeriods
                .Select((period, index) => new TokenCostDailySummary
                {
                    Date = m_CurrentMonthStart.AddDays(index),
                    Summary = period.ToSummary(),
                })
                .ToArray(),
            Models = m_ModelPeriods
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => pair.Value.ToStatistics(pair.Key))
                .ToArray(),
        };
    }

    private sealed class ModelPeriodAccumulator
    {
        private readonly PeriodAccumulator m_TodayPeriod;
        private readonly PeriodAccumulator m_LastSevenDaysPeriod;
        private readonly PeriodAccumulator m_LastThirtyDaysPeriod;
        private readonly PeriodAccumulator m_CurrentWeekPeriod;
        private readonly PeriodAccumulator m_CurrentMonthPeriod;
        private readonly PeriodAccumulator m_LifetimePeriod;

        /// <summary>
        /// Creates model-specific accumulators with the requested missing-cost behavior.
        /// </summary>
        public ModelPeriodAccumulator(bool requireCompleteCosts)
        {
            m_TodayPeriod = new PeriodAccumulator(requireCompleteCosts);
            m_LastSevenDaysPeriod = new PeriodAccumulator(requireCompleteCosts);
            m_LastThirtyDaysPeriod = new PeriodAccumulator(requireCompleteCosts);
            m_CurrentWeekPeriod = new PeriodAccumulator(requireCompleteCosts);
            m_CurrentMonthPeriod = new PeriodAccumulator(requireCompleteCosts);
            m_LifetimePeriod = new PeriodAccumulator(requireCompleteCosts);
        }

        /// <summary>
        /// Adds one model event to every matching calendar period.
        /// </summary>
        public void Add(
            DateTime eventDate,
            DateTime today,
            DateTime lastSevenDaysStart,
            DateTime currentWeekStart,
            DateTime currentMonthStart,
            long tokens,
            decimal? costUsd,
            long cacheReadTokens,
            long cacheableInputTokens)
        {
            if (eventDate == today)
            {
                m_TodayPeriod.Add(tokens, costUsd, cacheReadTokens, cacheableInputTokens);
            }

            if (eventDate >= lastSevenDaysStart)
            {
                m_LastSevenDaysPeriod.Add(tokens, costUsd, cacheReadTokens, cacheableInputTokens);
            }

            if (eventDate >= today.AddDays(-29))
            {
                m_LastThirtyDaysPeriod.Add(tokens, costUsd, cacheReadTokens, cacheableInputTokens);
            }

            if (eventDate >= currentWeekStart)
            {
                m_CurrentWeekPeriod.Add(tokens, costUsd, cacheReadTokens, cacheableInputTokens);
            }

            if (eventDate >= currentMonthStart)
            {
                m_CurrentMonthPeriod.Add(tokens, costUsd, cacheReadTokens, cacheableInputTokens);
            }

            m_LifetimePeriod.Add(tokens, costUsd, cacheReadTokens, cacheableInputTokens);
        }

        /// <summary>
        /// Converts one model's accumulated periods into display statistics.
        /// </summary>
        public TokenCostModelStatistics ToStatistics(string model)
        {
            return new TokenCostModelStatistics
            {
                Model = model,
                Today = m_TodayPeriod.ToSummary(),
                LastSevenDays = m_LastSevenDaysPeriod.ToSummary(),
                LastThirtyDays = m_LastThirtyDaysPeriod.ToSummary(),
                CurrentWeek = m_CurrentWeekPeriod.ToSummary(),
                CurrentMonth = m_CurrentMonthPeriod.ToSummary(),
                Lifetime = m_LifetimePeriod.ToSummary(),
            };
        }
    }

    private sealed class PeriodAccumulator
    {
        private readonly bool m_RequireCompleteCosts;
        private bool m_HasUnpricedUsage;
        private long m_TotalTokens;
        private long m_CacheReadTokens;
        private long m_CacheableInputTokens;
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
        public void Add(long tokens, decimal? costUsd, long cacheReadTokens, long cacheableInputTokens)
        {
            m_TotalTokens = checked(m_TotalTokens + tokens);
            m_CacheReadTokens = checked(m_CacheReadTokens + Math.Max(0, cacheReadTokens));
            m_CacheableInputTokens = checked(m_CacheableInputTokens + Math.Max(0, cacheableInputTokens));
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
                CacheReadTokens = m_CacheReadTokens,
                CacheableInputTokens = m_CacheableInputTokens,
            };
        }
    }
}
