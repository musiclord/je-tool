using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>依專案 provider 路由 tag 矩陣傳票層分頁(比照 <see cref="ProviderRoutingFilterHitsPageRepository"/>)。</summary>
public sealed class ProviderRoutingTagMatrixVoucherPageRepository(
    ProjectProviderResolver resolver,
    ITagMatrixVoucherPageRepository sqlite,
    ITagMatrixVoucherPageRepository sqlServer,
    ITagMatrixVoucherPageRepository duckDb)
    : ITagMatrixVoucherPageRepository,
      IWorkpaperStep4StreamRepository
{
    public async Task<(PageResult<VoucherTagRow> Page, IReadOnlyDictionary<string, IReadOnlyList<int>> PositionsByDoc)> GetPageAsync(
        string projectId, GlPopulationContext context, PageRequest request,
        IReadOnlyList<int>? scenarioPositions, CancellationToken cancellationToken)
    {
        var provider = await resolver.ResolveAsync(projectId, cancellationToken);
        return await ProviderSelection.Pick(provider, sqlite, sqlServer, duckDb)
            .GetPageAsync(projectId, context, request, scenarioPositions, cancellationToken);
    }

    async IAsyncEnumerable<WorkpaperStep4VoucherRow>
        IWorkpaperStep4StreamRepository.StreamAsync(
            string projectId,
            GlPopulationContext context,
            IReadOnlyList<int> scenarioPositions,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken)
    {
        var provider = await resolver.ResolveAsync(projectId, cancellationToken);
        var selected = ProviderSelection.Pick(provider, sqlite, sqlServer, duckDb);
        if (selected is not IWorkpaperStep4StreamRepository stream)
        {
            throw new InvalidOperationException(
                $"Provider '{provider}' 缺少 WorkingPaper step4 stream port。");
        }

        await foreach (var row in stream.StreamAsync(
                           projectId,
                           context,
                           scenarioPositions,
                           cancellationToken))
        {
            yield return row;
        }
    }
}
