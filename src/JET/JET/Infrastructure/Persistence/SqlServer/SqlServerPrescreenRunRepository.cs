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
/// LIMIT 50 → TOP (50)。每條逐筆規則的述詞都從 <see cref="PrescreenRulePredicates"/> 取，和本地倉儲、第五步同一個方法。
/// 診斷日誌（dev-only）：每個 COUNT/彙總 SELECT 走 <see cref="DiagnosticDb"/> 擴充方法記錄完整 SQL/參數。
/// </summary>
public sealed class SqlServerPrescreenRunRepository(SqlServerProjectDatabase database, ILogger<SqlServerPrescreenRunRepository>? logger = null)
    : IPrescreenFactsPort
{
    private const int SummaryRowLimit = 50;
    private const string Provider = "sqlServer";

    private static readonly GlRulePredicates Predicates =
        new(SqlServerDialect.Instance, GlPopulationScopeSql.Predicate);

    private readonly ILogger _log = logger ?? NullLogger<SqlServerPrescreenRunRepository>.Instance;

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
        // 每條規則的述詞都從同一張對應表取；第五步「預篩選」條件與傳票明細也是同一個方法。
        string Rule(string key, FilterSqlParameterPlanBuilder cmd) =>
            PrescreenRulePredicates.Build(Predicates, key, cmd, filterContext, schemaPrefix);

        (long HitLines, long HitVouchers) postPeriod = (0L, 0L);
        if (input.RunPostPeriodApproval)
        {
            postPeriod = await CountWhereAsync(
                connection, projectId, input, cancellationToken,
                cmd => Rule(PrescreenRuleKeys.PostPeriodApproval, cmd));
        }

        var suspicious = await CountWhereAsync(
            connection, projectId, input, cancellationToken,
            cmd => Rule(PrescreenRuleKeys.SuspiciousKeywords, cmd));

        (long HitLines, long HitVouchers) unexpectedPair = (0L, 0L);
        if (input.RunUnexpectedAccountPair)
        {
            unexpectedPair = await CountWhereAsync(
                connection, projectId, input, cancellationToken,
                cmd => Rule(PrescreenRuleKeys.UnexpectedAccountPair, cmd));
        }

        // 尾數門檻是規則定義的一部分（固定六個 0），述詞和第五步「整數尾數符合」同一個。
        var trailingZeros = await CountWhereAsync(
            connection, projectId, input, cancellationToken,
            cmd => Rule(PrescreenRuleKeys.TrailingZeros, cmd));

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
            cmd => Rule(PrescreenRuleKeys.WeekendPosting, cmd));
        var weekendApproval = input.RunWeekendApproval
            ? await CountWhereAsync(
                connection, projectId, input, cancellationToken,
                cmd => Rule(PrescreenRuleKeys.WeekendApproval, cmd))
            : (HitLines: 0L, HitVouchers: 0L);

        (long HitLines, long HitVouchers) holidayPosting = (0L, 0L);
        (long HitLines, long HitVouchers) holidayApproval = (0L, 0L);
        if (input.RunHolidayPosting)
        {
            holidayPosting = await CountWhereAsync(
                connection, projectId, input, cancellationToken,
                cmd => Rule(PrescreenRuleKeys.HolidayPosting, cmd));
        }
        if (input.RunHolidayApproval)
        {
            holidayApproval = await CountWhereAsync(
                connection, projectId, input, cancellationToken,
                cmd => Rule(PrescreenRuleKeys.HolidayApproval, cmd));
        }

        var blankDescription = await CountWhereAsync(
            connection, projectId, input, cancellationToken,
            cmd => Rule(PrescreenRuleKeys.BlankDescription, cmd));


        // 非授權編製人員：授權清單未匯入時不執行（計 0、handler 標 na）。
        (long HitLines, long HitVouchers) nonAuthorizedPreparer = (0L, 0L);
        if (input.RunNonAuthorizedPreparer)
        {
            nonAuthorizedPreparer = await CountWhereAsync(
                connection, projectId, input, cancellationToken,
                cmd => Rule(PrescreenRuleKeys.NonAuthorizedPreparer, cmd));
        }

        // 編製分錄較少的人員與使用較少的科目：固定門檻 11 由對應表交給「科目與人員統計」同一個方法；
        // 兩條都永遠計算，收尾再依規則需求表決定要不要帶出去。
        var lowFrequencyPreparer = await CountWhereAsync(
            connection, projectId, input, cancellationToken,
            cmd => Rule(PrescreenRuleKeys.LowFrequencyPreparer, cmd));
        var lowFrequencyAccount = await CountWhereAsync(
            connection, projectId, input, cancellationToken,
            cmd => Rule(PrescreenRuleKeys.LowFrequencyAccount, cmd));
        var lowFrequencyAccountCount = await CountLowFrequencyAccountsAsync(
            connection, projectId, filterContext, schemaPrefix, cancellationToken);

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

    private async Task<long> CountLowFrequencyAccountsAsync(
        SqlConnection connection, string projectId, FilterRuleContext context, string schemaPrefix,
        CancellationToken cancellationToken)
    {
        var parameters = new FilterSqlParameterPlanBuilder(SqlServerDialect.Instance);
        var predicate = PrescreenRulePredicates.Build(Predicates, PrescreenRuleKeys.LowFrequencyAccount, parameters, context, schemaPrefix);
        // 科目和述詞同樣去頭尾空白後算一個（對應本地倉儲）。
        await using var command = database.CreateCommand(connection, projectId,
            $"SELECT COUNT_BIG(DISTINCT {SqlServerDialect.Instance.Trim("g.account_code")}) FROM {{s}}.target_gl_entry g "
            + $"WHERE {GlEffectivePopulation.SqlPredicate("g")} AND ({predicate});");
        parameters.Build(predicate).BindParametersTo(command);
        return Convert.ToInt64(await command.ExecuteScalarLoggedAsync(_log, Provider, cancellationToken));
    }

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
        // 人員依去空白、不分大小寫的識別值分組，顯示值取同組碼位最小的去空白寫法（與本地倉儲同口徑）。
        // 分組鍵的轉大寫走方言（跨資料庫一致性計畫第三節 D）。
        var person = LocalPrescreenRunRepository.PersonKey(SqlServerDialect.Instance);
        await using var command = database.CreateCommand(connection, projectId,
            $$"""
            SELECT TOP ({{SummaryRowLimit}})
                   MIN({{person}} COLLATE Latin1_General_BIN2),
                   COUNT_BIG(*),
                   COALESCE(SUM(debit_amount_scaled), 0),
                   COALESCE(SUM(credit_amount_scaled), 0),
                   COALESCE(SUM(CAST(CASE WHEN is_manual = 1 THEN 1 ELSE 0 END AS BIGINT)), 0)
            FROM {s}.target_gl_entry
            WHERE {{GlEffectivePopulation.SqlPredicate()}}
            GROUP BY {{SqlServerDialect.Instance.Upper(person)}}
            ORDER BY COUNT_BIG(*) DESC, MIN({{person}} COLLATE Latin1_General_BIN2);
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
                   COUNT_BIG(DISTINCT {{SqlServerDialect.Instance.Upper(LocalPrescreenRunRepository.PersonKey(SqlServerDialect.Instance))}})
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
