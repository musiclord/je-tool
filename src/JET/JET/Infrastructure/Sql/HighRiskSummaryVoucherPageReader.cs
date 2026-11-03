using System.Data.Common;
using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 高風險條件彙總傳票層分頁的共用兩段查詢讀取器(provider 中立)。三 provider 的分頁差異由
/// 呼叫端帶入 <see cref="ISqlDialect"/> 的 LimitClause；
/// 其餘 SQL 純 ANSI,故讀取邏輯共用一處，避免兩個 repository 逐字重複。
///
/// 查詢 1(命中傳票 keyset 頁):GROUP BY voucher_number、以所選 filter_hit 判定
/// 命中傳票，再對該傳票的完整 GL 行聚合；排除 NULL voucher_number、聚合
/// MIN(gl_date)/MIN(created_by)/COALESCE(SUM(debit_amount_scaled),0)、
/// 游標展開布林式 `voucher_number > @cursor`、ORDER BY voucher_number、LimitClause。reader 末欄取
/// voucher_number 供編游標。查 pageSize+1 作 lookahead，只有 probe 存在才回本頁末鍵 cursor。
///
/// 查詢 2(本頁傳票命中位置,鍵範圍 (@lo, @hi]):DISTINCT (voucher_number, filter_scenario.position)、
/// 排除 NULL voucher_number、`voucher_number <= @hi`(@hi=本頁末鍵),非首頁再加 `voucher_number > @lo`
/// (@lo=本頁游標)。鍵範圍與查詢 1 完全一致,故每傳票位置不漏不溢。空頁→不跑查詢 2,回空 dict。
/// </summary>
internal static class HighRiskSummaryVoucherPageReader
{
    // schemaPrefix 預設 ""（SQLite 裸名，逐字不變）；SQL Server 傳 QualifierFor(projectId) 前綴專案表名。
    private static string PageSqlHead(
        string schemaPrefix,
        string scenarioPredicate,
        string populationPredicate,
        string hitPopulationPredicate) =>
        "SELECT g.voucher_number AS voucher_number, MIN(g.gl_date) AS first_gl_date, MIN(g.created_by) AS first_created_by, " +
        "       COALESCE(SUM(g.debit_amount_scaled), 0) AS voucher_total " +
        $"FROM {schemaPrefix}gl g " +
        $"WHERE {populationPredicate} AND g.voucher_number IS NOT NULL " +
        "  AND EXISTS (" +
        $"      SELECT 1 FROM {schemaPrefix}filter_hit r JOIN {schemaPrefix}filter_scenario fs ON fs.scenario_id = r.scenario_id " +
        $"      JOIN {schemaPrefix}gl hit ON hit.entry_id = r.entry_id " +
        "      WHERE hit.voucher_number = g.voucher_number " +
        $"        AND {hitPopulationPredicate} " +
        scenarioPredicate +
        "  ) ";

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

        // 查詢 1:本頁命中傳票 + 聚合。彙總包成子查詢，排序、搜尋與游標述詞才能用彙總欄。
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
                    GlPopulationScopeSql.Predicate(context, "hit")) + PageSqlTail +
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

        // 空頁:無傳票 → 無位置。不跑查詢 2。
        if (rows.Count == 0)
        {
            return (
                new PageResult<VoucherTagRow>(rows, next, totalCount),
                new Dictionary<string, IReadOnlyList<int>>());
        }

        // 查詢 2:本頁傳票命中位置。排序後本頁不再是連續鍵範圍，改把本頁的傳票號碼全部當參數帶進 IN 清單
        // （一頁最多 500 張，低於各引擎的參數上限）。
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

                list.Add(pos); // ORDER BY + DISTINCT → 已有序去重
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
