using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 高風險條件彙總裡每個已存情境的命中數。用兩句 GROUP BY 查詢當場從 filter_hit 算出：
/// 命中傳票數是 COUNT(DISTINCT voucher_number)，命中分錄數是 COUNT(*)。兩句都 JOIN gl，並再套一次「只看納入測試的分錄」當作保險。
/// SQL 只用標準語法。兩句的結果合併成以情境位置為鍵的字典，值是傳票數與分錄數；沒有命中的位置不在字典裡。
/// </summary>
public sealed class LocalHighRiskSummaryScenariosRepository(ILocalProjectDatabase database)
    : IHighRiskSummaryScenariosRepository
{
    private static readonly string VoucherCountsSql =
        "SELECT fs.position, COUNT(DISTINCT g.voucher_number) " +
        "FROM filter_hit r JOIN filter_scenario fs ON fs.scenario_id = r.scenario_id " +
        "JOIN gl g ON g.entry_id = r.entry_id " +
        $"WHERE {GlExcludedPopulation.SqlPredicate("g")} " +
        "GROUP BY fs.position;";

    private static readonly string RowCountsSql =
        "SELECT fs.position, COUNT(*) FROM filter_hit r JOIN filter_scenario fs ON fs.scenario_id = r.scenario_id " +
        "JOIN gl g ON g.entry_id = r.entry_id " +
        $"WHERE {GlExcludedPopulation.SqlPredicate("g")} " +
        "GROUP BY fs.position;";

    public async Task<IReadOnlyDictionary<int, (long VoucherHitCount, long RowHitCount)>> GetCountsAsync(
        string projectId, CancellationToken cancellationToken)
    {
        await database.EnsureReadyAsync(projectId, cancellationToken);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        return await HighRiskSummaryScenarioCounts.ReadAsync(
            connection, VoucherCountsSql, RowCountsSql, cancellationToken);
    }

    public async Task<IReadOnlyDictionary<int, long>> GetWholeVoucherRowCountsAsync(
        string projectId, IReadOnlyList<int> scenarioPositions, CancellationToken cancellationToken)
    {
        await database.EnsureReadyAsync(projectId, cancellationToken);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        return await HighRiskSummaryScenarioCounts.ReadWholeVoucherRowCountsAsync(
            connection, schemaPrefix: string.Empty, "COUNT(*)", scenarioPositions, cancellationToken);
    }

    public async Task<IReadOnlyDictionary<int, long>> GetBlankVoucherNumberHitRowCountsAsync(
        string projectId, CancellationToken cancellationToken)
    {
        await database.EnsureReadyAsync(projectId, cancellationToken);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        return await HighRiskSummaryScenarioCounts.ReadBlankVoucherNumberRowCountsAsync(
            connection, schemaPrefix: string.Empty, "COUNT(*)", cancellationToken);
    }
}
