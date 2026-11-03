using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 高風險條件彙總情境摘要命中數(SQL Server,鏡像 <see cref="LocalHighRiskSummaryScenariosRepository"/>)。
/// SQL 純 ANSI,兩種計數都 JOIN target 以防過期的命中繞過「只看納入測試的分錄」；唯一方言差異為 COUNT → COUNT_BIG;
/// 合併邏輯共用 <see cref="HighRiskSummaryScenarioCounts"/>。
/// </summary>
public sealed class SqlServerHighRiskSummaryScenariosRepository(SqlServerProjectDatabase database)
    : IHighRiskSummaryScenariosRepository
{
    // {s} token：執行前由命令工廠收斂為專案 schema(provider 中立的 reader 只負責執行傳入字串)。
    private static readonly string VoucherCountsSql =
        "SELECT fs.position, COUNT_BIG(DISTINCT g.voucher_number) " +
        "FROM {s}.filter_hit r JOIN {s}.filter_scenario fs ON fs.scenario_id = r.scenario_id " +
        "JOIN {s}.gl g ON g.entry_id = r.entry_id " +
        $"WHERE {GlExcludedPopulation.SqlPredicate("g")} " +
        "GROUP BY fs.position;";

    private static readonly string RowCountsSql =
        "SELECT fs.position, COUNT_BIG(*) FROM {s}.filter_hit r JOIN {s}.filter_scenario fs ON fs.scenario_id = r.scenario_id " +
        "JOIN {s}.gl g ON g.entry_id = r.entry_id " +
        $"WHERE {GlExcludedPopulation.SqlPredicate("g")} " +
        "GROUP BY fs.position;";

    public async Task<IReadOnlyDictionary<int, (long VoucherHitCount, long RowHitCount)>> GetCountsAsync(
        string projectId, CancellationToken cancellationToken)
    {
        await database.EnsureCreatedAsync(projectId, cancellationToken);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        // {s} 由命令工廠收斂為專案 schema;reader 為 provider 中立,故先展開 token 再傳入。
        string voucherSql, rowSql;
        await using (var v = database.CreateCommand(connection, projectId, VoucherCountsSql))
        {
            voucherSql = v.CommandText;
        }
        await using (var r = database.CreateCommand(connection, projectId, RowCountsSql))
        {
            rowSql = r.CommandText;
        }

        return await HighRiskSummaryScenarioCounts.ReadAsync(
            connection, voucherSql, rowSql, cancellationToken);
    }

    public async Task<IReadOnlyDictionary<int, long>> GetWholeVoucherRowCountsAsync(
        string projectId, IReadOnlyList<int> scenarioPositions, CancellationToken cancellationToken)
    {
        await database.EnsureCreatedAsync(projectId, cancellationToken);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        return await HighRiskSummaryScenarioCounts.ReadWholeVoucherRowCountsAsync(
            connection, SqlServerProjectSchema.QualifierFor(projectId), "COUNT_BIG(*)", scenarioPositions, cancellationToken);
    }

    public async Task<IReadOnlyDictionary<int, long>> GetBlankDocumentHitRowCountsAsync(
        string projectId, CancellationToken cancellationToken)
    {
        await database.EnsureCreatedAsync(projectId, cancellationToken);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        return await HighRiskSummaryScenarioCounts.ReadBlankDocumentRowCountsAsync(
            connection, SqlServerProjectSchema.QualifierFor(projectId), "COUNT_BIG(*)", cancellationToken);
    }
}
