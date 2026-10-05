using JET.Domain;

namespace JET.AuditCore;

/// <summary>
/// Canonical filter document keeps the original JSON as the persistence authority and a
/// typed projection for validation and SQL compilation. The raw document is never rebuilt
/// from the typed projection.
/// </summary>
internal sealed record CanonicalFilterDocument(
    int Position,
    string RawJson,
    FilterScenarioSpec Spec);

/// <summary>Bounded Application input for filter.preview or filter.commit.</summary>
internal sealed record FilterRequest(
    string ActionName,
    string ProjectId,
    IReadOnlyList<CanonicalFilterDocument> Documents,
    FilterRuleContext RuleContext,
    FilterValidationContext ValidationContext);

/// <summary>AuditCore-owned filter plan shared by preview and materialization.</summary>
internal sealed record FilterPlan(
    FilterRequest Request,
    IReadOnlyList<FilterCommitItem> CommitItems);

/// <summary>Raw execution facts returned by the Infrastructure filter port.</summary>
internal sealed record FilterFacts(
    FilterPreviewResult? Preview,
    int MaterializedScenarioCount);

/// <summary>Finalized typed filter result; outward wire shaping remains in Application.</summary>
internal sealed record FilterResult(
    FilterPlan Plan,
    FilterPreviewResult? Preview,
    int MaterializedScenarioCount);

/// <summary>
/// Typed filter execution port. Infrastructure performs provider-specific command binding,
/// set-based execution and materialization without owning scenario validity decisions.
/// </summary>
internal interface IFilterFactsPort
{
    Task<FilterFacts> ExecuteAsync(
        FilterPlan plan,
        CancellationToken cancellationToken);
}

public static partial class JetAuditProgram
{
    private const string FilterPreviewAction = "filter.preview";
    private const string FilterCommitAction = "filter.commit";
    private const int MaxFilterScenarios = FilterScenarioLimits.MaxSavedScenarios;

    /// <summary>
    /// Plans one preview document or a bounded commit set. Domain validation remains the
    /// sole rule validator; AuditCore owns when it is applied in the production lifecycle.
    /// </summary>
    internal static FilterPlan Plan(
        FilterRequest request,
        IReadOnlyList<SavedFilterScenario>? definitions = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Documents);
        ArgumentNullException.ThrowIfNull(request.RuleContext);
        ArgumentNullException.ThrowIfNull(request.ValidationContext);

        switch (request.ActionName)
        {
            case FilterPreviewAction when request.Documents.Count != 1:
                throw new InvalidOperationException("filter.preview 必須且只能規劃一個情境。");
            case FilterCommitAction when request.Documents.Count > MaxFilterScenarios:
                throw new JetActionException(
                    JetErrorCodes.ScenarioLimitReached,
                    $"最多儲存 {MaxFilterScenarios} 個篩選情境。");
            case FilterPreviewAction:
            case FilterCommitAction:
                break;
            default:
                throw new InvalidOperationException(
                    $"Filter lifecycle 不支援 action '{request.ActionName}'。");
        }

        foreach (var document in request.Documents)
        {
            ArgumentNullException.ThrowIfNull(document);
            var errors = FilterScenarioValidator.Validate(
                document.Spec,
                request.ValidationContext,
                forSave: request.ActionName == FilterCommitAction);
            if (errors.Count > 0)
            {
                throw FilterScenarioErrorDetails.InvalidScenario(errors);
            }
        }

        var savedDefinitions = definitions ?? [];
        IReadOnlyList<FilterCommitItem> commitItems;
        if (string.Equals(request.ActionName, FilterCommitAction, StringComparison.Ordinal))
        {
            // Handler 在逐份解析時重用 Plan 做累進驗證；definitions 尚未完成前只規劃／驗證，
            // 最終 execution plan 會明示傳入（即使是空陣列）並建立 paired commit items。
            if (definitions is null)
            {
                commitItems = [];
                return new FilterPlan(request, commitItems);
            }

            if (savedDefinitions.Count != request.Documents.Count)
            {
                throw new InvalidOperationException(
                    "filter.commit definitions 與 canonical documents 數量不一致。");
            }

            var items = new FilterCommitItem[savedDefinitions.Count];
            for (var index = 0; index < items.Length; index++)
            {
                var definition = savedDefinitions[index];
                var document = request.Documents[index];
                if (definition.Position != document.Position)
                {
                    throw new InvalidOperationException(
                        "filter.commit definition 與 canonical document 位置不一致。");
                }

                items[index] = new FilterCommitItem(definition, document.Spec);
            }

            commitItems = items;
        }
        else
        {
            if (savedDefinitions.Count != 0)
            {
                throw new InvalidOperationException("filter.preview 不接受保存 definitions。");
            }

            commitItems = [];
        }

        return new FilterPlan(request, commitItems);
    }

    /// <summary>Finalizes raw facts without changing preview or hit-set semantics.</summary>
    internal static FilterResult Finalize(
        FilterPlan plan,
        FilterFacts facts)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(facts);

        if (string.Equals(plan.Request.ActionName, FilterPreviewAction, StringComparison.Ordinal)
            && facts.Preview is null)
        {
            throw new InvalidOperationException("filter.preview 未收到 preview facts。");
        }

        if (string.Equals(plan.Request.ActionName, FilterCommitAction, StringComparison.Ordinal)
            && facts.MaterializedScenarioCount != plan.Request.Documents.Count)
        {
            throw new InvalidOperationException("filter.commit materialized scenario count 與 plan 不一致。");
        }

        return new FilterResult(
            plan,
            facts.Preview,
            facts.MaterializedScenarioCount);
    }

}
