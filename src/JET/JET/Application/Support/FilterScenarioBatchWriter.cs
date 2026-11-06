using System.Text.Json;
using JET.AuditCore;
using JET.Domain;
using Microsoft.Extensions.Logging;

namespace JET.Application;

/// <summary>一次只存、刪或重新檢查一個情境時用到的資料庫介面；由 handler 從作用中案件逐一取出後傳入。</summary>
internal sealed record FilterScenarioStores(
    IFilterScenarioStore Scenarios,
    IFilterFactsPort Facts,
    IResultStore Results,
    IResultStaleStateStore StaleStates,
    IMappingStateStore MappingStates,
    IAccountMappingStore AccountMappings,
    IAuthorizedPreparerStore AuthorizedPreparers,
    IAccountCategoryStore AccountCategories,
    ICalendarStore Calendar);

/// <summary>
/// 第五步一次只存、刪或重新檢查一個情境（裁定 2026-10-13-24）。
/// 後端是已存情境的唯一來源：畫面只送正在處理的那一個，其他情境由這裡從資料庫讀出、照原樣寫回，
/// 不經畫面重組，也不用儲存規則重新檢查。寫回的內容由 <see cref="FilterScenarioRuleUpgrade.PlanWrite"/> 決定：
/// 平常儲存只算這一個情境的結果、移除只刪這一個的結果，其他情境存好的結果不重算（裁定 2026-10-30-33）；有不合格的情境時只寫定義、
/// 清掉結果，高風險條件彙總、報告與第六步維持鎖住。
/// </summary>
internal static class FilterScenarioBatchWriter
{
    internal const string SaveAction = "filter.scenario.save";
    internal const string RemoveAction = "filter.scenario.remove";
    internal const string RecheckAction = "filter.scenario.recheck";

    private const string MissingContentMessage = "缺少必要的條件內容，請重新開啟第五步再試一次。";

    /// <summary>三個動作共用的前置條件與驗證環境，與 filter.commit 相同。</summary>
    internal sealed record Prepared(
        string ProjectId,
        ProjectDocument Document,
        FilterValidationContext Validation,
        FilterConditionContext Rule,
        GlPopulationScope PopulationScope,
        FilterScenarioMetadataContext Metadata);

    /// <summary>要寫回的整批，以及這次存下的那一個在清單上的位置（儲存才有）或移除的那一個（移除才有）。</summary>
    internal sealed record Change(
        IReadOnlyList<SavedFilterScenario> Batch,
        int? EditedPosition,
        FilterScenarioWriteMode Mode,
        SavedFilterScenario? Removed = null);

    internal static async Task<Prepared> LoadAsync(
        IProjectStore projectStore,
        string projectId,
        FilterScenarioStores stores,
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        // 和 filter.commit 一樣要求驗證結果是目前的。
        await CompletenessEligibilitySupport.RequireCurrentAsync(stores.Results, projectId, cancellationToken);

        var document = await projectStore.FindAsync(projectId, cancellationToken)
            ?? throw new JetActionException(JetErrorCodes.ProjectNotFound, ProjectFolderMessages.ActiveProjectMissing);

        var glMapping = await stores.MappingStates.FindAsync(projectId, DatasetKind.Gl, cancellationToken)
            ?? throw new JetActionException(
                JetErrorCodes.GlMappingNotConfirmed,
                "尚未確認 GL 欄位配對，請先到第三步按「確認配對」。");

        if (payload.ValueKind != JsonValueKind.Object)
        {
            throw new JetActionException(JetErrorCodes.InvalidPayload, MissingContentMessage);
        }

        var populationScope = FilterPopulationScopeParser.ReadPayload(payload);
        var accountMappingState = await stores.AccountMappings.FindStateAsync(projectId, cancellationToken);
        var hasAuthorizedPreparers = await stores.AuthorizedPreparers.CountAsync(projectId, cancellationToken) > 0;
        var calendarLists = await FilterCalendarLists.ReadAsync(stores.Calendar, projectId, cancellationToken);
        var categorySnapshot = await stores.AccountCategories.ReadAsync(projectId, cancellationToken);

        var validation = FilterValidationContextFactory.Create(
            document,
            glMapping,
            accountMappingState,
            hasAuthorizedPreparers,
            populationScope,
            categorySnapshot,
            calendarLists);
        var rule = new FilterConditionContext(
            document.MoneyScale,
            document.LastAccountingPeriodDate,
            document.PeriodStart,
            document.PeriodEnd,
            document.NonWorkingDays,
            populationScope)
        {
            RdeFields = glMapping.GlOptions?.RdeFields ?? [],
            DateParseOptions = document.DateParseOptions,
            ZerosThreshold = TrailingZeroThreshold.FromCommittedGl(glMapping, document.MoneyScale)
        };
        return new Prepared(projectId, document, validation, rule, populationScope,
            FilterScenarioMetadataContext.Create(document, glMapping, categorySnapshot));
    }

