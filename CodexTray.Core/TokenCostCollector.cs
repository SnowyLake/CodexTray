using System.Globalization;
using System.Text.Json;

namespace CodexTray.Core;

public sealed class TokenCostSummary
{
    public long TotalTokens { get; init; }

    public decimal? CostUsd { get; init; }
}

public sealed class TokenCostStatistics
{
    public TokenCostSummary Today { get; init; } = new();

    public TokenCostSummary Yesterday { get; init; } = new();

    public TokenCostSummary Week { get; init; } = new();

    public TokenCostSummary Month { get; init; } = new();

    public TokenCostSummary SevenDay { get; init; } = new();

    public TokenCostSummary ThirtyDay { get; init; } = new();
}

public sealed class TokenCostCollector
{
    private readonly string m_PricingPath;

    /// <summary>
    /// Creates a collector using the published model pricing resource.
    /// </summary>
    public TokenCostCollector()
        : this(Path.Combine(AppContext.BaseDirectory, CodexTrayDefaults.ResourcesDirectoryName, CodexTrayDefaults.ModelPricingFileName))
    {
    }

    /// <summary>
    /// Creates a collector using an explicit model pricing file.
    /// </summary>
    public TokenCostCollector(string pricingPath)
    {
        m_PricingPath = pricingPath;
    }

    /// <summary>
    /// Collects today's exact Codex token usage and API-equivalent cost.
    /// </summary>
    public TokenCostSummary CollectToday(string? codexDirectory = null, DateTimeOffset? now = null)
    {
        return Collect(codexDirectory, now).Today;
    }

    /// <summary>
    /// Collects Codex token usage for the supported calendar periods.
    /// </summary>
    public TokenCostStatistics Collect(string? codexDirectory = null, DateTimeOffset? now = null)
    {
        string root = codexDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        DateTimeOffset current = now ?? DateTimeOffset.Now;
        DateTime today = current.LocalDateTime.Date;
        DateTime weekStart = today.AddDays(-((int)today.DayOfWeek + 6) % 7);
        DateTime monthStart = new(today.Year, today.Month, 1);
        Dictionary<string, ModelPricing> pricing = LoadPricing();
        PeriodAccumulator todayPeriod = new();
        PeriodAccumulator yesterdayPeriod = new();
        PeriodAccumulator weekPeriod = new();
        PeriodAccumulator monthPeriod = new();
        PeriodAccumulator sevenDayPeriod = new();
        PeriodAccumulator thirtyDayPeriod = new();

        foreach (string path in EnumerateSessionFiles(root))
        {
            CollectFile(path, today, weekStart, monthStart, pricing, todayPeriod, yesterdayPeriod, weekPeriod, monthPeriod, sevenDayPeriod, thirtyDayPeriod);
        }

        return new TokenCostStatistics
        {
            Today = todayPeriod.ToSummary(),
            Yesterday = yesterdayPeriod.ToSummary(),
            Week = weekPeriod.ToSummary(),
            Month = monthPeriod.ToSummary(),
            SevenDay = sevenDayPeriod.ToSummary(),
            ThirtyDay = thirtyDayPeriod.ToSummary(),
        };
    }

