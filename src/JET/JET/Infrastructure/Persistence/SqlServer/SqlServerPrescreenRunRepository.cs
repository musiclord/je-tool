using System.Data.Common;
using JET.AuditCore;
using JET.Domain;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace JET.Infrastructure;

/// <summary>
/// 預篩選規則的 SQL Server 實作(對應 <see cref="LocalPrescreenRunRepository"/>)。
/// 述詞共用 <see cref="GlRulePredicates"/>,僅注入 <see cref="SqlServerDialect"/>;
/// 彙總用 COUNT_BIG 與 CAST(... AS BIGINT)(SQL Server COUNT/SUM(int) 回 INT,需 BIGINT 對齊 long)、
/// LIMIT 50 → TOP (50)。Production typed path 的連續零尾數門檻只消費
/// AuditCore plan 所提供的 execution input；public compatibility path 仍依
/// <see cref="TrailingZeroThreshold.DefaultZerosThreshold"/> 建立相同輸入。
/// 診斷日誌（dev-only）：每個 COUNT/彙總 SELECT 走 <see cref="DiagnosticDb"/> 擴充方法記錄完整 SQL/參數。
/// </summary>
public sealed class SqlServerPrescreenRunRepository(SqlServerProjectDatabase database, ILogger<SqlServerPrescreenRunRepository>? logger = null)
    : IPrescreenRunRepository, IPrescreenFactsPort
{
    private const int SummaryRowLimit = 50;
    private const string Provider = "sqlServer";

    private static readonly GlRulePredicates Predicates =
        new(SqlServerDialect.Instance, GlPopulationScopeSql.Predicate);

    private readonly ILogger _log = logger ?? NullLogger<SqlServerPrescreenRunRepository>.Instance;

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
        await database.EnsureCreatedAsync(projectId, cancellationToken);

        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);
        var filterContext = new FilterRuleContext(
            input.MoneyScale,
            input.LastPeriodStart,
            input.PeriodStart,
            input.PeriodEnd,
            input.NonWorkingDays,
            GlPopulationScope.AuditPeriod);

        // schema 限定詞唯一來源（集中驗證）；共用述詞片段內的專案表名前綴 [prj_xxx].。
        var schemaPrefix = SqlServerProjectSchema.QualifierFor(projectId);

        (long HitLines, long HitVouchers) postPeriod = (0L, 0L);
        if (input.RunPostPeriodApproval)
        {
            postPeriod = await CountWhereAsync(
                connection, projectId, input, cancellationToken,
                cmd => Predicates.PostPeriodApproval(cmd, input.LastPeriodStart!));
        }

        var suspicious = await CountWhereAsync(
            connection, projectId, input, cancellationToken,
            cmd => Predicates.SuspiciousKeywords(cmd));

        (long HitLines, long HitVouchers) unexpectedPair = (0L, 0L);
        if (input.RunUnexpectedAccountPair)
        {
            unexpectedPair = await CountWhereAsync(
                connection, projectId, input, cancellationToken,
                cmd => Predicates.UnexpectedAccountPair(cmd, filterContext, schemaPrefix));
        }

        var zeroModulus = TrailingZeroThreshold.UnitModulus(input.ZerosThreshold);
        var trailingZeros = await CountWhereAsync(
            connection, projectId, input, cancellationToken,
            cmd => Predicates.TrailingZeros(cmd, zeroModulus, input.MoneyScale));

        var creators = input.RunCreatorSummary
            ? await ReadCreatorsAsync(connection, projectId, input, cancellationToken)
            : [];

        // rulePeriod 的分母不依賴 createBy mapping，故永遠取得全期 GL 行母體；
        // 相異編製人員只在 creator_summary 適用時向外使用。
        var (observedPreparerCount, totalEntries) =
            await ReadPreparerPopulationAsync(connection, projectId, input, cancellationToken);
        var totalPreparers = input.RunCreatorSummary ? observedPreparerCount : 0L;

        var (distinctAccounts, accounts) = await ReadAccountUsageAsync(connection, projectId, input, cancellationToken);

        var weekendPosting = await CountWhereAsync(
            connection, projectId, input, cancellationToken,
            _ => Predicates.Weekend("post_date", input.NonWorkingDays, schemaPrefix));
        var weekendApproval = input.RunWeekendApproval
            ? await CountWhereAsync(
                connection, projectId, input, cancellationToken,
                _ => Predicates.Weekend("approval_date", input.NonWorkingDays, schemaPrefix))
            : (HitLines: 0L, HitVouchers: 0L);

        (long HitLines, long HitVouchers) holidayPosting = (0L, 0L);
        (long HitLines, long HitVouchers) holidayApproval = (0L, 0L);
        if (input.RunHolidayPosting)
        {
            holidayPosting = await CountWhereAsync(
                connection, projectId, input, cancellationToken,
                _ => Predicates.Holiday("post_date", schemaPrefix));
        }
        if (input.RunHolidayApproval)
        {
            holidayApproval = await CountWhereAsync(
                connection, projectId, input, cancellationToken,
                _ => Predicates.Holiday("approval_date", schemaPrefix));
        }

        var blankDescription = await CountWhereAsync(
            connection, projectId, input, cancellationToken,
            _ => Predicates.BlankDescription());

        var backdatedPosting = await CountWhereAsync(
            connection, projectId, input, cancellationToken,
            _ => Predicates.Backdated());

        // 非授權編製人員：授權清單未匯入時閘控跳過（計 0、handler 標 na）。
        (long HitLines, long HitVouchers) nonAuthorizedPreparer = (0L, 0L);
        if (input.RunNonAuthorizedPreparer)
        {
            nonAuthorizedPreparer = await CountWhereAsync(
                connection, projectId, input, cancellationToken,
                _ => Predicates.NonAuthorizedPreparer(schemaPrefix));
        }

        // 低頻編製者：無閘控、永遠跑（固定預設門檻）。
        var lowFrequencyPreparer = await CountWhereAsync(
            connection, projectId, input, cancellationToken,
            cmd => Predicates.LowFrequencyPreparer(
                cmd, PreparerFrequency.DefaultMaxEntries, filterContext, schemaPrefix));

        // C9 低頻科目:無閘控、永遠跑(固定預設門檻)。
        var lowFrequencyAccount = await CountWhereAsync(
            connection, projectId, input, cancellationToken,
            cmd => Predicates.LowFrequencyAccount(
                cmd, AccountFrequency.DefaultMaxEntries, filterContext, schemaPrefix));

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
            totalEntries);
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
            facts.LowFrequencyAccountCount);

    private async Task<(long HitLines, long HitVouchers)> CountWhereAsync(
        SqlConnection connection,
        string projectId,
        PrescreenExecutionInput input,
        CancellationToken cancellationToken,
        Func<FilterSqlParameterPlanBuilder, string> predicateFactory)
    {
        await using var command = connection.CreateCommand();
        // 一般命中列只取投影已落地的有效分錄；規則自己的日期窗口仍由 predicateFactory 參數化。
        var parameters = new FilterSqlParameterPlanBuilder(SqlServerDialect.Instance);
        var predicate = predicateFactory(parameters);
        parameters.Build(predicate).BindParametersTo(command);
        // {s} 由命令工廠收斂;述詞需先綁到 command,故借工廠展開 token 後回填本命令。
        await using (var expand = database.CreateCommand(connection, projectId,
            $"SELECT COUNT_BIG(*), COUNT_BIG(DISTINCT g.document_number) " +
            $"FROM {{s}}.target_gl_entry g WHERE {GlEffectivePopulation.SqlPredicate("g")} AND ({predicate});"))
        {
            command.CommandText = expand.CommandText;
        }

        await using var reader = await command.ExecuteReaderLoggedAsync(_log, Provider, cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return (0L, 0L);
        }

        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    private async Task<IReadOnlyList<CreatorSummaryRow>> ReadCreatorsAsync(
        SqlConnection connection, string projectId, PrescreenExecutionInput input, CancellationToken cancellationToken)
    {
        // 編製者彙總與預篩選規則共用有效分錄母體。
        await using var command = database.CreateCommand(connection, projectId,
            $$"""
            SELECT TOP ({{SummaryRowLimit}})
                   COALESCE(created_by, ''),
                   COUNT_BIG(*),
                   COALESCE(SUM(debit_amount_scaled), 0),
                   COALESCE(SUM(credit_amount_scaled), 0),
                   COALESCE(SUM(CAST(CASE WHEN is_manual = 1 THEN 1 ELSE 0 END AS BIGINT)), 0)
            FROM {s}.target_gl_entry
            WHERE {{GlEffectivePopulation.SqlPredicate()}}
            GROUP BY created_by
            ORDER BY COUNT_BIG(*) DESC, created_by;
            """);
        var rows = new List<CreatorSummaryRow>();
        await using var reader = await command.ExecuteReaderLoggedAsync(_log, Provider, cancellationToken);
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

    /// <summary>
    /// 編製者集中度的兩個分母，一次掃描取得（對應 <see cref="LocalPrescreenRunRepository"/>）：
    /// 期間分錄總筆數與相異編製人員總數；分組鍵與編製者彙總同為 COALESCE(created_by, '')。
    /// </summary>
    private async Task<(long TotalPreparers, long TotalEntries)> ReadPreparerPopulationAsync(
        SqlConnection connection, string projectId, PrescreenExecutionInput input, CancellationToken cancellationToken)
    {
        // 與編製者彙總同口徑：只取有效分錄。
        await using var command = database.CreateCommand(connection, projectId,
            $$"""
            SELECT COUNT_BIG(*),
                   COUNT_BIG(DISTINCT COALESCE(created_by, ''))
            FROM {s}.target_gl_entry
            WHERE {{GlEffectivePopulation.SqlPredicate()}};
            """);

        await using var reader = await command.ExecuteReaderLoggedAsync(_log, Provider, cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return (0L, 0L);
        }

        return (reader.GetInt64(1), reader.GetInt64(0));
    }

    private async Task<(long Distinct, IReadOnlyList<AccountUsageRow> Accounts)> ReadAccountUsageAsync(
        SqlConnection connection, string projectId, PrescreenExecutionInput input, CancellationToken cancellationToken)
    {
        // 罕用科目/科目使用彙總與預篩選規則共用有效分錄母體。
        long distinct;
        await using (var countCommand = database.CreateCommand(connection, projectId,
            $"SELECT COUNT_BIG(DISTINCT account_code) FROM {{s}}.target_gl_entry WHERE {GlEffectivePopulation.SqlPredicate()};"))
        {
            distinct = Convert.ToInt64(await countCommand.ExecuteScalarLoggedAsync(_log, Provider, cancellationToken));
        }

        await using var command = database.CreateCommand(connection, projectId,
            $$"""
            SELECT TOP ({{SummaryRowLimit}})
                   COALESCE(account_code, ''),
                   MAX(account_name),
                   COUNT_BIG(*),
                   COALESCE(SUM(debit_amount_scaled), 0),
                   COALESCE(SUM(credit_amount_scaled), 0)
            FROM {s}.target_gl_entry
            WHERE {{GlEffectivePopulation.SqlPredicate()}}
            GROUP BY account_code
            ORDER BY COUNT_BIG(*) ASC, account_code;
            """);
        var rows = new List<AccountUsageRow>();
        await using var reader = await command.ExecuteReaderLoggedAsync(_log, Provider, cancellationToken);
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
}