    /// <summary>
    /// 儲存一個情境。<c>position</c> 為 null 表示新增或另存副本，加在清單最後；有值時取代清單上那一個，
    /// 而且 <c>expectedName</c> 必須是資料庫那一筆的名稱，否則不寫入。只用儲存規則檢查這一個。
    /// </summary>
    internal static Change PlanSave(
        Prepared prepared,
        IReadOnlyList<SavedFilterScenario> saved,
        JsonElement payload,
        DateTimeOffset nowUtc)
    {
        if (!payload.TryGetProperty("scenario", out var scenarioElement))
        {
            throw new JetActionException(JetErrorCodes.InvalidPayload, MissingContentMessage);
        }

        var position = ReadPosition(payload, required: false);
        int target;
        if (position is null)
        {
            if (saved.Count >= FilterScenarioLimits.MaxSavedScenarios)
            {
                throw new JetActionException(
                    JetErrorCodes.ScenarioLimitReached,
                    FilterScenarioLimits.MaxSavedScenariosMessage);
            }

            target = saved.Count + 1;
        }
        else
        {
            RequireShownScenario(saved, position.Value, ReadExpectedName(payload));
            target = position.Value;
        }

        CanonicalFilterDocument canonical;
        try
        {
            canonical = FilterScenarioPayloadParser.ParseDocument(
                scenarioElement,
                prepared.Document.MoneyScale,
                target);
        }
        catch (JetActionException exception) when (exception.Code == JetErrorCodes.InvalidScenario)
        {
            // 形狀錯誤（例如條件類型或比較方式已停用）也標上是這個情境，畫面才知道錯誤屬於目前草稿。
            throw FilterScenarioErrorDetails.ForScenario(exception, target, ShownName(scenarioElement));
        }

        // 裁定 2026-10-29-16：名稱或動機留白時依條件補上自動文字；撞名時和第五步畫面一樣附上數字，編輯中的情境不和自己比。
        var (persistName, persistRationale) = FilterScenarioAutomaticMetadata.Resolve(
            scenarioElement,
            canonical.Spec,
            prepared.Metadata,
            saved.Where((_, index) => index != target - 1).Select(other => other.Name));
        canonical = canonical with { Spec = canonical.Spec with { Name = persistName, Rationale = persistRationale } };
        var errors = FilterScenarioValidator.Validate(canonical.Spec, prepared.Validation, forSave: true);
        if (errors.Count > 0)
        {
            throw FilterScenarioErrorDetails.InvalidScenario(errors, target, persistName);
        }

        if (saved.Where((_, index) => index != target - 1)
            .Any(other => string.Equals(other.Name, persistName, StringComparison.Ordinal)))
        {
            throw new JetActionException(
                JetErrorCodes.InvalidScenario,
                $"情境名稱重複：「{persistName}」。");
        }

        using var raw = JsonDocument.Parse(canonical.RawJson);
        var row = new SavedFilterScenario(
            target,
            persistName,
            persistRationale,
            // 驗證通過後才整理：這個情境的條件只保留各類型用得到的屬性（裁定 2026-10-15-01）。
            RuleLogicVersions.StampSavedFilterDefinition(raw.RootElement),
            nowUtc)
        {
            // 修改沿用原本的情境編號；新增取目前最大的編號加一，不和還在的情境重複。
            ScenarioId = target <= saved.Count
                ? saved[target - 1].ScenarioId
                : saved.Count == 0 ? 1 : saved.Max(other => other.ScenarioId) + 1
        };
        var batch = saved.ToList();
        if (target <= batch.Count)
        {
            batch[target - 1] = row;
        }
        else
        {
            batch.Add(row);
        }

        return new Change(batch, target, FilterScenarioWriteMode.KeepOthersVerbatim);
    }

