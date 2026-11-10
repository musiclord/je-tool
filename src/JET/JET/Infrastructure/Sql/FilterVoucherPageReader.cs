using System.Data.Common;
using System.Globalization;
using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 命中傳票分頁。草稿以同一份 WHERE plan 對查核期間母體逐列算 is_hit，再依傳票聚合；已儲存的情境改讀儲存時存下的
/// filter_hit 與 filter_hit_condition，不再判斷條件，傳票的分錄數、總額與日期仍讀 GL。兩條路的聚合、排序、搜尋與
/// 分頁是同一段 SQL，畫面看到的列才會相同。傳票摘要頁只列有命中列的傳票；展開分錄頁列出該傳票全部列（含參考列），
/// 並標示本列符合哪些條件。兩值語意，沒有待判定。
/// </summary>
internal static class FilterVoucherPageReader
{
    internal static async Task<string> ReadRevisionAsync(DbConnection connection, string prefix, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        var key = prefix.Length == 0 ? "key" : "[key]";
        command.CommandText = $"SELECT value FROM {prefix}schema_info WHERE {key} = 'filter_data_revision'";
        return Convert.ToString(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) ?? "0";
    }

    internal static async Task<FilterVoucherPage> ReadAsync(DbConnection connection, ISqlDialect dialect, string prefix,
        FilterScenarioSpec scenario, FilterConditionContext context, string? voucherNumber, PageRequest request,
        bool countWholeVoucherRows, CancellationToken ct)
    {
        var builder = new GlFilterWhereBuilder(dialect, new GlRulePredicates(dialect, GlPopulationScopeSql.Predicate));
        var plan = builder.BuildPlan(scenario, context, prefix, includeEvidence: voucherNumber is not null);
        await using var command = connection.CreateCommand();
        plan.BindParametersTo(command);
        var evidenceColumns = string.Concat(plan.EvidencePredicates.Select((item, index) =>
            $", CASE WHEN ({item.Predicate}) THEN 1 ELSE 0 END AS evidence_{index}"));
        var cte = $"WITH evaluated AS (SELECT g.*, CASE WHEN ({plan.Sql}) THEN 1 ELSE 0 END AS is_hit"
            + (voucherNumber is null ? "" : evidenceColumns) + " "
            + $"FROM {prefix}gl g WHERE {GlPopulationScopeSql.Predicate(context, "g")}"
            + (voucherNumber is null ? "" : " AND g.voucher_number = @document") + ") ";
        if (voucherNumber is null)
        {
            return await ReadVouchersAsync(command, dialect, cte, request, countWholeVoucherRows, ct);
        }

        var details = await ReadDetailsAsync(command, dialect, cte,
            string.Concat(plan.EvidencePredicates.Select((_, index) => $",e.evidence_{index}")), voucherNumber, request,
            reader =>
            {
                const int evidenceStart = 11;
                IReadOnlyList<FilterConditionPosition> Positions(Func<FilterConditionEvidenceSql, bool> select) =>
                    plan.EvidencePredicates.Select((item, index) => (item, index))
                        .Where(pair => select(pair.item) && Convert.ToInt32(reader.GetValue(evidenceStart + pair.index)) == 1)
                        .Select(pair => pair.item.Position).ToArray();
                return (Positions(item => !item.Voucher), Positions(item => item.Voucher));
            }, ct);
        return details;
    }

