using JET.Domain;

namespace JET.AuditCore;

/// <summary>
/// 一個篩選情境的定義。<c>RawJson</c> 是原始 JSON，存進資料庫的就是這一份；<c>Spec</c> 是解析後的內容，
/// 供檢查與組 SQL 使用。不會反過來用 <c>Spec</c> 重組原始 JSON。
/// </summary>
internal sealed record CanonicalFilterDocument(
    int Position,
    string RawJson,
    FilterScenarioSpec Spec);

/// <summary>
/// Application 交來的輸入，用於 <c>filter.preview</c>、<c>filter.commit</c> 或一次處理一個情境的動作；
/// 情境的數量有上限。
/// </summary>
internal sealed record FilterRequest(
    string ActionName,
    string ProjectId,
    IReadOnlyList<CanonicalFilterDocument> Documents,
    FilterConditionContext RuleContext,
    FilterValidationContext ValidationContext);

/// <summary>AuditCore 產生的篩選計畫；預覽和把篩選結果寫進資料庫都用這一份。</summary>
internal sealed record FilterPlan(
    FilterRequest Request,
    IReadOnlyList<FilterCommitItem> CommitItems)
{
    /// <summary>
    /// 預覽的「符合條件的分錄筆數」用整張命中傳票的分錄數。判斷與 Criteria Selection 摘要、
    /// 工作底稿逐列標記相同（<see cref="JetAuditProgram.UsesLegacyWholeVoucherRows"/>），畫面才不會和報告不同。
    /// </summary>
    internal bool PreviewCountsWholeVoucherRows { get; init; }

    /// <summary>
    /// 只儲存或修改一個情境：換掉這個情境的定義與結果，其他情境的定義與存好的結果照原樣，不重算。
    /// 由 <see cref="JetAuditProgram.PlanOneScenario"/> 設定。
    /// </summary>
    internal bool SavesOneScenario { get; init; }
}

/// <summary>Infrastructure 執行篩選後回傳的原始結果：預覽的數字，以及寫進資料庫的情境數。</summary>
internal sealed record FilterFacts(
    FilterPreviewResult? Preview,
    int MaterializedScenarioCount);

/// <summary><c>JetAuditProgram.Finalize</c> 核對過的篩選結果；轉成 action 回應的格式由 Application 負責。</summary>
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
/// 執行篩選的介面，由 Infrastructure 實作。實作負責把參數綁到資料庫命令、用集合式 SQL 執行，
/// 並把篩選結果寫進資料庫；情境是否有效不由它判斷。
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
    /// 規劃一份要預覽的情境，或一組數量有上限、要發布的情境。條件規則只由 Domain 的
    /// <see cref="FilterScenarioValidator"/> 檢查；AuditCore 決定在流程的哪個時點檢查。
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
                        document.Spec.Name);
            }
        }

        var savedDefinitions = definitions ?? [];
        IReadOnlyList<FilterCommitItem> commitItems;
        if (!isPreview)
        {
            // filter.commit 的 handler 逐份解析情境時，每解析一份，就把到目前為止解析好的情境交給 Plan 檢查一次。這時還沒有 definitions，
            // 所以只檢查，不建立發布項目。最後要執行的那一次一定會帶 definitions，空陣列也照帶，
            // 這時才把每份 definition 和它的 Spec 配成一個發布項目。
            // 一次處理一個情境的三個動作沒有逐份檢查這一段，一定要帶 definitions。
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

    /// <summary>
    /// 一次只存或改一個情境、其他情境的結果仍可用時的計畫：只帶這一個情境的定義，檢查與整批發布相同，
    /// 執行時只換掉它自己的定義與結果。
    /// </summary>
    internal static FilterPlan PlanOneScenario(
        FilterRequest request,
        SavedFilterScenario definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (!ReviewedBatchActions.Contains(request.ActionName) || request.Documents.Count != 1)
        {
            throw new InvalidOperationException($"{request.ActionName} 只儲存一個情境時必須且只能規劃一個情境。");
        }

        return Plan(request, [definition]) with { SavesOneScenario = true };
    }

    /// <summary>
    /// 核對 Infrastructure 回傳的結果：預覽要有預覽數字，發布時寫進資料庫的情境數要和計畫相同。
    /// 核對過再包成 <see cref="FilterResult"/>；不改預覽數字，也不改命中的分錄。
    /// </summary>
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
