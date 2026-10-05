using System.Diagnostics;
using System.Text.Json;
using JET.AuditCore;
using JET.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace JET.Infrastructure;

/// <summary>
/// TB staging → target 投影。診斷日誌（dev-only）：一次性 clear/select 走 <see cref="DiagnosticDb"/>、
/// transaction 走 scope；逐列 INSERT 不逐筆記事件，改以投影結束後一筆 projection.milestone 收斂。
/// </summary>
public sealed class LocalTbRepository(ILocalProjectDatabase database, ILogger<LocalTbRepository>? logger = null)
    : ITbRepository
{
    private const int ProgressRowInterval = 20_000;

    // 診斷 provider 標籤由方言注入（sqlite／duckdb），不再寫死。
    private readonly string _provider = database.Dialect.ProviderName;

    private static readonly JsonSerializerOptions JsonOptions = JetJsonStorage.Options;

    /// <summary>TB target 批量寫入的欄位順序（各引擎的 <see cref="IBulkRowWriter"/> 依此對位；balance_id 由引擎補齊）。</summary>
    private static readonly string[] TargetColumns =
        ["batch_id", "source_row_number", "account_code", "account_name", "change_amount_scaled"];

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
            clear.CommandText = "DELETE FROM target_tb_balance;";
            await clear.ExecuteNonQueryLoggedAsync(_log, _provider, cancellationToken);
        }

        // 重投影改寫 target,既有規則結果失效(投影失敗 rollback 時清除一併回退)。
        await RuleRunResultReset.ClearWithinAsync(
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
            FROM staging_tb_raw_row
            WHERE batch_id = @batchId
            ORDER BY row_number;
            """;
        select.AddWithValue("@batchId", batchId);

        // 批量列寫入：SQLite 包裝參數化 INSERT、DuckDB 走 Appender。
        // SQL 文本／欄序不變（見 TargetColumns）；balance_id auto-id 由引擎補齊。
        await using var insert = database.CreateBulkRowWriter(connection, transaction, "target_tb_balance", TargetColumns);

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

                // 值依 TargetColumns 順序對位（batch_id 每列供值；null 由寫入器轉 NULL）。
                await insert.AppendAsync(
                    [batchId, rowNumber, projected!.AccountCode, projected.AccountName, projected.ChangeAmountScaled],
                    cancellationToken);
                insertedCount++;
            }
        }

        // reader 已關閉後才 flush（DuckDB Appender 的 Close 須在 staging reader 迴圈結束後；本機探針實證）。
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

        var sourceDefinitions = await LocalFieldDefinitionPersistence.ReadStatesAsync(
            connection,
            transaction,
            batchId,
            LegacyFieldDefinitionScope.Source,
            cancellationToken);
        await LocalFieldDefinitionPersistence.ReplaceScopeAsync(
            connection,
            transaction,
            batchId,
            LegacyFieldDefinitionScope.Target,
            LegacyFieldDefinitionProjector.ProjectTb(sourceDefinitions, spec, moneyScale),
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
