using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>依專案 provider 路由 tag 矩陣行層分頁(比照 <see cref="ProviderRoutingTagMatrixVoucherPageRepository"/>)。</summary>
public sealed class ProviderRoutingTagMatrixRowPageRepository(
    ProjectProviderResolver resolver,
    ITagMatrixRowPageRepository sqlite,
    ITagMatrixRowPageRepository sqlServer,
    ITagMatrixRowPageRepository duckDb)
    : ITagMatrixRowPageRepository,
      IWorkpaperStep41PageRepository,
      IWorkpaperStep41PreparedSessionFactory
{
    public async Task<(PageResult<RowTagRow> Page, IReadOnlyList<long> EntryIds, IReadOnlyDictionary<long, IReadOnlyList<int>> PositionsByEntry)> GetPageAsync(
        string projectId, GlPopulationContext context, PageRequest request,
        IReadOnlyList<int>? scenarioPositions, CancellationToken cancellationToken)
    {
        var provider = await resolver.ResolveAsync(projectId, cancellationToken);
        return await ProviderSelection.Pick(provider, sqlite, sqlServer, duckDb)
            .GetPageAsync(projectId, context, request, scenarioPositions, cancellationToken);
    }

    async Task<WorkpaperStep41Page> IWorkpaperStep41PageRepository.GetPageAsync(
        string projectId,
        GlPopulationContext context,
        PageRequest request,
        IReadOnlyList<int> scenarioPositions,
        LegacyFieldKind lineItemKind,
        CancellationToken cancellationToken)
    {
        var provider = await resolver.ResolveAsync(projectId, cancellationToken);
        var selected = ProviderSelection.Pick(provider, sqlite, sqlServer, duckDb);
        if (selected is not IWorkpaperStep41PageRepository workpaper)
        {
            throw new InvalidOperationException(
                $"Provider '{provider}' 缺少 WorkingPaper step4-1 typed page port。");
        }

        return await workpaper.GetPageAsync(
            projectId,
            context,
            request,
            scenarioPositions,
            lineItemKind,
            cancellationToken);
    }

    async Task<IWorkpaperStep41PreparedSession>
        IWorkpaperStep41PreparedSessionFactory.PrepareAsync(
            string projectId,
            GlPopulationContext context,
            IReadOnlyList<int> scenarioPositions,
            LegacyFieldKind lineItemKind,
            CancellationToken cancellationToken)
    {
        var provider = await resolver.ResolveAsync(projectId, cancellationToken);
        var selected = ProviderSelection.Pick(provider, sqlite, sqlServer, duckDb);
        if (selected is not IWorkpaperStep41PreparedSessionFactory prepared)
        {
            throw new InvalidOperationException(
                $"Provider '{provider}' 缺少 WorkingPaper step4-1 prepared session port。");
        }

        var session = await prepared.PrepareAsync(
            projectId,
            context,
            scenarioPositions,
            lineItemKind,
            cancellationToken);
        session.Metrics.ProviderResolutions++;
        return session;
    }

    async Task<IWorkpaperStep41PreparedSession>
        IWorkpaperStep41PreparedSessionFactory.PrepareAsync(
            string projectId,
            GlPopulationContext context,
            IReadOnlyList<int> scenarioPositions,
            LegacyFieldKind lineItemKind,
            CancellationToken cancellationToken,
            IReadOnlyList<int> hitVoucherScenarioPositions)
    {
        var provider = await resolver.ResolveAsync(projectId, cancellationToken);
        var selected = ProviderSelection.Pick(provider, sqlite, sqlServer, duckDb);
        if (selected is not IWorkpaperStep41PreparedSessionFactory prepared)
        {
            throw new InvalidOperationException(
                $"Provider '{provider}' 缺少 WorkingPaper step4-1 prepared session port。");
        }

        var session = await prepared.PrepareAsync(
            projectId,
            context,
            scenarioPositions,
            lineItemKind,
            cancellationToken,
            hitVoucherScenarioPositions);
        session.Metrics.ProviderResolutions++;
        return session;
    }
}
