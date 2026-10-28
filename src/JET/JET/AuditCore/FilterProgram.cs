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

/// <summary>Bounded Application input for filter.preview, filter.commit or the one-scenario changes.</summary>
internal sealed record FilterRequest(
    string ActionName,
    string ProjectId,
    IReadOnlyList<CanonicalFilterDocument> Documents,
    FilterRuleContext RuleContext,
    FilterValidationContext ValidationContext);

/// <summary>AuditCore-owned filter plan shared by preview and materialization.</summary>
internal sealed record FilterPlan(
    FilterRequest Request,
    IReadOnlyList<FilterCommitItem> CommitItems)
{
    /// <summary>
    /// 預覽的「符合條件的分錄筆數」改用整張命中傳票的分錄數。判斷與 Criteria Selection 摘要、
    /// 工作底稿逐列標記相同（<see cref="JetAuditProgram.UsesLegacyWholeVoucherRows"/>），畫面才不會和報告不同。
    /// </summary>
    internal bool PreviewCountsWholeVoucherRows { get; init; }
}

/// <summary>Raw execution facts returned by the Infrastructure filter port.</summary>
internal sealed record FilterFacts(
    FilterPreviewResult? Preview,
    int MaterializedScenarioCount);

/// <summary>Finalized typed filter result; outward wire shaping remains in Application.</summary>
internal sealed record FilterResult(
    FilterPlan Plan,
    FilterPreviewResult? Preview,
    int MaterializedScenarioCount)
{
    /// <summary>預覽的「符合條件的分錄筆數」：整張傳票計數的情境用整張命中傳票的分錄數，其他用直接符合的分錄數。</summary>
    internal long? PreviewSummaryRowCount => Preview is null
        ? null
        : Plan.PreviewCountsWholeVoucherRows ? Preview.WholeVoucherRowCount : Preview.Count;
}

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
    /// 一次只存、刪或重新檢查一個情境的三個動作：整批發布，但每一份已由 Application 逐筆檢查過。
    /// 這裡只做不套儲存規則的安全檢查（正在編輯的那一份已用儲存規則檢查；其他情境照資料庫原樣保留，
    /// 不套儲存規則），而且一定要帶 definitions。
    /// </summary>
    private static readonly HashSet<string> ReviewedBatchActions = new(StringComparer.Ordinal)
    {
        "filter.scenario.save",
        "filter.scenario.remove",
        "filter.scenario.recheck"
    };

    private static bool PublishesBatch(string actionName) =>
        string.Equals(actionName, FilterCommitAction, StringComparison.Ordinal)
        || ReviewedBatchActions.Contains(actionName);

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

        var isPreview = string.Equals(request.ActionName, FilterPreviewAction, StringComparison.Ordinal);
        if (isPreview && request.Documents.Count != 1)
        {
            throw new InvalidOperationException("filter.preview 必須且只能規劃一個情境。");
        }

        if (!isPreview && !PublishesBatch(request.ActionName))
        {
            throw new InvalidOperationException(
                $"Filter lifecycle 不支援 action '{request.ActionName}'。");
        }

        if (!isPreview && request.Documents.Count > MaxFilterScenarios)
        {
            throw new JetActionException(
                JetErrorCodes.ScenarioLimitReached,
                FilterScenarioLimits.MaxSavedScenariosMessage);
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
                // 預覽的位置 1 是虛設的，不標情境；整批發布時標出錯誤屬於第幾個情境，前端才不會標錯條件列。
                throw isPreview
                    ? FilterScenarioErrorDetails.InvalidScenario(errors)
                    : FilterScenarioErrorDetails.InvalidScenario(
                        errors,
                        document.Position,
                        FilterScenarioSources.ResolvePersistable(document.Spec, document.Position).Name);
            }
        }

        var savedDefinitions = definitions ?? [];
        IReadOnlyList<FilterCommitItem> commitItems;
        if (!isPreview)
        {
            // filter.commit 的 handler 在逐份解析時重用 Plan 做累進驗證；definitions 尚未完成前只規劃／驗證，
            // 最終 execution plan 會明示傳入（即使是空陣列）並建立 paired commit items。
            // 一次處理一個情境的三個動作沒有累進驗證，一定要帶 definitions。
            if (definitions is null)
            {
                if (ReviewedBatchActions.Contains(request.ActionName))
                {
                    throw new InvalidOperationException($"{request.ActionName} 必須帶要發布的 definitions。");
                }

                commitItems = [];
                return new FilterPlan(request, commitItems);
            }

            if (savedDefinitions.Count != request.Documents.Count)
            {
                throw new InvalidOperationException(
                    $"{request.ActionName} definitions 與 canonical documents 數量不一致。");
            }

            var items = new FilterCommitItem[savedDefinitions.Count];
            for (var index = 0; index < items.Length; index++)
            {
                var definition = savedDefinitions[index];
                var document = request.Documents[index];
                if (definition.Position != document.Position)
                {
                    throw new InvalidOperationException(
                        $"{request.ActionName} definition 與 canonical document 位置不一致。");
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

        return new FilterPlan(request, commitItems)
        {
            PreviewCountsWholeVoucherRows = isPreview && UsesLegacyWholeVoucherRows(request.Documents[0].Spec)
        };
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

        if (plan.PreviewCountsWholeVoucherRows && facts.Preview?.WholeVoucherRowCount is null)
        {
            throw new InvalidOperationException("filter.preview 未收到整張命中傳票的分錄數。");
        }

        if (PublishesBatch(plan.Request.ActionName)
            && facts.MaterializedScenarioCount != plan.Request.Documents.Count)
        {
            throw new InvalidOperationException(
                $"{plan.Request.ActionName} materialized scenario count 與 plan 不一致。");
        }

        return new FilterResult(
            plan,
            facts.Preview,
            facts.MaterializedScenarioCount);
    }

}
