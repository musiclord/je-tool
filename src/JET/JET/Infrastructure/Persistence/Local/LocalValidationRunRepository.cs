using System.Data.Common;
using JET.AuditCore;
using JET.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace JET.Infrastructure;

/// <summary>
/// 用集合式 SQL 執行四項資料驗證。規則一律在資料庫裡算，不可以把整份資料讀進記憶體再用 LINQ 算。
/// 完整性測試用 LEFT JOIN 加 UNION ALL 做出 FULL OUTER JOIN 的結果；SQLite 3.39 起才有 FULL OUTER JOIN，這裡不依賴它。
/// 診斷日誌（開發用）：每一句 SELECT 與 INSERT 經 <see cref="DiagnosticDb"/> 記錄，交易的開始與結束也記一筆。
/// </summary>
public sealed class LocalValidationRunRepository(ILocalProjectDatabase database, ILogger<LocalValidationRunRepository>? logger = null)
    : IValidationFactsPort
{
    private readonly string _provider = database.Dialect.ProviderName;

    private readonly ILogger _log = logger ?? NullLogger<LocalValidationRunRepository>.Instance;

    // 完整性的 CTE 只定義在 ValidationProcedures.CompletenessDiffCte，分頁讀差異科目的 repository 也用同一份。
    // GL 那一側只取已經寫進 gl、而且納入測試的分錄。
    private static readonly string CompletenessDiffCte = ValidationProcedures.CompletenessDiffCte;

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
        await database.EnsureReadyAsync(projectId, cancellationToken);

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
            // 找借貸不平傳票的 SQL 取自 ValidationProcedures.UnbalancedCore，只看納入測試的分錄。
            $"SELECT COUNT(*) FROM (SELECT voucher_number {ValidationProcedures.UnbalancedCore()});");

        var unbalancedDetail = await ReadUnbalancedDetailAsync(connection, transaction, input, cancellationToken);

        var infSampleCount = await InsertInfSampleAsync(connection, transaction, input, cancellationToken);

        var (blankAccountNumber, blankVoucherNumber, blankDescription, approvalDateNotInPeriod) =
            await ReadBlankRecordsAsync(connection, transaction, input, plan.RunOutOfPeriodApproval, cancellationToken);
        var sourceQualityFindingCount = await ReadSourceQualityFindingCountAsync(
            connection,
            transaction,
            cancellationToken);

        var blankDetail = await ReadBlankDetailAsync(connection, transaction, input, plan.RunOutOfPeriodApproval, cancellationToken);

        var controlTotals = await ReadControlTotalsAsync(connection, transaction, cancellationToken);
        var voucherNumberAcrossGlDates = await ReadVoucherNumberAcrossGlDatesAsync(connection, transaction, cancellationToken);

        var sourceQualityPage = await SourceQualityPageReader.ReadAsync(connection, transaction, database.Dialect,
            string.Empty, new PageRequest(null, ResultPreviewLimits.SummaryRows), cancellationToken);

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
        await LocalResultStore.SaveWithinAsync(connection, transaction, record, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await transaction.CommitAsync(CancellationToken.None);
        txLog.Committed();
        return record;
    }

    private async Task<VoucherNumberAcrossGlDatesCounts> ReadVoucherNumberAcrossGlDatesAsync(
        DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = ValidationProcedures.VoucherNumberAcrossGlDatesSummary(database.Dialect);
        await using var reader = await command.ExecuteReaderLoggedAsync(_log, _provider, cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return new VoucherNumberAcrossGlDatesCounts(reader.GetInt64(0), reader.GetInt64(1));
    }

    /// <summary>
    /// 讀出投影時寫進 gl_control_total 的數字，這張表只有一列。筆數與合計是否相符，由 AuditCore 在
    /// Finalize 時拿這些數字和目前 GL 的數字比對後判定；Infrastructure 不做審計判斷。
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
            SELECT gl_row_count, gl_debit_scaled, gl_credit_scaled
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
            $"""
            SELECT COUNT(*),
                   COALESCE(SUM(debit_amount_scaled), 0),
                   COALESCE(SUM(credit_amount_scaled), 0),
                   COALESCE(SUM(CASE WHEN is_excluded = 0 THEN 1 ELSE 0 END), 0),
                   COUNT(DISTINCT CASE WHEN is_excluded = 0 THEN voucher_number END),
                   COALESCE(SUM(CASE WHEN is_excluded = 0 THEN debit_amount_scaled ELSE 0 END), 0),
                   COALESCE(SUM(CASE WHEN is_excluded = 0 THEN credit_amount_scaled ELSE 0 END), 0),
                   COALESCE(SUM(CASE WHEN is_excluded = 0 THEN amount_scaled ELSE 0 END), 0),
                   COALESCE(SUM(CASE WHEN is_excluded = 1 THEN 1 ELSE 0 END), 0),
                   COALESCE(SUM(CASE WHEN {GlExcludedPopulation.ExcludedByPeriodSqlPredicate()} THEN 1 ELSE 0 END), 0),
                   COALESCE(SUM(CASE WHEN exclusion_reason = @postingStatusReason THEN 1 ELSE 0 END), 0)
            FROM gl;
            """;
        command.AddWithValue("@postingStatusReason", GlExcludedPopulation.PostingStatusStorageReason);

        await using var reader = await command.ExecuteReaderLoggedAsync(_log, _provider, cancellationToken);
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
    /// 納入測試的分錄依金額絕對值分成幾個級距，算出各級距的筆數。分級的 CASE 與各級距的門檻由 AuditCore 提供，
    /// 門檻是放大成整數的金額；這裡只執行那一句帶參數的 GROUP BY CASE。
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
            FROM gl
            WHERE {GlExcludedPopulation.SqlPredicate()}
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
            $"""

            SELECT account_number, account_name, tb_s, gl_s, tb_s - gl_s, not_in_tb
            FROM diff
            WHERE tb_s <> gl_s
            ORDER BY ABS(tb_s - gl_s) DESC, account_number
            LIMIT {ResultPreviewLimits.SummaryRows};
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
        // INF 抽樣的 INSERT 取自 ValidationProcedures.InfSampleInsert：只看納入測試的分錄，取 N 列的寫法依資料庫而定。
        command.CommandText = ValidationProcedures.InfSampleInsert(
            schemaPrefix: string.Empty,
            database.Dialect);
        command.AddWithValue("@resultId", input.ResultId);
        command.AddWithValue("@seed", input.SampleSeed);
        command.AddWithValue("@n", input.SampleSize);
        return await command.ExecuteNonQueryLoggedAsync(_log, _provider, cancellationToken);
    }

    private async Task<(long, long, long, long)> ReadBlankRecordsAsync(
        DbConnection connection, DbTransaction transaction,
        ValidationRequest input, bool approvalDateAvailable, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // 四類的判斷條件都取自 BlankRecordsCategoryPredicate，計數、明細與分頁用同一份；期外日期用 approval_date 判斷。
        // 四類都只看納入測試的分錄。
        // 日期在投影時已經正規化成 yyyy-MM-dd 字串，所以文字比大小就是日期先後。
        var columns = string.Join(",\n                ",
            BlankRecordsCategoryPredicate.All.Select(c =>
                $"COALESCE(SUM(CASE WHEN {BlankRecordsCategoryPredicate.ScopedWhenChecked(c, database.Dialect, approvalDateAvailable)} THEN 1 ELSE 0 END), 0)"));
        command.CommandText =
            $"""
            SELECT
                {columns}
            FROM gl;
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
            $"SELECT COUNT(*) FROM gl WHERE {ValidationProcedures.BlankGlDateSourceQualityPredicate};";
        var result = await command.ExecuteScalarLoggedAsync(_log, _provider, cancellationToken);
        return result is null or DBNull ? 0L : Convert.ToInt64(result);
    }

    private async Task<IReadOnlyList<UnbalancedVoucher>> ReadUnbalancedDetailAsync(
        DbConnection connection, DbTransaction transaction, ValidationRequest input, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // 找借貸不平傳票的 SQL 取自 ValidationProcedures.UnbalancedCore，只看納入測試的分錄。
        command.CommandText =
            "SELECT voucher_number, " +
            "COALESCE(SUM(debit_amount_scaled), 0), " +
            "COALESCE(SUM(credit_amount_scaled), 0), " +
            "COALESCE(SUM(amount_scaled), 0) " +
            ValidationProcedures.UnbalancedCore() +
            $" ORDER BY ABS(SUM(amount_scaled)) DESC, voucher_number LIMIT {ResultPreviewLimits.SummaryRows};";
        var rows = new List<UnbalancedVoucher>();
        await using var reader = await command.ExecuteReaderLoggedAsync(_log, _provider, cancellationToken);
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
        DbConnection connection, DbTransaction transaction,
        ValidationRequest input, bool approvalDateAvailable, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // 四類的旗標與 WHERE 都取自 BlankRecordsCategoryPredicate，而且都只看納入測試的分錄；這次沒有檢查的類別一律不成立。
        var flags = string.Join(",\n                   ",
            BlankRecordsCategoryPredicate.All.Select(c =>
                $"CASE WHEN {BlankRecordsCategoryPredicate.ScopedWhenChecked(c, database.Dialect, approvalDateAvailable)} THEN 1 ELSE 0 END"));
        var anyMatch = string.Join("\n               OR ",
            BlankRecordsCategoryPredicate.All.Select(c => BlankRecordsCategoryPredicate.ScopedWhenChecked(c, database.Dialect, approvalDateAvailable)));
        command.CommandText =
            $"""
            SELECT voucher_number, account_number, gl_date, description,
                   {flags},
                   approval_date
            FROM gl
            WHERE ({anyMatch})
            ORDER BY source_row_number, entry_id
            LIMIT {ResultPreviewLimits.SummaryRows};
            """;
        BindPeriod(command, input);

        var rows = new List<BlankRecordRow>();
        await using var reader = await command.ExecuteReaderLoggedAsync(_log, _provider, cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new BlankRecordRow(
                reader.IsDBNull(0) ? null : reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetInt64(4) != 0,
                reader.GetInt64(5) != 0,
                reader.GetInt64(6) != 0,
                reader.GetInt64(7) != 0,
                ApprovalDate: reader.IsDBNull(8) ? null : reader.GetString(8)));
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

    /// <summary>只給判斷 approval_date 是否在查核期間外的規則綁期間參數；不可以拿這兩個參數當限定母體的一般條件。</summary>
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
