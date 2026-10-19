using System.Data.Common;
using System.Globalization;
using JET.AuditCore;

namespace JET.Infrastructure;

/// <summary>
/// tag 矩陣情境摘要命中數的共用合併器:跑「傳票層」與「行層」兩個 GROUP BY,
/// 把每個 scenario_position 的兩筆 count 併成 dict&lt;position,(voucher,row)&gt;。
/// 兩 provider 唯一差異是 SELECT 片段(COUNT vs COUNT_BIG)由呼叫端各自帶入;
/// 合併與讀取邏輯 provider 中立(DbConnection/DbCommand),避免兩處重複。
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

    /// <summary>
    /// 指定情境的整張命中傳票分錄數。集合與 <see cref="TagMatrixRowPageReader"/> 查詢 1 相同：命中列所屬
    /// 具名傳票（排除空白傳票號碼）在有效母體內的全部分錄。兩 provider 只差 COUNT 與 COUNT_BIG 及 schema 前綴。
    /// </summary>
    public static async Task<IReadOnlyDictionary<int, long>> ReadWholeVoucherRowCountsAsync(
        DbConnection connection, string schemaPrefix, string countExpression,
        IReadOnlyList<int> scenarioPositions, CancellationToken cancellationToken)
    {
        var result = new Dictionary<int, long>();
        if (scenarioPositions.Count == 0)
        {
            return result;
        }

        await using var command = connection.CreateCommand();
        var names = new string[scenarioPositions.Count];
        for (var index = 0; index < names.Length; index++)
        {
            names[index] = "@position" + index.ToString(CultureInfo.InvariantCulture);
            command.AddWithValue(names[index], scenarioPositions[index]);
        }
        command.CommandText =
            $"SELECT hit.scenario_position, {countExpression} " +
            "FROM (SELECT DISTINCT r.scenario_position, g2.document_number " +
            $"FROM {schemaPrefix}result_filter_run r " +
            $"JOIN {schemaPrefix}target_gl_entry g2 ON g2.entry_id = r.entry_id " +
            $"WHERE g2.document_number IS NOT NULL AND {GlEffectivePopulation.SqlPredicate("g2")} " +
            $"AND r.scenario_position IN ({string.Join(", ", names)})) hit " +
            $"JOIN {schemaPrefix}target_gl_entry g ON g.document_number = hit.document_number " +
            $"WHERE {GlEffectivePopulation.SqlPredicate("g")} " +
            "GROUP BY hit.scenario_position;";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result[Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture)] =
                Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture);
        }

        return result;
    }

    /// <summary>
    /// 各情境命中列中傳票號碼空白的分錄數：行層命中數裡，<see cref="TagMatrixRowPageReader"/> 不會展開的那一部分。
    /// 兩 provider 只差 COUNT 與 COUNT_BIG 及 schema 前綴。
    /// </summary>
    public static async Task<IReadOnlyDictionary<int, long>> ReadBlankDocumentRowCountsAsync(
        DbConnection connection, string schemaPrefix, string countExpression, CancellationToken cancellationToken) =>
        await ReadPositionCountsAsync(
            connection,
            $"SELECT r.scenario_position, {countExpression} FROM {schemaPrefix}result_filter_run r " +
            $"JOIN {schemaPrefix}target_gl_entry g ON g.entry_id = r.entry_id " +
            $"WHERE g.document_number IS NULL AND {GlEffectivePopulation.SqlPredicate("g")} " +
            "GROUP BY r.scenario_position;",
            cancellationToken);

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
