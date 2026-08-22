using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;

namespace CodexTray.Core;

public sealed class TokenCostSummary
{
    public long TotalTokens { get; init; }

    public decimal? CostUsd { get; init; }

    public long CacheReadTokens { get; init; }

    public long CacheableInputTokens { get; init; }

    /// <summary>
    /// Returns cache-read tokens as a percent of cacheable input for this period.
    /// </summary>
    public int GetCacheHitPercent()
    {
        if (CacheableInputTokens <= 0)
        {
            return 0;
        }

        return (int)Math.Clamp(Math.Round(CacheReadTokens * 100d / CacheableInputTokens), 0, 100);
    }
}

public sealed class TokenCostDailySummary
{
    public DateTime Date { get; init; }

    public TokenCostSummary Summary { get; init; } = new();
}

public sealed class TokenCostModelStatistics
{
    public string Model { get; init; } = string.Empty;

    public TokenCostSummary Today { get; init; } = new();

    public TokenCostSummary LastSevenDays { get; init; } = new();

    public TokenCostSummary LastThirtyDays { get; init; } = new();

    public TokenCostSummary CurrentWeek { get; init; } = new();

    public TokenCostSummary CurrentMonth { get; init; } = new();

    public TokenCostSummary Lifetime { get; init; } = new();
}

public sealed class TokenCostStatistics
{
    public TokenCostSummary Today { get; init; } = new();

    public TokenCostSummary LastSevenDays { get; init; } = new();

    public TokenCostSummary LastThirtyDays { get; init; } = new();

    public TokenCostSummary CurrentWeek { get; init; } = new();

    public TokenCostSummary CurrentMonth { get; init; } = new();

    public TokenCostSummary Lifetime { get; init; } = new();

    public IReadOnlyList<TokenCostDailySummary> LastSevenDaysDaily { get; init; } = [];

    public IReadOnlyList<TokenCostDailySummary> LastThirtyDaysDaily { get; init; } = [];

    public IReadOnlyList<TokenCostDailySummary> CurrentMonthDaily { get; init; } = [];

    public IReadOnlyList<TokenCostModelStatistics> Models { get; init; } = [];
}

public sealed class TokenCostCollector
{
    private const long GrokCostTicksPerUsd = 10_000_000_000L;
    private const long MaxGrokSessionFileBytes = 50L * 1024 * 1024;
    private readonly string m_PricingPath;
    private readonly Dictionary<string, CachedFileUsage> m_FileUsageCache = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, ModelPricing>? m_CachedPricing;
    private long m_CachedPricingLength;
    private DateTime m_CachedPricingWriteUtc;

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
    /// Collects Codex session token usage for the supported calendar periods.
    /// </summary>
    public TokenCostStatistics Collect(string? codexDirectory = null, DateTimeOffset? now = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string root = codexDirectory ?? Path.Combine(userProfile, ".codex");
        DateTimeOffset current = now ?? DateTimeOffset.Now;
        Dictionary<string, ModelPricing> pricing = LoadPricing(cancellationToken);
        TokenCostPeriodAccumulator accumulator = new(current);
        string[] sessionFiles = EnumerateSessionFiles(root, cancellationToken).ToArray();
        Dictionary<string, string> rolloutIndex = BuildRolloutIndex(sessionFiles, cancellationToken);
        HashSet<string> seenFiles = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> seenRollouts = new(StringComparer.OrdinalIgnoreCase);

        foreach (string path in sessionFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            seenFiles.Add(path);
            if (TryGetRolloutThreadId(path, out string threadId) && !seenRollouts.Add(threadId))
            {
                continue;
            }

            CollectFile(path, pricing, rolloutIndex, accumulator, cancellationToken);
        }

        PruneFileUsageCache(seenFiles);
        return accumulator.ToStatistics();
    }

