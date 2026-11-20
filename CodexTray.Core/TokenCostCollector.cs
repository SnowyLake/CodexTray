using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using ZstdSharp;

namespace CodexTray.Core;

public sealed class TokenCostSummary
{
    public long TotalTokens { get; init; }

    public decimal? CostUsd { get; init; }

    public bool HasUnpricedUsage { get; init; }

    public long CacheReadTokens { get; init; }

    public long CacheableInputTokens { get; init; }

    public long TimedOutputTokens { get; init; }

    public long TimedDurationMilliseconds { get; init; }

    /// <summary>
    /// Returns average output speed from paired tokens and response durations, or null without timing.
    /// </summary>
    public decimal? GetOutputTokensPerSecond()
    {
        return TimedDurationMilliseconds > 0 && TimedOutputTokens >= 0 ? TimedOutputTokens * 1000m / TimedDurationMilliseconds : null;
    }

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

    public TokenCostSummary LastTwentyFourHours { get; init; } = new();

    public TokenCostSummary Today { get; init; } = new();

    public TokenCostSummary LastSevenDays { get; init; } = new();

    public TokenCostSummary LastThirtyDays { get; init; } = new();

    public TokenCostSummary CurrentWeek { get; init; } = new();

    public TokenCostSummary CurrentMonth { get; init; } = new();

    public TokenCostSummary CurrentYear { get; init; } = new();

    public TokenCostSummary Lifetime { get; init; } = new();
}

/// <summary>
/// One billed Codex increment or one Grok model turn from the same collect pass.
/// </summary>
public sealed class SessionLogEntry
{
    public DateTimeOffset Timestamp { get; init; }

    public string Model { get; init; } = string.Empty;

    public decimal? CostUsd { get; init; }

    public bool HasUnpricedUsage { get; init; }

    public long TimedOutputTokens { get; init; }

    public long TimedDurationMilliseconds { get; init; }

    /// <summary>
    /// Returns this row's output speed, or null when the call has no accepted timing sample.
    /// </summary>
    public decimal? GetOutputTokensPerSecond()
    {
        return TimedDurationMilliseconds > 0 && TimedOutputTokens >= 0 ? TimedOutputTokens * 1000m / TimedDurationMilliseconds : null;
    }
}

public sealed class TokenCostStatistics
{
    public bool IsSpeedEstimated { get; init; }

    public TokenCostSummary LastTwentyFourHours { get; init; } = new();

    public TokenCostSummary Today { get; init; } = new();

    public TokenCostSummary LastSevenDays { get; init; } = new();

    public TokenCostSummary LastThirtyDays { get; init; } = new();

    public TokenCostSummary CurrentWeek { get; init; } = new();

    public TokenCostSummary CurrentMonth { get; init; } = new();

    public TokenCostSummary CurrentYear { get; init; } = new();

    public TokenCostSummary Lifetime { get; init; } = new();

    public IReadOnlyList<TokenCostDailySummary> LastThirtyDaysDaily { get; init; } = [];

    public IReadOnlyList<TokenCostDailySummary> CurrentMonthDaily { get; init; } = [];

    public IReadOnlyList<TokenCostModelStatistics> Models { get; init; } = [];

    public IReadOnlyList<SessionLogEntry> SessionLogs { get; init; } = [];
}

public sealed class TokenCostCollector
{
    private const long GrokCostTicksPerUsd = 10_000_000_000L;
    private const long MaxGrokSessionFileBytes = 50L * 1024 * 1024;
    private const long SameFlushSlackMilliseconds = 100;
    private const long MaxPlausibleLatencyMilliseconds = 60L * 60 * 1000;
    private const long EstimatedSpeedMinOutputTokens = 200;
    private const long EstimatedSpeedMinDurationMilliseconds = 1000;
    private const long MeasuredSpeedMinOutputTokens = 100;
    private const long MeasuredSpeedMinDurationMilliseconds = 100;
    private const string k_ExcludedUsageModel = "codex-auto-review";
    private readonly string m_PricingPath;
    private readonly Dictionary<string, CachedFileUsage> m_FileUsageCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CachedFileUsage> m_GeminiFileUsageCache = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, ModelPricing>? m_CachedPricing;
    private Dictionary<string, ModelPricing>? m_CodexPricing;
    private Dictionary<string, ModelPricing>? m_GeminiPricing;
    private readonly Lock m_PricingLock = new();
    private readonly Lock m_GeminiCacheLock = new();
    private string? m_CachedPricingJson;

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
    /// Collects Codex session usage and estimated per-response output speed for the supported periods.
    /// </summary>
    public TokenCostStatistics CollectCodex(string? codexDirectory = null, DateTimeOffset? now = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string root = codexDirectory ?? Path.Combine(userProfile, ".codex");
        DateTimeOffset current = now ?? DateTimeOffset.Now;
        Dictionary<string, ModelPricing> pricing = LoadPricing(cancellationToken);
        if (!ReferenceEquals(m_CodexPricing, pricing))
        {
            m_FileUsageCache.Clear();
            m_CodexPricing = pricing;
        }
        TokenCostPeriodAccumulator accumulator = new(current);
        string[] sessionFiles = EnumerateCodexSessionFiles(root, cancellationToken).ToArray();
        List<string[]> sessionGroups = GroupCodexSessionFiles(sessionFiles);
        Dictionary<string, IReadOnlyList<string>> rolloutIndex = BuildRolloutIndex(sessionGroups);
        HashSet<string> seenFiles = new(StringComparer.OrdinalIgnoreCase);
        List<SessionLogEntry> sessionLogs = [];

        foreach (string[] paths in sessionGroups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (string path in paths)
            {
                seenFiles.Add(path);
            }

            CollectCodexThread(paths, pricing, rolloutIndex, accumulator, sessionLogs, current.LocalDateTime.Date, cancellationToken);
        }

        PruneFileUsageCache(seenFiles);
        return accumulator.ToStatistics(isSpeedEstimated: true, sessionLogs: SortSessionLogsNewestFirst(sessionLogs));
    }

