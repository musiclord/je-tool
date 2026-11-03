using System.Text.Json;
using JET.AuditCore;
using JET.Domain;
using Microsoft.Extensions.Logging;

namespace JET.Application;

/// <summary>
/// filter.preview：解析條件 AST → Domain 驗證 → 參數化 SQL set-based 評估
/// （無狀態，previewRows ≤ 50）。
/// </summary>
public sealed class FilterPreviewHandler : IApplicationActionHandler
{
    private readonly IProjectStore projectStore;
    private readonly ProjectSession session;

    internal FilterPreviewHandler(
        IProjectStore projectStore,
        ProjectSession session)
    {
        this.projectStore = projectStore;
        this.session = session;
    }

    public string Action => "filter.preview";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var (projectId, repositories) = session.RequireActive();

        var glMapping = await repositories.MappingStates.FindAsync(projectId, DatasetKind.Gl, cancellationToken)
            ?? throw new JetActionException(
                JetErrorCodes.GlMappingNotConfirmed,
                "尚未確認 GL 欄位配對，請先到第三步按「確認配對」。");

        var document = await projectStore.FindAsync(projectId, cancellationToken)
            ?? throw new JetActionException(JetErrorCodes.ProjectNotFound, ProjectFolderMessages.ActiveProjectMissing);

        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("scenario", out var scenarioElement))
        {
            throw new JetActionException(JetErrorCodes.InvalidPayload, "缺少必要的條件內容，請重新開啟第五步再試一次。");
        }

        var populationScope = FilterPopulationScopeParser.ReadPayload(payload);

        var accountMappingState =
            await repositories.AccountMappings.FindStateAsync(projectId, cancellationToken);
        var hasAuthorizedPreparers =
            await repositories.AuthorizedPreparers.CountAsync(projectId, cancellationToken) > 0;
        var categorySnapshot = await repositories.AccountCategories.ReadAsync(projectId, cancellationToken);

        var canonicalDocument = FilterScenarioPayloadParser.ParseDocument(
            scenarioElement,
            document.MoneyScale,
            position: 1);
        var validationContext = FilterValidationContextFactory.Create(
            document,
            glMapping,
            accountMappingState,
            hasAuthorizedPreparers,
            populationScope,
            categorySnapshot);
        var ruleContext = new FilterConditionContext(
            document.MoneyScale,
            document.LastAccountingPeriodDate,
            document.PeriodStart,
            document.PeriodEnd,
            document.NonWorkingDays,
            populationScope)
        {
            RdeFields = glMapping.GlOptions?.RdeFields ?? [],
            DateParseOptions = document.DateParseOptions
        };
        var plan = JetAuditProgram.Plan(new FilterRequest(
            Action,
            projectId,
            [canonicalDocument],
            ruleContext,
            validationContext));
        var facts = await repositories.FilterFacts.ExecuteAsync(
            plan,
            cancellationToken);
        var result = JetAuditProgram.Finalize(plan, facts);
        var preview = result.Preview
            ?? throw new InvalidOperationException("filter.preview 未產生 preview result。");
        var spec = canonicalDocument.Spec;

        var scale = document.MoneyScale;
        return new
        {
            scenario = new
            {
                name = spec.Name,
                populationScope = GlPopulationScopeValues.ToValue(populationScope),
                count = preview.Count,
                voucherCount = preview.VoucherCount,
                // 畫面上的「符合條件的分錄筆數」，口徑與 Criteria Selection 摘要相同。
                summaryRowCount = result.PreviewSummaryRowCount,
                countsWholeVoucherRows = plan.PreviewCountsWholeVoucherRows,
                previewRows = preview.PreviewRows.Select(r => new
                {
                    voucherNumber = r.VoucherNumber,
                    lineItem = r.LineItem,
                    glDate = r.GlDate,
                    accountNumber = r.AccountNumber,
                    accountName = r.AccountName,
                    description = r.Description,
                    amount = (decimal)r.AmountScaled / scale,
                    drCr = r.DrCr
                }).ToArray()
            }
        };
    }
}

