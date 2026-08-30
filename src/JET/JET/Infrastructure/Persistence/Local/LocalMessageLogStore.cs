using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// app_message_log 的本地引擎實作。每專案保留最近 RetainedCount 則，
/// 每次寫入後以 AUTOINCREMENT 單調 id 修剪（id 不回收，位移修剪恆正確）。
/// </summary>
public sealed class LocalMessageLogStore(ILocalProjectDatabase database) : IMessageLogStore
{
    public const int RetainedCount = 500;

    public async Task AppendAsync(string projectId, string level, string text, CancellationToken cancellationToken)
    {
        await database.EnsureCreatedAsync(projectId, cancellationToken);

        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO app_message_log (occurred_utc, level, text) VALUES (@utc, @level, @text);
            DELETE FROM app_message_log
            WHERE message_id <= (SELECT MAX(message_id) FROM app_message_log) - @retained;
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
        await database.EnsureCreatedAsync(projectId, cancellationToken);

        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT occurred_utc, level, text
            FROM app_message_log
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