    /// <summary>
    /// Collects local Grok Build usage and optional Cursor / Grok API cloud events, keeping session logs local.
    /// </summary>
    public TokenCostStatistics CollectGrok(string? grokDirectory = null, DateTimeOffset? now = null, CancellationToken cancellationToken = default, IReadOnlyList<CursorUsageEvent>? cursorUsage = null)
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
            CollectGrokFile(path, pricing, turns, cancellationToken);
        }

        TokenCostPeriodAccumulator accumulator = new(current);
        DateTime today = current.LocalDateTime.Date;
        List<SessionLogEntry> sessionLogs = new(turns.Count);
        foreach (GrokTurnUsage usage in turns.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            accumulator.Add(usage.Timestamp, usage.Counts.Total, usage.CostUsd, usage.Model, usage.Counts.CacheReadTokens, usage.Counts.CacheableInputTokens,
                            usage.TimedOutputTokens, usage.TimedDurationMilliseconds, usage.HasUnpricedUsage);
            if (usage.Timestamp.LocalDateTime.Date > today)
            {
                continue;
            }

            sessionLogs.Add(new SessionLogEntry
            {
                Timestamp = usage.Timestamp,
                Model = usage.Model,
                CostUsd = usage.CostUsd,
                HasUnpricedUsage = usage.HasUnpricedUsage,
                TimedOutputTokens = usage.TimedOutputTokens,
                TimedDurationMilliseconds = usage.TimedDurationMilliseconds,
            });
        }

        if (cursorUsage != null)
        {
            foreach (CursorUsageEvent usage in cursorUsage)
            {
                cancellationToken.ThrowIfCancellationRequested();
                TokenCounts counts = new(usage.CacheableInputTokens, usage.CacheReadTokens, usage.TotalTokens - usage.CacheableInputTokens - usage.UnpricedTokens);
                decimal? cost = CalculateCost(pricing, NormalizeModel(usage.Model), counts, usage.Timestamp);
                string model = usage.Model + (usage.Product == "cursor" ? "*" : "-api*");
                accumulator.Add(usage.Timestamp, usage.TotalTokens, cost, model, usage.CacheReadTokens, usage.CacheableInputTokens, hasUnpricedUsage: usage.UnpricedTokens > 0);
            }
        }

        return accumulator.ToStatistics(sessionLogs: SortSessionLogsNewestFirst(sessionLogs));
    }

    /// <summary>
    /// Collects local Antigravity and Gemini CLI session usage and estimated response speed for the supported periods.
    /// </summary>
    public TokenCostStatistics CollectGemini(string? geminiDirectory = null, DateTimeOffset? now = null, CancellationToken cancellationToken = default)
    {
        string? userProfileDirectory = string.IsNullOrWhiteSpace(geminiDirectory) ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : null;
        return CollectGemini(geminiDirectory, userProfileDirectory, now, cancellationToken);
    }

    internal TokenCostStatistics CollectGemini(string? geminiDirectory, string? userProfileDirectory, DateTimeOffset? now, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string root = GeminiUsageCollector.ResolveGeminiHomePath(geminiDirectory);
        DateTimeOffset current = now ?? DateTimeOffset.Now;
        Dictionary<string, ModelPricing> pricing = LoadPricing(cancellationToken);
        TokenCostPeriodAccumulator accumulator = new(current);
        List<SessionLogEntry> sessionLogs = [];
        DateTime today = current.LocalDateTime.Date;
        HashSet<string> seenFiles = new(StringComparer.OrdinalIgnoreCase);

        lock (m_GeminiCacheLock)
        {
            if (!ReferenceEquals(m_GeminiPricing, pricing))
            {
                m_GeminiFileUsageCache.Clear();
                m_GeminiPricing = pricing;
            }

            foreach ((string path, string? database) in EnumerateGeminiTranscriptFiles(root, userProfileDirectory, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (seenFiles.Add(path))
                {
                    CollectGeminiCachedFile(path, database, () => ParseAntigravityTranscriptEvents(path, database, pricing, cancellationToken), accumulator, sessionLogs, today);
                }
            }

            foreach (string path in EnumerateGeminiCliSessionFiles(root, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                seenFiles.Add(path);
                CollectGeminiCachedFile(path, null, () => (ParseGeminiCliSessionEvents(path, pricing, cancellationToken), true), accumulator, sessionLogs, today);
            }

            List<string> stale = [];
            foreach (string path in m_GeminiFileUsageCache.Keys)
            {
                if (!seenFiles.Contains(path))
                {
                    stale.Add(path);
                }
            }

            foreach (string path in stale)
            {
                m_GeminiFileUsageCache.Remove(path);
            }
        }

        return accumulator.ToStatistics(isSpeedEstimated: true, sessionLogs: SortSessionLogsNewestFirst(sessionLogs));
    }

    /// <summary>
    /// Replays a cached Gemini session file when unchanged on disk, otherwise parses and updates its cached events.
    /// </summary>
    private void CollectGeminiCachedFile(
        string path,
        string? database,
        Func<(List<CachedUsageEvent> Events, bool Cacheable)> parseEvents,
        TokenCostPeriodAccumulator accumulator,
        List<SessionLogEntry> sessionLogs,
        DateTime today)
    {
        FileFingerprint[] fingerprints = GetGeminiFingerprints(path, database);
        if (fingerprints.Length > 0
            && m_GeminiFileUsageCache.TryGetValue(path, out CachedFileUsage? cached)
            && cached.Files.AsSpan().SequenceEqual(fingerprints))
        {
            AddCachedUsage(cached.Events, accumulator);
            AppendSessionLogs(sessionLogs, cached.Events, today);
            return;
        }

        (List<CachedUsageEvent> events, bool cacheable) = parseEvents();
        if (cacheable && fingerprints.Length > 0 && fingerprints.AsSpan().SequenceEqual(GetGeminiFingerprints(path, database)))
        {
            m_GeminiFileUsageCache[path] = new CachedFileUsage
            {
                Files = fingerprints,
                Events = events,
            };
        }
        else
        {
            m_GeminiFileUsageCache.Remove(path);
        }

        AddCachedUsage(events, accumulator);
        AppendSessionLogs(sessionLogs, events, today);
    }

    private static FileFingerprint[] GetGeminiFingerprints(string path, string? database)
    {
        if (!TryGetFileFingerprint(path, out long length, out DateTime lastWriteUtc))
        {
            return [];
        }

        FileFingerprint transcript = new(path, length, lastWriteUtc);
        if (database == null || !TryGetFileFingerprint(database, out length, out lastWriteUtc))
        {
            return [transcript];
        }

        FileFingerprint db = new(database, length, lastWriteUtc);
        string wal = database + "-wal";
        return TryGetFileFingerprint(wal, out length, out lastWriteUtc) ? [transcript, db, new FileFingerprint(wal, length, lastWriteUtc)] : [transcript, db];
    }

    /// <summary>
    /// Enumerates Antigravity brain transcript logs without following directory links.
    /// </summary>
    private static IEnumerable<(string Path, string? Database)> EnumerateGeminiTranscriptFiles(string geminiDirectory, string? userProfileDirectory, CancellationToken cancellationToken)
    {
        EnumerationOptions options = new()
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.ReparsePoint,
            MaxRecursionDepth = 8,
        };
        List<string> brainDirectories =
        [
            Path.Combine(geminiDirectory, "antigravity-cli", "brain"),
            Path.Combine(geminiDirectory, "antigravity", "brain"),
            Path.Combine(geminiDirectory, "brain"),
        ];

        if (!string.IsNullOrEmpty(userProfileDirectory))
        {
            DirectoryInfo profiles = new(Path.Combine(userProfileDirectory, ".t3", "userdata", "providers", "antigravity"));
            if (profiles.Exists && (profiles.Attributes & FileAttributes.ReparsePoint) == 0)
            {
                EnumerationOptions direct = new() { AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true };
                foreach (DirectoryInfo profile in profiles.EnumerateDirectories("*", direct))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    foreach (DirectoryInfo acp in profile.EnumerateDirectories("antigravity-acp", direct))
                    {
                        brainDirectories.AddRange(acp.EnumerateDirectories("brain", direct).Select(brain => brain.FullName));
                    }
                }
            }
        }

        Dictionary<Guid, (FileInfo Transcript, string Database)> sessions = [];
        foreach (string brainDirectory in brainDirectories)
        {
            DirectoryInfo brain = new(brainDirectory);
            if (!brain.Exists)
            {
                continue;
            }

            foreach (FileInfo transcript in brain.EnumerateFiles("transcript.jsonl", options))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string[] parts = Path.GetRelativePath(brain.FullName, transcript.FullName).Split(Path.DirectorySeparatorChar);
                if (parts is [string id, ".system_generated", "logs", "transcript.jsonl"] && Guid.TryParseExact(id, "D", out Guid sessionId))
                {
                    // shortcut: prefer the latest snapshot; merge by step only if duplicated sessions can diverge.
                    if (!sessions.TryGetValue(sessionId, out var previous) || transcript.LastWriteTimeUtc > previous.Transcript.LastWriteTimeUtc)
                    {
                        sessions[sessionId] = (transcript, Path.Combine(brain.Parent!.FullName, "conversations", id + ".db"));
                    }
                }
                else
                {
                    yield return (transcript.FullName, null);
                }
            }
        }

        foreach ((FileInfo transcript, string database) in sessions.Values)
        {
            yield return (transcript.FullName, database);
        }
    }

    /// <summary>
    /// Enumerates Gemini CLI chat sessions and nested subagent sessions without following directory links.
    /// </summary>
    private static IEnumerable<string> EnumerateGeminiCliSessionFiles(string geminiDirectory, CancellationToken cancellationToken)
    {
        string tmpDirectory = Path.Combine(geminiDirectory, "tmp");
        if (!Directory.Exists(tmpDirectory))
        {
            yield break;
        }

        EnumerationOptions options = new()
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.ReparsePoint,
            MaxRecursionDepth = 16,
        };
        foreach (string path in Directory.EnumerateFiles(tmpDirectory, "*.json*", options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? directory = Path.GetDirectoryName(path);
            string? parentName = Path.GetFileName(directory);
            string fileName = Path.GetFileNameWithoutExtension(path);
            bool isJsonl = path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase);
            if (string.Equals(parentName, "chats", StringComparison.OrdinalIgnoreCase) && fileName.StartsWith("session-", StringComparison.OrdinalIgnoreCase))
            {
                // Gemini CLI migrates the old JSON history into the same basename with a .jsonl extension.
                if (isJsonl || (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && !File.Exists(path + "l")))
                {
                    yield return path;
                }
            }
            else if (isJsonl && Guid.TryParseExact(fileName, "D", out _) && Guid.TryParseExact(parentName, "D", out _)
                && string.Equals(Path.GetFileName(Path.GetDirectoryName(directory)), "chats", StringComparison.OrdinalIgnoreCase))
            {
                yield return path;
            }
        }
    }

    /// <summary>
    /// Parses one Antigravity transcript JSONL file into priced events and estimated response speeds.
    /// </summary>
    private static (List<CachedUsageEvent> Events, bool Cacheable) ParseAntigravityTranscriptEvents(
        string path,
        string? database,
        Dictionary<string, ModelPricing> pricing,
        CancellationToken cancellationToken)
    {
        if (new FileInfo(path).Length > MaxGrokSessionFileBytes)
        {
            throw new IOException("Gemini transcript exceeds the supported size; costs are unavailable.");
        }

        List<CachedUsageEvent> events = [];
        Dictionary<long, AntigravityTranscriptModels.StepModel>? stepModels = null;
        bool attemptedLookup = false;
        int pendingTimingIndex = -1;
        DateTimeOffset pendingStartTime = default;
        long pendingOutputTokens = 0;

        foreach (string line in ReadLinesShared(path, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                using JsonDocument document = JsonDocument.Parse(line);
                JsonElement root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    pendingTimingIndex = -1;
                    continue;
                }

                bool hasTimestamp = DateTimeOffset.TryParse(
                    GetString(root, "created_at"),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out DateTimeOffset timestamp);
                string stepType = GetString(root, "type");
                string source = GetString(root, "source");

                if (pendingTimingIndex >= 0)
                {
                    if (hasTimestamp && !string.Equals(stepType, "USER_INPUT", StringComparison.OrdinalIgnoreCase))
                    {
                        long durationMilliseconds = (timestamp - pendingStartTime).Ticks / TimeSpan.TicksPerMillisecond;
                        if (TryPairEstimatedSpeed(pendingOutputTokens, durationMilliseconds, out long timedOutput, out long timedDuration))
                        {
                            events[pendingTimingIndex] = events[pendingTimingIndex] with
                            {
                                TimedOutputTokens = timedOutput,
                                TimedDurationMilliseconds = timedDuration,
                            };
                        }
                    }

                    pendingTimingIndex = -1;
                }

                bool isModelResponse = string.Equals(stepType, "PLANNER_RESPONSE", StringComparison.OrdinalIgnoreCase)
                    || (string.Equals(source, "MODEL", StringComparison.OrdinalIgnoreCase)
                        && (root.TryGetProperty("output_tokens", out _) || root.TryGetProperty("input_tokens", out _)));
                if (!isModelResponse || !hasTimestamp)
                {
                    continue;
                }

                long freshInput = Math.Max(0, GetInt64(root, "input_tokens"));
                long cachedInput = Math.Max(0, GetInt64(root, "cache_read_tokens", GetInt64(root, "cached_tokens")));
                long output = Math.Max(0, GetInt64(root, "output_tokens"));
                TokenCounts counts = new(checked(freshInput + cachedInput), cachedInput, output);
                if (!counts.HasUsage)
                {
                    continue;
                }

                string rawModel = GetString(root, "model");
                if (string.IsNullOrWhiteSpace(rawModel) || string.Equals(rawModel, "unknown", StringComparison.OrdinalIgnoreCase))
                {
                    rawModel = "unknown";
                    long step = GetInt64(root, "step_index", -1);
                    if (database != null && step >= 0)
                    {
                        if (!attemptedLookup)
                        {
                            stepModels = AntigravityTranscriptModels.Read(database, cancellationToken);
                            attemptedLookup = true;
                        }
                        if (stepModels != null && stepModels.TryGetValue(step, out AntigravityTranscriptModels.StepModel match)
                            && match.Matches(timestamp, freshInput, cachedInput, output))
                        {
                            rawModel = match.Model;
                        }
                    }
                }

                string model = NormalizeAntigravityModel(rawModel);
                if (IsExcludedUsageModel(model))
                {
                    continue;
                }

                decimal? costUsd = CalculateCost(pricing, model, counts, timestamp);
                events.Add(new CachedUsageEvent(timestamp, counts.Total, costUsd, model, counts.CacheReadTokens, counts.CacheableInputTokens, 0, 0));

                bool hasToolCalls = root.TryGetProperty("tool_calls", out JsonElement toolCalls)
                    && toolCalls.ValueKind == JsonValueKind.Array
                    && toolCalls.GetArrayLength() > 0;
                if (hasToolCalls && output >= EstimatedSpeedMinOutputTokens)
                {
                    pendingTimingIndex = events.Count - 1;
                    pendingStartTime = timestamp;
                    pendingOutputTokens = output;
                }
            }
            catch (Exception exception) when (exception is JsonException or ArgumentOutOfRangeException or OverflowException)
            {
                pendingTimingIndex = -1;
            }
        }

        return (events, !attemptedLookup || stepModels != null);
    }

    /// <summary>
    /// Parses one Gemini CLI session JSON or JSONL file, deduplicating incremental $set snapshots by message id.
    /// </summary>
    private static List<CachedUsageEvent> ParseGeminiCliSessionEvents(
        string path,
        Dictionary<string, ModelPricing> pricing,
        CancellationToken cancellationToken)
    {
        if (new FileInfo(path).Length > MaxGrokSessionFileBytes)
        {
            throw new IOException("Gemini CLI session exceeds the supported size; costs are unavailable.");
        }

        Dictionary<string, CachedUsageEvent> byMessageId = new(StringComparer.Ordinal);
        List<string> messageOrder = [];
        int unnamedIndex = 0;

        if (path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
        {
            foreach (string line in ReadLinesShared(path, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    using JsonDocument document = JsonDocument.Parse(line);
                    JsonElement root = document.RootElement;
                    if (root.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    JsonElement setElement = GetObject(root, "$set");
                    if (setElement.ValueKind == JsonValueKind.Object
                        && setElement.TryGetProperty("messages", out JsonElement snapshotMessages)
                        && snapshotMessages.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement message in snapshotMessages.EnumerateArray())
                        {
                            RecordGeminiCliMessage(message, pricing, byMessageId, messageOrder, ref unnamedIndex);
                        }
                    }
                    else
                    {
                        RecordGeminiCliMessage(root, pricing, byMessageId, messageOrder, ref unnamedIndex);
                    }
                }
                catch (Exception exception) when (exception is JsonException or ArgumentOutOfRangeException or OverflowException)
                {
                }
            }
        }
        else
        {
            try
            {
                using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using JsonDocument document = JsonDocument.Parse(stream);
                JsonElement root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Object
                    && root.TryGetProperty("messages", out JsonElement messages)
                    && messages.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement message in messages.EnumerateArray())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        RecordGeminiCliMessage(message, pricing, byMessageId, messageOrder, ref unnamedIndex);
                    }
                }
            }
            catch (Exception exception) when (exception is JsonException or ArgumentOutOfRangeException or OverflowException)
            {
            }
        }

        List<CachedUsageEvent> events = new(messageOrder.Count);
        foreach (string key in messageOrder)
        {
            if (byMessageId.TryGetValue(key, out CachedUsageEvent usageEvent))
            {
                events.Add(usageEvent);
            }
        }

        return events;
    }

    /// <summary>
    /// Converts one Gemini CLI model message with token counters into a deduplicated cached event.
    /// </summary>
    private static void RecordGeminiCliMessage(
        JsonElement message,
        Dictionary<string, ModelPricing> pricing,
        Dictionary<string, CachedUsageEvent> byMessageId,
        List<string> messageOrder,
        ref int unnamedIndex)
    {
        if (message.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        string messageType = GetString(message, "type");
        if (!string.IsNullOrEmpty(messageType)
            && !string.Equals(messageType, "gemini", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(messageType, "model", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(messageType, "assistant", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        JsonElement tokens = GetObject(message, "tokens");
        if (tokens.ValueKind != JsonValueKind.Object || !HasAnyProperty(tokens))
        {
            return;
        }

        if (!DateTimeOffset.TryParse(
                GetString(message, "timestamp"),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out DateTimeOffset timestamp))
        {
            return;
        }

        string rawModel = GetString(message, "model");
        string model = NormalizeAntigravityModel(rawModel.Length > 0 ? rawModel : "unknown");
        if (IsExcludedUsageModel(model))
        {
            return;
        }

        long rawInput = Math.Max(0, GetInt64(tokens, "input"));
        long cached = Math.Max(0, GetInt64(tokens, "cached"));
        long output = Math.Max(0, GetInt64(tokens, "output"));
        long thoughts = Math.Max(0, GetInt64(tokens, "thoughts"));
        long toolInput = Math.Max(0, GetInt64(tokens, "tool"));

        // Gemini CLI input already includes cached tokens; tool-use prompts and thoughts are separate counters.
        long totalInput = checked(rawInput + toolInput);
        long totalOutput = checked(output + thoughts);
        TokenCounts counts = new(totalInput, cached, totalOutput);
        if (!counts.HasUsage)
        {
            return;
        }

        string messageId = GetString(message, "id");
        string key = messageId.Length > 0 ? messageId : $"anon:{unnamedIndex++}";
        if (!byMessageId.ContainsKey(key))
        {
            messageOrder.Add(key);
        }

        decimal? costUsd = CalculateCost(pricing, model, counts, timestamp);
        byMessageId[key] = new CachedUsageEvent(timestamp, counts.Total, costUsd, model, counts.CacheReadTokens, counts.CacheableInputTokens, 0, 0);
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
        lock (m_PricingLock)
        {
            return ReadPricing(cancellationToken);
        }
    }

    /// <summary>
    /// Compares pricing contents so same-length edits also invalidate calculated session costs.
    /// </summary>
    private Dictionary<string, ModelPricing> ReadPricing(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string json = File.ReadAllText(m_PricingPath);
        if (m_CachedPricing != null && string.Equals(json, m_CachedPricingJson, StringComparison.Ordinal))
        {
            return m_CachedPricing;
        }

        using JsonDocument document = JsonDocument.Parse(json);
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

            if (IsExcludedUsageModel(property.Name))
            {
                continue;
            }

            ModelPricing modelPricing = new(input, cachedInput, output, LoadPricingPeriods(value));
            pricing[property.Name] = modelPricing;
            if (value.TryGetProperty("aliases", out JsonElement aliases) && aliases.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement alias in aliases.EnumerateArray())
                {
                    if (alias.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(alias.GetString())
                        && !IsExcludedUsageModel(alias.GetString()!))
                    {
                        pricing[alias.GetString()!] = modelPricing;
                    }
                }
            }
        }

        m_CachedPricing = pricing;
        m_CachedPricingJson = json;
        return pricing;
    }

    /// <summary>
    /// Enumerates active and archived Codex session logs, including zstd rollouts from local history compression.
    /// </summary>
    private static IEnumerable<string> EnumerateCodexSessionFiles(string codexDirectory, CancellationToken cancellationToken)
    {
        string sessions = Path.Combine(codexDirectory, "sessions");
        string archived = Path.Combine(codexDirectory, "archived_sessions");
        foreach (string path in EnumerateCodexSessionDirectory(sessions, SearchOption.AllDirectories, cancellationToken))
        {
            yield return path;
        }

        foreach (string path in EnumerateCodexSessionDirectory(archived, SearchOption.TopDirectoryOnly, cancellationToken))
        {
            yield return path;
        }
    }

    /// <summary>
    /// Enumerates JSONL logs and keeps the plain file when a zstd sibling is still present.
    /// </summary>
    private static IEnumerable<string> EnumerateCodexSessionDirectory(string directory, SearchOption searchOption, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(directory))
        {
            yield break;
        }

        Dictionary<string, string> selected = new(StringComparer.OrdinalIgnoreCase);
        foreach (string pattern in new[] { "*.jsonl", "*.jsonl.zst" })
        {
            foreach (string path in Directory.EnumerateFiles(directory, pattern, searchOption))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsCodexSessionLog(path))
                {
                    continue;
                }

                string logicalPath = LogicalCodexSessionPath(path);
                if (!selected.TryGetValue(logicalPath, out string? existing))
                {
                    selected[logicalPath] = path;
                }
                else if (IsCompressedCodexSession(existing) && !IsCompressedCodexSession(path))
                {
                    selected[logicalPath] = path;
                }
            }
        }

        foreach (string path in selected.Values)
        {
            yield return path;
        }
    }

    /// <summary>
    /// Returns whether the path is a Codex rollout log, including a zstd-compressed one.
    /// </summary>
    private static bool IsCodexSessionLog(string path)
    {
        return path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)
            || IsCompressedCodexSession(path);
    }

    /// <summary>
    /// Returns whether Codex replaced this rollout with a zstd frame of the original JSONL.
    /// </summary>
    private static bool IsCompressedCodexSession(string path)
    {
        return path.EndsWith(".jsonl.zst", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns the uncompressed rollout path used to pair a plain file with its zstd sibling.
    /// </summary>
    private static string LogicalCodexSessionPath(string path)
    {
        return IsCompressedCodexSession(path) ? path[..^".zst".Length] : path;
    }

    /// <summary>
    /// Enumerates active and archived Grok Build update logs without following directory links.
    /// </summary>
    private static IEnumerable<string> EnumerateGrokSessionFiles(string grokDirectory, CancellationToken cancellationToken)
    {
        EnumerationOptions options = new()
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = false,
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
            throw new IOException("Grok session exceeds the supported size; costs are unavailable.");
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
    /// Adds one model's counters and paired API timing from a Grok turn using reported cost before local fallback pricing.
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
        if (IsExcludedUsageModel(model))
        {
            return;
        }

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
        decimal? localCost = counts.HasUsage && (!reportedCost.HasValue || costIsPartial) ? CalculateCost(pricing, model, counts, timestamp) : null;
        decimal? cost = !counts.HasUsage
            ? reportedCost
            : reportedCost.HasValue && !costIsPartial ? reportedCost : localCost ?? reportedCost;
        long timedOutputTokens = GetInt64(value, "outputTokens", -1);
        long apiDurationMilliseconds = GetInt64(value, "apiDurationMs");
        if (timedOutputTokens < MeasuredSpeedMinOutputTokens || apiDurationMilliseconds < MeasuredSpeedMinDurationMilliseconds)
        {
            timedOutputTokens = 0;
            apiDurationMilliseconds = 0;
        }

        bool incomplete = cost == null || costIsPartial && localCost == null;
        turns[$"{sessionId}\0{turnKey}\0{model}"] = new GrokTurnUsage(counts, cost, timestamp, model, timedOutputTokens, apiDurationMilliseconds, incomplete);
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
    /// Adds accepted Codex token deltas and estimated response windows while excluding tool waits and replayed usage.
    /// </summary>
    private void CollectCodexThread(
        string[] paths,
        Dictionary<string, ModelPricing> pricing,
        Dictionary<string, IReadOnlyList<string>> rolloutIndex,
        TokenCostPeriodAccumulator accumulator,
        List<SessionLogEntry> sessionLogs,
        DateTime today,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (TryGetCachedThreadUsage(paths, rolloutIndex, out List<CachedUsageEvent>? cachedEvents))
        {
            AddCachedUsage(cachedEvents, accumulator);
            AppendSessionLogs(sessionLogs, cachedEvents, today);
            return;
        }

        ReplayContext? context = ReadReplayContext(paths[0], cancellationToken);
        IReadOnlyList<TokenUsageSignature>? parentSignatures = null;
        IReadOnlyList<string> parentPaths = [];
        if (context != null)
        {
            if (!rolloutIndex.TryGetValue(context.Value.ParentId, out parentPaths!)
                || !TryLoadParentSignatures(parentPaths, context.Value.Cutoff, out parentSignatures, cancellationToken))
            {
                return;
            }
        }

        string model = "unknown";
        string? activeTurnId = null;
        bool hasTurnContext = false;
        string? responseModel = null;
        DateTimeOffset? responseStart = null;
        DateTimeOffset? responseEnd = null;
        DateTimeOffset? lastTokenCount = null;
        DateTimeOffset? usageRecordEnd = null;
        TokenCounts? usageRecordCounts = null;
        TokenCounts? totalHighWater = null;
        TokenUsageSnapshot? previousSnapshot = null;
        List<CachedUsageEvent> events = [];
        int parentOffset = 0;
        bool matchingReplay = parentSignatures?.Count > 0;
        foreach (string path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (string line in ReadLinesShared(path, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!line.Contains("\"turn_context\"", StringComparison.Ordinal)
                    && !line.Contains("\"event_msg\"", StringComparison.Ordinal)
                    && !line.Contains("\"response_item\"", StringComparison.Ordinal)
                    && !line.Contains("\"token_usage_record\"", StringComparison.Ordinal)
                    && !line.Contains("\"compacted\"", StringComparison.Ordinal))
                {
                    continue;
                }

                try
                {
                    using JsonDocument document = JsonDocument.Parse(line);
                    JsonElement root = document.RootElement;
                    string type = GetString(root, "type");
                    JsonElement payload = GetObject(root, "payload");
                    string eventType = GetString(payload, "type");
                    bool hasTimestamp = DateTimeOffset.TryParse(GetString(root, "timestamp"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset timestamp);
                    if (type == "event_msg" && eventType == "task_started")
                    {
                        string turnId = GetString(payload, "turn_id");
                        activeTurnId = hasTimestamp && turnId.Length > 0 ? turnId : null;
                        hasTurnContext = false;
                        responseStart = activeTurnId != null ? timestamp : null;
                        responseEnd = null;
                        responseModel = null;
                        usageRecordEnd = null;
                        usageRecordCounts = null;
                        continue;
                    }

                    if (type == "compacted" || type == "event_msg" && eventType is "task_complete" or "turn_aborted" or "context_compacted" or "error")
                    {
                        activeTurnId = null;
                        hasTurnContext = false;
                        responseStart = responseEnd = null;
                        responseModel = null;
                        usageRecordEnd = null;
                        usageRecordCounts = null;
                        continue;
                    }

                    if (type == "token_usage_record")
                    {
                        JsonElement usage = GetObject(payload, "usage");
                        if (hasTimestamp && HasAnyProperty(usage))
                        {
                            usageRecordEnd = timestamp;
                            usageRecordCounts = ParseCounts(usage);
                        }

                        continue;
                    }

                    if (type == "turn_context")
                    {
                        string candidate = GetString(payload, "model");
                        model = candidate.Length > 0 ? NormalizeModel(candidate) : model;
                        if (!hasTimestamp || GetString(payload, "turn_id") != activeTurnId || responseModel != null && responseModel != model)
                        {
                            activeTurnId = null;
                            hasTurnContext = false;
                            responseStart = responseEnd = null;
                            responseModel = null;
                            usageRecordEnd = null;
                            usageRecordCounts = null;
                        }
                        else if (responseEnd == null)
                        {
                            hasTurnContext = true;
                            responseStart = hasTimestamp ? timestamp : null;
                        }

                        continue;
                    }

                    if (type == "response_item" || type == "event_msg" && eventType == "user_message")
                    {
                        string role = GetString(payload, "role");
                        bool isGenerated = type == "response_item" && (eventType == "reasoning" || eventType == "message" && role == "assistant" || eventType.EndsWith("_call", StringComparison.Ordinal));
                        bool isInput = eventType is "function_call_output" or "custom_tool_call_output" or "agent_message" or "user_message"
                            || eventType == "message" && role is "user" or "developer" or "system";
                        if (!hasTimestamp || responseStart.HasValue && timestamp < responseStart.Value || responseEnd.HasValue && timestamp < responseEnd.Value)
                        {
                            responseStart = responseEnd = null;
                            responseModel = null;
                        }
                        else if (activeTurnId != null && hasTurnContext && isGenerated && responseStart.HasValue)
                        {
                            if (lastTokenCount is { } previousCount
                                && responseEnd == null
                                && timestamp >= previousCount
                                && (timestamp - previousCount).TotalMilliseconds <= SameFlushSlackMilliseconds)
                            {
                                continue;
                            }

                            responseEnd = timestamp;
                            responseModel ??= model;
                        }
                        else if (activeTurnId != null && hasTurnContext && isInput && responseEnd == null)
                        {
                            responseStart = timestamp;
                        }

                        continue;
                    }

                    if (type != "event_msg" || eventType != "token_count")
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
                    if (!isReplay && !shouldBill)
                    {
                        continue;
                    }

                    DateTimeOffset? windowEnd = usageRecordEnd is { } recordEnd
                        && usageRecordCounts is { } recordCounts
                        && TryGetUsageCounts(info, "last_token_usage", out TokenCounts lastUsage)
                        && recordCounts == lastUsage
                            ? recordEnd
                            : responseEnd;
                    long timedDurationMilliseconds = hasTimestamp && activeTurnId != null && responseStart.HasValue && windowEnd.HasValue
                        && windowEnd.Value > responseStart.Value && windowEnd.Value <= timestamp
                        ? (windowEnd.Value - responseStart.Value).Ticks / TimeSpan.TicksPerMillisecond
                        : 0;
                    string? timedModel = responseModel;
                    if (hasTimestamp)
                    {
                        lastTokenCount = timestamp;
                    }

                    usageRecordEnd = null;
                    usageRecordCounts = null;
                    responseStart = hasTimestamp && activeTurnId != null ? timestamp : null;
                    responseEnd = null;
                    responseModel = null;
                    if (isReplay
                        || !shouldBill
                        || !hasTimestamp)
                    {
                        continue;
                    }

                    // High water is already updated, so a later total-only delta does not absorb this usage.
                    if (IsExcludedUsageModel(model))
                    {
                        continue;
                    }

                    decimal? costUsd = CalculateCost(pricing, model, increment, timestamp);
                    bool hasTimedOutput = timedDurationMilliseconds > 0 && timedModel == model && increment.Output > 0
                        && TryGetUsageCounts(info, "last_token_usage", out TokenCounts last) && last == increment;
                    long timedOutputTokens = 0;
                    if (hasTimedOutput)
                    {
                        TryPairEstimatedSpeed(increment.Output, timedDurationMilliseconds, out timedOutputTokens, out timedDurationMilliseconds);
                    }
                    else
                    {
                        timedDurationMilliseconds = 0;
                    }

                    events.Add(new CachedUsageEvent(timestamp, increment.Total, costUsd, model, increment.CacheReadTokens, increment.CacheableInputTokens,
                                                   timedOutputTokens, timedDurationMilliseconds));
                    accumulator.Add(timestamp, increment.Total, costUsd, model, increment.CacheReadTokens, increment.CacheableInputTokens, timedOutputTokens, timedDurationMilliseconds);
                }
                catch (JsonException)
                {
                    // Codex can leave a partially written final JSONL line while a session is active.
                    activeTurnId = null;
                    hasTurnContext = false;
                    responseStart = responseEnd = null;
                    responseModel = null;
                    usageRecordEnd = null;
                    usageRecordCounts = null;
                }
            }
        }

        StoreCachedThreadUsage([.. paths, .. parentPaths], context?.ParentId, events);
        AppendSessionLogs(sessionLogs, events, today);
    }

    /// <summary>
    /// Adds one session-log row per increment that is not after the collect's local day.
    /// </summary>
    private static void AppendSessionLogs(List<SessionLogEntry> sessionLogs, List<CachedUsageEvent> events, DateTime today)
    {
        foreach (CachedUsageEvent usageEvent in events)
        {
            if (usageEvent.Timestamp.LocalDateTime.Date > today)
            {
                continue;
            }

            sessionLogs.Add(new SessionLogEntry
            {
                Timestamp = usageEvent.Timestamp,
                Model = usageEvent.Model,
                CostUsd = usageEvent.CostUsd,
                HasUnpricedUsage = usageEvent.CostUsd == null && usageEvent.Tokens > 0,
                TimedOutputTokens = usageEvent.TimedOutputTokens,
                TimedDurationMilliseconds = usageEvent.TimedDurationMilliseconds,
            });
        }
    }

    /// <summary>
    /// Orders rows newest-first while keeping equal timestamps in collect order.
    /// </summary>
    private static SessionLogEntry[] SortSessionLogsNewestFirst(List<SessionLogEntry> entries)
    {
        return entries.OrderByDescending(entry => entry.Timestamp).ToArray();
    }

    /// <summary>
    /// Accepts estimated Codex speed only for responses with enough output and a plausible duration.
    /// </summary>
    private static bool TryPairEstimatedSpeed(long outputTokens, long durationMilliseconds, out long timedOutputTokens, out long timedDurationMilliseconds)
    {
        timedOutputTokens = 0;
        timedDurationMilliseconds = 0;
        if (outputTokens < EstimatedSpeedMinOutputTokens
            || durationMilliseconds < EstimatedSpeedMinDurationMilliseconds
            || durationMilliseconds > MaxPlausibleLatencyMilliseconds)
        {
            return false;
        }

        timedOutputTokens = outputTokens;
        timedDurationMilliseconds = durationMilliseconds;
        return true;
    }

    /// <summary>
    /// Calculates API-equivalent cost for one model usage event.
    /// </summary>
    private static decimal? CalculateCost(Dictionary<string, ModelPricing> pricing, string model, TokenCounts counts, DateTimeOffset timestamp)
    {
        if (!TryFindPricing(pricing, model, out ModelPricing modelPricing))
        {
            return null;
        }

        decimal input = modelPricing.Input;
        decimal cachedInput = modelPricing.CachedInput;
        decimal output = modelPricing.Output;
        foreach (PricingPeriod period in modelPricing.Periods)
        {
            if (!PeriodMatches(period, timestamp))
            {
                continue;
            }

            input = period.Input;
            cachedInput = period.CachedInput;
            output = period.Output;
            break;
        }

        long cached = Math.Min(counts.CachedInput, counts.Input);
        long freshInput = counts.Input - cached;
        return (freshInput * input + cached * cachedInput + counts.Output * output) / 1_000_000m;
    }

    /// <summary>
    /// Reads optional timed rate periods from a pricing entry.
    /// </summary>
    private static PricingPeriod[] LoadPricingPeriods(JsonElement value)
    {
        if (!value.TryGetProperty("periods", out JsonElement periods) || periods.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        List<PricingPeriod> result = [];
        foreach (JsonElement period in periods.EnumerateArray())
        {
            if (period.ValueKind != JsonValueKind.Object ||
                !TryGetDecimalProperty(period, "input", out decimal input) ||
                !TryGetDecimalProperty(period, "cachedInput", out decimal cachedInput) ||
                !TryGetDecimalProperty(period, "output", out decimal output) ||
                !TryGetOptionalClock(period, "startUtc", TimeSpan.Zero, out TimeSpan startUtc) ||
                !TryGetOptionalClock(period, "endUtc", TimeSpan.FromHours(24), out TimeSpan endUtc))
            {
                continue;
            }

            DayOfWeek[]? daysUtc = null;
            if (period.TryGetProperty("daysUtc", out JsonElement days) && days.ValueKind != JsonValueKind.Null)
            {
                if (days.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                List<DayOfWeek> parsed = [];
                bool validDays = true;
                foreach (JsonElement day in days.EnumerateArray())
                {
                    if (day.ValueKind != JsonValueKind.Number || !day.TryGetInt32(out int number) || number is < 0 or > 6)
                    {
                        validDays = false;
                        break;
                    }

                    parsed.Add((DayOfWeek)number);
                }

                if (!validDays || parsed.Count == 0)
                {
                    continue;
                }

                daysUtc = [.. parsed];
            }

            result.Add(new PricingPeriod(input, cachedInput, output, daysUtc, startUtc, endUtc));
        }

        return [.. result];
    }

    /// <summary>
    /// Reads an optional UTC clock time, using the fallback when the field is omitted.
    /// </summary>
    private static bool TryGetOptionalClock(JsonElement period, string name, TimeSpan fallback, out TimeSpan time)
    {
        time = fallback;
        if (!period.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        return value.ValueKind == JsonValueKind.String &&
            TimeSpan.TryParse(value.GetString(), CultureInfo.InvariantCulture, out time);
    }

    /// <summary>
    /// Returns whether a usage timestamp falls inside one configured UTC pricing window.
    /// </summary>
    private static bool PeriodMatches(PricingPeriod period, DateTimeOffset timestamp)
    {
        DateTime utc = timestamp.UtcDateTime;
        if (period.DaysUtc is { Length: > 0 } days && Array.IndexOf(days, utc.DayOfWeek) < 0)
        {
            return false;
        }

        TimeSpan time = utc.TimeOfDay;
        return time >= period.StartUtc && time < period.EndUtc;
    }

    /// <summary>
    /// Groups Codex logs by thread id, stitches dual-segment pages, and drops archived twins of the same rollout.
    /// </summary>
    private static List<string[]> GroupCodexSessionFiles(string[] paths)
    {
        Dictionary<string, List<string>> threads = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> seenRollouts = new(StringComparer.OrdinalIgnoreCase);
        List<string[]> groups = [];
        foreach (string path in paths)
        {
            if (!TryParseRolloutIds(path, out string threadId, out string rolloutId))
            {
                groups.Add([path]);
                continue;
            }

            if (!seenRollouts.Add(rolloutId))
            {
                continue;
            }

            if (!threads.TryGetValue(threadId, out List<string>? pages))
            {
                pages = [];
                threads[threadId] = pages;
            }

            pages.Add(path);
        }

        foreach (List<string> pages in threads.Values)
        {
            groups.Add([.. pages.OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)]);
        }

        return groups;
    }

    /// <summary>
    /// Indexes stitched rollout pages by stable thread UUID.
    /// </summary>
    private static Dictionary<string, IReadOnlyList<string>> BuildRolloutIndex(List<string[]> groups)
    {
        Dictionary<string, IReadOnlyList<string>> result = new(StringComparer.OrdinalIgnoreCase);
        foreach (string[] paths in groups)
        {
            if (TryParseRolloutIds(paths[0], out string threadId, out _))
            {
                result.TryAdd(threadId, paths);
            }
        }

        return result;
    }

    /// <summary>
    /// Reads the thread and rollout UUIDs from a Codex filename, including dual-segment desktop pages.
    /// </summary>
    private static bool TryParseRolloutIds(string path, out string threadId, out string rolloutId)
    {
        threadId = string.Empty;
        rolloutId = string.Empty;
        string name = Path.GetFileNameWithoutExtension(LogicalCodexSessionPath(path));
        if (name.Length < 36 || !Guid.TryParse(name.AsSpan(name.Length - 36, 36), out Guid rollout))
        {
            return false;
        }

        rolloutId = rollout.ToString();
        threadId = rolloutId;
        if (name.Length >= 73
            && name[name.Length - 37] == '_'
            && Guid.TryParse(name.AsSpan(name.Length - 73, 36), out Guid thread))
        {
            threadId = thread.ToString();
        }

        return true;
    }

    /// <summary>
    /// Loads parent token signatures when the stitched parent log has already reached the child fork time.
    /// </summary>
    private static bool TryLoadParentSignatures(
        IReadOnlyList<string> parentPaths,
        DateTimeOffset cutoff,
        out IReadOnlyList<TokenUsageSignature> signatures,
        CancellationToken cancellationToken)
    {
        signatures = [];
        List<TokenUsageSignature> result = [];
        DateTimeOffset? lastTimestamp = null;
        foreach (string path in parentPaths)
        {
            foreach (string line in ReadLinesShared(path, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!line.Contains("\"timestamp\"", StringComparison.Ordinal))
                {
                    continue;
                }

                try
                {
                    using JsonDocument document = JsonDocument.Parse(line);
                    JsonElement root = document.RootElement;
                    if (!DateTimeOffset.TryParse(GetString(root, "timestamp"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset timestamp))
                    {
                        continue;
                    }

                    lastTimestamp = lastTimestamp is { } previous && previous > timestamp ? previous : timestamp;
                    JsonElement payload = GetObject(root, "payload");
                    if (GetString(root, "type") != "event_msg"
                        || GetString(payload, "type") != "token_count"
                        || timestamp > cutoff
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
        }

        if (lastTimestamp == null || lastTimestamp < cutoff)
        {
            return false;
        }

        signatures = result;
        return true;
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
    /// Reads an active Codex JSONL file without blocking its writer. Compressed rollouts are zstd frames of the same JSONL.
    /// </summary>
    private static IEnumerable<string> ReadLinesShared(string path, CancellationToken cancellationToken)
    {
        bool compressed = IsCompressedCodexSession(path);
        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }
        catch (Exception exception) when (compressed && exception is not OperationCanceledException)
        {
            yield break;
        }

        using (stream)
        {
            Stream content = stream;
            DecompressionStream? decompressed = null;
            if (compressed)
            {
                try
                {
                    decompressed = new DecompressionStream(stream);
                    content = decompressed;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    yield break;
                }
            }

            using (decompressed)
            using (StreamReader reader = new(content, leaveOpen: true))
            {
                while (TryReadSessionLine(reader, compressed, out string? line))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return line;
                }
            }
        }
    }

    /// <summary>
    /// Reads one JSONL line. A damaged compressed rollout stops that file instead of failing every session.
    /// </summary>
    private static bool TryReadSessionLine(StreamReader reader, bool compressed, [NotNullWhen(true)] out string? line)
    {
        try
        {
            line = reader.ReadLine();
            return line != null;
        }
        catch (Exception exception) when (compressed && exception is not OperationCanceledException)
        {
            line = null;
            return false;
        }
    }

    /// <summary>
    /// Matches exact models, Antigravity internal orchestrator IDs, and effort/variant suffixes.
    /// </summary>
    private static bool TryFindPricing(Dictionary<string, ModelPricing> pricing, string model, out ModelPricing result)
    {
        if (pricing.TryGetValue(model, out result))
        {
            return true;
        }

        string mapped = MapAntigravityModelId(model);
        if (!string.Equals(mapped, model, StringComparison.OrdinalIgnoreCase) && pricing.TryGetValue(mapped, out result))
        {
            return true;
        }

        string[] suffixes = ["-minimal", "-low", "-medium", "-high", "-xhigh", "-thinking", "-g1-pro", "-g1-ultra", "-preview", "-latest"];
        string candidate = model;
        while (suffixes.FirstOrDefault(candidate.EndsWith) is string suffix)
        {
            candidate = candidate[..^suffix.Length];
            if (pricing.TryGetValue(candidate, out result))
            {
                return true;
            }

            string mappedCandidate = MapAntigravityModelId(candidate);
            if (!string.Equals(mappedCandidate, candidate, StringComparison.OrdinalIgnoreCase)
                && pricing.TryGetValue(mappedCandidate, out result))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Normalizes a Gemini or Antigravity model ID after stripping snapshot dates and provider prefixes.
    /// </summary>
    private static string NormalizeAntigravityModel(string value)
    {
        return MapAntigravityModelId(NormalizeModel(value));
    }

    /// <summary>
    /// Maps internal Antigravity model identifiers onto canonical pricing names.
    /// </summary>
    public static string MapAntigravityModelId(string model)
    {
        return model.ToLowerInvariant() switch
        {
            "gemini-pro-orchestrator-g1-pro" or "gemini-pro-orchestrator" => "gemini-4-argon",
            "gemini-pro-orchestrator-g1-ultra" or "gemini-ultra-orchestrator" => "gemini-4-carbon",
            "gemini-flash-executor" or "gemini-flash-worker-agy" => "gemini-4-hydrogen",
            "gemini-3.8-flash-n" => "gemini-3.8-flash",
            _ => model,
        };
    }

    /// <summary>
    /// Returns whether this model is left out of totals, cost, charts, and session logs.
    /// </summary>
    private static bool IsExcludedUsageModel(string model)
    {
        return string.Equals(NormalizeModel(model), k_ExcludedUsageModel, StringComparison.Ordinal);
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
    /// Replays cached usage and paired response timing into a fresh period accumulator.
    /// </summary>
    private static void AddCachedUsage(List<CachedUsageEvent> events, TokenCostPeriodAccumulator accumulator)
    {
        foreach (CachedUsageEvent usageEvent in events)
        {
            accumulator.Add(usageEvent.Timestamp, usageEvent.Tokens, usageEvent.CostUsd, usageEvent.Model, usageEvent.CacheReadTokens, usageEvent.CacheableInputTokens,
                            usageEvent.TimedOutputTokens, usageEvent.TimedDurationMilliseconds);
        }
    }

    /// <summary>
    /// Returns cached usage when the thread and its replay parent still match their stored fingerprints.
    /// </summary>
    private bool TryGetCachedThreadUsage(string[] paths, Dictionary<string, IReadOnlyList<string>> rolloutIndex, [NotNullWhen(true)] out List<CachedUsageEvent>? events)
    {
        events = null;
        if (!m_FileUsageCache.TryGetValue(paths[0], out CachedFileUsage? cached))
        {
            return false;
        }

        if (cached.ParentId != null)
        {
            if (!rolloutIndex.TryGetValue(cached.ParentId, out IReadOnlyList<string>? parentPaths))
            {
                return false;
            }
            paths = [.. paths, .. parentPaths];
        }
        if (cached.Files.Length != paths.Length)
        {
            return false;
        }

        for (int index = 0; index < paths.Length; index++)
        {
            FileFingerprint fingerprint = cached.Files[index];
            if (!string.Equals(fingerprint.Path, paths[index], StringComparison.OrdinalIgnoreCase)
                || !TryGetFileFingerprint(paths[index], out long length, out DateTime lastWriteUtc)
                || fingerprint.Length != length
                || fingerprint.LastWriteUtc != lastWriteUtc)
            {
                return false;
            }
        }

        events = cached.Events;
        return true;
    }

    /// <summary>
    /// Stores parsed usage for the current fingerprints of every stitched page.
    /// </summary>
    private void StoreCachedThreadUsage(string[] paths, string? parentId, List<CachedUsageEvent> events)
    {
        FileFingerprint[] files = new FileFingerprint[paths.Length];
        for (int index = 0; index < paths.Length; index++)
        {
            if (!TryGetFileFingerprint(paths[index], out long length, out DateTime lastWriteUtc))
            {
                return;
            }

            files[index] = new FileFingerprint(paths[index], length, lastWriteUtc);
        }

        m_FileUsageCache[paths[0]] = new CachedFileUsage
        {
            Files = files,
            ParentId = parentId,
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
        long CacheableInputTokens,
        long TimedOutputTokens,
        long TimedDurationMilliseconds);

    private readonly record struct FileFingerprint(string Path, long Length, DateTime LastWriteUtc);

    private sealed class CachedFileUsage
    {
        public required FileFingerprint[] Files { get; init; }

        public string? ParentId { get; init; }

        public required List<CachedUsageEvent> Events { get; init; }
    }

    private readonly record struct ModelPricing(decimal Input, decimal CachedInput, decimal Output, PricingPeriod[] Periods);

    private readonly record struct PricingPeriod(decimal Input, decimal CachedInput, decimal Output, DayOfWeek[]? DaysUtc, TimeSpan StartUtc, TimeSpan EndUtc);

    private readonly record struct GrokTurnUsage(TokenCounts Counts, decimal? CostUsd, DateTimeOffset Timestamp, string Model, long TimedOutputTokens, long TimedDurationMilliseconds, bool HasUnpricedUsage);

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
