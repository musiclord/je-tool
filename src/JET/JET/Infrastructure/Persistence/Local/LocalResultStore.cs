using JET.Domain;

namespace JET.Infrastructure;

/// <summary>驗證結果與預篩選結果的本機存放；兩種結果各寫一張表，表名由 <see cref="ResultTables"/> 決定。</summary>
public sealed class LocalResultStore(ILocalProjectDatabase database) : IResultStore
{
    public async Task SaveAsync(string projectId, ResultRecord record, CancellationToken cancellationToken)
    {
        await database.EnsureReadyAsync(projectId, cancellationToken);

        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await SaveWithinAsync(connection, transaction, record, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await transaction.CommitAsync(CancellationToken.None);
    }

    internal static async Task SaveWithinAsync(System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction transaction, ResultRecord record, CancellationToken cancellationToken)
    {
        // 重算成功後才在同一交易清除日期讀不出來的舊結果，避免壞時間文字一直排在新結果前面。
        // 有效舊結果與報告來源紀錄都保留；交易失敗時清理和新結果一起回復。
        var unreadableIds = new List<string>();
        await using (var find = connection.CreateCommand())
        {
            find.Transaction = transaction;
            find.CommandText = $"SELECT result_id, generated_utc FROM {ResultTables.For(record.Kind)};";
            await using var reader = await find.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (reader.GetValue(1) is string generated && DateTimeOffset.TryParse(generated, out _)) continue;
                unreadableIds.Add(reader.GetString(0));
            }
        }
        foreach (var resultId in unreadableIds)
        {
            await using var remove = connection.CreateCommand();
            remove.Transaction = transaction;
            remove.CommandText = $"DELETE FROM {ResultTables.For(record.Kind)} WHERE result_id = @resultId;";
            remove.AddWithValue("@resultId", resultId);
            if (await remove.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw LocalEngineErrors.DatabaseCorrupt();
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"""
            INSERT INTO {ResultTables.For(record.Kind)} (result_id, generated_utc, summary_json)
            VALUES (@resultId, @generatedUtc, @summaryJson);
            """;
        command.AddWithValue("@resultId", record.ResultId);
        command.AddWithValue("@generatedUtc", record.GeneratedUtc.ToString("O"));
        command.AddWithValue("@summaryJson", record.SummaryJson);

        await command.ExecuteNonQueryAsync(cancellationToken);
        await ResultStaleStateSql.ClearResultWithinAsync(
            connection,
            transaction,
            record.Kind,
            cancellationToken,
            schemaPrefix: string.Empty);
    }

    public async Task<ResultRecord?> FindLatestAsync(
        string projectId,
        ResultKind kind,
        CancellationToken cancellationToken)
    {
        await database.EnsureReadyAsync(projectId, cancellationToken);

        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        // generated_utc 記到 100 奈秒，兩次執行幾乎不可能同時；真的同時就以 result_id 當平手鍵，
        // 每次都選到同一筆。
        command.CommandText =
            $"""
            SELECT result_id, generated_utc, summary_json
            FROM {ResultTables.For(kind)}
            ORDER BY generated_utc DESC, result_id DESC
            LIMIT 1;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        try
        {
            return new ResultRecord(
                reader.GetString(0),
                kind,
                DateTimeOffset.TryParse(reader.GetString(1), out var generatedUtc)
                    ? generatedUtc : throw new FormatException("Stored result time cannot be read."),
                reader.GetString(2));
        }
        catch (Exception error) when (error is FormatException or OverflowException or InvalidCastException
            or InvalidOperationException or ArgumentException)
        {
            var button = kind == ResultKind.Prescreen ? "重新執行預篩選" : "重新執行驗證";
            throw new JetActionException(JetErrorCodes.StaleResult,
                $"這份報告依據的結果已不是目前資料的結果。請回到「資料驗證與測試」按「{button}」，再回來產生報告。",
                innerException: error);
        }
    }
}
