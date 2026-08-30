using System.Data.Common;

namespace JET.Infrastructure;

/// <summary>
/// 投影錯誤定位用的來源標籤（「檔名」或「檔名 [工作表]」）。
/// 單來源批次回傳 null：錯誤訊息維持與單檔時代一字不差，
/// 只有多來源批次才需要指出「哪個檔案的第幾列」。
/// </summary>
internal static class ProjectionSourceLabels
{
    public static async Task<IReadOnlyDictionary<int, string>?> LoadAsync(
        DbConnection connection,
        DbTransaction transaction,
        string batchId,
        CancellationToken cancellationToken,
        string schemaPrefix = "")
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"""
            SELECT source_no, source_file_name, sheet_name
            FROM {schemaPrefix}import_batch_source
            WHERE batch_id = @batchId
            ORDER BY source_no;
            """;
        var batchParam = command.CreateParameter();
        batchParam.ParameterName = "@batchId";
        batchParam.Value = batchId;
        command.Parameters.Add(batchParam);

        var labels = new Dictionary<int, string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var fileName = reader.GetString(1);
            var sheetName = reader.IsDBNull(2) ? null : reader.GetString(2);
            labels[reader.GetInt32(0)] = sheetName is null ? fileName : $"{fileName} [{sheetName}]";
        }

        return labels.Count > 1 ? labels : null;
    }
}
