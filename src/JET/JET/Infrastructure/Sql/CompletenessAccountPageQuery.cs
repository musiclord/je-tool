using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Runtime.CompilerServices;
using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 完整性全科目表與差異表的分頁查詢。
/// <para>科目編號可能空白，而且試算表與總帳各可能有一列空白科目（空值彼此不相等，所以不會合併）。
/// 直接用「科目編號大於上一頁最後一個」換頁，會在 SQLite 遇到空白科目當頁尾時讀回第一頁。這裡先依固定順序給每列一個序號：空白科目在最前，
/// 試算表那一列在總帳那一列之前，其餘依科目編號；換頁只比較序號。使用者另選排序時，序號用來決定同值的先後。</para>
/// <para>每一頁都要重新算出全部科目的 GL 與 TB 彙總，適合畫面一次讀一頁。正式輸出要讀完整份清單，
/// 改用 <see cref="StreamSql"/> 一個查詢讀完，彙總只算一次，讀出的科目與順序和逐頁讀完相同。</para>
/// </summary>
internal static class CompletenessAccountPageQuery
{
    private const string OrderedCte = """
        ,
        ordered AS (
            SELECT account_number, account_name, tb_s, gl_s, not_in_tb,
                   ROW_NUMBER() OVER (
                       ORDER BY CASE WHEN account_number IS NULL THEN 0 ELSE 1 END, account_number, not_in_tb
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
        "\nSELECT account_number, account_name, tb_s, gl_s, tb_s - gl_s AS difference_amount, not_in_tb, page_row" + paging.SelectSuffix + " " +
        "FROM ordered WHERE " + DifferenceFilter(differencesOnly) + paging.Predicate + " " +
        paging.OrderBy + " " + dialect.LimitClause("@pageSize") + ";";

    /// <summary>
    /// 正式輸出讀完整份清單的查詢。科目彙總、固定順序的序號與差異條件都和 <see cref="Sql"/> 相同，
    /// 不分頁也不搜尋，依序號從頭讀到尾，結果和沒有指定排序與搜尋時逐頁讀完相同。
    /// </summary>
    /// <param name="diffCte"><see cref="ValidationProcedures.CompletenessDiffCte"/> 或其帶 schema 前綴的版本。</param>
    /// <param name="differencesOnly">差異表只列有差異的科目；全科目表列出全部。</param>
    public static string StreamSql(string diffCte, bool differencesOnly) =>
        diffCte + OrderedCte +
        "\nSELECT account_number, account_name, tb_s, gl_s, tb_s - gl_s AS difference_amount, not_in_tb " +
        "FROM ordered WHERE " + DifferenceFilter(differencesOnly) + " ORDER BY page_row;";

    public static void Bind(DbCommand command, KeysetPagePlan paging, PageRequest request)
    {
        foreach (var parameter in paging.Parameters)
        {
            command.AddWithValue(parameter.Key, parameter.Value);
        }

        command.AddWithValue("@pageSize", request.ClampedPageSize + 1);
    }

    public static async Task<PageResult<CompletenessDiffAccount>> ReadAsync(
        DbCommand command, KeysetPagePlan paging, PageRequest request, CancellationToken cancellationToken, ISqlDialect dialect, string diffCte)
    {
        var totalCount = await PageTotalCount.ReadKeysetAsync(command, request, paging, dialect, cancellationToken, diffCte + OrderedCte);
        var buffer = new KeysetPageBuffer<(CompletenessDiffAccount Row, long PageRow)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            buffer.Add(
                (ReadAccount(reader), reader.GetInt64(6)),
                paging.HasSort ? reader.GetValue(7) : null);
        }

        var page = buffer.ToPage(request, paging, static item => item.PageRow);
        return new PageResult<CompletenessDiffAccount>(
            page.Rows.Select(static item => item.Row).ToArray(),
            page.NextCursor, totalCount);
    }

    /// <summary>執行 <see cref="StreamSql"/> 組出的命令，由只能往前讀的 reader 逐列交出科目。</summary>
    public static async IAsyncEnumerable<CompletenessDiffAccount> StreamAsync(
        DbCommand command,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        command.CommandTimeout = 0;
        await using var reader = await command.ExecuteReaderAsync(
            CommandBehavior.SequentialAccess | CommandBehavior.SingleResult,
            cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return ReadAccount(reader);
        }
    }

    private static string DifferenceFilter(bool differencesOnly) => differencesOnly ? ValidationProcedures.CompletenessDifferencePredicate : "1 = 1";

    // 前六欄依序是科目編號、名稱、TB 彙總、GL 彙總、差異，以及是否 GL 有而 TB 無；分頁與整份讀完共用。
    private static CompletenessDiffAccount ReadAccount(DbDataReader reader) =>
        new(
            reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4),
            Convert.ToInt64(reader.GetValue(5), CultureInfo.InvariantCulture) != 0);
}
