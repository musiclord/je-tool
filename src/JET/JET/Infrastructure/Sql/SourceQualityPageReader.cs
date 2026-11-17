using System.Data.Common;
using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

internal static class SourceQualityPageReader
{
    internal static async Task<PageResult<SourceQualityFindingRow>> ReadAsync(
        DbConnection connection, DbTransaction? transaction, ISqlDialect dialect,
        string schemaPrefix, PageRequest request, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var paging = KeysetPaging.Plan(dialect, request, ResultPageSorting.SourceQuality);
        foreach (var parameter in paging.Parameters) command.AddWithValue(parameter.Key, parameter.Value);
        command.AddWithValue("@pageSize", request.ClampedPageSize + 1);
        command.CommandText =
            SelectColumns + paging.SelectSuffix + " "
            + FromAndWhere(schemaPrefix) + paging.Predicate + " "
            + paging.OrderBy + " " + dialect.LimitClause("@pageSize") + ";";
        var totalCount = await PageTotalCount.ReadKeysetAsync(command, request, paging, dialect, cancellationToken);
        var buffer = new KeysetPageBuffer<SourceQualityFindingRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            buffer.Add(ReadFinding(reader), paging.HasSort ? reader.GetValue(8) : null);
        }
        return buffer.ToPage(request, paging, static row => row.EntryId, totalCount);
    }

    /// <summary>
    /// 資料驗證報告讀完全部明細：欄位、來源與條件都和 <see cref="ReadAsync"/> 相同，不分頁也不搜尋，
    /// 依分頁查詢沒有指定排序時的穩定鍵從頭讀到尾，所以讀出的分錄與順序和逐頁讀完相同。
    /// </summary>
    internal static async IAsyncEnumerable<SourceQualityFindingRow> StreamAsync(
        DbConnection connection,
        string schemaPrefix,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            SelectColumns + " "
            + FromAndWhere(schemaPrefix) + " "
            + $"ORDER BY {ResultPageSorting.SourceQuality.StableKey.Sql};";
        command.CommandTimeout = 0;
        await using var reader = await command.ExecuteReaderAsync(
            System.Data.CommandBehavior.SingleResult,
            cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return ReadFinding(reader);
        }
    }

    private const string SelectColumns =
        "SELECT s.source_row_number, bs.source_file_name, bs.sheet_name, "
        + "g.voucher_number, g.account_number, g.gl_date, g.description, g.entry_id";

    private static string FromAndWhere(string schemaPrefix) =>
        $"FROM {schemaPrefix}gl g "
        + $"JOIN {schemaPrefix}pbc_gl s ON s.batch_id = g.batch_id "
        + "AND s.row_number = g.source_row_number "
        + $"JOIN {schemaPrefix}import_batch_source bs ON bs.batch_id = s.batch_id AND bs.source_no = s.source_no "
        + "WHERE " + ValidationProcedures.BlankGlDateSourceQualityPredicateWith("g");

    // 前八欄依序是來源列號、來源檔名、工作表名稱、傳票號碼、科目編號、總帳日期、摘要與 entry_id；分頁與整份讀完共用。
    private static SourceQualityFindingRow ReadFinding(DbDataReader reader)
    {
        var fileName = reader.GetString(1);
        var sourceLabel = reader.IsDBNull(2) ? fileName : $"{fileName} [{reader.GetString(2)}]";
        return new SourceQualityFindingRow("blankGlDate", reader.GetInt32(0), sourceLabel,
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6), reader.GetInt64(7));
    }
}