/// <summary>
/// filter.commit：保存情境定義（replace-all、≤10、名稱不可重複、逐情境重驗）。
/// 保存時以同一 AST set-based 落地命中，並回傳這一批的版本（見 FilterScenarioRevision）。
/// </summary>
public sealed class FilterCommitHandler : IApplicationActionHandler
{
    private const int MaxScenarios = FilterScenarioLimits.MaxSavedScenarios;

    private readonly IProjectStore projectStore;
    private readonly ProjectSession session;
    private readonly ILogger? logger;

    internal FilterCommitHandler(
        IProjectStore projectStore,
        ProjectSession session, ILogger? logger = null)
    {
        this.projectStore = projectStore;
        this.session = session;
        this.logger = logger;
    }

    public string Action => "filter.commit";

    /// <summary>存下情境後，案件至少推進到第五步。</summary>
    internal const int WorkflowStep = 5;

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var (projectId, repositories) = session.RequireActive();
        SupportOperationStage.Enter("state_read");
        await CompletenessEligibilitySupport.RequireCurrentAsync(
            repositories.Results,
            projectId,
            cancellationToken);

        var document = await projectStore.FindAsync(projectId, cancellationToken)
            ?? throw new JetActionException(JetErrorCodes.ProjectNotFound, ProjectFolderMessages.ActiveProjectMissing);

        var glMapping = await repositories.MappingStates.FindAsync(projectId, DatasetKind.Gl, cancellationToken)
            ?? throw new JetActionException(
                JetErrorCodes.GlMappingNotConfirmed,
                "尚未確認 GL 欄位配對，請先到第三步按「確認配對」。");

        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("scenarios", out var scenariosElement)
            || scenariosElement.ValueKind != JsonValueKind.Array)
        {
            throw new JetActionException(JetErrorCodes.InvalidPayload, "缺少必要的條件內容，請重新開啟第五步再試一次。");
        }

        if (scenariosElement.GetArrayLength() > MaxScenarios)
        {
            throw new JetActionException(
                JetErrorCodes.ScenarioLimitReached,
                FilterScenarioLimits.MaxSavedScenariosMessage);
        }

        var populationScope = FilterPopulationScopeParser.ReadPayload(payload);

        var accountMappingState =
            await repositories.AccountMappings.FindStateAsync(projectId, cancellationToken);
        var hasAuthorizedPreparers =
            await repositories.AuthorizedPreparers.CountAsync(projectId, cancellationToken) > 0;
        var categorySnapshot = await repositories.AccountCategories.ReadAsync(projectId, cancellationToken);

        SupportOperationStage.Enter("scenario_parse");
        var savedUtc = DateTimeOffset.UtcNow;
        var generatedUtc = savedUtc.ToUniversalTime().ToString("O");
        var saved = new List<SavedFilterScenario>();
        var canonicalDocuments = new List<CanonicalFilterDocument>();
        var seenNames = new HashSet<string>(StringComparer.Ordinal);
        var validationContext = FilterValidationContextFactory.Create(
            document,
            glMapping,
            accountMappingState,
            hasAuthorizedPreparers,
            populationScope,
            categorySnapshot);
        var ruleContext = new FilterConditionContext(
            document.MoneyScale,
            document.LastAccountingPeriodDate,
            document.PeriodStart,
            document.PeriodEnd,
            document.NonWorkingDays,
            populationScope)
        {
            RdeFields = glMapping.GlOptions?.RdeFields ?? [],
            DateParseOptions = document.DateParseOptions
        };

        var metadataContext = FilterScenarioMetadataContext.Create(document, glMapping, categorySnapshot);
        foreach (var scenarioElement in scenariosElement.EnumerateArray())
        {
            // 裁定 2026-10-29-16：名稱或動機留白時依條件補上自動文字，和第五步畫面帶入的文字相同；
            // 去重、錯誤標示與存檔都用補上後的名稱。
            var position = saved.Count + 1;
            var canonicalDocument = FilterScenarioPayloadParser.ParseDocument(
                scenarioElement,
                document.MoneyScale,
                position);
            var (persistName, persistRationale) = FilterScenarioAutomaticMetadata.Resolve(
                scenarioElement, canonicalDocument.Spec, metadataContext, seenNames);
            canonicalDocument = canonicalDocument with
            {
                Spec = canonicalDocument.Spec with { Name = persistName, Rationale = persistRationale }
            };
            canonicalDocuments.Add(canonicalDocument);
            _ = JetAuditProgram.Plan(new FilterRequest(
                Action,
                projectId,
                canonicalDocuments,
                ruleContext,
                validationContext));

            if (!seenNames.Add(persistName))
            {
                throw new JetActionException(
                    JetErrorCodes.InvalidScenario,
                    $"情境名稱重複：「{persistName}」。");
            }

            using var rawDocument = JsonDocument.Parse(canonicalDocument.RawJson);
            saved.Add(new SavedFilterScenario(
                position,
                persistName,
                persistRationale,
                RuleLogicVersions.StampSavedFilterDefinition(rawDocument.RootElement),
                savedUtc));
        }

        var plan = JetAuditProgram.Plan(new FilterRequest(
            Action,
            projectId,
            canonicalDocuments,
            ruleContext,
            validationContext),
            saved);

        // definitions 與命中 entry_id 由 provider repository 在同一 transaction 整批發布；
        // 只有原子 commit 成功後才可能推進步驟。
        SupportOperationStage.Enter("filter_facts");
        var facts = await repositories.FilterFacts.ExecuteAsync(
            plan,
            cancellationToken);
        var result = JetAuditProgram.Finalize(plan, facts);

        SupportOperationStage.Enter("after_commit");
        if (result.MaterializedScenarioCount > 0)
        {
            await MappingCommitShared.AdvanceStepAsync(
                projectStore,
                document,
                WorkflowStep,
                CancellationToken.None, logger);
        }

        return new
        {
            ok = true,
            savedCount = saved.Count,
            scenarios = saved.Select(FilterScenarioSummaryRenderer.Render).ToArray(),
            resultRef = new
            {
                // 整批的版本和逐一儲存相同，見 FilterScenarioRevision；沒有情境時沿用保存時間。
                revision = saved.Count == 0 ? generatedUtc : FilterScenarioRevision.Of(saved),
                generatedUtc,
                logicVersion = RuleLogicVersions.Filter,
                populationScope = GlPopulationScopeValues.ToValue(populationScope)
            }
        };
    }
}