    /// <summary>移除清單上的一個情境；其他情境不重新檢查、不改內容。</summary>
    internal static Change PlanRemove(IReadOnlyList<SavedFilterScenario> saved, JsonElement payload)
    {
        var position = ReadPosition(payload, required: true)!.Value;
        RequireShownScenario(saved, position, ReadExpectedName(payload));
        var batch = saved.Where((_, index) => index != position - 1).ToArray();
        return new Change(batch, null, FilterScenarioWriteMode.KeepOthersVerbatim, saved[position - 1]);
    }

    /// <summary>重新檢查：能套用目前規則的情境改用目前規則，不能套用的照原樣保留並列出原因。</summary>
    internal static Change PlanRecheck(IReadOnlyList<SavedFilterScenario> saved) =>
        new(saved, null, FilterScenarioWriteMode.UpgradeApplicable);

    /// <summary>
    /// 寫回並組成回應。儲存或移除時，其他情境都合格、已是目前規則而且存好的結果沒有過期，就只寫這一個：
    /// 儲存只算這一個情境的結果，移除只刪這一個的定義與結果，其他情境不重新檢查版本也不重算。
    /// 否則照整批處理：全部合格時用 filter facts 發布定義與命中（篩選失效旗標歸零），並把案件推進到第五步；
    /// 有不合格的情境時只寫定義，同一筆交易清掉命中並標成失效，結果維持鎖住，和改成逐一存放之前相同。
    /// 清單是空的而且不必寫入（重新檢查時沒有情境）就直接回應。
    /// </summary>
    internal static async Task<object> PublishAsync(
        string action,
        int workflowStep,
        IProjectStore projectStore,
        FilterScenarioStores stores,
        Prepared prepared,
        Change change,
        bool skipWhenEmpty,
        DateTimeOffset nowUtc,
        ILogger? logger,
        CancellationToken cancellationToken)
    {
        var resultsCurrent = !(await stores.StaleStates.ReadAsync(prepared.ProjectId, cancellationToken)).Filter;
        var write = FilterScenarioRuleUpgrade.PlanWrite(
            change.Batch,
            change.EditedPosition,
            change.Mode,
            prepared.Validation,
            prepared.Document.MoneyScale,
            nowUtc,
            othersResultsCurrent: resultsCurrent);
        FilterScenarioDiagnostics.LogProblems(logger, action, write.Problems);

        if (!(skipWhenEmpty && write.Rows.Count == 0))
        {
            if (write.KeepsOtherResults)
            {
                if (change.EditedPosition is int edited)
                {
                    var row = write.Rows[edited - 1];
                    using var definition = JsonDocument.Parse(row.DefinitionJson);
                    var document = FilterScenarioPayloadParser.ParseDocument(
                        definition.RootElement,
                        prepared.Document.MoneyScale,
                        row.Position);
                    var plan = JetAuditProgram.PlanOneScenario(
                        new FilterRequest(action, prepared.ProjectId, [document], prepared.Rule, prepared.Validation),
                        row);
                    JetAuditProgram.Finalize(plan, await stores.Facts.ExecuteAsync(plan, cancellationToken));
                }
                else if (change.Removed is not null)
                {
                    await stores.Scenarios.RemoveAsync(prepared.ProjectId, change.Removed, cancellationToken);
                }

                if (write.Rows.Count > 0)
                {
                    await MappingCommitShared.AdvanceStepAsync(
                        projectStore,
                        prepared.Document,
                        workflowStep,
                        CancellationToken.None,
                        logger);
                }
            }
            else if (write.AllValid)
            {
                var documents = write.Rows.Select(row =>
                {
                    using var definition = JsonDocument.Parse(row.DefinitionJson);
                    return FilterScenarioPayloadParser.ParseDocument(
                        definition.RootElement,
                        prepared.Document.MoneyScale,
                        row.Position);
                }).ToArray();
                var plan = JetAuditProgram.Plan(
                    new FilterRequest(action, prepared.ProjectId, documents, prepared.Rule, prepared.Validation),
                    write.Rows);
                var facts = await stores.Facts.ExecuteAsync(plan, cancellationToken);
                var result = JetAuditProgram.Finalize(plan, facts);
                if (result.MaterializedScenarioCount > 0)
                {
                    await MappingCommitShared.AdvanceStepAsync(
                        projectStore,
                        prepared.Document,
                        workflowStep,
                        CancellationToken.None,
                        logger);
                }
            }
            else
            {
                await stores.Scenarios.ReplaceAllAsync(prepared.ProjectId, write.Rows, cancellationToken);
            }
        }

        // 寫入已提交；之後的取消不能丟掉權威狀態。
        var current = await WorkflowResultStateSupport.ReadCurrentAsync(
            prepared.ProjectId,
            stores.Results,
            stores.StaleStates,
            stores.Scenarios,
            CancellationToken.None);
        var revision = write.AllValid && write.Rows.Count > 0 ? current.FilterRevision : null;
        return new
        {
            ok = true,
            position = change.EditedPosition,
            savedCount = write.Rows.Count,
            scenarios = write.Rows.Select(FilterScenarioSummaryRenderer.Render).ToArray(),
            resultRef = revision is null ? null : new
            {
                revision = revision.Revision,
                generatedUtc = revision.GeneratedUtc,
                logicVersion = RuleLogicVersions.Filter,
                populationScope = GlPopulationScopeValues.ToValue(revision.PopulationScope)
            },
            filterScenarioCheck = new
            {
                status = write.AllValid ? "current" : "needsEdit",
                recalculatedCount = write.UpgradedOthers,
                problems = write.Problems.Select(problem => new
                {
                    position = problem.Position,
                    name = problem.Name,
                    messages = problem.Messages
                }).ToArray()
            },
            staleState = new
            {
                validation = current.StaleState.Validation,
                prescreen = current.StaleState.Prescreen,
                filter = current.StaleState.Filter
            }
        };
    }

