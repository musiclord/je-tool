using System.Diagnostics;
using System.Text.Json;
using JET.AuditCore;
using JET.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace JET.Infrastructure;

/// <summary>
/// 確認 TB 配對時，把匯入暫存表 <c>pbc_tb</c> 的每一列轉寫進 <c>tb</c>，以下稱投影。
/// 診斷日誌（開發用）：只執行一次的清除與查詢經 <see cref="DiagnosticDb"/> 記錄，交易的開始與結束也記一筆；
/// 逐列的 INSERT 不一筆一筆記，改在投影結束後記一筆 projection.milestone。
/// </summary>
public sealed class LocalTbRepository(ILocalProjectDatabase database, ILogger<LocalTbRepository>? logger = null)
    : ITbRepository
{
    private const int ProgressRowInterval = 20_000;

    private readonly string _provider = database.Dialect.ProviderName;

    private static readonly JsonSerializerOptions JsonOptions = JetJsonStorage.Options;

    /// <summary>批量寫入 tb 時的欄位順序，交給 <see cref="IBulkRowWriter"/> 的值照這個順序排；balance_id 由資料庫自動編號。</summary>
    private static readonly string[] TargetColumns =
        ["batch_id", "source_row_number", "account_number", "account_name", "change_amount_scaled"];

    private readonly ILogger _log = logger ?? NullLogger<LocalTbRepository>.Instance;

    public async Task<ProjectionResult> ProjectStagingToTargetAsync(
        string projectId,
        string batchId,
        TbMappingSpec spec,
        int moneyScale,
        DateTimeOffset committedUtc,
        CancellationToken cancellationToken,
        Action<ProjectionProgress>? progress = null)
    {
        await database.EnsureReadyAsync(projectId, cancellationToken);
        var stopwatch = Stopwatch.StartNew();

        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        using var txLog = DiagnosticDb.BeginTransaction(_log, _provider);

        await using (var clear = connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM tb;";
            await clear.ExecuteNonQueryLoggedAsync(_log, _provider, cancellationToken);
        }

        // 重新投影會改寫 tb，已有的規則結果跟著失效，在同一個交易裡清除；投影失敗 rollback 時，這次清除也一起回復。
        await ResultReset.ClearWithinAsync(
            connection,
            transaction,
            cancellationToken,
            AuditMutation.TbProjection);

        var sourceLabels = await ProjectionSourceLabels.LoadAsync(connection, transaction, batchId, cancellationToken);

        await using var select = connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText =
            """
            SELECT row_number, source_no, source_row_number, row_json
            FROM pbc_tb
            WHERE batch_id = @batchId
            ORDER BY row_number;
            """;
        select.AddWithValue("@batchId", batchId);

        await using var insert = database.CreateBulkRowWriter(connection, transaction, "tb", TargetColumns);

        var errors = new ProjectionErrorCollector();
        var insertedCount = 0;
        long sourceRowCount = 0;

        await using (var reader = await select.ExecuteReaderLoggedAsync(_log, _provider, cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                sourceRowCount++;
                if (sourceRowCount % ProgressRowInterval == 0)
                {
                    progress?.Invoke(new ProjectionProgress(sourceRowCount));
                }

                var rowNumber = reader.GetInt64(0);
                var sourceNo = reader.GetInt32(1);
                var sourceRowNumber = reader.GetInt32(2);
                var values = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(3), JsonOptions)
                    ?? [];

                var stagingRow = new StagingRow(sourceRowNumber, values);

                if (!TbRowProjector.TryProject(stagingRow, spec, moneyScale, out var projected, out var error))
                {
                    errors.Observe(error! with { SourceLabel = sourceLabels?.GetValueOrDefault(sourceNo) });

                    continue;
                }

                if (errors.TotalErrorCount > 0)
                {
                    continue;
                }

                // 值照 TargetColumns 的順序放，每一列都要給 batch_id；null 由寫入器轉成 NULL。
                await insert.AppendAsync(
                    [batchId, rowNumber, projected!.AccountNumber, projected.AccountName, projected.ChangeAmountScaled],
                    cancellationToken);
                insertedCount++;
            }
        }

        await insert.CompleteAsync(cancellationToken);

        if (sourceRowCount > 0 && sourceRowCount % ProgressRowInterval != 0)
        {
            progress?.Invoke(new ProjectionProgress(sourceRowCount));
        }

        if (errors.TotalErrorCount > 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            txLog.RolledBack();
            return errors.FailedResult();
        }

        var sourceDefinitions = await LocalFieldInfoPersistence.ReadStatesAsync(
            connection,
            transaction,
            batchId,
            FieldInfoScope.Source,
            cancellationToken);
        await LocalFieldInfoPersistence.ReplaceScopeAsync(
            connection,
            transaction,
            batchId,
            FieldInfoScope.Target,
            FieldInfoProjector.ProjectTb(sourceDefinitions, spec, moneyScale),
            cancellationToken);

        var warnings = await TbMappedColumnAudit.ReadAsync(
            connection, transaction, spec, insertedCount, database.Dialect, cancellationToken);
        await LocalMappingStateStore.SaveWithinAsync(connection, transaction,
            new CommittedMapping(DatasetKind.Tb, spec.Mapping, TbChangeModeNames.ToWireName(spec.ChangeMode), batchId, committedUtc),
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await transaction.CommitAsync(CancellationToken.None);
        txLog.Committed();
        DiagnosticDbLog.ProjectionMilestone(_log, "tb-projection", insertedCount, stopwatch.ElapsedMilliseconds,
            insertedCount * 1000.0 / Math.Max(1, stopwatch.ElapsedMilliseconds));
        return new ProjectionResult(insertedCount, []) { Warnings = warnings };
    }
}
