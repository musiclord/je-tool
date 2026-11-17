using System.Data.Common;
using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 高風險條件彙總傳票層的分頁讀取，一頁用兩個查詢。
///
/// 第一個查詢讀這一頁的命中傳票。傳票裡只要有一筆分錄在所選情境的 filter_hit 裡就算命中；
/// voucher_number 是 NULL 的分錄不算。命中傳票的號碼和分錄層共用同一個子查詢，整個查詢只取一次，
/// 不讓每一筆分錄各自回頭找同一張傳票的命中分錄，大傳票的工作量才不會隨分錄數平方成長。
/// 命中的傳票依 voucher_number 分組，用它在 GL 的全部分錄算出
/// MIN(gl_date)、MIN(created_by) 與 COALESCE(SUM(debit_amount_scaled), 0)。排序與游標由
/// <c>KeysetPaging.Plan</c> 依 <c>ResultPageSorting.HighRiskSummaryVoucher</c> 組出，沒有指定排序時依 voucher_number 升冪。
/// 換頁用上一頁最後一筆的排序鍵接下一頁。每次多讀一筆，多出來的那一筆存在才回傳下一頁的游標。
/// 每列的 voucher_number 也用來編游標。
///
/// 第二個查詢讀這一頁每張傳票命中了哪些情境位置：取不重複的 voucher_number 與 filter_scenario.position，
/// 排除 voucher_number 是 NULL 的列，只看這一頁的傳票號碼，所以每張傳票的命中位置不多也不少。
/// 這一頁沒有傳票時不執行第二個查詢，回傳空的對照表。
/// </summary>
internal static class HighRiskSummaryVoucherPageReader
{
    private static string PageSqlHead(
        string schemaPrefix,
        string scenarioPredicate,
        string populationPredicate,
        string hitPopulationPredicate) =>
        "SELECT g.voucher_number AS voucher_number, MIN(g.gl_date) AS first_gl_date, MIN(g.created_by) AS first_created_by, " +
        "       COALESCE(SUM(g.debit_amount_scaled), 0) AS voucher_total " +
        $"FROM {schemaPrefix}gl g " +
        $"WHERE {populationPredicate} AND g.voucher_number IS NOT NULL " +
        $"  AND {HighRiskSummaryRowPageReader.HitVoucherPredicate(schemaPrefix, scenarioPredicate, hitPopulationPredicate)}";

    private const string PageSqlTail =
        "GROUP BY g.voucher_number";

    public static async Task<(PageResult<VoucherTagRow> Page, IReadOnlyDictionary<string, IReadOnlyList<int>> PositionsByDoc)> ReadAsync(
        DbConnection connection, ISqlDialect dialect, GlPopulationContext context, PageRequest request,
        IReadOnlyList<int>? scenarioPositions, CancellationToken cancellationToken,
        string schemaPrefix = "")
    {
        var scenarioScope = HighRiskSummaryScenarioSqlScope.Create(scenarioPositions);
        if (scenarioScope.IsEmpty)
        {
            return (
                new PageResult<VoucherTagRow>([], null, request.IncludeTotalCount ? 0 : null),
                new Dictionary<string, IReadOnlyList<int>>());
        }

        var paging = KeysetPaging.Plan(dialect, request, ResultPageSorting.HighRiskSummaryVoucher);

        // 第一個查詢：這一頁的命中傳票與各傳票的彙總值。彙總包成子查詢，排序、搜尋與游標的條件才能用彙總欄。
        long? totalCount = null;
        var buffer = new KeysetPageBuffer<VoucherTagRow>();
        await using (var command = connection.CreateCommand())
        {
            GlPopulationScopeSql.Plan(dialect, context, "g").BindParametersTo(command);
            command.CommandText =
                "SELECT voucher_number, first_gl_date, first_created_by, voucher_total" + paging.SelectSuffix + " FROM (" +
                PageSqlHead(
                    schemaPrefix,
                    scenarioScope.Predicate(),
                    GlPopulationScopeSql.Predicate(context, "g"),
                    GlPopulationScopeSql.Predicate(context, "g2")) + PageSqlTail +
                ") v WHERE 1 = 1" + paging.Predicate + " " + paging.OrderBy + " " +
                dialect.LimitClause("@pageSize") + ";";
            scenarioScope.AddParameters(command);
            foreach (var parameter in paging.Parameters)
            {
                command.Parameters.Add(Param(command, parameter.Key, parameter.Value));
            }

            command.Parameters.Add(Param(command, "@pageSize", request.ClampedPageSize + 1));

            totalCount = await PageTotalCount.ReadKeysetAsync(command, request, paging, dialect, cancellationToken);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                buffer.Add(
                    new VoucherTagRow(
                        reader.GetString(0),
                        reader.IsDBNull(1) ? null : reader.GetString(1),
                        reader.IsDBNull(2) ? null : reader.GetString(2),
                        reader.GetInt64(3)),
                    paging.HasSort ? reader.GetValue(4) : null);
            }
        }

        var page = buffer.ToPage(request, paging, static row => row.VoucherNumber ?? string.Empty);
        var rows = page.Rows;
        var next = page.NextCursor;

        // 這一頁沒有傳票，就沒有命中位置可讀，不執行第二個查詢。
        if (rows.Count == 0)
        {
            return (
                new PageResult<VoucherTagRow>(rows, next, totalCount),
                new Dictionary<string, IReadOnlyList<int>>());
        }

        // 第二個查詢：這一頁每張傳票命中的情境位置。依欄位排序時，這一頁的傳票號碼不是連續的範圍，
        // 所以把這一頁的傳票號碼全部當參數放進 IN 清單。一頁最多 500 張，低於 SQLite 的參數上限。
        var positions = new Dictionary<string, List<int>>();
        await using (var command = connection.CreateCommand())
        {
            GlPopulationScopeSql.Plan(dialect, context, "g").BindParametersTo(command);
            var docParameters = rows.Select((row, index) => $"@d{index}").ToArray();
            command.CommandText =
                "SELECT DISTINCT g.voucher_number, fs.position " +
                $"FROM {schemaPrefix}filter_hit r JOIN {schemaPrefix}filter_scenario fs ON fs.scenario_id = r.scenario_id " +
                $"JOIN {schemaPrefix}gl g ON g.entry_id = r.entry_id " +
                $"WHERE g.voucher_number IS NOT NULL AND {GlPopulationScopeSql.Predicate(context, "g")} " +
                scenarioScope.Predicate() +
                $"AND g.voucher_number IN ({string.Join(", ", docParameters)}) " +
                "ORDER BY g.voucher_number, fs.position;";
            scenarioScope.AddParameters(command);
            for (var index = 0; index < rows.Count; index++)
            {
                command.Parameters.Add(Param(command, docParameters[index], rows[index].VoucherNumber ?? string.Empty));
            }

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var doc = reader.GetString(0);
                var pos = reader.GetInt32(1);
                if (!positions.TryGetValue(doc, out var list))
                {
                    list = [];
                    positions[doc] = list;
                }

                list.Add(pos); // 查詢用了 DISTINCT 與 ORDER BY，讀到的位置已經排好序而且不重複。
            }
        }

        var positionsByDoc = positions.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyList<int>)kv.Value);

        return (new PageResult<VoucherTagRow>(rows, next, totalCount), positionsByDoc);
    }

    private static DbParameter Param(DbCommand command, string name, object value)
    {
        var p = command.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        return p;
    }
}
