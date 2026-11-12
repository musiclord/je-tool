using System.Data.Common;
using System.Globalization;
using JET.AuditCore;

namespace JET.Infrastructure;

/// <summary>
/// 高風險條件彙總情境摘要命中數的合併器:跑「傳票層」與「行層」兩個 GROUP BY,
/// 把每個情境位置(filter_scenario.position)的兩筆 count 併成 dict&lt;position,(voucher,row)&gt;。
/// 無命中的位置不會出現在任一查詢結果,故自然不入 dict。
/// </summary>
internal static class HighRiskSummaryScenarioCounts
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
    /// 指定情境的整張命中傳票分錄數。集合與 <see cref="HighRiskSummaryRowPageReader"/> 查詢 1 相同：命中列所屬
    /// 具名傳票（排除空白傳票號碼）在 GL 中的全部分錄。
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
            "FROM (SELECT DISTINCT fs.position AS scenario_position, g2.voucher_number " +
            $"FROM {schemaPrefix}filter_hit r JOIN {schemaPrefix}filter_scenario fs ON fs.scenario_id = r.scenario_id " +
            $"JOIN {schemaPrefix}gl g2 ON g2.entry_id = r.entry_id " +
            $"WHERE g2.voucher_number IS NOT NULL AND {GlExcludedPopulation.SqlPredicate("g2")} " +
            $"AND fs.position IN ({string.Join(", ", names)})) hit " +
            $"JOIN {schemaPrefix}gl g ON g.voucher_number = hit.voucher_number " +
            $"WHERE {GlExcludedPopulation.SqlPredicate("g")} " +
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
    /// 各情境命中列中傳票號碼空白的分錄數：行層命中數裡，<see cref="HighRiskSummaryRowPageReader"/> 不會展開的那一部分。
    /// </summary>
    public static async Task<IReadOnlyDictionary<int, long>> ReadBlankVoucherNumberRowCountsAsync(
        DbConnection connection, string schemaPrefix, string countExpression, CancellationToken cancellationToken) =>
        await ReadPositionCountsAsync(
            connection,
            $"SELECT fs.position, {countExpression} FROM {schemaPrefix}filter_hit r JOIN {schemaPrefix}filter_scenario fs ON fs.scenario_id = r.scenario_id " +
            $"JOIN {schemaPrefix}gl g ON g.entry_id = r.entry_id " +
            $"WHERE g.voucher_number IS NULL AND {GlExcludedPopulation.SqlPredicate("g")} " +
            "GROUP BY fs.position;",
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