    /// <summary>
    /// 已儲存情境的傳票頁：命中讀 filter_hit，符合第幾組第幾條讀 filter_hit_condition，都以情境編號
    /// <paramref name="scenarioId"/> 取出；只看命中傳票裡的分錄，聚合與分頁和草稿相同。
    /// </summary>
    internal static async Task<FilterVoucherPage> ReadSavedAsync(DbConnection connection, ISqlDialect dialect, string prefix,
        int scenarioId, FilterScenarioSpec scenario, FilterConditionContext context, string? voucherNumber, PageRequest request,
        bool countWholeVoucherRows, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.AddWithValue("@scenarioId", scenarioId);
        var cte = "WITH evaluated AS (SELECT g.*, CASE WHEN h.entry_id IS NULL THEN 0 ELSE 1 END AS is_hit "
            + $"FROM {prefix}gl g LEFT JOIN {prefix}filter_hit h ON h.scenario_id = @scenarioId AND h.entry_id = g.entry_id "
            + $"WHERE {GlPopulationScopeSql.Predicate(context, "g")} AND g.voucher_number IN ("
            + $"SELECT hg.voucher_number FROM {prefix}filter_hit hh JOIN {prefix}gl hg ON hg.entry_id = hh.entry_id "
            + $"WHERE hh.scenario_id = @scenarioId AND hg.voucher_number IS NOT NULL AND {GlPopulationScopeSql.Predicate(context, "hg")})"
            + (voucherNumber is null ? "" : " AND g.voucher_number = @document") + ") ";
        if (voucherNumber is null)
        {
            return await ReadVouchersAsync(command, dialect, cte, request, countWholeVoucherRows, ct);
        }

        var page = await ReadDetailsAsync(command, dialect, cte, string.Empty, voucherNumber, request,
            static _ => ((IReadOnlyList<FilterConditionPosition>)[], (IReadOnlyList<FilterConditionPosition>)[]), ct);
        if (page.Details.Count == 0)
        {
            return page;
        }

        var matched = await ReadStoredConditionsAsync(connection, prefix, scenarioId, page.Details, ct);
        if (matched.Values.SelectMany(positions => positions).Any(position =>
            position.Group < 1 || position.Group > scenario.Groups.Count
            || position.Rule < 1 || position.Rule > scenario.Groups[position.Group - 1].Conditions.Count))
        {
            throw new JetActionException(JetErrorCodes.StaleResult, "資料或條件已改變，請重新預覽再查看傳票。");
        }
        bool IsVoucherCondition(FilterConditionPosition position) =>
            position.Group >= 1 && position.Group <= scenario.Groups.Count
            && position.Rule >= 1
            && position.Rule <= scenario.Groups[position.Group - 1].Conditions.Count
            && scenario.Groups[position.Group - 1].Conditions[position.Rule - 1].IsVoucherCondition;
        var details = page.Details.Select(row =>
        {
            var positions = matched.GetValueOrDefault(row.EntryId) ?? [];
            return row with
            {
                PrimaryConditions = positions.Where(position => !IsVoucherCondition(position)).ToArray(),
                VoucherConditions = positions.Where(IsVoucherCondition).ToArray()
            };
        }).ToArray();
        return page with { Details = details };
    }

