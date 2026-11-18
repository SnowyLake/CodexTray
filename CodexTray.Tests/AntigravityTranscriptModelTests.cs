using System.Text;
using System.Text.Json;
using CodexTray.Core;
using Microsoft.Data.Sqlite;

namespace CodexTray.Tests;

internal static class AntigravityTranscriptModelTests
{
    public static Task RunAsync()
    {
        using TempDirectory temp = new();
        const string conversationId = "fe494328-5bf4-4fcc-b284-fa067f84dc03";
        string agy = Path.Combine(temp.Path, "antigravity-cli");
        string logs = Path.Combine(agy, "brain", conversationId, ".system_generated", "logs");
        Directory.CreateDirectory(logs);
        DateTimeOffset asOf = DateTimeOffset.Now;
        DateTimeOffset timestamp = DateTimeOffset.FromUnixTimeSeconds(asOf.ToUnixTimeSeconds());
        File.WriteAllLines(Path.Combine(logs, "transcript.jsonl"), Enumerable.Range(1, 7).Select(index => JsonSerializer.Serialize(new
        {
            type = "PLANNER_RESPONSE",
            step_index = index * 2,
            created_at = timestamp.AddSeconds(-index),
            input_tokens = index == 4 ? 101 : 100,
            cache_read_tokens = 30,
            output_tokens = 20,
            model = index == 7 ? "gemini-4-carbon" : null,
        })));
        TokenCostCollector collector = new(Path.Combine(AppContext.BaseDirectory, "Resources", "model-pricing.json"));
        TokenCostStatistics before = collector.CollectGemini(temp.Path, asOf);
        Check(before.SessionLogs.Count(row => row.Model == "unknown") == 6, "missing database keeps unknown models");

        string conversations = Path.Combine(agy, "conversations");
        Directory.CreateDirectory(conversations);
        using SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = Path.Combine(conversations, conversationId + ".db"), Pooling = false }.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            CREATE TABLE gen_metadata (idx INTEGER PRIMARY KEY, data BLOB);
            CREATE TABLE steps (idx INTEGER PRIMARY KEY, step_type INTEGER, metadata BLOB);
            """;
        command.ExecuteNonQuery();

        SqliteParameter blob = command.Parameters.Add("$blob", SqliteType.Blob);
        for (int index = 1; index <= 7; index++)
        {
            byte[] usage = [.. Varint(2, 100), .. Varint(5, 30), .. Varint(3, 20), .. Bytes(11, Encoding.ASCII.GetBytes($"response-{index}"))];
            byte[] stepUsage = index == 6
                ? [.. Varint(2, 100), .. Varint(5, 30), .. Varint(3, 20), .. Bytes(11, "different-id"u8.ToArray())]
                : usage;
            blob.Value = (byte[])[.. Bytes(1, Varint(1, timestamp.AddSeconds(index == 5 ? -15 : -index).ToUnixTimeSeconds())), .. Bytes(9, stepUsage)];
            command.CommandText = $"INSERT INTO steps VALUES ({index * 2}, 15, $blob)";
            command.ExecuteNonQuery();

            blob.Value = Generation(usage, index == 2 ? "gemini-3.8-flash-n" : "gemini-4-hydrogen");
            command.CommandText = $"INSERT INTO gen_metadata VALUES ({index + 20}, $blob)";
            command.ExecuteNonQuery();
            if (index == 3)
            {
                blob.Value = Generation(usage, "gemini-4-carbon");
                command.CommandText = "INSERT INTO gen_metadata VALUES (99, $blob)";
                command.ExecuteNonQuery();
            }
        }

        TokenCostStatistics after = collector.CollectGemini(temp.Path, asOf);
        Check(after.SessionLogs.Count == 7 && after.Lifetime.TotalTokens == before.Lifetime.TotalTokens, "database contributes model IDs without adding usage");
        Check(after.SessionLogs.Count(row => row.Model == "unknown") == 4 && after.Lifetime.HasUnpricedUsage, "conflicts, stale steps and different response IDs stay unknown");
        Check(after.SessionLogs.Single(row => row.Model == "gemini-4-hydrogen").CostUsd == 0.00030225m, "recovered Hydrogen ID is priced");
        Check(after.SessionLogs.Single(row => row.Model == "gemini-3.8-flash").CostUsd == 0.0003045m, "recovered Flash backend ID uses the public model and price");
        Check(after.SessionLogs.Count(row => row.Model == "gemini-4-carbon") == 1, "transcript model takes precedence");

        command.CommandText = "DELETE FROM gen_metadata WHERE idx = 22";
        command.ExecuteNonQuery();
        TokenCostStatistics afterWalChange = collector.CollectGemini(temp.Path, asOf);
        Check(afterWalChange.SessionLogs.Count(row => row.Model == "unknown") == 5, "a database WAL update invalidates cached transcript models");
        return Task.CompletedTask;
    }

    private static byte[] Generation(byte[] usage, string model)
    {
        return Bytes(1, [.. Bytes(4, usage), .. Bytes(19, Encoding.ASCII.GetBytes(model))]);
    }

    private static byte[] Bytes(int field, byte[] value)
    {
        return [.. Varint(0, field * 8 + 2), .. Varint(0, value.Length), .. value];
    }

    private static byte[] Varint(int field, long value)
    {
        using MemoryStream buffer = new();
        using BinaryWriter writer = new(buffer);
        if (field != 0)
        {
            writer.Write7BitEncodedInt(field * 8);
        }
        writer.Write7BitEncodedInt64(value);
        return buffer.ToArray();
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
