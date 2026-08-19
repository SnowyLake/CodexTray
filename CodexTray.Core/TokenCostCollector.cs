using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CodexTray.Core;

public sealed class TokenCostSummary
{
    public long TotalTokens { get; init; }

    public decimal? CostUsd { get; init; }
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
    /// Collects Codex and OpenCode token usage for the supported calendar periods.
    /// </summary>
    public TokenCostStatistics Collect(string? codexDirectory = null, DateTimeOffset? now = null, string? openCodeDirectory = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string root = codexDirectory ?? Path.Combine(userProfile, ".codex");
        string openCodeRoot = openCodeDirectory ?? Path.Combine(userProfile, ".local", "share", "opencode");
        DateTimeOffset current = now ?? DateTimeOffset.Now;
        Dictionary<string, ModelPricing> pricing = LoadPricing(cancellationToken);
        TokenCostPeriodAccumulator accumulator = new(current);
        string[] sessionFiles = EnumerateSessionFiles(root, cancellationToken).ToArray();
        Dictionary<string, string> rolloutIndex = BuildRolloutIndex(sessionFiles, cancellationToken);

        foreach (string path in sessionFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CollectFile(path, pricing, rolloutIndex, accumulator, cancellationToken);
        }

        CollectOpenCode(Path.Combine(openCodeRoot, "opencode.db"), pricing, accumulator, cancellationToken);
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

        TokenCostPeriodAccumulator accumulator = new(current, requireCompleteCosts: true);
        foreach (GrokTurnUsage usage in turns.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
                accumulator.Add(usage.Timestamp, usage.Counts.Total, usage.CostUsd);
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
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(m_PricingPath));
        Dictionary<string, ModelPricing> pricing = new(StringComparer.OrdinalIgnoreCase);
        foreach (JsonProperty property in document.RootElement.EnumerateObject())
        {
            cancellationToken.ThrowIfCancellationRequested();
            JsonElement value = property.Value;
            ModelPricing modelPricing = new(
                value.GetProperty("input").GetDecimal(),
                value.GetProperty("cachedInput").GetDecimal(),
                value.GetProperty("output").GetDecimal());
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
                if (updateKind.Length > 0 && updateKind != "turn_completed" ||
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
        TokenCounts counts = new(
            Math.Max(0, GetInt64(value, "inputTokens")),
            Math.Max(0, GetInt64(value, "cachedReadTokens")),
            Math.Max(0, GetInt64(value, "outputTokens")));
        if (counts.Total == 0 && counts.CachedInput == 0)
        {
            return;
        }

        long costTicks = Math.Max(0, GetInt64(value, "costUsdTicks"));
        decimal? reportedCost = costTicks > 0 ? costTicks / (decimal)GrokCostTicksPerUsd : null;
        bool costIsPartial = eventCostIsPartial || GetBoolean(value, "costIsPartial");
        decimal? localCost = CalculateCost(pricing, model, counts);
        decimal? cost = reportedCost.HasValue && !costIsPartial ? reportedCost : localCost ?? reportedCost;
        turns[$"{sessionId}\0{turnKey}\0{model}"] = new GrokTurnUsage(counts, cost, timestamp);
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
    private static void CollectFile(
        string path,
        Dictionary<string, ModelPricing> pricing,
        Dictionary<string, string> rolloutIndex,
        TokenCostPeriodAccumulator accumulator,
        CancellationToken cancellationToken)
    {
        string model = "unknown";
        TokenCounts? previous = null;
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<TokenUsageSignature>? parentSignatures = LoadParentSignatures(path, rolloutIndex, cancellationToken);
        int parentOffset = 0;
        bool matchingReplay = parentSignatures?.Count > 0;
        foreach (string line in ReadLinesShared(path, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
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

                accumulator.Add(timestamp, delta.Total, CalculateCost(pricing, model, delta), model);
            }
            catch (JsonException)
            {
                // Codex can leave a partially written final JSONL line while a session is active.
            }
        }
    }

    /// <summary>
    /// Adds OpenAI token usage from the local OpenCode database.
    /// </summary>
    private static void CollectOpenCode(
        string databasePath,
        Dictionary<string, ModelPricing> pricing,
        TokenCostPeriodAccumulator accumulator,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(databasePath))
        {
            return;
        }

        try
        {
            using SqliteConnection connection = new(new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT time_created, data FROM message;";
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    using JsonDocument document = JsonDocument.Parse(reader.GetString(1));
                    JsonElement root = document.RootElement;
                    if (GetString(root, "role") != "assistant"
                        || !string.Equals(GetString(root, "providerID"), "openai", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    JsonElement tokens = GetObject(root, "tokens");
                    JsonElement cache = GetObject(tokens, "cache");
                    TokenCounts counts = new(
                        checked(GetInt64(tokens, "input") + GetInt64(cache, "read") + GetInt64(cache, "write")),
                        GetInt64(cache, "read"),
                        checked(GetInt64(tokens, "output") + GetInt64(tokens, "reasoning")));
                    if (counts.Total == 0)
                    {
                        continue;
                    }

                    DateTimeOffset timestamp = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(0));
                    string model = NormalizeModel(GetString(root, "modelID"));
                    accumulator.Add(timestamp, counts.Total, CalculateCost(pricing, model, counts), model);
                }
                catch (Exception exception) when (exception is JsonException or OverflowException or ArgumentOutOfRangeException)
                {
                    // Ignore one malformed or partially written OpenCode message.
                }
            }
        }
        catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException)
        {
            // OpenCode usage is optional and must not hide available Codex usage.
        }
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

    private readonly record struct ModelPricing(decimal Input, decimal CachedInput, decimal Output);

    private readonly record struct GrokTurnUsage(TokenCounts Counts, decimal? CostUsd, DateTimeOffset Timestamp);

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