    private static async Task<FilterVoucherPage> ReadVouchersAsync(DbCommand command, ISqlDialect dialect, string cte,
        PageRequest request, bool countWholeVoucherRows, CancellationToken ct)
    {
        // 傳票摘要：彙總包成子查詢，排序、搜尋與游標述詞才能用彙總欄（first_gl_date、hit_rows、total_rows）。
        // 整張傳票計數的情境，符合分錄數就是整張傳票的分錄數，排序與游標也用這個值。
        var size = request.ClampedPageSize;
        command.AddWithValue("@pageSize", size + 1);
        var paging = KeysetPaging.Plan(dialect, request, ResultPageSorting.FilterVoucher);
        foreach (var parameter in paging.Parameters) command.AddWithValue(parameter.Key, parameter.Value);
        var hitRows = countWholeVoucherRows ? "COUNT(*)" : "CAST(SUM(CAST(is_hit AS BIGINT)) AS BIGINT)";
        command.CommandText = cte + "SELECT voucher_number, first_gl_date, hit_rows, total_rows, voucher_total" + paging.SelectSuffix + " FROM ("
            + "SELECT voucher_number, MIN(gl_date) AS first_gl_date, " + hitRows + " AS hit_rows, COUNT(*) AS total_rows, "
            + "CAST(COALESCE(SUM(debit_amount_scaled), 0) AS BIGINT) AS voucher_total "
            + "FROM evaluated WHERE voucher_number IS NOT NULL GROUP BY voucher_number HAVING MAX(is_hit) = 1) v "
            + "WHERE 1 = 1" + paging.Predicate + " " + paging.OrderBy + " " + dialect.LimitClause("@pageSize");
        var totalCount = await PageTotalCount.ReadKeysetAsync(command, request, paging, dialect, ct, cte);
        var buffer = new KeysetPageBuffer<FilterVoucherSummary>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            buffer.Add(new(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1),
                Convert.ToInt64(reader.GetValue(2)), Convert.ToInt64(reader.GetValue(3)), Convert.ToInt64(reader.GetValue(4))),
                paging.HasSort ? reader.GetValue(5) : null);
        var page = buffer.ToPage(request, paging, static row => row.VoucherNumber);
        return new(page.Rows, [], page.NextCursor, totalCount);
    }

    private static async Task<FilterVoucherPage> ReadDetailsAsync(DbCommand command, ISqlDialect dialect, string cte,
        string extraColumns, string voucherNumber, PageRequest request,
        Func<DbDataReader, (IReadOnlyList<FilterConditionPosition> Primary, IReadOnlyList<FilterConditionPosition> Voucher)> conditions,
        CancellationToken ct)
    {
        var size = request.ClampedPageSize;
        command.AddWithValue("@pageSize", size + 1);
        var afterKey = request.Cursor;
        long afterId = 0;
        if (afterKey is not null && (!long.TryParse(afterKey, NumberStyles.None, CultureInfo.InvariantCulture, out afterId) || afterId < 0))
            throw new JetActionException(JetErrorCodes.InvalidPayload, "無法接續上一頁，請重新預覽。");
        command.AddWithValue("@document", voucherNumber);
        command.AddWithValue("@afterId", afterId);
        command.CommandText = cte + "SELECT e.entry_id,e.voucher_number,e.line_item,e.gl_date,e.approval_date,"
            + "e.account_number,e.account_name,e.description,e.amount_scaled,e.dr_cr,e.is_hit"
            + extraColumns + " "
            + "FROM evaluated e WHERE e.voucher_number = @document AND e.entry_id > @afterId "
            + "AND EXISTS (SELECT 1 FROM evaluated hit WHERE hit.voucher_number = e.voucher_number AND hit.is_hit = 1) "
            + "ORDER BY e.entry_id " + dialect.LimitClause("@pageSize");
        var detailCount = await PageTotalCount.ReadAsync(command, request, cte
            + "SELECT COUNT(*) "
            + "FROM evaluated e WHERE e.voucher_number = @document "
            + "AND EXISTS (SELECT 1 FROM evaluated hit WHERE hit.voucher_number = e.voucher_number AND hit.is_hit = 1)", ct);
        var details = new List<FilterVoucherDetail>();
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            string? Text(int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
            while (await reader.ReadAsync(ct))
            {
                var (primary, voucher) = conditions(reader);
                details.Add(new(Convert.ToInt64(reader.GetValue(0)), reader.GetString(1), Text(2), Text(3), Text(4),
                    Text(5), Text(6), Text(7), Convert.ToInt64(reader.GetValue(8)), reader.GetString(9),
                    Convert.ToInt32(reader.GetValue(10)) == 1)
                {
                    PrimaryConditions = primary,
                    VoucherConditions = voucher
                });
            }
        }
        var moreDetails = details.Count > size;
        if (moreDetails) details.RemoveAt(size);
        return new([], details, moreDetails ? details[^1].EntryId.ToString(CultureInfo.InvariantCulture) : null, detailCount);
    }

    /// <summary>這一頁分錄存下的「符合第幾組第幾條」，依組號、條號排序，和草稿逐條判斷的順序相同。</summary>
    private static async Task<Dictionary<long, List<FilterConditionPosition>>> ReadStoredConditionsAsync(
        DbConnection connection, string prefix, int scenarioId, IReadOnlyList<FilterVoucherDetail> rows, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.AddWithValue("@scenarioId", scenarioId);
        var names = new string[rows.Count];
        for (var index = 0; index < rows.Count; index++)
        {
            names[index] = "@e" + index.ToString(CultureInfo.InvariantCulture);
            command.AddWithValue(names[index], rows[index].EntryId);
        }

        command.CommandText = "SELECT c.entry_id, c.group_no, c.condition_no "
            + $"FROM {prefix}filter_hit_condition c WHERE c.scenario_id = @scenarioId "
            + $"AND c.entry_id IN ({string.Join(", ", names)}) "
            + "ORDER BY c.entry_id, c.group_no, c.condition_no";
        var matched = new Dictionary<long, List<FilterConditionPosition>>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var entryId = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);
            if (!matched.TryGetValue(entryId, out var list)) matched[entryId] = list = [];
            list.Add(LocalEngineErrors.ReadStoredValue(() => new FilterConditionPosition(
                Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture),
                Convert.ToInt32(reader.GetValue(2), CultureInfo.InvariantCulture))));
        }

        return matched;
    }
}
