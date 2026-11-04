using JET.AuditCore;
using JET.Domain;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace JET.Infrastructure;

/// <summary>
/// 四項資料驗證的 SQL Server 實作(對應 <see cref="LocalValidationRunRepository"/>)。
/// 規則 SQL 與 SQLite 同為 set-based;方言差異:分頁 LIMIT → TOP / OFFSET-FETCH、
/// 計數用 COUNT_BIG 與 CAST(... AS BIGINT)(SQL Server COUNT/SUM(int) 回 INT,需 BIGINT 以對齊 long)。
/// 完整性測試的 LEFT JOIN + UNION ALL(模擬 FULL OUTER JOIN)為 ANSI,照抄。
/// 診斷日誌（dev-only）：每個 SELECT/INSERT 走 <see cref="DiagnosticDb"/>、transaction 走 scope。
/// </summary>
public sealed class SqlServerValidationRunRepository(SqlServerProjectDatabase database, ILogger<SqlServerValidationRunRepository>? logger = null)
    : IValidationFactsPort
{
    private const string Provider = "sqlServer";

    private readonly ILogger _log = logger ?? NullLogger<SqlServerValidationRunRepository>.Instance;

    // 完整性 CTE 單一事實來源:見 ValidationProcedures.CompletenessDiffCte(completenessDiffPage repo 共用同一份)。
    // SQL Server 路徑以 CompletenessDiffCteFor 前綴專案 schema(內含 gl/tb)。

    Task<ResultRecord> IValidationFactsPort.ExecuteAsync(
        ValidationPlan plan,
        Func<ValidationFacts, ResultRecord> finalize,
        CancellationToken cancellationToken) =>
        ExecuteAsync(plan, finalize, cancellationToken);

    internal async Task<ResultRecord> ExecuteAsync(
        ValidationPlan plan,
        Func<ValidationFacts, ResultRecord> finalize,
        CancellationToken cancellationToken)
    {
        var input = plan.Request;
        var projectId = input.ProjectId;
        await database.EnsureCreatedAsync(projectId, cancellationToken);

        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        using var txLog = DiagnosticDb.BeginTransaction(_log, Provider);

        var populationSummary = await ReadPopulationSummaryAsync(
            connection,
            projectId,
            transaction,
            cancellationToken);
        var amountBinCounts = await ReadAmountDistributionAsync(
            connection,
            projectId,
            transaction,
            plan,
            cancellationToken);

        long completenessCount = 0;
        IReadOnlyList<CompletenessDiffAccount> completenessDiffs = [];
        if (plan.RunCompleteness)
        {
            (completenessCount, completenessDiffs) = await ReadCompletenessAsync(connection, projectId, input, transaction, cancellationToken);
        }

        // 借貸不平母體核心走 ValidationProcedures.UnbalancedCore(有效分錄限定;{s} 由 CreateCommand 展開)。
        var unbalancedCount = await ScalarAsync(
            connection, projectId, input, transaction, cancellationToken,
            $"SELECT COUNT_BIG(*) FROM (SELECT voucher_number {ValidationProcedures.UnbalancedCore("{s}.")}) AS unbalanced;");

        var unbalancedDetail = await ReadUnbalancedDetailAsync(connection, projectId, input, transaction, cancellationToken);

        var infSampleCount = await InsertInfSampleAsync(connection, projectId, transaction, input, cancellationToken);

        var (blankAccountNumber, blankVoucherNumber, blankDescription, approvalDateNotInPeriod) =
            await ReadBlankRecordsAsync(connection, projectId, transaction, input, cancellationToken);
        var sourceQualityFindingCount = await ReadSourceQualityFindingCountAsync(
            connection,
            projectId,
            transaction,
            cancellationToken);

        var blankDetail = await ReadBlankDetailAsync(connection, projectId, transaction, input, cancellationToken);

        var controlTotals = await ReadControlTotalsAsync(connection, projectId, transaction, cancellationToken);
        var voucherNumberAcrossGlDates = await ReadVoucherNumberAcrossGlDatesAsync(connection, projectId, transaction, cancellationToken);

        var sourceQualityPage = await SourceQualityPageReader.ReadAsync(connection, transaction, SqlServerDialect.Instance,
            SqlServerProjectSchema.QualifierFor(projectId), new PageRequest(null, ResultPreviewLimits.SummaryRows), cancellationToken);
        var facts = new ValidationFacts(
            populationSummary,
            completenessCount,
            completenessDiffs,
            unbalancedCount,
            infSampleCount,
            blankAccountNumber,
            blankVoucherNumber,
            blankDescription,
            approvalDateNotInPeriod,
            sourceQualityFindingCount,
            unbalancedDetail,
            blankDetail,
            controlTotals,
            amountBinCounts,
            voucherNumberAcrossGlDates, sourceQualityPage.Rows);
        var record = finalize(facts);
        await SqlServerResultStore.SaveWithinAsync(database, connection, transaction, projectId, record, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await transaction.CommitAsync(CancellationToken.None);
        txLog.Committed();
        return record;
    }

    private async Task<VoucherNumberAcrossGlDatesCounts> ReadVoucherNumberAcrossGlDatesAsync(
        SqlConnection connection, string projectId, SqlTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = database.CreateCommand(connection, projectId,
            ValidationProcedures.VoucherNumberAcrossGlDatesSummary(SqlServerDialect.Instance, "{s}.", "COUNT_BIG"));
        command.Transaction = transaction;
        await using var reader = await command.ExecuteReaderLoggedAsync(_log, Provider, cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return new VoucherNumberAcrossGlDatesCounts(reader.GetInt64(0), reader.GetInt64(1));
    }

    /// <summary>
    /// 讀取投影時落地的 gl_control_total raw facts（單列）。match flags 由 AuditCore
    /// Finalize 對照目前 GL population facts 裁定；Infrastructure 不作審計判斷。
    /// </summary>
    private async Task<ValidationControlTotalsFacts?> ReadControlTotalsAsync(
        SqlConnection connection,
        string projectId,
        SqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = database.CreateCommand(connection, projectId,
            """
            SELECT gl_row_count, gl_debit_scaled, gl_credit_scaled
            FROM {s}.gl_control_total WHERE singleton = 1;
            """);
        command.Transaction = transaction;

        await using var reader = await command.ExecuteReaderLoggedAsync(_log, Provider, cancellationToken);
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
        SqlConnection connection, string projectId, SqlTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = database.CreateCommand(connection, projectId,
            $$"""
            SELECT COUNT_BIG(*),
                   COALESCE(SUM(debit_amount_scaled), 0),
                   COALESCE(SUM(credit_amount_scaled), 0),
                   COALESCE(SUM(CAST(CASE WHEN is_excluded = 0 THEN 1 ELSE 0 END AS BIGINT)), 0),
                   COUNT_BIG(DISTINCT CASE WHEN is_excluded = 0 THEN voucher_number END),
                   COALESCE(SUM(CASE WHEN is_excluded = 0 THEN debit_amount_scaled ELSE 0 END), 0),
                   COALESCE(SUM(CASE WHEN is_excluded = 0 THEN credit_amount_scaled ELSE 0 END), 0),
                   COALESCE(SUM(CASE WHEN is_excluded = 0 THEN amount_scaled ELSE 0 END), 0),
                   COALESCE(SUM(CAST(CASE WHEN is_excluded = 1 THEN 1 ELSE 0 END AS BIGINT)), 0),
                   COALESCE(SUM(CAST(CASE WHEN {{GlExcludedPopulation.ExcludedByPeriodSqlPredicate()}} THEN 1 ELSE 0 END AS BIGINT)), 0),
                   COALESCE(SUM(CAST(CASE WHEN exclusion_reason = @postingStatusReason THEN 1 ELSE 0 END AS BIGINT)), 0)
            FROM {s}.gl;
            """);
        command.Transaction = transaction;
        command.Parameters.AddWithValue("@postingStatusReason", GlExcludedPopulation.PostingStatusStorageReason);

        await using var reader = await command.ExecuteReaderLoggedAsync(_log, Provider, cancellationToken);
        await reader.ReadAsync(cancellationToken);

        return new GlPopulationSummary(
            new GlRawPopulationTotals(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetInt64(2)),
            new ValidationGlTotals(
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
    /// AuditCore plan 提供；SQL Server 僅以 COUNT_BIG 執行同一個參數化 GROUP BY CASE。
    /// </summary>
    private async Task<IReadOnlyList<ValidationAmountBinCount>> ReadAmountDistributionAsync(
        SqlConnection connection,
        string projectId,
        SqlTransaction transaction,
        ValidationPlan plan,
        CancellationToken cancellationToken)
    {
        var bucketCase = ValidationAmountDistributionCatalog.CaseExpression("amount_scaled");
        await using var command = database.CreateCommand(
            connection,
            projectId,
            $$"""
            SELECT {{bucketCase}} AS bin_key,
                   COUNT_BIG(*)
            FROM {s}.gl
            WHERE {{GlExcludedPopulation.SqlPredicate()}}
            GROUP BY {{bucketCase}};
            """);
        command.Transaction = transaction;
        BindAmountThresholds(command, plan.AmountDistributionPlan);

        var rows = new List<ValidationAmountBinCount>();
        await using var reader = await command.ExecuteReaderLoggedAsync(
            _log,
            Provider,
            cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new ValidationAmountBinCount(
                reader.GetString(0),
                reader.GetInt64(1)));
        }

        return rows;
    }

    private async Task<(long Count, IReadOnlyList<CompletenessDiffAccount> Diffs)> ReadCompletenessAsync(
        SqlConnection connection, string projectId, ValidationRequest input, SqlTransaction transaction, CancellationToken cancellationToken)
    {
        var completenessDiffCte = ValidationProcedures.CompletenessDiffCteFor(SqlServerProjectSchema.QualifierFor(projectId));

        var count = await ScalarAsync(
            connection, projectId, input, transaction, cancellationToken,
            completenessDiffCte + "\nSELECT COUNT_BIG(*) FROM diff WHERE tb_s <> gl_s;");

        var diffs = new List<CompletenessDiffAccount>();

        await using var command = database.CreateCommand(connection, projectId,
            completenessDiffCte +
            $"""

            SELECT TOP ({ResultPreviewLimits.SummaryRows}) account_number, account_name, tb_s, gl_s, tb_s - gl_s, not_in_tb
            FROM diff
            WHERE tb_s <> gl_s
            ORDER BY ABS(tb_s - gl_s) DESC, account_number;
            """);
        command.Transaction = transaction;
        await using var reader = await command.ExecuteReaderLoggedAsync(_log, Provider, cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            diffs.Add(new CompletenessDiffAccount(
                reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetInt64(2),
                reader.GetInt64(3),
                reader.GetInt64(4),
                reader.GetInt32(5) != 0));
        }

        return (count, diffs);
    }

    private async Task<int> InsertInfSampleAsync(
        SqlConnection connection, string projectId, SqlTransaction transaction,
        ValidationRequest input, CancellationToken cancellationToken)
    {
        // INF 抽樣 INSERT 走 ValidationProcedures.InfSampleInsert(只看納入測試的分錄;依資料庫語法取 N 列走
        // SqlServerDialect 的 OFFSET/FETCH，結果集與原 TOP(@n) 等價)。source_row_number 與 @seed 皆 BIGINT；
        // Feistel 排序鍵的中間值與輸出都在 signed BIGINT 內，三 provider 精確等價。
        // {s} 由 CreateCommand 展開專案 schema。
        await using var command = database.CreateCommand(connection, projectId,
            ValidationProcedures.InfSampleInsert(
                schemaPrefix: "{s}.",
                SqlServerDialect.Instance));
        command.Transaction = transaction;
        command.Parameters.AddWithValue("@resultId", input.ResultId);
        command.Parameters.AddWithValue("@seed", input.SampleSeed);
        command.Parameters.AddWithValue("@n", input.SampleSize);
        return await command.ExecuteNonQueryLoggedAsync(_log, Provider, cancellationToken);
    }

    private async Task<(long, long, long, long)> ReadBlankRecordsAsync(
        SqlConnection connection, string projectId, SqlTransaction transaction,
        ValidationRequest input, CancellationToken cancellationToken)
    {
        // 四類述詞走 BlankRecordsCategoryPredicate 中心(空白判定由方言的去空白方法產生;期外日期
        // = approval_date)，且全部只看納入測試的分錄。SUM(CASE→int) 在 SQL Server 回 INT,CAST AS BIGINT 對齊 long。
        // 日期為投影時正規化的 yyyy-MM-dd ISO 字串,文字比較即時間序比較。
        var columns = string.Join(",\n                ",
            BlankRecordsCategoryPredicate.All.Select(c =>
                $"COALESCE(SUM(CAST(CASE WHEN {BlankRecordsCategoryPredicate.Scoped(c, SqlServerDialect.Instance)} THEN 1 ELSE 0 END AS BIGINT)), 0)"));
        await using var command = database.CreateCommand(connection, projectId,
            $$"""
            SELECT
                {{columns}}
            FROM {s}.gl;
            """);
        command.Transaction = transaction;
        BindPeriod(command, input);

        await using var reader = await command.ExecuteReaderLoggedAsync(_log, Provider, cancellationToken);
        await reader.ReadAsync(cancellationToken);

        return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3));
    }

    private async Task<long> ReadSourceQualityFindingCountAsync(
        SqlConnection connection,
        string projectId,
        SqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = database.CreateCommand(
            connection,
            projectId,
            $"SELECT COUNT_BIG(*) FROM {{s}}.gl WHERE {ValidationProcedures.BlankGlDateSourceQualityPredicate};");
        command.Transaction = transaction;
        var result = await command.ExecuteScalarLoggedAsync(_log, Provider, cancellationToken);
        return result is null or DBNull ? 0L : Convert.ToInt64(result);
    }

    private async Task<IReadOnlyList<UnbalancedVoucher>> ReadUnbalancedDetailAsync(
        SqlConnection connection, string projectId, ValidationRequest input, SqlTransaction transaction, CancellationToken cancellationToken)
    {
        // 母體核心走 ValidationProcedures.UnbalancedCore(有效分錄限定;{s} 由 CreateCommand 展開)。
        await using var command = database.CreateCommand(connection, projectId,
            $"SELECT TOP ({ResultPreviewLimits.SummaryRows}) voucher_number, " +
            "COALESCE(SUM(debit_amount_scaled), 0), " +
            "COALESCE(SUM(credit_amount_scaled), 0), " +
            "COALESCE(SUM(amount_scaled), 0) " +
            ValidationProcedures.UnbalancedCore("{s}.") +
            " ORDER BY ABS(SUM(amount_scaled)) DESC, voucher_number;");
        command.Transaction = transaction;
        var rows = new List<UnbalancedVoucher>();
        await using var reader = await command.ExecuteReaderLoggedAsync(_log, Provider, cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new UnbalancedVoucher(
                reader.IsDBNull(0) ? null : reader.GetString(0),
                reader.GetInt64(1),
                reader.GetInt64(2),
                reader.GetInt64(3)));
        }

        return rows;
    }

    private async Task<IReadOnlyList<BlankRecordRow>> ReadBlankDetailAsync(
        SqlConnection connection, string projectId, SqlTransaction transaction,
        ValidationRequest input, CancellationToken cancellationToken)
    {
        // 四類旗標與 WHERE 皆走 BlankRecordsCategoryPredicate 中心，且全部只看納入測試的分錄。
        var flags = string.Join(",\n                   ",
            BlankRecordsCategoryPredicate.All.Select(c =>
                $"CASE WHEN {BlankRecordsCategoryPredicate.Scoped(c, SqlServerDialect.Instance)} THEN 1 ELSE 0 END"));
        var anyMatch = string.Join("\n               OR ",
            BlankRecordsCategoryPredicate.All.Select(c => BlankRecordsCategoryPredicate.Scoped(c, SqlServerDialect.Instance)));
        await using var command = database.CreateCommand(connection, projectId,
            $$"""
            SELECT TOP ({{ResultPreviewLimits.SummaryRows}}) voucher_number, account_number, gl_date, description,
                   {{flags}}
            FROM {s}.gl
            WHERE ({{anyMatch}})
            ORDER BY source_row_number, entry_id;
            """);
        command.Transaction = transaction;
        BindPeriod(command, input);

        var rows = new List<BlankRecordRow>();
        await using var reader = await command.ExecuteReaderLoggedAsync(_log, Provider, cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new BlankRecordRow(
                reader.IsDBNull(0) ? null : reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetInt32(4) != 0,
                reader.GetInt32(5) != 0,
                reader.GetInt32(6) != 0,
                reader.GetInt32(7) != 0));
        }

        return rows;
    }

    private async Task<long> ScalarAsync(
        SqlConnection connection, string projectId, ValidationRequest input, SqlTransaction transaction,
        CancellationToken cancellationToken, string sql)
    {
        await using var command = database.CreateCommand(connection, projectId, sql);
        command.Transaction = transaction;
        var result = await command.ExecuteScalarLoggedAsync(_log, Provider, cancellationToken);
        return result is null or DBNull ? 0L : Convert.ToInt64(result);
    }

    /// <summary>只供 approval_date out-of-range 規則窗口使用；不得作一般母體述詞。</summary>
    private static void BindPeriod(SqlCommand command, ValidationRequest input)
    {
        command.Parameters.AddWithValue("@periodStart", input.PeriodStart);
        command.Parameters.AddWithValue("@periodEnd", input.PeriodEnd);
    }

    private static void BindAmountThresholds(
        SqlCommand command,
        ValidationAmountDistributionPlan plan)
    {
        foreach (var threshold in plan.Thresholds)
        {
            command.Parameters.AddWithValue(threshold.ParameterName, threshold.ValueScaled);
        }
    }
}
