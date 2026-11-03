using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 本地 provider 的預篩選命中 keyset 分頁；述詞與 filter.preview 共用同一 builder。
/// 第四步明細與預篩選報告的明細、摘要筆數都從這裡取，列出哪些分錄由 <see cref="HitRowsPredicate"/> 決定。
/// </summary>
public sealed class LocalPrescreenPageRepository(ILocalProjectDatabase database) : IPrescreenPageRepository
{
    private readonly GlFilterWhereBuilder _whereBuilder =
        new(
            database.Dialect,
            new GlRulePredicates(database.Dialect, GlPopulationScopeSql.Predicate));

    public async Task<PageResult<PrescreenHitRow>> GetPageAsync(
        string projectId,
        string ruleKey,
        FilterConditionContext context,
        PageRequest request,
        CancellationToken cancellationToken)
    {
        await database.EnsureReadyAsync(projectId, cancellationToken);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();

        var plan = _whereBuilder.BuildPlan(
            PrescreenScenario(ruleKey),
            context,
            includePopulationParameters: false);
        plan.BindParametersTo(command);
        GlPopulationScopeSql.Plan(database.Dialect, context, "g").BindParametersTo(command);
        var paging = KeysetPaging.Plan(database.Dialect, request, ResultPageSorting.Prescreen);
        foreach (var parameter in paging.Parameters)
        {
            command.AddWithValue(parameter.Key, parameter.Value);
        }

        command.AddWithValue("@pageSize", request.ClampedPageSize + 1);
        command.CommandText =
            "SELECT g.entry_id, g.voucher_number, g.line_item, g.gl_date, g.account_number, g.account_name, " +
            "       g.amount_scaled, g.dr_cr, g.description" + paging.SelectSuffix + " " +
            "FROM gl g " +
            $"WHERE {GlExcludedPopulation.SqlPredicate("g")} AND ({HitRowsPredicate(ruleKey, plan.Sql, string.Empty)})" + paging.Predicate + " " +
            paging.OrderBy + " " + database.Dialect.LimitClause("@pageSize") + ";";

        return await ReadPageAsync(command, request, paging, database.Dialect, cancellationToken);
    }

    public async Task<PrescreenHitCounts> GetCountsAsync(
        string projectId,
        string ruleKey,
        FilterConditionContext context,
        CancellationToken cancellationToken)
    {
        await database.EnsureReadyAsync(projectId, cancellationToken);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();

        var plan = _whereBuilder.BuildPlan(
            PrescreenScenario(ruleKey),
            context,
            includePopulationParameters: false);
        plan.BindParametersTo(command);
        GlPopulationScopeSql.Plan(database.Dialect, context, "g").BindParametersTo(command);
        command.CommandText =
            "SELECT COUNT(DISTINCT g.voucher_number), COUNT(*) " +
            "FROM gl g " +
            $"WHERE {GlExcludedPopulation.SqlPredicate("g")} AND ({HitRowsPredicate(ruleKey, plan.Sql, string.Empty)});";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new PrescreenHitCounts(0, 0);
        }

        return new PrescreenHitCounts(
            Convert.ToInt64(reader.GetValue(0)),
            Convert.ToInt64(reader.GetValue(1)));
    }

    /// <summary>
    /// 第四步的卡片筆數與明細、預篩選報告的明細與摘要筆數，列出一條規則命中的哪些分錄；呼叫端外層的別名是 g，有效分錄母體由呼叫端加上。
    /// 未預期借貸組合照舊版 R3 列整張命中傳票（舊版 idea-tool.bas 第 8526 到 8538 行只依傳票號碼對上；裁定 2026-10-29-29），
    /// 和第五步只用這一個條件時的整張傳票是同一個判斷（<see cref="JetAuditProgram.UsesLegacyWholeVoucherRows"/>）：
    /// 先找直接命中的列，再列出這些傳票在同一個母體範圍裡的全部分錄。其他規則只列直接命中的列，原樣回傳規則述詞。
    /// 子查詢也用別名 g，規則述詞裡的 g 指子查詢的命中列。
    /// </summary>
    internal static string HitRowsPredicate(string ruleKey, string predicateSql, string schemaPrefix)
    {
        if (!JetAuditProgram.UsesLegacyWholeVoucherRows(PrescreenScenario(ruleKey)))
        {
            return predicateSql;
        }

        return $"g.voucher_number IN (SELECT g.voucher_number FROM {schemaPrefix}gl g "
            + $"WHERE g.voucher_number IS NOT NULL AND {GlExcludedPopulation.SqlPredicate("g")} AND ({predicateSql}))";
    }

    internal static FilterScenarioSpec PrescreenScenario(string ruleKey) => new(
        Name: string.Empty,
        Rationale: string.Empty,
        Groups:
        [
            new FilterGroupSpec(
                FilterJoin.And,
                [new FilterConditionSpec(
                    FilterJoin.And, FilterConditionType.Prescreen, ruleKey, null, [], TextMatchMode.Contains,
                    null, null, null, null, null, null)])
        ]);

    internal static async Task<PageResult<PrescreenHitRow>> ReadPageAsync(
        System.Data.Common.DbCommand command,
        PageRequest request,
        KeysetPagePlan paging,
        ISqlDialect dialect,
        CancellationToken cancellationToken)
    {
        var totalCount = await PageTotalCount.ReadKeysetAsync(command, request, paging, dialect, cancellationToken);
        var buffer = new KeysetPageBuffer<PrescreenHitRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            buffer.Add(
                new PrescreenHitRow(
                    reader.GetInt64(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.GetInt64(6),
                    reader.GetString(7),
                    reader.IsDBNull(8) ? null : reader.GetString(8)),
                paging.HasSort ? reader.GetValue(9) : null);
        }

        return buffer.ToPage(request, paging, static row => row.EntryId, totalCount);
    }
}