    /// <summary>位置是畫面清單上的第幾個（從 1 起算）。省略或 null 只在 <paramref name="required"/> 為 false 時允許。</summary>
    private static int? ReadPosition(JsonElement payload, bool required)
    {
        if (!payload.TryGetProperty("position", out var property)
            || property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return required
                ? throw new JetActionException(JetErrorCodes.InvalidPayload, MissingContentMessage)
                : null;
        }

        if (property.ValueKind != JsonValueKind.Number || !property.TryGetInt32(out var position) || position < 1)
        {
            throw new JetActionException(JetErrorCodes.InvalidPayload, MissingContentMessage);
        }

        return position;
    }

    /// <summary>畫面上這個情境目前的名稱；比對時不修剪空白，必須與資料庫那一筆完全相同。</summary>
    private static string ReadExpectedName(JsonElement payload) =>
        payload.TryGetProperty("expectedName", out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()!
            : throw new JetActionException(JetErrorCodes.InvalidPayload, MissingContentMessage);

    private static void RequireShownScenario(IReadOnlyList<SavedFilterScenario> saved, int position, string expectedName)
    {
        if (position > saved.Count
            || !string.Equals(saved[position - 1].Name, expectedName, StringComparison.Ordinal))
        {
            throw new JetActionException(JetErrorCodes.StaleResult, FilterScenarioRuleMessages.ScenarioListOutOfDate);
        }
    }

    private static string? ShownName(JsonElement scenario) =>
        scenario.ValueKind == JsonValueKind.Object
        && scenario.TryGetProperty("name", out var name)
        && name.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(name.GetString())
            ? name.GetString()!.Trim()
            : null;
}
