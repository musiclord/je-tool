using System.Data.Common;
using System.Numerics;
using JET.AuditCore;
using JET.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace JET.Infrastructure;

/// <summary>
/// 用集合式 SQL 執行預篩選規則。逐筆判斷的規則都用同一句
/// SELECT COUNT(*), COUNT(DISTINCT voucher_number) FROM gl g WHERE 加上各自的條件，
/// 一次取得命中的分錄數與不重複的傳票數。
/// 編製者彙總與罕用科目是彙總查詢，各自最多回 50 列。每條逐筆規則的條件都從 <see cref="PrescreenRulePredicates"/> 取，和第五步同一個方法。
/// 每一句計數與彙總的 SELECT 都經 <see cref="DiagnosticDb"/> 的擴充方法執行；記錄層級開到 Debug 時（開發用），診斷日誌會記下完整的 SQL 與參數。
/// </summary>
public sealed class LocalPrescreenRunRepository(ILocalProjectDatabase database, ILogger<LocalPrescreenRunRepository>? logger = null)
    : IPrescreenFactsPort
{
    private readonly string _provider = database.Dialect.ProviderName;

    private readonly GlRulePredicates _predicates =
        new(database.Dialect, GlPopulationScopeSql.Predicate);

    private readonly ILogger _log = logger ?? NullLogger<LocalPrescreenRunRepository>.Instance;

    async Task<PrescreenFacts> IPrescreenFactsPort.ExecuteAsync(
        PrescreenPlan plan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return await ExecuteFactsAsync(
            plan.Request.ProjectId,
            PrescreenExecutionInput.FromPlan(plan),
            cancellationToken);
    }

    private async Task<PrescreenFacts> ExecuteFactsAsync(
        string projectId,
        PrescreenExecutionInput input,
        CancellationToken cancellationToken)
    {
        await database.EnsureReadyAsync(projectId, cancellationToken);

        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);
        var filterContext = input.FilterContext();
        // 每條規則的述詞都從同一張對應表取；第五步「預篩選」條件與傳票明細也是同一個方法。
        // 計數和第四步明細、預篩選報告列出同一批分錄：未預期借貸組合算整張命中傳票，其他規則算直接命中的列。
        string Rule(string key, FilterSqlParameterPlanBuilder cmd) =>
            LocalPrescreenPageRepository.HitRowsPredicate(
                key, PrescreenRulePredicates.Build(_predicates, key, cmd, filterContext), string.Empty);

        (long HitLines, long HitVouchers) prepPeriod = (0L, 0L);
        if (input.RunPrepPeriodApproval)
        {
            prepPeriod = await CountWhereAsync(
                connection, input, cancellationToken,
                cmd => Rule(PrescreenRuleKeys.PrepPeriodApproval, cmd));
        }

        var suspicious = await CountWhereAsync(
            connection, input, cancellationToken,
            cmd => Rule(PrescreenRuleKeys.SuspiciousKeywords, cmd));

        (long HitLines, long HitVouchers) unexpectedPair = (0L, 0L);
        if (input.RunUnexpectedAccountPair)
        {
            unexpectedPair = await CountWhereAsync(
                connection, input, cancellationToken,
                cmd => Rule(PrescreenRuleKeys.UnexpectedAccountPair, cmd));
        }

        // 0 的個數由 GL 算出、跟著請求帶進來，述詞和第五步「尾數連續多個 0」同一個。
        var trailingZeros = await CountWhereAsync(
            connection, input, cancellationToken,
            cmd => Rule(PrescreenRuleKeys.TrailingZeros, cmd));

        var preparers = input.RunPreparerSummary
            ? await ReadPreparersAsync(connection, input, cancellationToken)
            : [];

        // 期間分錄總筆數不需要配對編製人員欄位就算得出來，所以一律查；
        // 相異編製人員數只在編製者彙總適用時回傳，不適用時回 0。
        var (observedPreparerCount, totalEntries) =
            await ReadPreparerPopulationAsync(connection, input, cancellationToken);
        var totalPreparers = input.RunPreparerSummary ? observedPreparerCount : 0L;

        var (distinctAccounts, accounts) = await ReadAccountUsageAsync(connection, input, cancellationToken);

        var weekendPosting = await CountWhereAsync(
            connection, input, cancellationToken,
            cmd => Rule(PrescreenRuleKeys.WeekendPosting, cmd));
        var weekendApproval = input.RunWeekendApproval
            ? await CountWhereAsync(
                connection, input, cancellationToken,
                cmd => Rule(PrescreenRuleKeys.WeekendApproval, cmd))
            : (HitLines: 0L, HitVouchers: 0L);

        (long HitLines, long HitVouchers) holidayPosting = (0L, 0L);
        (long HitLines, long HitVouchers) holidayApproval = (0L, 0L);
        if (input.RunHolidayPosting)
        {
            holidayPosting = await CountWhereAsync(
                connection, input, cancellationToken,
                cmd => Rule(PrescreenRuleKeys.HolidayPosting, cmd));
        }
        if (input.RunHolidayApproval)
        {
            holidayApproval = await CountWhereAsync(
                connection, input, cancellationToken,
                cmd => Rule(PrescreenRuleKeys.HolidayApproval, cmd));
        }

        var blankDescription = await CountWhereAsync(
            connection, input, cancellationToken,
            cmd => Rule(PrescreenRuleKeys.BlankDescription, cmd));


        // 非授權編製人員：授權清單未匯入時不執行（計 0、handler 標 na）。
        (long HitLines, long HitVouchers) nonAuthorizedPreparer = (0L, 0L);
        if (input.RunNonAuthorizedPreparer)
        {
            nonAuthorizedPreparer = await CountWhereAsync(
                connection, input, cancellationToken,
                cmd => Rule(PrescreenRuleKeys.NonAuthorizedPreparer, cmd));
        }

        return new PrescreenFacts(
            prepPeriod.HitLines,
            suspicious.HitLines,
            unexpectedPair.HitLines,
            trailingZeros.HitLines,
            preparers,
            distinctAccounts,
            accounts,
            weekendPosting.HitLines,
            weekendApproval.HitLines,
            holidayPosting.HitLines,
            holidayApproval.HitLines,
            blankDescription.HitLines,
            nonAuthorizedPreparer.HitLines,
            new Dictionary<string, long>(StringComparer.Ordinal)
            {
                [PrescreenRuleKeys.PrepPeriodApproval] = prepPeriod.HitVouchers,
                [PrescreenRuleKeys.SuspiciousKeywords] = suspicious.HitVouchers,
                [PrescreenRuleKeys.UnexpectedAccountPair] = unexpectedPair.HitVouchers,
                [PrescreenRuleKeys.TrailingZeros] = trailingZeros.HitVouchers,
                [PrescreenRuleKeys.WeekendPosting] = weekendPosting.HitVouchers,
                [PrescreenRuleKeys.WeekendApproval] = weekendApproval.HitVouchers,
                [PrescreenRuleKeys.HolidayPosting] = holidayPosting.HitVouchers,
                [PrescreenRuleKeys.HolidayApproval] = holidayApproval.HitVouchers,
                [PrescreenRuleKeys.BlankDescription] = blankDescription.HitVouchers,
                [PrescreenRuleKeys.NonAuthorizedPreparer] = nonAuthorizedPreparer.HitVouchers
            },
            totalPreparers,
            totalEntries);
    }

    private async Task<(long HitLines, long HitVouchers)> CountWhereAsync(
        DbConnection connection,
        PrescreenExecutionInput input,
        CancellationToken cancellationToken,
        Func<FilterSqlParameterPlanBuilder, string> predicateFactory)
    {
        await using var command = connection.CreateCommand();
        // 只算已經寫進 gl、而且納入測試的分錄。規則自己的日期範圍，以及要算直接命中的列還是整張傳票，
        // 由 predicateFactory 組出的條件決定。
        var parameters = new FilterSqlParameterPlanBuilder(database.Dialect);
        var predicate = predicateFactory(parameters);
        parameters.Build(predicate).BindParametersTo(command);
        command.CommandText =
            $"SELECT COUNT(*), COUNT(DISTINCT g.voucher_number) " +
            $"FROM gl g WHERE {GlExcludedPopulation.SqlPredicate("g")} AND ({predicate});";

        await using var reader = await command.ExecuteReaderLoggedAsync(_log, _provider, cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return (0L, 0L);
        }

        return (ReadInt64(reader, 0), ReadInt64(reader, 1));
    }

    private async Task<IReadOnlyList<PreparerSummaryRow>> ReadPreparersAsync(
        DbConnection connection, PrescreenExecutionInput input, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        // 編製者彙總與預篩選規則共用有效分錄母體。人員依去空白、不分大小寫的識別值分組，
        // 顯示值固定取同一組裡碼位最小的去空白寫法（例如 U1 與 u1 顯示 U1），所以結果不受資料列順序影響。
        var person = PersonIdentity.PreparerDisplay(database.Dialect);
        command.CommandText =
            $"""
            SELECT MIN({person}),
                   COUNT(*),
                   COALESCE(SUM(debit_amount_scaled), 0),
                   COALESCE(SUM(credit_amount_scaled), 0),
                   COALESCE(SUM(CASE WHEN is_manual = 1 THEN 1 ELSE 0 END), 0)
            FROM gl
            WHERE {GlExcludedPopulation.SqlPredicate()}
            GROUP BY {PersonIdentity.GroupKey(database.Dialect, person)}
            ORDER BY {ConcentrationLimits.MostEntriesFirst}, MIN({person})
            LIMIT {ConcentrationLimits.CandidateRows};
            """;
        var rows = new List<PreparerSummaryRow>();
        await using var reader = await command.ExecuteReaderLoggedAsync(_log, _provider, cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new PreparerSummaryRow(
                reader.GetString(0),
                reader.GetInt64(1),
                reader.GetInt64(2),
                reader.GetInt64(3),
                reader.GetInt64(4)));
        }

        return rows;
    }

    /// <summary>
    /// 編製者集中度的兩個分母，一次掃描取得：期間分錄總筆數與相異編製人員總數。
    /// 分組鍵與編製者彙總同為去空白、不分大小寫的識別值，故各人筆數合計恆等於總筆數。
    /// </summary>
    private async Task<(long TotalPreparers, long TotalEntries)> ReadPreparerPopulationAsync(
        DbConnection connection, PrescreenExecutionInput input, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        // 與編製者彙總同口徑：只取有效分錄。
        command.CommandText =
            $"""
            SELECT COUNT(*),
                   COUNT(DISTINCT {PersonIdentity.GroupKey(database.Dialect, PersonIdentity.PreparerDisplay(database.Dialect))})
            FROM gl
            WHERE {GlExcludedPopulation.SqlPredicate()};
            """;

        await using var reader = await command.ExecuteReaderLoggedAsync(_log, _provider, cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return (0L, 0L);
        }

        return (ReadInt64(reader, 1), ReadInt64(reader, 0));
    }

    private async Task<(long Distinct, IReadOnlyList<AccountUsageRow> Accounts)> ReadAccountUsageAsync(
        DbConnection connection, PrescreenExecutionInput input, CancellationToken cancellationToken)
    {
        // 罕用科目/科目使用彙總與預篩選規則共用有效分錄母體。
        long distinct;
        await using (var countCommand = connection.CreateCommand())
        {
            countCommand.CommandText =
                $"SELECT COUNT(DISTINCT account_number) FROM gl WHERE {GlExcludedPopulation.SqlPredicate()};";
            distinct = Convert.ToInt64(await countCommand.ExecuteScalarLoggedAsync(_log, _provider, cancellationToken));
        }

        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT COALESCE(account_number, ''),
                   MAX(account_name),
                   COUNT(*),
                   COALESCE(SUM(debit_amount_scaled), 0),
                   COALESCE(SUM(credit_amount_scaled), 0)
            FROM gl
            WHERE {GlExcludedPopulation.SqlPredicate()}
            GROUP BY account_number
            ORDER BY {ConcentrationLimits.FewestEntriesFirst}, account_number
            LIMIT {ConcentrationLimits.CandidateRows};
            """;
        var rows = new List<AccountUsageRow>();
        await using var reader = await command.ExecuteReaderLoggedAsync(_log, _provider, cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new AccountUsageRow(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetInt64(2),
                reader.GetInt64(3),
                reader.GetInt64(4)));
        }

        return (distinct, rows);
    }

    private static long ReadInt64(DbDataReader reader, int ordinal)
    {
        var value = reader.GetValue(ordinal);
        return value is BigInteger bigInteger
            ? checked((long)bigInteger)
            : Convert.ToInt64(value);
    }
}
