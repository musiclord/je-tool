using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// tag 矩陣情境摘要命中數(SQL Server,鏡像 <see cref="LocalTagMatrixScenariosRepository"/>)。
/// SQL 純 ANSI,兩種計數都 JOIN target 以防 stale hit 繞過有效母體；唯一方言差異為 COUNT → COUNT_BIG;
/// 合併邏輯共用 <see cref="TagMatrixScenarioCounts"/>。
/// </summary>
public sealed class SqlServerTagMatrixScenariosRepository(SqlServerProjectDatabase database)
    : ITagMatrixScenariosRepository
{
    // {s} token：執行前由命令工廠收斂為專案 schema(provider 中立的 reader 只負責執行傳入字串)。
    private static readonly string VoucherCountsSql =
        "SELECT r.scenario_position, COUNT_BIG(DISTINCT g.document_number) " +
        "FROM {s}.result_filter_run r " +
        "JOIN {s}.target_gl_entry g ON g.entry_id = r.entry_id " +
        $"WHERE {GlEffectivePopulation.SqlPredicate("g")} " +
        "GROUP BY r.scenario_position;";

    private static readonly string RowCountsSql =
        "SELECT r.scenario_position, COUNT_BIG(*) FROM {s}.result_filter_run r " +
        "JOIN {s}.target_gl_entry g ON g.entry_id = r.entry_id " +
        $"WHERE {GlEffectivePopulation.SqlPredicate("g")} " +
        "GROUP BY r.scenario_position;";

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

        return await TagMatrixScenarioCounts.ReadAsync(
            connection, voucherSql, rowSql, cancellationToken);
    }
}
