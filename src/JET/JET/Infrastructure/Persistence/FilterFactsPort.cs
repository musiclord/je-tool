using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// Production filter lifecycle port. Existing provider-routing repositories remain the
/// compatibility execution mechanisms while AuditCore owns the typed plan and result.
/// </summary>
internal sealed class FilterFactsPort(
    IFilterRunRepository previewRepository,
    IFilterCommitRepository commitRepository) : IFilterFactsPort
{
    public async Task<FilterFacts> ExecuteAsync(
        FilterPlan plan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);

        return plan.Request.ActionName switch
        {
            "filter.preview" => await PreviewAsync(plan, cancellationToken),
            "filter.commit" => await CommitAsync(plan, cancellationToken),
            _ => throw new InvalidOperationException(
                $"FilterFactsPort 不支援 action '{plan.Request.ActionName}'。")
        };
    }

    private async Task<FilterFacts> PreviewAsync(
        FilterPlan plan,
        CancellationToken cancellationToken)
    {
        var document = plan.Request.Documents.Single();
        var preview = await previewRepository.PreviewAsync(
            plan.Request.ProjectId,
            document.Spec,
            plan.Request.RuleContext,
            cancellationToken);
        return new FilterFacts(preview, MaterializedScenarioCount: 0);
    }

    private async Task<FilterFacts> CommitAsync(
        FilterPlan plan,
        CancellationToken cancellationToken)
    {
        await commitRepository.CommitAsync(
            plan.Request.ProjectId,
            plan.CommitItems,
            plan.Request.RuleContext,
            cancellationToken);
        return new FilterFacts(
            Preview: null,
            MaterializedScenarioCount: plan.CommitItems.Count);
    }
}
