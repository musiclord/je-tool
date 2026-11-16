using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 分頁讀出借貸不平的傳票，分頁用 keyset 方式：用上一頁最後一筆的排序鍵接下一頁。
/// 先依 voucher_number 彙總，留下 SUM(amount_scaled) 不是 0 的傳票，包成子查詢；
/// 排序、搜尋與換頁條件由 <c>KeysetPaging.Plan</c> 依 <c>ResultPageSorting.VoucherBalance</c> 組出，套在子查詢外層，
/// 沒有指定排序時依 voucher_number 升冪。LIMIT 的寫法取自 <see cref="ISqlDialect"/>。
/// </summary>
public sealed class LocalVoucherBalancePageRepository(ILocalProjectDatabase database)
    : IVoucherBalancePageRepository
{
    public async Task<PageResult<UnbalancedVoucher>> GetPageAsync(
        string projectId, int moneyScale, string periodStart, string periodEnd, PageRequest request, CancellationToken cancellationToken)
    {
        await database.EnsureReadyAsync(projectId, cancellationToken);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        var paging = KeysetPaging.Plan(database.Dialect, request, ResultPageSorting.VoucherBalance);
        foreach (var parameter in paging.Parameters)
        {
            command.AddWithValue(parameter.Key, parameter.Value);
        }

        command.AddWithValue("@pageSize", request.ClampedPageSize + 1);

        // 找借貸不平傳票的 SQL 取自 ValidationProcedures.UnbalancedCore，只看納入測試的分錄，和計數用同一段。
        // 彙總後包成子查詢，排序、搜尋與換頁條件才能用彙總出來的欄位（debit_s、credit_s、diff_s）。
        command.CommandText =
            "SELECT voucher_number, debit_s, credit_s, diff_s" + paging.SelectSuffix + " FROM (" +
            "SELECT voucher_number, " +
            "COALESCE(SUM(debit_amount_scaled), 0) AS debit_s, " +
            "COALESCE(SUM(credit_amount_scaled), 0) AS credit_s, " +
            "COALESCE(SUM(amount_scaled), 0) AS diff_s " +
            ValidationProcedures.UnbalancedCore(string.Empty) +
            ") u WHERE 1 = 1" + paging.Predicate + " " +
            paging.OrderBy + " " + database.Dialect.LimitClause("@pageSize") + ";";

        var totalCount = await PageTotalCount.ReadKeysetAsync(command, request, paging, database.Dialect, cancellationToken);
        var buffer = new KeysetPageBuffer<UnbalancedVoucher>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            buffer.Add(
                new UnbalancedVoucher(
                    reader.IsDBNull(0) ? null : reader.GetString(0),
                    reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3)),
                paging.HasSort ? reader.GetValue(4) : null);
        }

        return buffer.ToPage(request, paging, static row => row.VoucherNumber ?? string.Empty, totalCount);
    }

    public async IAsyncEnumerable<UnbalancedVoucherDateRow> StreamVoucherDateRowsAsync(
        string projectId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await database.EnsureReadyAsync(projectId, cancellationToken);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = ValidationProcedures.UnbalancedVoucherDateSummary(string.Empty) + ";";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            yield return new UnbalancedVoucherDateRow(
                reader.IsDBNull(0) ? null : reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                ReadInt64(reader, 2),
                ReadInt64(reader, 3));
        }
    }

    private static long ReadInt64(System.Data.Common.DbDataReader reader, int ordinal)
    {
        var value = reader.GetValue(ordinal);
        return value is System.Numerics.BigInteger bigInteger
            ? checked((long)bigInteger)
            : Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }
}
