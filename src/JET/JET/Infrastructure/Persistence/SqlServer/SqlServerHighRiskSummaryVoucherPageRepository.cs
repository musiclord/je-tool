using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 高風險條件彙總傳票層 keyset 分頁(SQL Server,鏡像 <see cref="LocalHighRiskSummaryVoucherPageRepository"/>)。
/// 兩段查詢委派給 provider 中立的 <see cref="HighRiskSummaryVoucherPageReader"/>,差異只在
/// <see cref="SqlServerDialect"/> 出 OFFSET 0 ROWS FETCH NEXT(查詢 1 已具 ORDER BY);其餘 SQL 純 ANSI。
/// </summary>
public sealed class SqlServerHighRiskSummaryVoucherPageRepository(SqlServerProjectDatabase database)
    : IHighRiskSummaryVoucherPageRepository,
      IWorkingPaperStep4StreamRepository
{
    private static readonly ISqlDialect Dialect = SqlServerDialect.Instance;

    public async Task<(PageResult<VoucherTagRow> Page, IReadOnlyDictionary<string, IReadOnlyList<int>> PositionsByDoc)> GetPageAsync(
        string projectId, GlPopulationContext context, PageRequest request,
        IReadOnlyList<int>? scenarioPositions, CancellationToken cancellationToken)
    {
        await database.EnsureCreatedAsync(projectId, cancellationToken);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        return await HighRiskSummaryVoucherPageReader.ReadAsync(
            connection, Dialect, context, request, scenarioPositions, cancellationToken,
            SqlServerProjectSchema.QualifierFor(projectId));
    }

    async IAsyncEnumerable<WorkingPaperStep4VoucherRow>
        IWorkingPaperStep4StreamRepository.StreamAsync(
            string projectId,
            GlPopulationContext context,
            IReadOnlyList<int> scenarioPositions,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken)
    {
        await database.EnsureCreatedAsync(projectId, cancellationToken);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        await foreach (var row in WorkingPaperStep4StreamReader.ReadAsync(
                           connection,
                           Dialect,
                           context,
                           scenarioPositions,
                           cancellationToken,
                           SqlServerProjectSchema.QualifierFor(projectId)))
        {
            yield return row;
        }
    }
}
