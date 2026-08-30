using System.Data.Common;
using JET.AuditCore;
using JET.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace JET.Infrastructure;

/// <summary>
/// 四項資料驗證的 set-based SQL 執行（guide §1.5.2：規則一律在 DB 引擎計算，
/// 不得載入完整 row set 用 LINQ）。完整性測試以 LEFT JOIN + UNION ALL 模擬
/// FULL OUTER JOIN（guide §13：不依賴 SQLite 3.39+ 方言）。
/// 診斷日誌（dev-only）：每個 SELECT/INSERT 走 <see cref="DiagnosticDb"/>、transaction 走 scope。
/// </summary>
public sealed class LocalValidationRunRepository(ILocalProjectDatabase database, ILogger<LocalValidationRunRepository>? logger = null)
    : IValidationFactsPort
{
    // 診斷 provider 標籤由方言注入（sqlite／duckdb），不再寫死。
    private readonly string _provider = database.Dialect.ProviderName;

    private readonly ILogger _log = logger ?? NullLogger<LocalValidationRunRepository>.Instance;

    // 完整性 CTE 單一事實來源:見 ValidationProcedures.CompletenessDiffCte(completenessDiffPage repo 共用同一份;
    // GL 側只取投影已落地的有效分錄)。
    private static readonly string CompletenessDiffCte = ValidationProcedures.CompletenessDiffCte;

    Task<ValidationFacts> IValidationFactsPort.ExecuteAsync(
        ValidationPlan plan,
        CancellationToken cancellationToken) =>
        ExecuteAsync(plan, cancellationToken);

    internal async Task<ValidationFacts> ExecuteAsync(
        ValidationPlan plan,
        CancellationToken cancellationToken)
    {
        var input = plan.Request;
        var projectId = input.ProjectId;
        await database.EnsureCreatedAsync(projectId, cancellationToken);

        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        using var txLog = DiagnosticDb.BeginTransaction(_log, _provider);

        var populationSummary = await ReadPopulationSummaryAsync(
            connection,
            transaction,
            cancellationToken);
        var amountBinCounts = await ReadAmountDistributionAsync(
            connection,
            transaction,
            plan,
            cancellationToken);

        long completenessCount = 0;
        IReadOnlyList<CompletenessDiffAccount> completenessDiffs = [];
        if (plan.RunCompleteness)
        {
            (completenessCount, completenessDiffs) = await ReadCompletenessAsync(connection, transaction, input, cancellationToken);
        }

        var unbalancedCount = await ScalarAsync(
            connection, transaction, input, cancellationToken,
            // 借貸不平母體核心走 ValidationProcedures.UnbalancedCore(有效分錄限定)。
            $"SELECT COUNT(*) FROM (SELECT document_number {ValidationProcedures.UnbalancedCore()});");

        var unbalancedDetail = await ReadUnbalancedDetailAsync(connection, transaction, input, cancellationToken);

        var infSampleCount = await InsertInfSampleAsync(connection, transaction, input, cancellationToken);

        var (nullAccount, nullDocument, nullDescription, outOfRangeDate) =
            await ReadNullRecordsAsync(connection, transaction, input, cancellationToken);
        var sourceQualityFindingCount = await ReadSourceQualityFindingCountAsync(
            connection,
            transaction,
            cancellationToken);

        var nullDetail = await ReadNullDetailAsync(connection, transaction, input, cancellationToken);

        var controlTotals = await ReadControlTotalsAsync(connection, transaction, cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        await transaction.CommitAsync(CancellationToken.None);
        txLog.Committed();

        return new ValidationFacts(
            populationSummary,
            completenessCount,
            completenessDiffs,
            unbalancedCount,
            infSampleCount,
            nullAccount,
            nullDocument,
            nullDescription,
            outOfRangeDate,
            sourceQualityFindingCount,
            unbalancedDetail,
            nullDetail,
            controlTotals,
            amountBinCounts);
    }

    /// <summary>
    /// 讀取投影時落地的 gl_control_total raw facts（單列）。match flags 由 AuditCore
    /// Finalize 對照目前 GL population facts 裁定；Infrastructure 不作審計判斷。
    /// </summary>
    private async Task<ValidationControlTotalsFacts?> ReadControlTotalsAsync(
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT effective_row_count, effective_debit_scaled, effective_credit_scaled
            FROM gl_control_total WHERE singleton = 1;
            """;

        await using var reader = await command.ExecuteReaderLoggedAsync(_log, _provider, cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        if (reader.IsDBNull(0) || reader.IsDBNull(1) || reader.IsDBNull(2))
        {
            return null;
        }

        return new ValidationControlTotalsFacts(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2));
    }

    private async Task<GlPopulationSummary> ReadPopulationSummaryAsync(
        DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT COUNT(*),
                   COALESCE(SUM(debit_amount_scaled), 0),
                   COALESCE(SUM(credit_amount_scaled), 0),
                   COALESCE(SUM(CASE WHEN is_effective = 1 THEN 1 ELSE 0 END), 0),
                   COUNT(DISTINCT CASE WHEN is_effective = 1 THEN document_number END),
                   COALESCE(SUM(CASE WHEN is_effective = 1 THEN debit_amount_scaled ELSE 0 END), 0),
                   COALESCE(SUM(CASE WHEN is_effective = 1 THEN credit_amount_scaled ELSE 0 END), 0),
                   COALESCE(SUM(CASE WHEN is_effective = 1 THEN amount_scaled ELSE 0 END), 0),
                   COALESCE(SUM(CASE WHEN is_effective = 0 THEN 1 ELSE 0 END), 0),
                   COALESCE(SUM(CASE WHEN exclusion_reason = @periodReason THEN 1 ELSE 0 END), 0),
                   COALESCE(SUM(CASE WHEN exclusion_reason = @postingStatusReason THEN 1 ELSE 0 END), 0)
            FROM target_gl_entry;
            """;
        command.AddWithValue("@periodReason", GlEffectivePopulation.PeriodStorageReason);
        command.AddWithValue("@postingStatusReason", GlEffectivePopulation.PostingStatusStorageReason);

        await using var reader = await command.ExecuteReaderLoggedAsync(_log, _provider, cancellationToken);
        await reader.ReadAsync(cancellationToken);

        return new GlPopulationSummary(
            new GlRawPopulationTotals(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetInt64(2)),
            new ValidationEffectivePopulationTotals(
                reader.GetInt64(3),
                reader.GetInt64(4),
                reader.GetInt64(5),
                reader.GetInt64(6),
                reader.GetInt64(7)),
            new GlExcludedPopulationTotals(
                reader.GetInt64(8),
                reader.GetInt64(9),
                reader.GetInt64(10)));
    }

    /// <summary>
    /// 有效分錄內依 |amount_scaled| 定義的金額級距。CASE 與 scaled thresholds 由
    /// AuditCore plan 提供；Local provider 只執行同一個參數化 GROUP BY CASE。
    /// </summary>
    private async Task<IReadOnlyList<ValidationAmountBinCount>> ReadAmountDistributionAsync(
        DbConnection connection,
        DbTransaction transaction,
        ValidationPlan plan,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var bucketCase = ValidationAmountDistributionCatalog.CaseExpression("amount_scaled");
        command.CommandText =
            $"""
            SELECT {bucketCase} AS bin_key,
                   COUNT(*)
            FROM target_gl_entry
            WHERE {GlEffectivePopulation.SqlPredicate()}
            GROUP BY {bucketCase};
            """;
        BindAmountThresholds(command, plan.AmountDistributionPlan);

        var rows = new List<ValidationAmountBinCount>();
        await using var reader = await command.ExecuteReaderLoggedAsync(
            _log,
            _provider,
            cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new ValidationAmountBinCount(
                reader.GetString(0),
                Convert.ToInt64(reader.GetValue(1))));
        }

        return rows;
    }

    private async Task<(long Count, IReadOnlyList<CompletenessDiffAccount> Diffs)> ReadCompletenessAsync(
        DbConnection connection, DbTransaction transaction, ValidationRequest input, CancellationToken cancellationToken)
    {
        var count = await ScalarAsync(
            connection, transaction, input, cancellationToken,
            CompletenessDiffCte + "\nSELECT COUNT(*) FROM diff WHERE tb_s <> gl_s;");

        var diffs = new List<CompletenessDiffAccount>();

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            CompletenessDiffCte +
            """

            SELECT account_code, account_name, tb_s, gl_s, tb_s - gl_s, not_in_tb
            FROM diff
            WHERE tb_s <> gl_s
            ORDER BY ABS(tb_s - gl_s) DESC, account_code
            LIMIT 50;
            """;
        await using var reader = await command.ExecuteReaderLoggedAsync(_log, _provider, cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            diffs.Add(new CompletenessDiffAccount(
                reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetInt64(2),
                reader.GetInt64(3),
                reader.GetInt64(4),
                reader.GetInt64(5) != 0));
        }

        return (count, diffs);
    }

    private async Task<int> InsertInfSampleAsync(
        DbConnection connection, DbTransaction transaction,
        ValidationRequest input, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // INF 抽樣 INSERT 走 ValidationProcedures.InfSampleInsert(有效母體、方言取 N 列)。
        command.CommandText = ValidationProcedures.InfSampleInsert(
            schemaPrefix: string.Empty,
            database.Dialect,
            input.SampleSeedVersion);
        command.AddWithValue("@runId", input.RunId);
        command.AddWithValue("@seed", input.SampleSeed);
        command.AddWithValue("@n", input.SampleSize);
        return await command.ExecuteNonQueryLoggedAsync(_log, _provider, cancellationToken);
    }

    private async Task<(long, long, long, long)> ReadNullRecordsAsync(
        DbConnection connection, DbTransaction transaction,
        ValidationRequest input, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // §4 收斂:四類述詞走 NullRecordsCategoryPredicate 中心(計數/明細/分頁共用同一份;期外日期判準
        // = approval_date，2026-06-23 決策)，且全部限有效母體。
        // 日期為投影時正規化的 yyyy-MM-dd ISO 字串，文字比較即時間序比較。
        var columns = string.Join(",\n                ",
            NullRecordsCategoryPredicate.All.Select(c =>
                $"COALESCE(SUM(CASE WHEN {NullRecordsCategoryPredicate.ScopedSqlite(c)} THEN 1 ELSE 0 END), 0)"));
        command.CommandText =
            $"""
            SELECT
                {columns}
            FROM target_gl_entry;
            """;
        BindPeriod(command, input);

        await using var reader = await command.ExecuteReaderLoggedAsync(_log, _provider, cancellationToken);
        await reader.ReadAsync(cancellationToken);

        return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3));
    }

    private async Task<long> ReadSourceQualityFindingCountAsync(
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"SELECT COUNT(*) FROM target_gl_entry WHERE {ValidationProcedures.NullPostDateSourceQualityPredicate};";
        var result = await command.ExecuteScalarLoggedAsync(_log, _provider, cancellationToken);
        return result is null or DBNull ? 0L : Convert.ToInt64(result);
    }

    private async Task<IReadOnlyList<UnbalancedDocument>> ReadUnbalancedDetailAsync(
        DbConnection connection, DbTransaction transaction, ValidationRequest input, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // 母體核心走 ValidationProcedures.UnbalancedCore(有效分錄限定)。
        command.CommandText =
            "SELECT document_number, " +
            "COALESCE(SUM(debit_amount_scaled), 0), " +
            "COALESCE(SUM(credit_amount_scaled), 0), " +
            "COALESCE(SUM(amount_scaled), 0) " +
            ValidationProcedures.UnbalancedCore() +
            " ORDER BY ABS(SUM(amount_scaled)) DESC, document_number LIMIT 50;";
        var rows = new List<UnbalancedDocument>();
        await using var reader = await command.ExecuteReaderLoggedAsync(_log, _provider, cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new UnbalancedDocument(
                reader.IsDBNull(0) ? null : reader.GetString(0),
                reader.GetInt64(1),
                reader.GetInt64(2),
                reader.GetInt64(3)));
        }

        return rows;
    }

    private async Task<IReadOnlyList<NullRecordRow>> ReadNullDetailAsync(
        DbConnection connection, DbTransaction transaction,
        ValidationRequest input, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // 四類旗標與 WHERE 皆走 NullRecordsCategoryPredicate 中心，且全部限有效母體。
        var flags = string.Join(",\n                   ",
            NullRecordsCategoryPredicate.All.Select(c =>
                $"CASE WHEN {NullRecordsCategoryPredicate.ScopedSqlite(c)} THEN 1 ELSE 0 END"));
        var anyMatch = string.Join("\n               OR ",
            NullRecordsCategoryPredicate.All.Select(NullRecordsCategoryPredicate.ScopedSqlite));
        command.CommandText =
            $"""
            SELECT document_number, account_code, post_date, document_description,
                   {flags}
            FROM target_gl_entry
            WHERE ({anyMatch})
            ORDER BY source_row_number, entry_id
            LIMIT 50;
            """;
        BindPeriod(command, input);

        var rows = new List<NullRecordRow>();
        await using var reader = await command.ExecuteReaderLoggedAsync(_log, _provider, cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new NullRecordRow(
                reader.IsDBNull(0) ? null : reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetInt64(4) != 0,
                reader.GetInt64(5) != 0,
                reader.GetInt64(6) != 0,
                reader.GetInt64(7) != 0));
        }

        return rows;
    }

    private async Task<long> ScalarAsync(
        DbConnection connection, DbTransaction transaction,
        ValidationRequest input, CancellationToken cancellationToken, string sql)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        var result = await command.ExecuteScalarLoggedAsync(_log, _provider, cancellationToken);
        return result is null or DBNull ? 0L : Convert.ToInt64(result);
    }

    /// <summary>只供 approval_date out-of-range 規則窗口使用；不得作一般母體述詞。</summary>
    private static void BindPeriod(DbCommand command, ValidationRequest input)
    {
        command.AddWithValue("@periodStart", input.PeriodStart);
        command.AddWithValue("@periodEnd", input.PeriodEnd);
    }

    private static void BindAmountThresholds(
        DbCommand command,
        ValidationAmountDistributionPlan plan)
    {
        foreach (var threshold in plan.Thresholds)
        {
            command.AddWithValue(threshold.ParameterName, threshold.ValueScaled);
        }
    }
}
