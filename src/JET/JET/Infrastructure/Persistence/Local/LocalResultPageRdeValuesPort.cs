using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// SQLite／DuckDB 共用的 result-page RDE reader。只接受單一 page 的 entry ids，以參數化
/// IN set 一次回取 present cells；不依 action field subset 分支，也不逐 entry 查詢。
/// </summary>
public sealed class LocalResultPageRdeValuesPort(ILocalProjectDatabase database)
    : IResultPageRdeValuesPort
{
    public async Task<IReadOnlyList<ResultPageRdeValue>> ReadAsync(
        string projectId,
        IReadOnlyList<long> entryIds,
        CancellationToken cancellationToken)
    {
        ResultPageRdeValueBatch.Validate(entryIds);
        if (entryIds.Count == 0)
        {
            return [];
        }

        await database.EnsureReadyAsync(projectId, cancellationToken);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();

        var parameters = new string[entryIds.Count];
        for (var index = 0; index < entryIds.Count; index++)
        {
            var name = $"@entry{index}";
            parameters[index] = name;
            command.AddWithValue(name, entryIds[index]);
        }

        command.CommandText =
            "SELECT entry_id, field_id, value_type, text_value, date_value, amount_scaled " +
            "FROM target_gl_rde_value " +
            $"WHERE entry_id IN ({string.Join(',', parameters)}) " +
            "ORDER BY entry_id, field_id;";

        var values = new List<ResultPageRdeValue>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(new ResultPageRdeValue(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetInt64(5)));
        }

        return values;
    }
}
