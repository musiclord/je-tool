using JET.Domain;

namespace JET.Infrastructure;

/// <summary>SQL Server provider：以一頁 entry ids 回取相對應的原始 GL JSON。</summary>
public sealed class SqlServerRawGlExportRepository(SqlServerProjectDatabase database) : IRawGlExportRepository
{
    public async Task<IReadOnlyDictionary<long, string>> FetchJsonByEntryIdsAsync(
        string projectId,
        IReadOnlyList<long> entryIds,
        CancellationToken cancellationToken)
    {
        if (entryIds.Count == 0)
        {
            return new Dictionary<long, string>();
        }

        if (entryIds.Count > PageRequest.MaxPageSize)
        {
            throw new ArgumentOutOfRangeException(nameof(entryIds), "原始列回取一次不得超過單頁上限。");
        }

        await database.EnsureCreatedAsync(projectId, cancellationToken);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        var parameters = new string[entryIds.Count];
        await using var command = database.CreateCommand(
            connection,
            projectId,
            "SELECT g.entry_id, r.row_json " +
            "FROM {s}.target_gl_entry g " +
            "JOIN {s}.staging_gl_raw_row r ON r.batch_id = g.batch_id AND r.row_number = g.source_row_number " +
            "WHERE g.entry_id IN (__ENTRY_IDS__);");

        for (var index = 0; index < entryIds.Count; index++)
        {
            parameters[index] = $"@id{index}";
            command.Parameters.AddWithValue(parameters[index], entryIds[index]);
        }

        command.CommandText = command.CommandText.Replace(
            "__ENTRY_IDS__", string.Join(",", parameters), StringComparison.Ordinal);

        var rows = new Dictionary<long, string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows[reader.GetInt64(0)] = reader.GetString(1);
        }

        return rows;
    }
}
