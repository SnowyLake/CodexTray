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

    public TokenCostSummary Total { get; init; } = new();
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
    /// Collects Codex token usage for the supported calendar periods.
    /// </summary>
    public TokenCostStatistics Collect(string? codexDirectory = null, DateTimeOffset? now = null)
    {
        string root = codexDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        DateTimeOffset current = now ?? DateTimeOffset.Now;
        Dictionary<string, ModelPricing> pricing = LoadPricing();
        TokenCostPeriodAccumulator accumulator = new(current);
        string[] sessionFiles = EnumerateSessionFiles(root).ToArray();
        Dictionary<string, string> rolloutIndex = BuildRolloutIndex(sessionFiles);

        foreach (string path in sessionFiles)
        {
            CollectFile(path, pricing, rolloutIndex, accumulator);
        }

        return accumulator.ToStatistics();
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
        Dictionary<string, ModelPricing> pricing,
        Dictionary<string, string> rolloutIndex,
        TokenCostPeriodAccumulator accumulator)
    {
        string model = "unknown";
        TokenCounts? previous = null;
        IReadOnlyList<TokenUsageSignature>? parentSignatures = LoadParentSignatures(path, rolloutIndex);
        int parentOffset = 0;
        bool matchingReplay = parentSignatures?.Count > 0;
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
                bool isReplay = false;
                if (matchingReplay && TryParseTokenSignature(info, out TokenUsageSignature signature))
                {
                    int match = FindSignature(parentSignatures!, parentOffset, signature);
                    if (match >= 0)
                    {
                        parentOffset = match + 1;
                        isReplay = true;
                    }
                    else
                    {
                        matchingReplay = false;
                    }
                }
                else
                {
                    matchingReplay = false;
                }

                TokenCounts current = ParseCounts(usage);
                TokenCounts delta = isCumulative && previous != null ? current.Subtract(previous.Value) : current;
                previous = isCumulative ? current : previous;
                if (isReplay)
                {
                    continue;
                }

                if (!DateTimeOffset.TryParse(GetString(root, "timestamp"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset timestamp)
                    || delta.Total == 0)
                {
                    continue;
                }

                decimal? cost = null;
                if (TryFindPricing(pricing, model, out ModelPricing modelPricing))
                {
                    long cached = Math.Min(delta.CachedInput, delta.Input);
                    long freshInput = delta.Input - cached;
                    cost = (freshInput * modelPricing.Input + cached * modelPricing.CachedInput + delta.Output * modelPricing.Output) / 1_000_000m;
                }

                accumulator.Add(timestamp, delta.Total, cost);
            }
            catch (JsonException)
            {
                // Codex can leave a partially written final JSONL line while a session is active.
            }
        }
    }

    /// <summary>
    /// Indexes rollout files by the thread UUID at the end of each filename.
    /// </summary>
    private static Dictionary<string, string> BuildRolloutIndex(IEnumerable<string> paths)
    {
        Dictionary<string, string> result = new(StringComparer.OrdinalIgnoreCase);
        foreach (string path in paths)
        {
            string name = Path.GetFileNameWithoutExtension(path);
            if (name.Length >= 36 && Guid.TryParse(name[^36..], out Guid threadId))
            {
                result.TryAdd(threadId.ToString(), path);
            }
        }

        return result;
    }

    /// <summary>
    /// Loads token signatures from the explicit parent rollout before the child starts.
    /// </summary>
    private static IReadOnlyList<TokenUsageSignature>? LoadParentSignatures(string path, Dictionary<string, string> rolloutIndex)
    {
        ReplayContext? context = ReadReplayContext(path);
        if (context == null || !rolloutIndex.TryGetValue(context.Value.ParentId, out string? parentPath))
        {
            return null;
        }

        List<TokenUsageSignature> result = [];
        foreach (string line in ReadLinesShared(parentPath))
        {
            if (!line.Contains("\"token_count\"", StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                using JsonDocument document = JsonDocument.Parse(line);
                JsonElement root = document.RootElement;
                JsonElement payload = GetObject(root, "payload");
                if (GetString(root, "type") != "event_msg"
                    || GetString(payload, "type") != "token_count"
                    || !DateTimeOffset.TryParse(GetString(root, "timestamp"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset timestamp)
                    || timestamp > context.Value.Cutoff
                    || !TryParseTokenSignature(GetObject(payload, "info"), out TokenUsageSignature signature))
                {
                    continue;
                }

                result.Add(signature);
            }
            catch (JsonException)
            {
                // Active parent rollouts can also end with a partially written JSONL line.
            }
        }

        return result;
    }

    /// <summary>
    /// Reads the explicit parent identity and fork timestamp from a rollout.
    /// </summary>
    private static ReplayContext? ReadReplayContext(string path)
    {
        foreach (string line in ReadLinesShared(path))
        {
            if (!line.Contains("\"session_meta\"", StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                using JsonDocument document = JsonDocument.Parse(line);
                JsonElement root = document.RootElement;
                JsonElement payload = GetObject(root, "payload");
                if (GetString(root, "type") != "session_meta"
                    || !DateTimeOffset.TryParse(GetString(root, "timestamp"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset cutoff))
                {
                    return null;
                }

                string forkedFrom = GetString(payload, "forked_from_id");
                string spawnedFrom = GetString(GetObject(GetObject(GetObject(payload, "source"), "subagent"), "thread_spawn"), "parent_thread_id");
                if (forkedFrom.Length > 0 && spawnedFrom.Length > 0 && forkedFrom != spawnedFrom)
                {
                    return null;
                }

                string parentId = forkedFrom.Length > 0 ? forkedFrom : spawnedFrom;
                if (parentId.Length == 0)
                {
                    string id = GetString(payload, "id");
                    string sessionId = GetString(payload, "session_id");
                    parentId = sessionId.Length > 0 && sessionId != id ? sessionId : string.Empty;
                }

                return parentId.Length > 0 ? new ReplayContext(parentId, cutoff) : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>
    /// Finds a child token signature in the remaining ordered parent sequence.
    /// </summary>
    private static int FindSignature(IReadOnlyList<TokenUsageSignature> signatures, int start, TokenUsageSignature target)
    {
        for (int index = start; index < signatures.Count; index++)
        {
            if (signatures[index] == target)
            {
                return index;
            }
        }

        return -1;
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
    /// Parses the complete token counters used to identify replayed parent events.
    /// </summary>
    private static bool TryParseTokenSignature(JsonElement info, out TokenUsageSignature signature)
    {
        TokenCounterSignature? total = ParseCounterSignature(info, "total_token_usage");
        TokenCounterSignature? last = ParseCounterSignature(info, "last_token_usage");
        signature = new TokenUsageSignature(total, last);
        return total != null || last != null;
    }

    /// <summary>
    /// Parses one optional token counter object without replacing absent fields with zero.
    /// </summary>
    private static TokenCounterSignature? ParseCounterSignature(JsonElement value, string name)
    {
        JsonElement counters = GetObject(value, name);
        return counters.ValueKind == JsonValueKind.Object
            ? new TokenCounterSignature(
                GetNullableInt64(counters, "input_tokens"),
                GetNullableInt64(counters, "cached_input_tokens") ?? GetNullableInt64(counters, "cache_read_input_tokens"),
                GetNullableInt64(counters, "output_tokens"),
                GetNullableInt64(counters, "reasoning_output_tokens"),
                GetNullableInt64(counters, "total_tokens"))
            : null;
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

    /// <summary>
    /// Gets an optional integer property without conflating a missing field with zero.
    /// </summary>
    private static long? GetNullableInt64(JsonElement value, string name)
    {
        return value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out JsonElement result) && result.TryGetInt64(out long number) ? number : null;
    }

    private readonly record struct ModelPricing(decimal Input, decimal CachedInput, decimal Output);

    private readonly record struct ReplayContext(string ParentId, DateTimeOffset Cutoff);

    private readonly record struct TokenCounterSignature(long? Input, long? CachedInput, long? Output, long? ReasoningOutput, long? Total);

    private readonly record struct TokenUsageSignature(TokenCounterSignature? Total, TokenCounterSignature? Last);

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
