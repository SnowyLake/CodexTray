using System.Text;
using Microsoft.Data.Sqlite;

namespace CodexTray.Core;

internal static class AntigravityTranscriptModels
{
    internal readonly record struct StepModel(long UnixSeconds, long FreshInput, long CachedInput, long Output, string Model)
    {
        public bool Matches(DateTimeOffset timestamp, long freshInput, long cachedInput, long output)
        {
            return UnixSeconds == timestamp.ToUnixTimeSeconds() && FreshInput == freshInput && CachedInput == cachedInput && Output == output;
        }
    }

    // A null result leaves the transcript uncached so a temporarily unavailable database can be retried.
    public static Dictionary<long, StepModel>? Read(string database, CancellationToken cancellationToken)
    {
        Dictionary<long, StepModel> steps = [];
        if (!File.Exists(database))
        {
            return steps;
        }

        try
        {
            using SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = database, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 1 }.ToString());
            connection.Open();
            using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;

            // Generation row indexes are ordinals. The usage message carries the same response ID in both tables.
            Dictionary<string, string?> models = new(StringComparer.Ordinal);
            command.CommandText = "SELECT data FROM gen_metadata WHERE typeof(data) = 'blob' AND length(data) <= 52428800";
            using (SqliteDataReader reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ReadOnlySpan<byte> chat = ProtobufFields.ReadBytes(reader.GetFieldValue<byte[]>(0), 1);
                    ReadOnlySpan<byte> usage = ProtobufFields.ReadBytes(chat, 4);
                    ReadOnlySpan<byte> modelId = ProtobufFields.ReadBytes(chat, 19);
                    if (modelId.Length is 0 or > 128 || ProtobufFields.ReadBytes(usage, 11).IsEmpty)
                    {
                        continue;
                    }

                    string model = Encoding.UTF8.GetString(modelId);
                    if (!model.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or '/' or ':'))
                    {
                        continue;
                    }

                    string key = Convert.ToBase64String(usage);
                    if (!models.TryAdd(key, model) && !string.Equals(models[key], model, StringComparison.OrdinalIgnoreCase))
                    {
                        models[key] = null;
                    }
                }
            }

            command.CommandText = "SELECT idx, metadata FROM steps WHERE step_type = 15 AND typeof(metadata) = 'blob' AND length(metadata) <= 52428800";
            using (SqliteDataReader reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    byte[] blob = reader.GetFieldValue<byte[]>(1);
                    ReadOnlySpan<byte> usage = ProtobufFields.ReadBytes(blob, 9);
                    long timestamp = ProtobufFields.ReadInt64(ProtobufFields.ReadBytes(blob, 1), 1, long.MinValue);
                    if (timestamp != long.MinValue && models.TryGetValue(Convert.ToBase64String(usage), out string? model) && model != null)
                    {
                        steps[reader.GetInt64(0)] = new StepModel(timestamp, ProtobufFields.ReadInt64(usage, 2), ProtobufFields.ReadInt64(usage, 5), ProtobufFields.ReadInt64(usage, 3), model);
                    }
                }
            }
            transaction.Commit();
            return steps;
        }
        catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