    /// <summary>
    /// Reads pricing entries from the external JSON resource.
    /// </summary>
    private Dictionary<string, ModelPricing> LoadPricing()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(m_PricingPath));
        Dictionary<string, ModelPricing> pricing = new(StringComparer.OrdinalIgnoreCase);
        foreach (JsonProperty property in document.RootElement.EnumerateObject())
        {
            pricing[property.Name] = new ModelPricing(
                property.Value.GetProperty("input").GetDecimal(),
                property.Value.GetProperty("cachedInput").GetDecimal(),
                property.Value.GetProperty("output").GetDecimal());
        }

        return pricing;
    }

    /// <summary>
    /// Enumerates active and archived Codex session logs.
    /// </summary>
    private static IEnumerable<string> EnumerateSessionFiles(string codexDirectory)
    {
        string sessions = Path.Combine(codexDirectory, "sessions");
        string archived = Path.Combine(codexDirectory, "archived_sessions");
        IEnumerable<string> activeFiles = Directory.Exists(sessions)
            ? Directory.EnumerateFiles(sessions, "*.jsonl", SearchOption.AllDirectories)
            : Enumerable.Empty<string>();
        IEnumerable<string> archivedFiles = Directory.Exists(archived)
            ? Directory.EnumerateFiles(archived, "*.jsonl", SearchOption.TopDirectoryOnly)
            : Enumerable.Empty<string>();
        return activeFiles.Concat(archivedFiles);
    }

    /// <summary>
    /// Adds token deltas from one Codex session file.
    /// </summary>
    private static void CollectFile(
        string path,
        DateTime today,
        DateTime weekStart,
        DateTime monthStart,
        Dictionary<string, ModelPricing> pricing,
        PeriodAccumulator todayPeriod,
        PeriodAccumulator yesterdayPeriod,
        PeriodAccumulator weekPeriod,
        PeriodAccumulator monthPeriod,
        PeriodAccumulator sevenDayPeriod,
        PeriodAccumulator thirtyDayPeriod)
    {
        string model = "unknown";
        TokenCounts? previous = null;
        bool replayingHistory = false;
        foreach (string line in ReadLinesShared(path))
        {
            if (!line.Contains("\"session_meta\"", StringComparison.Ordinal)
                && !line.Contains("\"turn_context\"", StringComparison.Ordinal)
                && !line.Contains("\"token_count\"", StringComparison.Ordinal)
                && !line.Contains("\"thread_settings_applied\"", StringComparison.Ordinal)
                && !line.Contains("\"inter_agent_communication", StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                using JsonDocument document = JsonDocument.Parse(line);
                JsonElement root = document.RootElement;
                string type = GetString(root, "type");
                JsonElement payload = GetObject(root, "payload");
                if (type == "session_meta")
                {
                    string id = GetString(payload, "id");
                    string sessionId = GetString(payload, "session_id");
                    replayingHistory = GetString(payload, "forked_from_id").Length > 0
                        || payload.TryGetProperty("source", out JsonElement source) && source.ValueKind == JsonValueKind.Object && source.TryGetProperty("subagent", out _)
                        || sessionId.Length > 0 && sessionId != id;
                    continue;
                }

                if (type.StartsWith("inter_agent_communication", StringComparison.Ordinal)
                    || type == "event_msg" && GetString(payload, "type") == "thread_settings_applied")
                {
                    replayingHistory = false;
                    continue;
                }

                if (type == "turn_context")
                {
                    string candidate = GetString(payload, "model");
                    model = candidate.Length > 0 ? NormalizeModel(candidate) : model;
                    continue;
                }

                if (type != "event_msg" || GetString(payload, "type") != "token_count")
                {
                    continue;
                }

                JsonElement info = GetObject(payload, "info");
                JsonElement total = GetObject(info, "total_token_usage");
                bool isCumulative = total.ValueKind == JsonValueKind.Object;
                JsonElement usage = isCumulative ? total : GetObject(info, "last_token_usage");
                if (usage.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                string eventModel = GetString(info, "model");
                eventModel = eventModel.Length > 0 ? eventModel : GetString(info, "model_name");
                model = eventModel.Length > 0 ? NormalizeModel(eventModel) : model;
                TokenCounts current = ParseCounts(usage);
                TokenCounts delta = isCumulative && previous != null ? current.Subtract(previous.Value) : current;
                previous = isCumulative ? current : previous;
                if (replayingHistory)
                {
                    continue;
                }

                if (!DateTimeOffset.TryParse(GetString(root, "timestamp"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset timestamp)
                    || delta.Total == 0)
                {
                    continue;
                }

                DateTime eventDate = timestamp.LocalDateTime.Date;
                decimal? cost = null;
                if (TryFindPricing(pricing, model, out ModelPricing modelPricing))
                {
                    long cached = Math.Min(delta.CachedInput, delta.Input);
                    long freshInput = delta.Input - cached;
                    cost = (freshInput * modelPricing.Input + cached * modelPricing.CachedInput + delta.Output * modelPricing.Output) / 1_000_000m;
                }

                if (eventDate == today)
                {
                    todayPeriod.Add(delta.Total, cost);
                }

                if (eventDate == today.AddDays(-1))
                {
                    yesterdayPeriod.Add(delta.Total, cost);
                }

                if (eventDate >= weekStart && eventDate <= today)
                {
                    weekPeriod.Add(delta.Total, cost);
                }

                if (eventDate >= monthStart && eventDate <= today)
                {
                    monthPeriod.Add(delta.Total, cost);
                }

                if (eventDate >= today.AddDays(-6) && eventDate <= today)
                {
                    sevenDayPeriod.Add(delta.Total, cost);
                }

                if (eventDate >= today.AddDays(-29) && eventDate <= today)
                {
                    thirtyDayPeriod.Add(delta.Total, cost);
                }
            }
            catch (JsonException)
            {
                // Codex can leave a partially written final JSONL line while a session is active.
            }
        }
    }

    /// <summary>
    /// Reads an active Codex JSONL file without blocking its writer.
    /// </summary>
    private static IEnumerable<string> ReadLinesShared(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using StreamReader reader = new(stream);
        while (reader.ReadLine() is string line)
        {
            yield return line;
        }
    }

    /// <summary>
    /// Matches exact models and reasoning-effort suffix variants.
    /// </summary>
    private static bool TryFindPricing(Dictionary<string, ModelPricing> pricing, string model, out ModelPricing result)
    {
        if (pricing.TryGetValue(model, out result))
        {
            return true;
        }

        string[] suffixes = ["-minimal", "-low", "-medium", "-high", "-xhigh"];
        string? baseModel = suffixes.FirstOrDefault(model.EndsWith) is string suffix ? model[..^suffix.Length] : null;
        return baseModel != null && pricing.TryGetValue(baseModel, out result);
    }

    /// <summary>
    /// Normalizes provider and snapshot decorations from a Codex model name.
    /// </summary>
    private static string NormalizeModel(string value)
    {
        string model = value.ToLowerInvariant();
        int slash = model.LastIndexOf('/');
        model = slash >= 0 ? model[(slash + 1)..] : model;
        if (model == "gpt-5.6")
        {
            return "gpt-5.6-sol";
        }
        if (model.Length > 11 && model[^11] == '-' && model[^10..].Where(character => character != '-').All(char.IsDigit))
        {
            model = model[..^11];
        }
        else if (model.Length > 9 && model[^9] == '-' && model[^8..].All(char.IsDigit))
        {
            model = model[..^9];
        }

        return model;
    }

    /// <summary>
    /// Parses cumulative token counters from a token count event.
    /// </summary>
    private static TokenCounts ParseCounts(JsonElement value)
    {
        return new TokenCounts(
            GetInt64(value, "input_tokens"),
            GetInt64(value, "cached_input_tokens", GetInt64(value, "cache_read_input_tokens")),
            GetInt64(value, "output_tokens"));
    }

    /// <summary>
    /// Gets an object property or an empty element.
    /// </summary>
    private static JsonElement GetObject(JsonElement value, string name)
    {
        return value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out JsonElement result) && result.ValueKind == JsonValueKind.Object ? result : default;
    }

    /// <summary>
    /// Gets a string property or an empty string.
    /// </summary>
    private static string GetString(JsonElement value, string name)
    {
        return value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out JsonElement result) && result.ValueKind == JsonValueKind.String ? result.GetString() ?? string.Empty : string.Empty;
    }

    /// <summary>
    /// Gets an integer property or a fallback value.
    /// </summary>
    private static long GetInt64(JsonElement value, string name, long fallback = 0)
    {
        return value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out JsonElement result) && result.TryGetInt64(out long number) ? number : fallback;
    }

    private readonly record struct ModelPricing(decimal Input, decimal CachedInput, decimal Output);

    private sealed class PeriodAccumulator
    {
        private long m_TotalTokens;
        private decimal m_TotalCost;

        /// <summary>
        /// Adds one usage delta to this period.
        /// </summary>
        public void Add(long tokens, decimal? cost)
        {
            m_TotalTokens += tokens;
            if (cost.HasValue)
            {
                m_TotalCost += cost.Value;
            }
        }

        /// <summary>
        /// Creates the immutable period summary.
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

    private readonly record struct TokenCounts(long Input, long CachedInput, long Output)
    {
        public long Total => Input + Output;

        /// <summary>
        /// Computes a non-negative delta from cumulative counters.
        /// </summary>
        public TokenCounts Subtract(TokenCounts previous)
        {
            return new TokenCounts(
                Math.Max(0, Input - previous.Input),
                Math.Max(0, CachedInput - previous.CachedInput),
                Math.Max(0, Output - previous.Output));
        }
    }
}
