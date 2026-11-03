using JET.AuditCore;
using JET.Domain;
using Microsoft.Data.SqlClient;

namespace JET.Infrastructure;

/// <summary>
/// 高風險條件彙總行層 keyset 分頁(SQL Server,鏡像 <see cref="LocalHighRiskSummaryRowPageRepository"/>)。
/// 兩段查詢委派給 provider 中立的 <see cref="HighRiskSummaryRowPageReader"/>,差異只在
/// <see cref="SqlServerDialect"/> 出 OFFSET 0 ROWS FETCH NEXT(查詢 1 已具 ORDER BY entry_id);
/// 其餘 SQL 純 ANSI。
/// </summary>
public sealed class SqlServerHighRiskSummaryRowPageRepository(SqlServerProjectDatabase database)
    : IHighRiskSummaryRowPageRepository,
      IWorkingPaperStep41PageRepository,
      IWorkingPaperStep41PreparedSessionFactory
{
    private static readonly ISqlDialect Dialect = SqlServerDialect.Instance;

    public async Task<(PageResult<RowTagRow> Page, IReadOnlyList<long> EntryIds, IReadOnlyDictionary<long, IReadOnlyList<int>> PositionsByEntry)> GetPageAsync(
        string projectId, GlPopulationContext context, PageRequest request,
        IReadOnlyList<int>? scenarioPositions, CancellationToken cancellationToken)
    {
        await database.EnsureCreatedAsync(projectId, cancellationToken);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        return await HighRiskSummaryRowPageReader.ReadAsync(
            connection, Dialect, context, request, scenarioPositions, cancellationToken,
            SqlServerProjectSchema.QualifierFor(projectId));
    }

    async Task<WorkingPaperStep41Page> IWorkingPaperStep41PageRepository.GetPageAsync(
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

        return await HighRiskSummaryRowPageReader.ReadWorkingPaperAsync(
            connection,
            Dialect,
            context,
            request,
            scenarioPositions,
            lineItemKind,
            cancellationToken,
            SqlServerProjectSchema.QualifierFor(projectId));
    }

    Task<IWorkingPaperStep41PreparedSession>
        IWorkingPaperStep41PreparedSessionFactory.PrepareAsync(
            string projectId,
            GlPopulationContext context,
            IReadOnlyList<int> scenarioPositions,
            LegacyFieldKind lineItemKind,
            CancellationToken cancellationToken) =>
        WorkingPaperStep41PreparedSession.CreateAsync(
            CreatePreparedConnection(projectId),
            SqlServerDialect.Instance,
            SqlServerProjectSchema.QualifierFor(projectId),
            context,
            scenarioPositions,
            lineItemKind,
            dedicatedConnection: true,
            cancellationToken);

    Task<IWorkingPaperStep41PreparedSession>
        IWorkingPaperStep41PreparedSessionFactory.PrepareAsync(
            string projectId,
            GlPopulationContext context,
            IReadOnlyList<int> scenarioPositions,
            LegacyFieldKind lineItemKind,
            CancellationToken cancellationToken,
            IReadOnlyList<int> hitVoucherScenarioPositions) =>
        WorkingPaperStep41PreparedSession.CreateAsync(
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
