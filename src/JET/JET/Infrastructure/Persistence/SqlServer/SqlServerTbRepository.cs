using System.Data;
using System.Diagnostics;
using System.Text.Json;
using JET.AuditCore;
using JET.Domain;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace JET.Infrastructure;

/// <summary>
/// TB staging → target 投影的 SQL Server 實作(對應 <see cref="LocalTbRepository"/>)。
/// 重用 Domain 純函式 <see cref="TbRowProjector"/>。TB 量小(每科目一列),採 row-by-row
/// prepared insert 即可;任一列失敗整批 rollback,語意與 SQLite 一致。
/// 診斷日誌（dev-only）：一次性 clear/select 走 <see cref="DiagnosticDb"/>、transaction 走 scope；
/// 逐列 INSERT 不逐筆記事件，改以投影結束後一筆 projection.milestone 收斂（與 SQLite 事件等價）。
/// </summary>
public sealed class SqlServerTbRepository(SqlServerProjectDatabase database, ILogger<SqlServerTbRepository>? logger = null)
    : ITbRepository
{
    private const int ProgressRowInterval = 20_000;
    private const string Provider = "sqlServer";

    private static readonly JsonSerializerOptions JsonOptions = JetJsonStorage.Options;

    private readonly ILogger _log = logger ?? NullLogger<SqlServerTbRepository>.Instance;

    public async Task<ProjectionResult> ProjectStagingToTargetAsync(
        string projectId,
        string batchId,
        TbMappingSpec spec,
        int moneyScale,
        DateTimeOffset committedUtc,
        CancellationToken cancellationToken,
        Action<ProjectionProgress>? progress = null)
    {
        await database.EnsureCreatedAsync(projectId, cancellationToken);
        var stopwatch = Stopwatch.StartNew();

        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        using var txLog = DiagnosticDb.BeginTransaction(_log, Provider);

        await using (var clear = database.CreateCommand(connection, projectId,
            "DELETE FROM {s}.target_tb_balance;"))
        {
            clear.Transaction = transaction;
            await clear.ExecuteNonQueryLoggedAsync(_log, Provider, cancellationToken);
        }

        // 重投影改寫 target,既有規則結果失效(投影失敗 rollback 時清除一併回退)。
        await RuleRunResultReset.ClearWithinAsync(
            connection,
            transaction,
            cancellationToken,
            AuditMutation.TbProjection,
            SqlServerProjectSchema.QualifierFor(projectId));

        var sourceLabels = await ProjectionSourceLabels.LoadAsync(connection, transaction, batchId, cancellationToken, SqlServerProjectSchema.QualifierFor(projectId));

        await using var select = database.CreateCommand(connection, projectId,
            """
            SELECT row_number, source_no, source_row_number, row_json
            FROM {s}.staging_tb_raw_row
            WHERE batch_id = @batchId
            ORDER BY row_number;
            """);
        select.Transaction = transaction;
        select.Parameters.AddWithValue("@batchId", batchId);

        await using var insert = database.CreateCommand(connection, projectId,
            """
            INSERT INTO {s}.target_tb_balance (
                batch_id, source_row_number, account_code, account_name, change_amount_scaled)
            VALUES (@batchId, @sourceRowNumber, @accountCode, @accountName, @changeScaled);
            """);
        insert.Transaction = transaction;

        var pBatch = insert.Parameters.Add("@batchId", SqlDbType.NVarChar, 64);
        var pRowNumber = insert.Parameters.Add("@sourceRowNumber", SqlDbType.BigInt);
        var pAccCode = insert.Parameters.Add("@accountCode", SqlDbType.NVarChar, 450);
        var pAccName = insert.Parameters.Add("@accountName", SqlDbType.NVarChar, 400);
        var pChange = insert.Parameters.Add("@changeScaled", SqlDbType.BigInt);
        pBatch.Value = batchId;

        var errors = new ProjectionErrorCollector();
        var insertedCount = 0;
        long sourceRowCount = 0;

        // SQL Server 不允許同連線在 reader 開啟時又下命令(無 MARS);TB 量小,先把 staging
        // 全數讀進記憶體、關閉 reader,再逐列投影 + 插入(語意與 SQLite 串流版相同)。
        var staged = new List<(long RowNumber, int SourceNo, StagingRow Row)>();
        await using (var reader = await select.ExecuteReaderLoggedAsync(_log, Provider, cancellationToken))
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
                staged.Add((rowNumber, sourceNo, new StagingRow(sourceRowNumber, values)));
            }
        }

        if (sourceRowCount > 0 && sourceRowCount % ProgressRowInterval != 0)
        {
            progress?.Invoke(new ProjectionProgress(sourceRowCount));
        }

        foreach (var (rowNumber, sourceNo, stagingRow) in staged)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!TbRowProjector.TryProject(stagingRow, spec, moneyScale, out var projected, out var error))
            {
                errors.Observe(error! with { SourceLabel = sourceLabels?.GetValueOrDefault(sourceNo) });

                continue;
            }

            if (errors.TotalErrorCount > 0)
            {
                continue;
            }

            pRowNumber.Value = rowNumber;
            pAccCode.Value = (object?)projected!.AccountCode ?? DBNull.Value;
            pAccName.Value = (object?)projected.AccountName ?? DBNull.Value;
            pChange.Value = projected.ChangeAmountScaled;

            await insert.ExecuteNonQueryAsync(cancellationToken);
            insertedCount++;
        }

        if (errors.TotalErrorCount > 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            txLog.RolledBack();
            return errors.FailedResult();
        }

        var sourceDefinitions = await SqlServerFieldDefinitionPersistence.LoadStatesAsync(
            database,
            connection,
            transaction,
            projectId,
            batchId,
            LegacyFieldDefinitionScope.Source,
            cancellationToken);
        await SqlServerFieldDefinitionPersistence.ReplaceAsync(
            database,
            connection,
            transaction,
            projectId,
            batchId,
            LegacyFieldDefinitionScope.Target,
            LegacyFieldDefinitionProjector.ProjectTb(sourceDefinitions, spec, moneyScale),
            cancellationToken);

        var warnings = await TbMappedColumnAudit.ReadAsync(connection, transaction, spec, insertedCount,
            SqlServerDialect.Instance, cancellationToken, SqlServerProjectSchema.QualifierFor(projectId));
        await SqlServerMappingStateStore.SaveWithinAsync(database, connection, transaction, projectId,
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