    /// <summary>
    /// Collects turn-level token usage and cost from local Grok Build session updates.
    /// </summary>
    public TokenCostStatistics CollectGrok(string? grokDirectory = null, DateTimeOffset? now = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string root = grokDirectory ?? Environment.GetEnvironmentVariable("GROK_HOME") ?? Path.Combine(userProfile, ".grok");
        DateTimeOffset current = now ?? DateTimeOffset.Now;
        Dictionary<string, ModelPricing> pricing = LoadOptionalPricing(cancellationToken);
        Dictionary<string, GrokTurnUsage> turns = new(StringComparer.Ordinal);

        foreach (string path in EnumerateGrokSessionFiles(root, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                CollectGrokFile(path, pricing, turns, cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // One unavailable session file must not hide usage from the remaining files.
            }
        }

        TokenCostPeriodAccumulator accumulator = new(current);
        foreach (GrokTurnUsage usage in turns.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            accumulator.Add(usage.Timestamp, usage.Counts.Total, usage.CostUsd, usage.Model, usage.Counts.CacheReadTokens, usage.Counts.CacheableInputTokens);
        }

        return accumulator.ToStatistics();
    }

    /// <summary>
    /// Loads pricing for Grok fallback calculations without discarding exact reported costs when the resource is unavailable.
    /// </summary>
    private Dictionary<string, ModelPricing> LoadOptionalPricing(CancellationToken cancellationToken)
    {
        try
        {
            return LoadPricing(cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return new Dictionary<string, ModelPricing>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Reads pricing entries from the external JSON resource.
    /// </summary>
    private Dictionary<string, ModelPricing> LoadPricing(CancellationToken cancellationToken)
    {
        if (TryGetFileFingerprint(m_PricingPath, out long length, out DateTime lastWriteUtc) &&
            m_CachedPricing != null &&
            length == m_CachedPricingLength &&
            lastWriteUtc == m_CachedPricingWriteUtc)
        {
            return m_CachedPricing;
        }

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(m_PricingPath));
        Dictionary<string, ModelPricing> pricing = new(StringComparer.OrdinalIgnoreCase);
        foreach (JsonProperty property in document.RootElement.EnumerateObject())
        {
            cancellationToken.ThrowIfCancellationRequested();
            JsonElement value = property.Value;
            if (!TryGetDecimalProperty(value, "input", out decimal input) ||
                !TryGetDecimalProperty(value, "cachedInput", out decimal cachedInput) ||
                !TryGetDecimalProperty(value, "output", out decimal output))
            {
                continue;
            }

            ModelPricing modelPricing = new(input, cachedInput, output);
            pricing[property.Name] = modelPricing;
            if (value.TryGetProperty("aliases", out JsonElement aliases) && aliases.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement alias in aliases.EnumerateArray())
                {
                    if (alias.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(alias.GetString()))
                    {
                        pricing[alias.GetString()!] = modelPricing;
                    }
                }
            }
        }

        m_CachedPricing = pricing;
        m_CachedPricingLength = length;
        m_CachedPricingWriteUtc = lastWriteUtc;
        return pricing;
    }

    /// <summary>
    /// Enumerates active and archived Codex session logs.
    /// </summary>
    private static IEnumerable<string> EnumerateSessionFiles(string codexDirectory, CancellationToken cancellationToken)
    {
        string sessions = Path.Combine(codexDirectory, "sessions");
        string archived = Path.Combine(codexDirectory, "archived_sessions");
        IEnumerable<string> activeFiles = Directory.Exists(sessions)
            ? Directory.EnumerateFiles(sessions, "*.jsonl", SearchOption.AllDirectories)
            : Enumerable.Empty<string>();
        IEnumerable<string> archivedFiles = Directory.Exists(archived)
            ? Directory.EnumerateFiles(archived, "*.jsonl", SearchOption.TopDirectoryOnly)
            : Enumerable.Empty<string>();
        foreach (string path in activeFiles.Concat(archivedFiles))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return path;
        }
    }

    /// <summary>
    /// Enumerates active and archived Grok Build update logs without following directory links.
    /// </summary>
    private static IEnumerable<string> EnumerateGrokSessionFiles(string grokDirectory, CancellationToken cancellationToken)
    {
        EnumerationOptions options = new()
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            MaxRecursionDepth = 16,
        };
        foreach (string directoryName in new[] { "sessions", "archived_sessions" })
        {
            string directory = Path.Combine(grokDirectory, directoryName);
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (string path in Directory.EnumerateFiles(directory, "updates.jsonl", options))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return path;
            }
        }
    }

    /// <summary>
    /// Collects independent turn-completed usage entries from one Grok Build update log.
    /// </summary>
    private static void CollectGrokFile(
        string path,
        Dictionary<string, ModelPricing> pricing,
        Dictionary<string, GrokTurnUsage> turns,
        CancellationToken cancellationToken)
    {
        if (new FileInfo(path).Length > MaxGrokSessionFileBytes)
        {
            return;
        }

        string sessionId = Directory.GetParent(path)?.Name ?? "unknown";
        int eventIndex = 0;
        foreach (string line in ReadLinesShared(path, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!line.Contains("\"usage\"", StringComparison.Ordinal) ||
                !line.Contains("_x.ai/session/update", StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                using JsonDocument document = JsonDocument.Parse(line);
                JsonElement root = document.RootElement;
                if (GetString(root, "method") != "_x.ai/session/update")
                {
                    continue;
                }

                JsonElement update = GetObject(GetObject(root, "params"), "update");
                string updateKind = GetString(update, "sessionUpdate");
                JsonElement usage = GetObject(update, "usage");
                if (updateKind != "turn_completed" ||
                    usage.ValueKind != JsonValueKind.Object ||
                    !TryParseGrokTimestamp(root, out DateTimeOffset timestamp))
                {
                    continue;
                }

                string promptId = GetString(update, "prompt_id");
                string turnKey = promptId.Length > 0 ? promptId : $"idx{eventIndex}";
                bool eventCostIsPartial = GetBoolean(usage, "costIsPartial");
                JsonElement modelUsage = GetObject(usage, "modelUsage");
                if (modelUsage.ValueKind == JsonValueKind.Object && modelUsage.EnumerateObject().Any())
                {
                    foreach (JsonProperty model in modelUsage.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
                    {
                        CollectGrokTurn(sessionId, turnKey, model.Name, model.Value, eventCostIsPartial, timestamp, pricing, turns);
                    }
                }
                else
                {
                    CollectGrokTurn(sessionId, turnKey, "unknown", usage, eventCostIsPartial, timestamp, pricing, turns);
                }

                eventIndex++;
            }
            catch (Exception exception) when (exception is JsonException or ArgumentOutOfRangeException or OverflowException)
            {
                // Grok Build can leave a partially written final JSONL line while a session is active.
            }
        }
    }

    /// <summary>
    /// Adds one model's counters from a Grok turn using reported cost before local fallback pricing.
    /// </summary>
    private static void CollectGrokTurn(
        string sessionId,
        string turnKey,
        string model,
        JsonElement value,
        bool eventCostIsPartial,
        DateTimeOffset timestamp,
        Dictionary<string, ModelPricing> pricing,
        Dictionary<string, GrokTurnUsage> turns)
    {
        model = NormalizeModel(model);
        TokenCounts counts = new(
            Math.Max(0, GetInt64(value, "inputTokens")),
            Math.Max(0, GetInt64(value, "cachedReadTokens")),
            Math.Max(0, GetInt64(value, "outputTokens")));
        long costTicks = Math.Max(0, GetInt64(value, "costUsdTicks"));
        decimal? reportedCost = costTicks > 0 ? costTicks / (decimal)GrokCostTicksPerUsd : null;
        if (!counts.HasUsage && reportedCost == null)
        {
            return;
        }

        bool costIsPartial = eventCostIsPartial || GetBoolean(value, "costIsPartial");
        decimal? localCost = counts.HasUsage ? CalculateCost(pricing, model, counts) : null;
        decimal? cost = !counts.HasUsage
            ? reportedCost
            : reportedCost.HasValue && !costIsPartial ? reportedCost : localCost ?? reportedCost;
        turns[$"{sessionId}\0{turnKey}\0{model}"] = new GrokTurnUsage(counts, cost, timestamp, model);
    }

    /// <summary>
    /// Parses the numeric or RFC 3339 timestamp used by Grok Build update events.
    /// </summary>
    private static bool TryParseGrokTimestamp(JsonElement value, out DateTimeOffset timestamp)
    {
        timestamp = default;
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("timestamp", out JsonElement candidate))
        {
            return false;
        }

        if (candidate.ValueKind == JsonValueKind.Number && candidate.TryGetInt64(out long epoch))
        {
            timestamp = epoch > 100_000_000_000L
                ? DateTimeOffset.FromUnixTimeMilliseconds(epoch)
                : DateTimeOffset.FromUnixTimeSeconds(epoch);
            return true;
        }

        return candidate.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(candidate.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out timestamp);
    }

    /// <summary>
    /// Adds token deltas from one Codex session file.
    /// </summary>
    private void CollectFile(
        string path,
        Dictionary<string, ModelPricing> pricing,
        Dictionary<string, string> rolloutIndex,
        TokenCostPeriodAccumulator accumulator,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (TryGetCachedFileUsage(path, out List<CachedUsageEvent>? cachedEvents))
        {
            AddCachedUsage(cachedEvents, accumulator);
            return;
        }

        string model = "unknown";
        TokenCounts? totalHighWater = null;
        TokenUsageSnapshot? previousSnapshot = null;
        List<CachedUsageEvent> events = [];
        IReadOnlyList<TokenUsageSignature>? parentSignatures = LoadParentSignatures(path, rolloutIndex, cancellationToken);
        int parentOffset = 0;
        bool matchingReplay = parentSignatures?.Count > 0;
        foreach (string line in ReadLinesShared(path, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!line.Contains("\"turn_context\"", StringComparison.Ordinal)
                && !line.Contains("\"token_count\"", StringComparison.Ordinal))
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
                if (!HasUsageObject(info, "last_token_usage") && !HasUsageObject(info, "total_token_usage"))
                {
                    continue;
                }

                string eventModel = GetString(info, "model");
                eventModel = eventModel.Length > 0 ? eventModel : GetString(info, "model_name");
                model = eventModel.Length > 0 ? NormalizeModel(eventModel) : model;
                bool isReplay = false;
                if (matchingReplay && TryParseTokenSignature(info, out TokenUsageSignature signature))
                {
                    int match = FindSignature(parentSignatures!, parentOffset, signature, cancellationToken);
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

                bool shouldBill = TryResolveCodexIncrement(info, ref totalHighWater, ref previousSnapshot, out TokenCounts increment);
                if (isReplay
                    || !shouldBill
                    || !DateTimeOffset.TryParse(GetString(root, "timestamp"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset timestamp))
                {
                    continue;
                }

                decimal? costUsd = CalculateCost(pricing, model, increment);
                events.Add(new CachedUsageEvent(timestamp, increment.Total, costUsd, model, increment.CacheReadTokens, increment.CacheableInputTokens));
                accumulator.Add(timestamp, increment.Total, costUsd, model, increment.CacheReadTokens, increment.CacheableInputTokens);
            }
            catch (JsonException)
            {
                // Codex can leave a partially written final JSONL line while a session is active.
            }
        }

        StoreCachedFileUsage(path, events);
    }

    /// <summary>
    /// Calculates API-equivalent cost for one model usage event.
    /// </summary>
    private static decimal? CalculateCost(Dictionary<string, ModelPricing> pricing, string model, TokenCounts counts)
    {
        if (!TryFindPricing(pricing, model, out ModelPricing modelPricing))
        {
            return null;
        }

        long cached = Math.Min(counts.CachedInput, counts.Input);
        long freshInput = counts.Input - cached;
        return (freshInput * modelPricing.Input + cached * modelPricing.CachedInput + counts.Output * modelPricing.Output) / 1_000_000m;
    }

    /// <summary>
    /// Indexes rollout files by the thread UUID at the end of each filename.
    /// </summary>
    private static Dictionary<string, string> BuildRolloutIndex(IEnumerable<string> paths, CancellationToken cancellationToken)
    {
        Dictionary<string, string> result = new(StringComparer.OrdinalIgnoreCase);
        foreach (string path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryGetRolloutThreadId(path, out string threadId))
            {
                result.TryAdd(threadId, path);
            }
        }

        return result;
    }

    /// <summary>
    /// Reads the thread UUID from the end of a Codex rollout file name.
    /// </summary>
    private static bool TryGetRolloutThreadId(string path, out string threadId)
    {
        threadId = string.Empty;
        string name = Path.GetFileNameWithoutExtension(path);
        if (name.Length < 36 || !Guid.TryParse(name[^36..], out Guid parsed))
        {
            return false;
        }

        threadId = parsed.ToString();
        return true;
    }

    /// <summary>
    /// Loads token signatures from the explicit parent rollout before the child starts.
    /// </summary>
    private static IReadOnlyList<TokenUsageSignature>? LoadParentSignatures(string path, Dictionary<string, string> rolloutIndex, CancellationToken cancellationToken)
    {
        ReplayContext? context = ReadReplayContext(path, cancellationToken);
        if (context == null || !rolloutIndex.TryGetValue(context.Value.ParentId, out string? parentPath))
        {
            return null;
        }

        List<TokenUsageSignature> result = [];
        foreach (string line in ReadLinesShared(parentPath, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
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
    private static ReplayContext? ReadReplayContext(string path, CancellationToken cancellationToken)
    {
        foreach (string line in ReadLinesShared(path, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
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
    private static int FindSignature(IReadOnlyList<TokenUsageSignature> signatures, int start, TokenUsageSignature target, CancellationToken cancellationToken)
    {
        for (int index = start; index < signatures.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
    private static IEnumerable<string> ReadLinesShared(string path, CancellationToken cancellationToken)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using StreamReader reader = new(stream);
        while (reader.ReadLine() is string line)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
    /// Resolves one Codex token_count event to a billed increment.
    /// </summary>
    private static bool TryResolveCodexIncrement(
        JsonElement info,
        ref TokenCounts? totalHighWater,
        ref TokenUsageSnapshot? previousSnapshot,
        out TokenCounts increment)
    {
        bool hasLast = TryGetUsageCounts(info, "last_token_usage", out TokenCounts last);
        bool hasTotal = TryGetUsageCounts(info, "total_token_usage", out TokenCounts total);
        increment = default;
        if (!hasLast && !hasTotal)
        {
            return false;
        }

        TokenUsageSnapshot snapshot = new(hasLast ? last : null, hasTotal ? total : null);
        bool duplicate = previousSnapshot == snapshot;
        previousSnapshot = snapshot;
        if (hasLast && last.HasUsage)
        {
            increment = last;
        }
        else if (hasTotal)
        {
            increment = totalHighWater is { } water ? total.Subtract(water) : total;
        }

        if (hasTotal)
        {
            totalHighWater = totalHighWater is { } water ? water.RaiseHighWater(total) : total;
        }

        return !duplicate && increment.HasUsage;
    }

    /// <summary>
    /// Reads a non-empty Codex usage object into token counters.
    /// </summary>
    private static bool TryGetUsageCounts(JsonElement parent, string name, out TokenCounts counts)
    {
        counts = default;
        JsonElement value = GetObject(parent, name);
        if (value.ValueKind != JsonValueKind.Object || !HasAnyProperty(value))
        {
            return false;
        }

        counts = ParseCounts(value);
        return true;
    }

    /// <summary>
    /// Returns whether a JSON object exists and contains at least one property.
    /// </summary>
    private static bool HasUsageObject(JsonElement parent, string name)
    {
        JsonElement value = GetObject(parent, name);
        return value.ValueKind == JsonValueKind.Object && HasAnyProperty(value);
    }

    /// <summary>
    /// Returns whether a JSON object has any properties.
    /// </summary>
    private static bool HasAnyProperty(JsonElement value)
    {
        foreach (JsonProperty _ in value.EnumerateObject())
        {
            return true;
        }

        return false;
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
    /// Gets a Boolean property or false when it is absent or invalid.
    /// </summary>
    private static bool GetBoolean(JsonElement value, string name)
    {
        return value.ValueKind == JsonValueKind.Object &&
            value.TryGetProperty(name, out JsonElement result) &&
            result.ValueKind is JsonValueKind.True or JsonValueKind.False &&
            result.GetBoolean();
    }

    /// <summary>
    /// Gets an integer property or a fallback value.
    /// </summary>
    private static long GetInt64(JsonElement value, string name, long fallback = 0)
    {
        return value.ValueKind == JsonValueKind.Object &&
            value.TryGetProperty(name, out JsonElement result) &&
            result.ValueKind == JsonValueKind.Number &&
            result.TryGetInt64(out long number)
                ? number
                : fallback;
    }

    /// <summary>
    /// Gets an optional integer property without conflating a missing field with zero.
    /// </summary>
    private static long? GetNullableInt64(JsonElement value, string name)
    {
        return value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out JsonElement result) && result.TryGetInt64(out long number) ? number : null;
    }

    /// <summary>
    /// Reads a required decimal pricing field.
    /// </summary>
    private static bool TryGetDecimalProperty(JsonElement value, string name, out decimal number)
    {
        number = 0;
        return value.TryGetProperty(name, out JsonElement result) &&
            result.ValueKind == JsonValueKind.Number &&
            result.TryGetDecimal(out number);
    }

    /// <summary>
    /// Replays cached usage events into a fresh period accumulator.
    /// </summary>
    private static void AddCachedUsage(List<CachedUsageEvent> events, TokenCostPeriodAccumulator accumulator)
    {
        foreach (CachedUsageEvent usageEvent in events)
        {
            accumulator.Add(usageEvent.Timestamp, usageEvent.Tokens, usageEvent.CostUsd, usageEvent.Model, usageEvent.CacheReadTokens, usageEvent.CacheableInputTokens);
        }
    }

    /// <summary>
    /// Returns cached usage when the file length and write time are unchanged.
    /// </summary>
    private bool TryGetCachedFileUsage(string path, [NotNullWhen(true)] out List<CachedUsageEvent>? events)
    {
        events = null;
        if (!TryGetFileFingerprint(path, out long length, out DateTime lastWriteUtc) ||
            !m_FileUsageCache.TryGetValue(path, out CachedFileUsage? cached) ||
            cached.Length != length ||
            cached.LastWriteUtc != lastWriteUtc)
        {
            return false;
        }

        events = cached.Events;
        return true;
    }

    /// <summary>
    /// Stores parsed usage for one file fingerprint.
    /// </summary>
    private void StoreCachedFileUsage(string path, List<CachedUsageEvent> events)
    {
        if (!TryGetFileFingerprint(path, out long length, out DateTime lastWriteUtc))
        {
            return;
        }

        m_FileUsageCache[path] = new CachedFileUsage
        {
            Length = length,
            LastWriteUtc = lastWriteUtc,
            Events = events,
        };
    }

    /// <summary>
    /// Drops cached files that were not seen in the current collection.
    /// </summary>
    private void PruneFileUsageCache(HashSet<string> seenFiles)
    {
        List<string> stale = [];
        foreach (string path in m_FileUsageCache.Keys)
        {
            if (!seenFiles.Contains(path))
            {
                stale.Add(path);
            }
        }

        foreach (string path in stale)
        {
            m_FileUsageCache.Remove(path);
        }
    }

    /// <summary>
    /// Reads the length and UTC write time of a local usage file.
    /// </summary>
    private static bool TryGetFileFingerprint(string path, out long length, out DateTime lastWriteUtc)
    {
        try
        {
            FileInfo info = new(path);
            if (!info.Exists)
            {
                length = 0;
                lastWriteUtc = default;
                return false;
            }

            length = info.Length;
            lastWriteUtc = info.LastWriteTimeUtc;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            length = 0;
            lastWriteUtc = default;
            return false;
        }
    }

    private readonly record struct CachedUsageEvent(
        DateTimeOffset Timestamp,
        long Tokens,
        decimal? CostUsd,
        string Model,
        long CacheReadTokens,
        long CacheableInputTokens);

    private sealed class CachedFileUsage
    {
        public required long Length { get; init; }

        public required DateTime LastWriteUtc { get; init; }

        public required List<CachedUsageEvent> Events { get; init; }
    }

    private readonly record struct ModelPricing(decimal Input, decimal CachedInput, decimal Output);

    private readonly record struct GrokTurnUsage(TokenCounts Counts, decimal? CostUsd, DateTimeOffset Timestamp, string Model);

    private readonly record struct ReplayContext(string ParentId, DateTimeOffset Cutoff);

    private readonly record struct TokenCounterSignature(long? Input, long? CachedInput, long? Output, long? ReasoningOutput, long? Total);

    private readonly record struct TokenUsageSignature(TokenCounterSignature? Total, TokenCounterSignature? Last);

    private readonly record struct TokenUsageSnapshot(TokenCounts? Last, TokenCounts? Total);

    private readonly record struct TokenCounts(long Input, long CachedInput, long Output)
    {
        public long Total => Input + Output;

        /// <summary>
        /// Returns whether this increment contains any billed tokens.
        /// </summary>
        public bool HasUsage => Total != 0 || CachedInput != 0;

        /// <summary>
        /// Returns cache-read tokens clamped to the inclusive input counter.
        /// </summary>
        public long CacheReadTokens => Math.Min(Math.Max(0, CachedInput), Math.Max(0, Input));

        /// <summary>
        /// Returns cacheable input tokens used as the cache hit-rate denominator.
        /// </summary>
        public long CacheableInputTokens => Math.Max(0, Input);

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

        /// <summary>
        /// Advances a high-water mark without allowing a later regression to lower it.
        /// </summary>
        public TokenCounts RaiseHighWater(TokenCounts current)
        {
            return new TokenCounts(
                Math.Max(Input, current.Input),
                Math.Max(CachedInput, current.CachedInput),
                Math.Max(Output, current.Output));
        }
    }
}
