using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 高風險條件彙總傳票層 keyset 分頁。兩段查詢委派給
/// <see cref="HighRiskSummaryVoucherPageReader"/>,本類別只負責開連線與帶入注入的 <see cref="ISqlDialect"/>
/// (LIMIT 由方言出)。鏡射 <see cref="LocalFilterHitsPageRepository"/> 的 keyset/游標/Dialect 範式。
/// </summary>
public sealed class LocalHighRiskSummaryVoucherPageRepository(ILocalProjectDatabase database)
    : IHighRiskSummaryVoucherPageRepository,
      IWorkingPaperStep4StreamRepository
{
    public async Task<(PageResult<VoucherTagRow> Page, IReadOnlyDictionary<string, IReadOnlyList<int>> PositionsByDoc)> GetPageAsync(
        string projectId, GlPopulationContext context, PageRequest request,
        IReadOnlyList<int>? scenarioPositions, CancellationToken cancellationToken)
    {
        await database.EnsureReadyAsync(projectId, cancellationToken);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        return await HighRiskSummaryVoucherPageReader.ReadAsync(
            connection, database.Dialect, context, request, scenarioPositions, cancellationToken);
    }

    async IAsyncEnumerable<WorkingPaperStep4VoucherRow>
        IWorkingPaperStep4StreamRepository.StreamAsync(
            string projectId,
            GlPopulationContext context,
            IReadOnlyList<int> scenarioPositions,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken)
    {
        await database.EnsureReadyAsync(projectId, cancellationToken);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        await foreach (var row in WorkingPaperStep4StreamReader.ReadAsync(
                           connection,
                           database.Dialect,
                           context,
                           scenarioPositions,
                           cancellationToken))
        {
            yield return row;
        }
    }
}
