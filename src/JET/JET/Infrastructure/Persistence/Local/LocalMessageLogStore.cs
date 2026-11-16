using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// message_log 的 SQLite 實作。每個案件只保留最近 RetainedCount 則訊息：每次寫入後，
/// 刪掉 message_id 比目前最大值小 RetainedCount 以上的舊訊息。message_id 是 AUTOINCREMENT，
/// 只增不減、不重複使用，所以用編號相減來刪一定正確。
/// </summary>
public sealed class LocalMessageLogStore(ILocalProjectDatabase database) : IMessageLogStore
{
    public const int RetainedCount = 500;

    public async Task AppendAsync(string projectId, string level, string text, CancellationToken cancellationToken)
    {
        await database.EnsureReadyAsync(projectId, cancellationToken);

        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO message_log (occurred_utc, level, text) VALUES (@utc, @level, @text);
            DELETE FROM message_log
            WHERE message_id <= (SELECT MAX(message_id) FROM message_log) - @retained;
            """;
        command.AddWithValue("@utc", DateTimeOffset.UtcNow.ToString("O"));
        command.AddWithValue("@level", level);
        command.AddWithValue("@text", text);
        command.AddWithValue("@retained", RetainedCount);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<MessageLogEntry>> GetRecentAsync(
        string projectId, int limit, CancellationToken cancellationToken)
    {
        await database.EnsureReadyAsync(projectId, cancellationToken);

        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT occurred_utc, level, text
            FROM message_log
            ORDER BY message_id DESC
            LIMIT @limit;
            """;
        command.AddWithValue("@limit", limit);

        var entries = new List<MessageLogEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            entries.Add(new MessageLogEntry(
                DateTimeOffset.Parse(reader.GetString(0)),
                reader.GetString(1),
                reader.GetString(2)));
        }

        return entries;
    }
}
