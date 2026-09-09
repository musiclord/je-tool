using System.Data.Common;
using System.Globalization;
using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 命中傳票分頁：以同一份 WHERE plan 對查核期間母體逐列算 is_hit，再依傳票聚合。傳票摘要頁只列有命中列的傳票；
/// 展開分錄頁列出該傳票全部列（含參考列），並用每條規則自己的述詞標示本列符合哪些條件。兩值語意，沒有待判定。
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
        FilterScenarioSpec scenario, FilterRuleContext context, string? documentNumber, PageRequest request, CancellationToken ct)
    {
        var builder = new GlFilterWhereBuilder(dialect, new GlRulePredicates(dialect, GlPopulationScopeSql.Predicate));
        var modulus = TrailingZeroThreshold.UnitModulus(TrailingZeroThreshold.DefaultZerosThreshold);
        var plan = builder.BuildPlan(scenario, context, modulus, prefix, includeEvidence: documentNumber is not null);
        await using var command = connection.CreateCommand();
        plan.BindParametersTo(command);
        var size = request.ClampedPageSize;
        command.AddWithValue("@pageSize", size + 1);
        var evidenceColumns = string.Concat(plan.EvidencePredicates.Select((item, index) =>
            $", CASE WHEN ({item.Predicate}) THEN 1 ELSE 0 END AS evidence_{index}"));
        var cte = $"WITH evaluated AS (SELECT g.*, CASE WHEN ({plan.Sql}) THEN 1 ELSE 0 END AS is_hit"
            + (documentNumber is null ? "" : evidenceColumns) + " "
            + $"FROM {prefix}target_gl_entry g WHERE {GlPopulationScopeSql.Predicate(context, "g")}"
            + (documentNumber is null ? "" : " AND g.document_number = @document") + ") ";
        if (documentNumber is null)
        {
            // 傳票摘要：彙總包成子查詢，排序、搜尋與游標述詞才能用彙總欄（first_post_date、hit_rows、total_rows）。
            var paging = KeysetPaging.Plan(dialect, request, ResultPageSorting.FilterVoucher);
            foreach (var parameter in paging.Parameters) command.AddWithValue(parameter.Key, parameter.Value);
            command.CommandText = cte + "SELECT document_number, first_post_date, hit_rows, total_rows, voucher_total" + paging.SelectSuffix + " FROM ("
                + "SELECT document_number, MIN(post_date) AS first_post_date, CAST(SUM(CAST(is_hit AS BIGINT)) AS BIGINT) AS hit_rows, COUNT(*) AS total_rows, "
                + "CAST(COALESCE(SUM(debit_amount_scaled), 0) AS BIGINT) AS voucher_total "
                + "FROM evaluated WHERE document_number IS NOT NULL GROUP BY document_number HAVING MAX(is_hit) = 1) v "
                + "WHERE 1 = 1" + paging.Predicate + " " + paging.OrderBy + " " + dialect.LimitClause("@pageSize");
            var buffer = new KeysetPageBuffer<FilterVoucherSummary>();
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                buffer.Add(new(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1),
                    Convert.ToInt64(reader.GetValue(2)), Convert.ToInt64(reader.GetValue(3)), Convert.ToInt64(reader.GetValue(4))),
                    paging.HasSort ? reader.GetValue(5) : null);
            var page = buffer.ToPage(request, paging, static row => row.DocumentNumber);
            return new(page.Rows, [], page.NextCursor);
        }
        var afterKey = request.Cursor;
        long afterId = 0;
        if (afterKey is not null && (!long.TryParse(afterKey, NumberStyles.None, CultureInfo.InvariantCulture, out afterId) || afterId < 0))
            throw new JetActionException(JetErrorCodes.InvalidPayload, "分錄游標無效，請重新預覽。");
        command.AddWithValue("@document", documentNumber);
        command.AddWithValue("@afterId", afterId);
        command.CommandText = cte + "SELECT e.entry_id,e.document_number,e.line_item,e.post_date,e.approval_date,"
            + "e.account_code,e.account_name,e.document_description,e.amount_scaled,e.dr_cr,e.is_hit"
            + string.Concat(plan.EvidencePredicates.Select((_, index) => $",e.evidence_{index}")) + " "
            + "FROM evaluated e WHERE e.document_number = @document AND e.entry_id > @afterId "
            + "AND EXISTS (SELECT 1 FROM evaluated hit WHERE hit.document_number = e.document_number AND hit.is_hit = 1) "
            + "ORDER BY e.entry_id " + dialect.LimitClause("@pageSize");
        var details = new List<FilterVoucherDetail>();
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            const int evidenceStart = 11;
            string? Text(int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
            IReadOnlyList<FilterConditionPosition> Positions(Func<FilterRuleEvidenceSql, bool> select) =>
                plan.EvidencePredicates.Select((item, index) => (item, index))
                    .Where(pair => select(pair.item) && Convert.ToInt32(reader.GetValue(evidenceStart + pair.index)) == 1)
                    .Select(pair => pair.item.Position).ToArray();
            while (await reader.ReadAsync(ct))
                details.Add(new(Convert.ToInt64(reader.GetValue(0)), reader.GetString(1), Text(2), Text(3), Text(4),
                    Text(5), Text(6), Text(7), Convert.ToInt64(reader.GetValue(8)), reader.GetString(9),
                    Convert.ToInt32(reader.GetValue(10)) == 1)
                {
                    PrimaryConditions = Positions(item => item.Primary && !item.Voucher),
                    EvidenceConditions = Positions(item => !item.Primary && !item.Voucher),
                    VoucherConditions = Positions(item => item.Voucher)
                });
        }
        var moreDetails = details.Count > size;
        if (moreDetails) details.RemoveAt(size);
        return new([], details, moreDetails ? details[^1].EntryId.ToString(CultureInfo.InvariantCulture) : null);
    }
}
