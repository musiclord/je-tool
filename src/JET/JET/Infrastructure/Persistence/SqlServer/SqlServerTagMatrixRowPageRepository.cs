using JET.AuditCore;
using JET.Domain;
using Microsoft.Data.SqlClient;

namespace JET.Infrastructure;

/// <summary>
/// tag 矩陣行層 keyset 分頁(SQL Server,鏡像 <see cref="LocalTagMatrixRowPageRepository"/>)。
/// 兩段查詢委派給 provider 中立的 <see cref="TagMatrixRowPageReader"/>,差異只在
/// <see cref="SqlServerDialect"/> 出 OFFSET 0 ROWS FETCH NEXT(查詢 1 已具 ORDER BY entry_id);
/// 其餘 SQL 純 ANSI。
/// </summary>
public sealed class SqlServerTagMatrixRowPageRepository(SqlServerProjectDatabase database)
    : ITagMatrixRowPageRepository,
      IWorkpaperStep41PageRepository,
      IWorkpaperStep41PreparedSessionFactory
{
    private static readonly ISqlDialect Dialect = SqlServerDialect.Instance;

    public async Task<(PageResult<RowTagRow> Page, IReadOnlyList<long> EntryIds, IReadOnlyDictionary<long, IReadOnlyList<int>> PositionsByEntry)> GetPageAsync(
        string projectId, GlPopulationContext context, PageRequest request,
        IReadOnlyList<int>? scenarioPositions, CancellationToken cancellationToken)
    {
        await database.EnsureCreatedAsync(projectId, cancellationToken);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        return await TagMatrixRowPageReader.ReadAsync(
            connection, Dialect, context, request, scenarioPositions, cancellationToken,
            SqlServerProjectSchema.QualifierFor(projectId));
    }

    async Task<WorkpaperStep41Page> IWorkpaperStep41PageRepository.GetPageAsync(
        string projectId,
        GlPopulationContext context,
        PageRequest request,
        IReadOnlyList<int> scenarioPositions,
        LegacyFieldKind lineItemKind,
        CancellationToken cancellationToken)
    {
        await database.EnsureCreatedAsync(projectId, cancellationToken);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        return await TagMatrixRowPageReader.ReadWorkpaperAsync(
            connection,
            Dialect,
            context,
            request,
            scenarioPositions,
            lineItemKind,
            cancellationToken,
            SqlServerProjectSchema.QualifierFor(projectId));
    }

    Task<IWorkpaperStep41PreparedSession>
        IWorkpaperStep41PreparedSessionFactory.PrepareAsync(
            string projectId,
            GlPopulationContext context,
            IReadOnlyList<int> scenarioPositions,
            LegacyFieldKind lineItemKind,
            CancellationToken cancellationToken) =>
        WorkpaperStep41PreparedSession.CreateAsync(
            CreatePreparedConnection(projectId),
            SqlServerDialect.Instance,
            SqlServerProjectSchema.QualifierFor(projectId),
            context,
            scenarioPositions,
            lineItemKind,
            dedicatedConnection: true,
            cancellationToken);

    Task<IWorkpaperStep41PreparedSession>
        IWorkpaperStep41PreparedSessionFactory.PrepareAsync(
            string projectId,
            GlPopulationContext context,
            IReadOnlyList<int> scenarioPositions,
            LegacyFieldKind lineItemKind,
            CancellationToken cancellationToken,
            IReadOnlyList<int> hitVoucherScenarioPositions) =>
        WorkpaperStep41PreparedSession.CreateAsync(
            CreatePreparedConnection(projectId),
            SqlServerDialect.Instance,
            SqlServerProjectSchema.QualifierFor(projectId),
            context,
            scenarioPositions,
            lineItemKind,
            dedicatedConnection: true,
            cancellationToken,
            hitVoucherScenarioPositions);

    private SqlConnection CreatePreparedConnection(string projectId)
    {
        using var pooled = database.CreateConnection(projectId);
        var builder = new SqlConnectionStringBuilder(pooled.ConnectionString)
        {
            Pooling = false
        };
        return new SqlConnection(builder.ConnectionString);
    }
}
