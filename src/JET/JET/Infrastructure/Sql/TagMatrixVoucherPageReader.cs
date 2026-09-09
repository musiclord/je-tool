using System.Data.Common;
using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// tag 矩陣傳票層分頁的共用兩段查詢讀取器(provider 中立)。三 provider 的分頁差異由
/// 呼叫端帶入 <see cref="ISqlDialect"/> 的 LimitClause；
/// 其餘 SQL 純 ANSI,故讀取邏輯共用一處(Linus 好品味:消除兩 repo 的逐字重複)。
///
/// 查詢 1(命中傳票 keyset 頁):GROUP BY document_number、以所選 result_filter_run 判定
/// 命中傳票，再對該傳票的完整 GL 行聚合；排除 NULL document_number、聚合
/// MIN(post_date)/MIN(created_by)/COALESCE(SUM(debit_amount_scaled),0)、
/// 游標展開布林式 `document_number > @cursor`、ORDER BY document_number、LimitClause。reader 末欄取
/// document_number 供編游標。查 pageSize+1 作 lookahead，只有 probe 存在才回本頁末鍵 cursor。
///
/// 查詢 2(本頁傳票命中位置,鍵範圍 (@lo, @hi]):DISTINCT (document_number, scenario_position)、
/// 排除 NULL document_number、`document_number <= @hi`(@hi=本頁末鍵),非首頁再加 `document_number > @lo`
/// (@lo=本頁游標)。鍵範圍與查詢 1 完全一致,故每傳票位置不漏不溢。空頁→不跑查詢 2,回空 dict。
/// </summary>
internal static class TagMatrixVoucherPageReader
{
    // schemaPrefix 預設 ""（SQLite 裸名，逐字不變）；SQL Server 傳 QualifierFor(projectId) 前綴專案表名。
    private static string PageSqlHead(
        string schemaPrefix,
        string scenarioPredicate,
        string populationPredicate,
        string hitPopulationPredicate) =>
        "SELECT g.document_number AS document_number, MIN(g.post_date) AS first_post_date, MIN(g.created_by) AS first_created_by, " +
        "       COALESCE(SUM(g.debit_amount_scaled), 0) AS voucher_total " +
        $"FROM {schemaPrefix}target_gl_entry g " +
        $"WHERE {populationPredicate} AND g.document_number IS NOT NULL " +
        "  AND EXISTS (" +
        $"      SELECT 1 FROM {schemaPrefix}result_filter_run r " +
        $"      JOIN {schemaPrefix}target_gl_entry hit ON hit.entry_id = r.entry_id " +
        "      WHERE hit.document_number = g.document_number " +
        $"        AND {hitPopulationPredicate} " +
        scenarioPredicate +
        "  ) ";

    private const string PageSqlTail =
        "GROUP BY g.document_number";

    public static async Task<(PageResult<VoucherTagRow> Page, IReadOnlyDictionary<string, IReadOnlyList<int>> PositionsByDoc)> ReadAsync(
        DbConnection connection, ISqlDialect dialect, GlPopulationContext context, PageRequest request,
        IReadOnlyList<int>? scenarioPositions, CancellationToken cancellationToken,
        string schemaPrefix = "")
    {
        var scenarioScope = TagMatrixScenarioSqlScope.Create(scenarioPositions);
        if (scenarioScope.IsEmpty)
        {
            return (
                new PageResult<VoucherTagRow>([], null),
                new Dictionary<string, IReadOnlyList<int>>());
        }

        var paging = KeysetPaging.Plan(dialect, request, ResultPageSorting.TagMatrixVoucher);

        // 查詢 1:本頁命中傳票 + 聚合。彙總包成子查詢，排序、搜尋與游標述詞才能用彙總欄。
        var buffer = new KeysetPageBuffer<VoucherTagRow>();
        await using (var command = connection.CreateCommand())
        {
            GlPopulationScopeSql.Plan(dialect, context, "g").BindParametersTo(command);
            command.CommandText =
                "SELECT document_number, first_post_date, first_created_by, voucher_total" + paging.SelectSuffix + " FROM (" +
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

        var page = buffer.ToPage(request, paging, static row => row.DocumentNumber ?? string.Empty);
        var rows = page.Rows;
        var next = page.NextCursor;

        // 空頁:無傳票 → 無位置。不跑查詢 2。
        if (rows.Count == 0)
        {
            return (
                new PageResult<VoucherTagRow>(rows, next),
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
                "SELECT DISTINCT g.document_number, r.scenario_position " +
                $"FROM {schemaPrefix}result_filter_run r " +
                $"JOIN {schemaPrefix}target_gl_entry g ON g.entry_id = r.entry_id " +
                $"WHERE g.document_number IS NOT NULL AND {GlPopulationScopeSql.Predicate(context, "g")} " +
                scenarioScope.Predicate() +
                $"AND g.document_number IN ({string.Join(", ", docParameters)}) " +
                "ORDER BY g.document_number, r.scenario_position;";
            scenarioScope.AddParameters(command);
            for (var index = 0; index < rows.Count; index++)
            {
                command.Parameters.Add(Param(command, docParameters[index], rows[index].DocumentNumber ?? string.Empty));
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

        return (new PageResult<VoucherTagRow>(rows, next), positionsByDoc);
    }

    private static DbParameter Param(DbCommand command, string name, object value)
    {
        var p = command.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        return p;
    }
}