/// <summary>
/// Preview 與 commit 的單一 mapping-aware validation context 組裝點。
/// 能使用哪些 GL 欄位只由欄位目錄判定（<see cref="JetFieldCatalog.AvailableGlFields"/>），每條條件需要什麼只由規則需求表判定。
/// </summary>
internal static class FilterValidationContextFactory
{
    internal static FilterValidationContext Create(
        ProjectDocument document,
        CommittedMapping glMapping,
        AccountMappingState? accountMappingState,
        bool hasAuthorizedPreparers,
        GlPopulationScope populationScope,
        AccountCategorySnapshot? categorySnapshot = null) =>
        new(
            HasPrepStartDate: document.LastAccountingPeriodDate is not null,
            HasAccountMapping: accountMappingState is not null,
            HasAuthorizedPreparers: hasAuthorizedPreparers,
            PopulationScope: populationScope,
            HasAnyAccountCategory: accountMappingState?.HasAnyCategory ?? false,
            HasRevenueCategory: accountMappingState?.HasRevenue ?? false,
            HasCounterpartCategory: accountMappingState?.HasCounterpart ?? false)
        {
            AvailableGlFields = JetFieldCatalog.AvailableGlFields(glMapping.Mapping, glMapping.GlOptions),
            CategoryIds = categorySnapshot is null
                ? AccountCategoryCatalog.BuiltInSnapshot.Categories
                    .Select(static category => category.CategoryId)
                    .ToArray()
                : categorySnapshot.Categories.Select(static category => category.CategoryId).ToArray(),
            RdeFields = glMapping.GlOptions?.RdeFields ?? [],
            MoneyScale = document.MoneyScale,
            DateParseOptions = document.DateParseOptions,
            CategoryLabels = AccountCategoryLabels.From(categorySnapshot)
        };
}
