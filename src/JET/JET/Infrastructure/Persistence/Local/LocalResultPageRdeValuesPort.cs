using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 讀出結果頁上各分錄的攸關資料元素欄位值。一次只接受一頁的 entry_id，用參數化的 IN 一次查回這些分錄在
/// gl_rde_value 裡有的值。不管 action 要顯示哪幾個欄位都查同一句，也不一筆分錄查一次。
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
            "FROM gl_rde_value " +
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
