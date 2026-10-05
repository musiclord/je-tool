using System.Data.Common;
using System.Globalization;
using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 完整性全科目表與差異表的分頁查詢，三個資料庫共用同一份 SQL。
/// <para>科目編號可能空白，而且試算表與總帳各可能有一列空白科目（空值彼此不相等，所以不會合併）。
/// 三個資料庫對空值的預設排序不同，直接用「科目編號大於上一頁最後一個」換頁，會在 DuckDB 漏掉空白科目，
/// 在 SQLite 遇到空白科目當頁尾時讀回第一頁。這裡先依固定順序給每列一個序號：空白科目在最前，
/// 試算表那一列在總帳那一列之前，其餘依科目編號；換頁只比較序號。使用者另選排序時，序號用來決定同值的先後。</para>
/// </summary>
internal static class CompletenessAccountPageQuery
{
    private const string OrderedCte = """
        ,
        ordered AS (
            SELECT account_code, account_name, tb_s, gl_s, not_in_tb,
                   ROW_NUMBER() OVER (
                       ORDER BY CASE WHEN account_code IS NULL THEN 0 ELSE 1 END, account_code, not_in_tb
                   ) AS page_row
            FROM diff
        )
        """;

    public static KeysetPagePlan Plan(ISqlDialect dialect, PageRequest request) =>
        KeysetPaging.Plan(dialect, request, ResultPageSorting.CompletenessDiff);

    /// <param name="diffCte"><see cref="ValidationProcedures.CompletenessDiffCte"/> 或其帶 schema 前綴的版本。</param>
    /// <param name="differencesOnly">差異表只列有差異的科目；全科目表列出全部。</param>
    public static string Sql(string diffCte, bool differencesOnly, KeysetPagePlan paging, ISqlDialect dialect) =>
        diffCte + OrderedCte +
        "\nSELECT account_code, account_name, tb_s, gl_s, tb_s - gl_s, not_in_tb, page_row" + paging.SelectSuffix + " " +
        "FROM ordered WHERE " + (differencesOnly ? "tb_s <> gl_s" : "1 = 1") + paging.Predicate + " " +
        paging.OrderBy + " " + dialect.LimitClause("@pageSize") + ";";

    public static void Bind(DbCommand command, KeysetPagePlan paging, PageRequest request)
    {
        foreach (var parameter in paging.Parameters)
        {
            command.AddWithValue(parameter.Key, parameter.Value);
        }

        command.AddWithValue("@pageSize", request.ClampedPageSize + 1);
    }

    public static async Task<PageResult<CompletenessDiffAccount>> ReadAsync(
        DbCommand command, KeysetPagePlan paging, PageRequest request, CancellationToken cancellationToken)
    {
        var buffer = new KeysetPageBuffer<(CompletenessDiffAccount Row, long PageRow)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            buffer.Add(
                (new CompletenessDiffAccount(
                    reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4),
                    // 本地引擎回 BIGINT，SQL Server 的常數 0/1 是 INT。
                    Convert.ToInt64(reader.GetValue(5), CultureInfo.InvariantCulture) != 0),
                 reader.GetInt64(6)),
                paging.HasSort ? reader.GetValue(7) : null);
        }

        var page = buffer.ToPage(request, paging, static item => item.PageRow);
        return new PageResult<CompletenessDiffAccount>(
            page.Rows.Select(static item => item.Row).ToArray(),
            page.NextCursor);
    }
}
