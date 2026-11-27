using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 分頁讀出高風險條件彙總的傳票清單，分頁用 keyset 方式：用上一頁最後一筆的排序鍵接下一頁。
/// 兩段查詢都交給 <see cref="HighRiskSummaryVoucherPageReader"/>，本類別只負責開連線，並把 <see cref="ISqlDialect"/> 傳進去；
/// LIMIT 的寫法由它提供。分頁、cursor 與方言的用法和 <see cref="LocalFilterHitsPageRepository"/> 相同。
/// </summary>
public sealed class LocalHighRiskSummaryVoucherPageRepository(ILocalProjectDatabase database)
    : IHighRiskSummaryVoucherPageRepository,
      IWorkingPaperHighRiskVoucherStreamRepository
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

    async IAsyncEnumerable<WorkingPaperHighRiskVoucherRow>
        IWorkingPaperHighRiskVoucherStreamRepository.StreamAsync(
            string projectId,
            GlPopulationContext context,
            IReadOnlyList<int> scenarioPositions,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken)
    {
        await database.EnsureReadyAsync(projectId, cancellationToken);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        await foreach (var row in WorkingPaperHighRiskVoucherStreamReader.ReadAsync(
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
