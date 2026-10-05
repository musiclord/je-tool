using JET.Domain;

namespace JET.Infrastructure;

/// <summary>本地 provider：以一頁 entry ids 回取相對應的原始 GL JSON。</summary>
public sealed class LocalRawGlExportRepository(ILocalProjectDatabase database) : IRawGlExportRepository
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

        await database.EnsureReadyAsync(projectId, cancellationToken);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();

        var parameters = new string[entryIds.Count];
        for (var index = 0; index < entryIds.Count; index++)
        {
            parameters[index] = $"@id{index}";
            command.AddWithValue(parameters[index], entryIds[index]);
        }

        command.CommandText =
            "SELECT g.entry_id, r.row_json " +
            "FROM target_gl_entry g " +
            "JOIN staging_gl_raw_row r ON r.batch_id = g.batch_id AND r.row_number = g.source_row_number " +
            $"WHERE g.entry_id IN ({string.Join(",", parameters)});";

        var rows = new Dictionary<long, string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows[reader.GetInt64(0)] = reader.GetString(1);
        }

        return rows;
    }
}
