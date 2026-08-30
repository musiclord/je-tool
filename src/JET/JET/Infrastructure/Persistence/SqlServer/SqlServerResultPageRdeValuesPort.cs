using System.Data;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// SQL Server result-page RDE reader。entry-id batch 上限由 Domain 固定為 500，遠低於 SQL Server
/// 參數上限；單一參數化 set query 回取全部 current carriers，Application 再依 action registry 投影。
/// </summary>
public sealed class SqlServerResultPageRdeValuesPort(SqlServerProjectDatabase database)
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

        await database.EnsureCreatedAsync(projectId, cancellationToken);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        var parameters = Enumerable.Range(0, entryIds.Count)
            .Select(static index => $"@entry{index}")
            .ToArray();
        await using var command = database.CreateCommand(
            connection,
            projectId,
            "SELECT entry_id, field_id, value_type, text_value, date_value, amount_scaled " +
            "FROM {s}.target_gl_rde_value " +
            $"WHERE entry_id IN ({string.Join(',', parameters)}) " +
            "ORDER BY entry_id, field_id;");
        for (var index = 0; index < entryIds.Count; index++)
        {
            command.Parameters.Add(parameters[index], SqlDbType.BigInt).Value = entryIds[index];
        }

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
