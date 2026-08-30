using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// tag 矩陣傳票層 keyset 分頁(本地引擎)。兩段查詢委派給 provider 中立的
/// <see cref="TagMatrixVoucherPageReader"/>,本類別只負責開連線與帶入注入的 <see cref="ISqlDialect"/>
/// (LIMIT 由方言出)。鏡射 <see cref="LocalFilterHitsPageRepository"/> 的 keyset/游標/Dialect 範式。
/// </summary>
public sealed class LocalTagMatrixVoucherPageRepository(ILocalProjectDatabase database)
    : ITagMatrixVoucherPageRepository,
      IWorkpaperStep4StreamRepository
{
    public async Task<(PageResult<VoucherTagRow> Page, IReadOnlyDictionary<string, IReadOnlyList<int>> PositionsByDoc)> GetPageAsync(
        string projectId, GlPopulationContext context, PageRequest request,
        IReadOnlyList<int>? scenarioPositions, CancellationToken cancellationToken)
    {
        await database.EnsureCreatedAsync(projectId, cancellationToken);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        return await TagMatrixVoucherPageReader.ReadAsync(
            connection, database.Dialect, context, request, scenarioPositions, cancellationToken);
    }

    async IAsyncEnumerable<WorkpaperStep4VoucherRow>
        IWorkpaperStep4StreamRepository.StreamAsync(
            string projectId,
            GlPopulationContext context,
            IReadOnlyList<int> scenarioPositions,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken)
    {
        await database.EnsureCreatedAsync(projectId, cancellationToken);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        await foreach (var row in WorkpaperStep4StreamReader.ReadAsync(
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
