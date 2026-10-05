using System.Data.Common;
using System.Numerics;
using JET.AuditCore;
using JET.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace JET.Infrastructure;

/// <summary>
/// 預篩選規則的 set-based SQL 執行。row-tag 規則一律以同一條
/// SELECT COUNT(*), COUNT(DISTINCT document_number) FROM target_gl_entry g WHERE 共用述詞片段，
/// 同時取得命中行與去重傳票數；
/// 編製者彙總/罕用科目為彙總（各限 50 列）。Production typed path 的連續零尾數
/// 門檻只消費 AuditCore plan 所提供的 execution input；public compatibility path
/// 仍依 <see cref="TrailingZeroThreshold.DefaultZerosThreshold"/> 建立相同輸入。
/// 診斷日誌（dev-only）：每個 COUNT/彙總 SELECT 走 <see cref="DiagnosticDb"/> 擴充方法記錄完整 SQL/參數。
/// </summary>
public sealed class LocalPrescreenRunRepository(ILocalProjectDatabase database, ILogger<LocalPrescreenRunRepository>? logger = null)
    : IPrescreenRunRepository, IPrescreenFactsPort
{
    private const int SummaryRowLimit = 50;

    // 診斷 provider 標籤由方言注入（sqlite／duckdb），不再寫死。
    private readonly string _provider = database.Dialect.ProviderName;

    private readonly GlRulePredicates Predicates =
        new(database.Dialect, GlPopulationScopeSql.Predicate);

    private readonly ILogger _log = logger ?? NullLogger<LocalPrescreenRunRepository>.Instance;

    public async Task<PrescreenRunResult> RunAsync(
        string projectId,
        PrescreenRunInput input,
        CancellationToken cancellationToken)
    {
        var executionInput = PrescreenExecutionInput.FromCompatibility(input);
        var facts = await ExecuteFactsAsync(
            projectId,
            executionInput,
            cancellationToken);
        return ToCompatibilityResult(input, executionInput, facts);
    }

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
        var filterContext = new FilterRuleContext(
            input.MoneyScale,
            input.LastPeriodStart,
            input.PeriodStart,
            input.PeriodEnd,
            input.NonWorkingDays,
            GlPopulationScope.AuditPeriod);

        (long HitLines, long HitVouchers) postPeriod = (0L, 0L);
        if (input.RunPostPeriodApproval)
        {
            postPeriod = await CountWhereAsync(
                connection, input, cancellationToken,
                cmd => Predicates.PostPeriodApproval(cmd, input.LastPeriodStart!));
        }

        var suspicious = await CountWhereAsync(
            connection, input, cancellationToken,
            cmd => Predicates.SuspiciousKeywords(cmd));

        (long HitLines, long HitVouchers) unexpectedPair = (0L, 0L);
        if (input.RunUnexpectedAccountPair)
        {
            unexpectedPair = await CountWhereAsync(
                connection, input, cancellationToken,
                cmd => Predicates.UnexpectedAccountPair(cmd, filterContext));
        }

        var zeroModulus = TrailingZeroThreshold.UnitModulus(input.ZerosThreshold);
        var trailingZeros = await CountWhereAsync(
            connection, input, cancellationToken,
            cmd => Predicates.TrailingZeros(cmd, zeroModulus, input.MoneyScale));

        var creators = input.RunCreatorSummary
            ? await ReadCreatorsAsync(connection, input, cancellationToken)
            : [];

        // rulePeriod 的分母不依賴 createBy mapping，故永遠取得全期 GL 行母體；
        // 相異編製人員只在 creator_summary 適用時向外使用。
        var (observedPreparerCount, totalEntries) =
            await ReadPreparerPopulationAsync(connection, input, cancellationToken);
        var totalPreparers = input.RunCreatorSummary ? observedPreparerCount : 0L;

        var (distinctAccounts, accounts) = await ReadAccountUsageAsync(connection, input, cancellationToken);

        var weekendPosting = await CountWhereAsync(
            connection, input, cancellationToken,
            _ => Predicates.Weekend("post_date", input.NonWorkingDays));
        var weekendApproval = input.RunWeekendApproval
            ? await CountWhereAsync(
                connection, input, cancellationToken,
                _ => Predicates.Weekend("approval_date", input.NonWorkingDays))
            : (HitLines: 0L, HitVouchers: 0L);

        (long HitLines, long HitVouchers) holidayPosting = (0L, 0L);
        (long HitLines, long HitVouchers) holidayApproval = (0L, 0L);
        if (input.RunHolidayPosting)
        {
            holidayPosting = await CountWhereAsync(
                connection, input, cancellationToken,
                _ => Predicates.Holiday("post_date"));
        }
        if (input.RunHolidayApproval)
        {
            holidayApproval = await CountWhereAsync(
                connection, input, cancellationToken,
                _ => Predicates.Holiday("approval_date"));
        }

        var blankDescription = await CountWhereAsync(
            connection, input, cancellationToken,
            _ => Predicates.BlankDescription());

        var backdatedPosting = await CountWhereAsync(
            connection, input, cancellationToken,
            _ => Predicates.Backdated());

        // 非授權編製人員：授權清單未匯入時不執行（計 0、handler 標 na）。
        (long HitLines, long HitVouchers) nonAuthorizedPreparer = (0L, 0L);
        if (input.RunNonAuthorizedPreparer)
        {
            nonAuthorizedPreparer = await CountWhereAsync(
                connection, input, cancellationToken,
                _ => Predicates.NonAuthorizedPreparer());
        }

        // 低頻編製者：沒有前置條件、永遠跑（固定預設門檻）。
        var lowFrequencyPreparer = await CountWhereAsync(
            connection, input, cancellationToken,
            cmd => Predicates.LowFrequencyPreparer(
                cmd, PreparerFrequency.DefaultMaxEntries, filterContext));

        // C9 低頻科目:沒有前置條件、永遠跑(固定預設門檻)。
        var lowFrequencyAccount = await CountWhereAsync(
            connection, input, cancellationToken,
            cmd => Predicates.LowFrequencyAccount(
                cmd, AccountFrequency.DefaultMaxEntries, filterContext));
        var lowFrequencyAccountCount = await CountLowFrequencyAccountsAsync(connection, filterContext, cancellationToken);

        return new PrescreenFacts(
            postPeriod.HitLines,
            suspicious.HitLines,
            unexpectedPair.HitLines,
            trailingZeros.HitLines,
            creators,
            distinctAccounts,
            accounts,
            weekendPosting.HitLines,
            weekendApproval.HitLines,
            holidayPosting.HitLines,
            holidayApproval.HitLines,
            blankDescription.HitLines,
            backdatedPosting.HitLines,
            nonAuthorizedPreparer.HitLines,
            lowFrequencyPreparer.HitLines,
            lowFrequencyAccount.HitLines,
            new Dictionary<string, long>(StringComparer.Ordinal)
            {
                [PrescreenRuleKeys.PostPeriodApproval] = postPeriod.HitVouchers,
                [PrescreenRuleKeys.SuspiciousKeywords] = suspicious.HitVouchers,
                [PrescreenRuleKeys.UnexpectedAccountPair] = unexpectedPair.HitVouchers,
                [PrescreenRuleKeys.TrailingZeros] = trailingZeros.HitVouchers,
                [PrescreenRuleKeys.WeekendPosting] = weekendPosting.HitVouchers,
                [PrescreenRuleKeys.WeekendApproval] = weekendApproval.HitVouchers,
                [PrescreenRuleKeys.HolidayPosting] = holidayPosting.HitVouchers,
                [PrescreenRuleKeys.HolidayApproval] = holidayApproval.HitVouchers,
                [PrescreenRuleKeys.BlankDescription] = blankDescription.HitVouchers,
                [PrescreenRuleKeys.BackdatedPosting] = backdatedPosting.HitVouchers,
                [PrescreenRuleKeys.NonAuthorizedPreparer] = nonAuthorizedPreparer.HitVouchers,
                [PrescreenRuleKeys.LowFrequencyPreparer] = lowFrequencyPreparer.HitVouchers,
                [PrescreenRuleKeys.LowFrequencyAccount] = lowFrequencyAccount.HitVouchers
            },
            totalPreparers,
            totalEntries)
        {
            LowFrequencyDistinctAccountCount = lowFrequencyAccountCount
        };
    }

    private static PrescreenRunResult ToCompatibilityResult(
        PrescreenRunInput input,
        PrescreenExecutionInput executionInput,
        PrescreenFacts facts) =>
        new(
            facts.PostPeriodApprovalCount,
            facts.SuspiciousKeywordsCount,
            facts.UnexpectedAccountPairCount,
            facts.TrailingZerosCount,
            executionInput.ZerosThreshold,
            facts.Creators,
            facts.DistinctAccountCount,
            facts.Accounts,
            facts.WeekendPostingCount,
            input.HasApprovalDate ? facts.WeekendApprovalCount : null,
            facts.HolidayPostingCount,
            input.HasHolidays && input.HasApprovalDate ? facts.HolidayApprovalCount : null,
            facts.BlankDescriptionCount,
            facts.BackdatedPostingCount,
            facts.NonAuthorizedPreparerCount,
            facts.LowFrequencyPreparerCount,
            facts.LowFrequencyAccountCount)
        {
            LowFrequencyDistinctAccountCount = facts.LowFrequencyDistinctAccountCount
        };

    private async Task<long> CountLowFrequencyAccountsAsync(
        DbConnection connection, FilterRuleContext context, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        var parameters = new FilterSqlParameterPlanBuilder(database.Dialect);
        var predicate = Predicates.LowFrequencyAccount(parameters, AccountFrequency.DefaultMaxEntries, context);
        parameters.Build(predicate).BindParametersTo(command);
        // 與命中列沿用同一述詞；在完整有效分錄母體計科目，不從前 50 筆摘要推算。
        command.CommandText = $"SELECT COUNT(DISTINCT g.account_code) FROM target_gl_entry g "
            + $"WHERE {GlEffectivePopulation.SqlPredicate("g")} AND ({predicate});";
        return Convert.ToInt64(await command.ExecuteScalarLoggedAsync(_log, _provider, cancellationToken));
    }

    private async Task<(long HitLines, long HitVouchers)> CountWhereAsync(
        DbConnection connection,
        PrescreenExecutionInput input,
        CancellationToken cancellationToken,
        Func<FilterSqlParameterPlanBuilder, string> predicateFactory)
    {
        await using var command = connection.CreateCommand();
        // 一般命中列只取投影已落地的有效分錄；規則自己的日期窗口仍由 predicateFactory 參數化。
        var parameters = new FilterSqlParameterPlanBuilder(database.Dialect);
        var predicate = predicateFactory(parameters);
        parameters.Build(predicate).BindParametersTo(command);
        command.CommandText =
            $"SELECT COUNT(*), COUNT(DISTINCT g.document_number) " +
            $"FROM target_gl_entry g WHERE {GlEffectivePopulation.SqlPredicate("g")} AND ({predicate});";

        await using var reader = await command.ExecuteReaderLoggedAsync(_log, _provider, cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return (0L, 0L);
        }

        return (ReadInt64(reader, 0), ReadInt64(reader, 1));
    }

    private async Task<IReadOnlyList<CreatorSummaryRow>> ReadCreatorsAsync(
        DbConnection connection, PrescreenExecutionInput input, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        // 編製者彙總與預篩選規則共用有效分錄母體。人員依去空白、不分大小寫的識別值分組（2026-10-04 裁定 C3），
        // 顯示值固定取同一組裡碼位最小的去空白寫法（例如 U1 與 u1 顯示 U1），兩個本地資料庫相同。
        var person = PersonKey(database.Dialect);
        command.CommandText =
            $"""
            SELECT MIN({person}),
                   COUNT(*),
                   COALESCE(SUM(debit_amount_scaled), 0),
                   COALESCE(SUM(credit_amount_scaled), 0),
                   COALESCE(SUM(CASE WHEN is_manual = 1 THEN 1 ELSE 0 END), 0)
            FROM target_gl_entry
            WHERE {GlEffectivePopulation.SqlPredicate()}
            GROUP BY UPPER({person})
            ORDER BY COUNT(*) DESC, MIN({person})
            LIMIT {SummaryRowLimit};
            """;
        var rows = new List<CreatorSummaryRow>();
        await using var reader = await command.ExecuteReaderLoggedAsync(_log, _provider, cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new CreatorSummaryRow(
                reader.GetString(0),
                reader.GetInt64(1),
                reader.GetInt64(2),
                reader.GetInt64(3),
                reader.GetInt64(4)));
        }

        return rows;
    }

    /// <summary>編製者的分組鍵：空白視為 ''，去掉和 .NET 相同的空白字元；呼叫端再套 UPPER 做不分大小寫分組。</summary>
    internal static string PersonKey(ISqlDialect dialect) => dialect.Trim("COALESCE(created_by, '')");

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
                   COUNT(DISTINCT UPPER({PersonKey(database.Dialect)}))
            FROM target_gl_entry
            WHERE {GlEffectivePopulation.SqlPredicate()};
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
                $"SELECT COUNT(DISTINCT account_code) FROM target_gl_entry WHERE {GlEffectivePopulation.SqlPredicate()};";
            distinct = Convert.ToInt64(await countCommand.ExecuteScalarLoggedAsync(_log, _provider, cancellationToken));
        }

        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT COALESCE(account_code, ''),
                   MAX(account_name),
                   COUNT(*),
                   COALESCE(SUM(debit_amount_scaled), 0),
                   COALESCE(SUM(credit_amount_scaled), 0)
            FROM target_gl_entry
            WHERE {GlEffectivePopulation.SqlPredicate()}
            GROUP BY account_code
            ORDER BY COUNT(*) ASC, account_code
            LIMIT {SummaryRowLimit};
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

    /// <summary>DuckDB aggregate 可能回 BigInteger；SQLite 則通常是 Int64。</summary>
    private static long ReadInt64(DbDataReader reader, int ordinal)
    {
        var value = reader.GetValue(ordinal);
        return value is BigInteger bigInteger
            ? checked((long)bigInteger)
            : Convert.ToInt64(value);
    }
}
