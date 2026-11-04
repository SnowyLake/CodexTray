using System.Globalization;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CodexTray.Core;

public sealed record CursorUsageEvent(DateTimeOffset Timestamp, string Model, long TotalTokens, long CacheReadTokens, long CacheableInputTokens);

/// <summary>
/// Reads Cursor product events from the unified billing ledger without modifying its login.
/// </summary>
public sealed class CursorUsageCollector
{
    private const string k_Endpoint = "https://cursor.com/api/dashboard/v2/query-billing-items";
    private const string k_EncryptedTokenPrefix = "cursor-safe-storage:v1:";
    private static readonly HttpClient s_HttpClient = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(10) };
    private readonly HttpClient m_HttpClient;
    private readonly string m_CursorDirectory;
    private readonly string? m_CachePath;
    private readonly string? m_LegacyCachePath;
    private readonly Dictionary<DateTimeOffset, BillingMonth> m_Months = [];
    private string? m_AccountId;
    private DateTimeOffset? m_HistoryStart;

    public CursorUsageCollector() : this(s_HttpClient, cachePath: Path.Combine(AppContext.BaseDirectory, CodexTrayDefaults.CursorUsageCacheFileName))
    {
    }

    public CursorUsageCollector(HttpClient httpClient, string? cursorDirectory = null, string? cachePath = null)
    {
        m_HttpClient = httpClient;
        m_CursorDirectory = cursorDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Cursor");
        m_CachePath = cachePath;
        m_LegacyCachePath = cachePath?.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) == true ? cachePath[..^3] : null;
    }

    /// <summary>
    /// Returns no cloud usage on a current-query failure; unavailable older months bound the readable history.
    /// </summary>
    public async Task<IReadOnlyList<CursorUsageEvent>> CollectAsync(DateTimeOffset? now = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            string? accessToken = ReadAccessToken();
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                ClearCache();
                return [];
            }
            if (accessToken.Split('.').Length != 3 || !OAuthTokenHelpers.TryReadJwtPayload(accessToken, out JsonElement claims))
            {
                throw new InvalidOperationException("Invalid Cursor credential.");
            }
            string accountId = claims.GetProperty("sub").GetString()!;
            if (string.IsNullOrWhiteSpace(accountId))
            {
                throw new InvalidOperationException("Missing Cursor account.");
            }
            accountId = accountId[(accountId.LastIndexOf('|') + 1)..];
            DateTimeOffset asOf = (now ?? DateTimeOffset.Now).ToUniversalTime();
            DateTimeOffset currentMonth = new(asOf.Year, asOf.Month, 1, 0, 0, 0, TimeSpan.Zero);
            if (accountId != m_AccountId)
            {
                ClearCache();
                m_AccountId = accountId;
                LoadCache(currentMonth);
            }

            if (!m_HistoryStart.HasValue)
            {
                using HttpRequestMessage identity = new(HttpMethod.Post, "https://api2.cursor.sh/aiserver.v1.DashboardService/GetMe");
                identity.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                identity.Headers.Add("connect-protocol-version", "1");
                identity.Content = new StringContent("{}", Encoding.UTF8, "application/json");
                using HttpResponseMessage response = await UsageHttp.SendAsync(m_HttpClient, identity, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    throw new UsageHttpException(response.StatusCode);
                }
                using JsonDocument user = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
                DateTimeOffset created = DateTimeOffset.Parse(user.RootElement.GetProperty("createdAt").GetString()!, CultureInfo.InvariantCulture).ToUniversalTime();
                if (created > asOf)
                {
                    throw new InvalidOperationException("Invalid Cursor account creation time.");
                }
                m_HistoryStart = new DateTimeOffset(created.Year, created.Month, 1, 0, 0, 0, TimeSpan.Zero);
            }

            string cookie = "WorkosCursorSessionToken=" + Uri.EscapeDataString(accountId + "::" + accessToken);
            for (DateTimeOffset month = currentMonth; month >= m_HistoryStart.Value; month = month.AddMonths(-1))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (month < currentMonth.AddMonths(-1) && m_Months.TryGetValue(month, out BillingMonth? closed) && closed.Through >= month.AddMonths(1))
                {
                    continue;
                }
                try
                {
                    BillingMonth collected = await CollectMonthAsync(month, asOf, cookie, cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!m_Months.TryGetValue(month, out BillingMonth? previous) || collected != previous)
                    {
                        m_Months[month] = collected;
                        SaveCache();
                    }
                }
                catch (Exception exception) when (!cancellationToken.IsCancellationRequested && month < currentMonth && !m_Months.ContainsKey(month) &&
                                                  !m_Months.Keys.Any(cachedMonth => cachedMonth < month) && IsCollectionError(exception))
                {
                    // Keep the readable history boundary so failed old pages do not delay every restart.
                    m_HistoryStart = month.AddMonths(1);
                    SaveCache();
                    break;
                }
            }
            return m_Months.Where(pair => pair.Key >= m_HistoryStart.Value && pair.Key <= currentMonth).SelectMany(pair => pair.Value.Events).ToArray();
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested && IsCollectionError(exception))
        {
            ClearCache();
            return [];
        }
    }

    private async Task<BillingMonth> CollectMonthAsync(DateTimeOffset month, DateTimeOffset asOf, string cookie, CancellationToken cancellationToken)
    {
        DateTimeOffset nextMonth = month.AddMonths(1);
        DateTimeOffset end = nextMonth < asOf ? nextMonth : asOf;
        string bounds = $"?scope=user&product=cursor&pageSize=500&startTime={month.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)}" +
                        $"&endTime={end.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)}";
        string? cursor = null;
        string? firstPage = null;
        HashSet<string> cursors = new(StringComparer.Ordinal);
        HashSet<string> ids = new(StringComparer.Ordinal);
        List<CursorUsageEvent> events = [];
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            string url = k_Endpoint + bounds + (cursor == null ? string.Empty : "&cursor=" + Uri.EscapeDataString(cursor));
            using HttpRequestMessage request = new(HttpMethod.Get, url);
            request.Headers.Add("Cookie", cookie);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using HttpResponseMessage response = await UsageHttp.SendAsync(m_HttpClient, request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new UsageHttpException(response.StatusCode);
            }
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (cursor == null)
            {
                firstPage = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)));
                // shortcut: unchanged heads reuse the month; independent older-page corrections require deleting the local cache.
                if (m_Months.TryGetValue(month, out BillingMonth? cached) && cached.FirstPageHash == firstPage)
                {
                    return end == nextMonth && cached.Through < end ? cached with { Through = end } : cached;
                }
            }
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;
            JsonElement rows = root.GetProperty("data");
            JsonElement pagination = root.GetProperty("pagination");
            bool hasMore = pagination.GetProperty("hasMore").GetBoolean();
            string? nextCursor = pagination.TryGetProperty("nextCursor", out JsonElement cursorValue) ? cursorValue.GetString() : null;
            if (hasMore != !string.IsNullOrWhiteSpace(nextCursor) || (hasMore && (rows.GetArrayLength() == 0 || !cursors.Add(nextCursor!))))
            {
                throw new InvalidOperationException("Incomplete Cursor billing pages.");
            }
            foreach (JsonElement row in rows.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (row.GetProperty("product").GetString() != "cursor")
                {
                    continue;
                }
                string? id = row.GetProperty("billingItemId").GetString();
                if (string.IsNullOrWhiteSpace(id))
                {
                    throw new InvalidOperationException("Missing Cursor billing ID.");
                }
                if (!ids.Add(id))
                {
                    continue;
                }
                DateTimeOffset timestamp = DateTimeOffset.Parse(row.GetProperty("eventTime").GetString()!, CultureInfo.InvariantCulture).ToUniversalTime();
                if (timestamp < month || timestamp > end)
                {
                    throw new InvalidOperationException("Cursor billing event outside requested month.");
                }
                // The API's inclusive end can return the next month's midnight in both windows.
                if (timestamp == nextMonth || !row.GetProperty("hasTokenMeters").GetBoolean())
                {
                    continue;
                }
                JsonElement usage = row.GetProperty("usage");
                long input = ReadCount(usage, "prompt-text-tokens");
                long output = ReadCount(usage, "completion-text-tokens");
                long cacheRead = ReadCount(usage, "cached-prompt-text-tokens");
                long cacheWrite = ReadCount(usage, "cache-write-text-tokens");
                long cacheableInput = checked(input + cacheRead + cacheWrite);
                long total = checked(cacheableInput + output);
                if (total != ReadCount(row, "totalTokens"))
                {
                    throw new InvalidOperationException("Cursor billing token total mismatch.");
                }
                string? model = row.TryGetProperty("displayExtras", out JsonElement extras) && extras.ValueKind != JsonValueKind.Null && extras.TryGetProperty("model", out JsonElement modelValue)
                    ? modelValue.GetString() : null;
                events.Add(new CursorUsageEvent(timestamp, string.IsNullOrWhiteSpace(model) ? "unknown" : model, total, cacheRead, cacheableInput));
            }
            cursor = hasMore ? nextCursor : null;
        } while (cursor != null);
        return new BillingMonth(firstPage!, end, events.ToArray());
    }

    private void LoadCache(DateTimeOffset currentMonth)
    {
        if (m_CachePath == null)
        {
            return;
        }
        bool legacy = !File.Exists(m_CachePath);
        string? sourcePath = legacy ? m_LegacyCachePath : m_CachePath;
        if (sourcePath == null || !File.Exists(sourcePath))
        {
            return;
        }
        try
        {
            UsageCacheFile? cache;
            using (FileStream file = File.OpenRead(sourcePath))
            using (Stream content = legacy ? file : new GZipStream(file, CompressionMode.Decompress))
            {
                cache = JsonSerializer.Deserialize<UsageCacheFile>(content);
            }
            string accountHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(m_AccountId!)));
            if (cache == null || cache.Version != 1 || cache.AccountHash != accountHash || cache.Months == null || cache.HistoryStart == default ||
                cache.HistoryStart.Offset != TimeSpan.Zero || cache.HistoryStart.Day != 1 || cache.HistoryStart.TimeOfDay != TimeSpan.Zero || cache.HistoryStart > currentMonth)
            {
                return;
            }
            foreach ((DateTimeOffset month, BillingMonth cached) in cache.Months)
            {
                if (month.Offset != TimeSpan.Zero || month.Day != 1 || month.TimeOfDay != TimeSpan.Zero || month < cache.HistoryStart || month > currentMonth ||
                    cached == null || cached.FirstPageHash == null || cached.FirstPageHash.Length != 64 || !cached.FirstPageHash.All(Uri.IsHexDigit) ||
                    cached.Through < month || cached.Through > month.AddMonths(1) || cached.Events == null ||
                    cached.Events.Any(usage => usage == null || string.IsNullOrWhiteSpace(usage.Model) || usage.Timestamp < month || usage.Timestamp >= month.AddMonths(1) ||
                                              usage.Timestamp > cached.Through || usage.CacheReadTokens < 0 || usage.CacheableInputTokens < usage.CacheReadTokens || usage.TotalTokens < usage.CacheableInputTokens))
                {
                    return;
                }
            }
            foreach ((DateTimeOffset month, BillingMonth cached) in cache.Months)
            {
                m_Months.Add(month, cached);
            }
            m_HistoryStart = cache.HistoryStart;
            if (legacy)
            {
                SaveCache();
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            // A disposable cache must not prevent a fresh collection.
        }
    }

    private void SaveCache()
    {
        if (m_CachePath == null || m_AccountId == null || !m_HistoryStart.HasValue)
        {
            return;
        }
        string temporary = m_CachePath + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(m_CachePath))!);
            string accountHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(m_AccountId)));
            using (FileStream stream = File.Create(temporary))
            using (GZipStream compressed = new(stream, CompressionLevel.SmallestSize))
            {
                JsonSerializer.Serialize(compressed, new UsageCacheFile(1, accountHash, m_HistoryStart.Value, m_Months));
            }
            File.Move(temporary, m_CachePath, overwrite: true);
            if (m_LegacyCachePath != null)
            {
                File.Delete(m_LegacyCachePath);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Keep the live result and the previous complete cache when persistence fails.
        }
    }

    private void ClearCache()
    {
        m_Months.Clear();
        m_AccountId = null;
        m_HistoryStart = null;
    }

    private static bool IsCollectionError(Exception exception) => exception is HttpRequestException or OperationCanceledException or IOException or UnauthorizedAccessException
        or SqliteException or JsonException or InvalidOperationException or CryptographicException or FormatException or ArgumentException or OverflowException or KeyNotFoundException;

    private static long ReadCount(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement value))
        {
            return 0;
        }
        if ((value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number) && number >= 0) ||
            (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out number) && number >= 0))
        {
            return number;
        }
        throw new InvalidOperationException("Invalid Cursor usage count.");
    }

    private sealed record BillingMonth(string FirstPageHash, DateTimeOffset Through, IReadOnlyList<CursorUsageEvent> Events);
    private sealed record UsageCacheFile(int Version, string AccountHash, DateTimeOffset HistoryStart, Dictionary<DateTimeOffset, BillingMonth> Months);

    private string? ReadAccessToken()
    {
        string database = Path.Combine(m_CursorDirectory, "User", "globalStorage", "state.vscdb");
        if (!File.Exists(database))
        {
            return null;
        }
        using SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = database, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 1 }.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM ItemTable WHERE key = 'cursorAuth/accessToken'";
        string? stored = command.ExecuteScalar() as string;
        if (stored == null || !stored.StartsWith(k_EncryptedTokenPrefix, StringComparison.Ordinal))
        {
            return stored;
        }
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        using JsonDocument state = JsonDocument.Parse(File.ReadAllText(Path.Combine(m_CursorDirectory, "Local State")));
        byte[] protectedKey = Convert.FromBase64String(state.RootElement.GetProperty("os_crypt").GetProperty("encrypted_key").GetString()!);
        byte[] encrypted = Convert.FromBase64String(stored[k_EncryptedTokenPrefix.Length..]);
        if (!protectedKey.AsSpan().StartsWith("DPAPI"u8) || !encrypted.AsSpan().StartsWith("v10"u8) || encrypted.Length < 31)
        {
            throw new CryptographicException("Unsupported Cursor credential format.");
        }
        byte[] key = UnprotectKey(protectedKey[5..]);
        byte[] plaintext = new byte[encrypted.Length - 31];
        try
        {
            using AesGcm aes = new(key, 16);
            aes.Decrypt(encrypted.AsSpan(3, 12), encrypted.AsSpan(15, plaintext.Length), encrypted.AsSpan(encrypted.Length - 16), plaintext);
            return Encoding.UTF8.GetString(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static byte[] UnprotectKey(byte[] encrypted)
    {
        DataBlob input = new() { Length = encrypted.Length, Data = Marshal.AllocHGlobal(encrypted.Length) };
        DataBlob output = default;
        try
        {
            Marshal.Copy(encrypted, 0, input.Data, encrypted.Length);
            if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output))
            {
                throw new CryptographicException("Cursor credential decryption failed.");
            }
            byte[] key = new byte[output.Length];
            Marshal.Copy(output.Data, key, 0, key.Length);
            return key;
        }
        finally
        {
            Marshal.FreeHGlobal(input.Data);
            if (output.Data != IntPtr.Zero)
            {
                LocalFree(output.Data);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Length;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
