using System.Data.Common;

namespace JET.Infrastructure;

/// <summary>
/// tag 矩陣情境摘要命中數的共用合併器:跑「傳票層」與「行層」兩個 GROUP BY,
/// 把每個 scenario_position 的兩筆 count 併成 dict&lt;position,(voucher,row)&gt;。
/// 兩 provider 唯一差異是 SELECT 片段(COUNT vs COUNT_BIG)由呼叫端各自帶入;
/// 合併與讀取邏輯 provider 中立(DbConnection/DbCommand),避免兩處重複(Linus 好品味:消除特例)。
/// 無命中的位置不會出現在任一查詢結果,故自然不入 dict。
/// </summary>
internal static class TagMatrixScenarioCounts
{
    public static async Task<IReadOnlyDictionary<int, (long VoucherHitCount, long RowHitCount)>> ReadAsync(
        DbConnection connection, string voucherCountsSql, string rowCountsSql, CancellationToken cancellationToken)
    {
        var voucher = await ReadPositionCountsAsync(connection, voucherCountsSql, cancellationToken);
        var row = await ReadPositionCountsAsync(connection, rowCountsSql, cancellationToken);

        var merged = new Dictionary<int, (long VoucherHitCount, long RowHitCount)>();
        foreach (var position in voucher.Keys.Union(row.Keys))
        {
            merged[position] = (
                voucher.GetValueOrDefault(position),
                row.GetValueOrDefault(position));
        }

        return merged;
    }

    private static async Task<Dictionary<int, long>> ReadPositionCountsAsync(
        DbConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        var result = new Dictionary<int, long>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result[reader.GetInt32(0)] = reader.GetInt64(1);
        }

        return result;
    }
}
