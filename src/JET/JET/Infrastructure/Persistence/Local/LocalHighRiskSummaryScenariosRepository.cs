using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 高風險條件彙總情境摘要命中數(本地引擎)。兩個 GROUP BY 查詢即時從 filter_hit 算:
/// 傳票層 COUNT(DISTINCT voucher_number) 與行層 COUNT(*) 都 JOIN gl，並保險起見再套一次「只看納入測試的分錄」。
/// SQL 為純 ANSI;合併成 dict&lt;position,(voucher,row)&gt;,無命中的位置不入 dict。
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
