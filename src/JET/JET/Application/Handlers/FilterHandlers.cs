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
                JetErrorCodes.NoTargetData,
                "尚未確認 GL 欄位配對，請先到第三步按「確認配對」。");

        var document = await projectStore.FindAsync(projectId, cancellationToken)
            ?? throw new JetActionException(JetErrorCodes.ProjectNotFound, $"找不到專案 '{projectId}'。");

        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("scenario", out var scenarioElement))
        {
            throw new JetActionException(JetErrorCodes.InvalidPayload, "缺少必要的條件內容，請重新開啟第五步再試一次。");
        }

        var populationScope = FilterPopulationScopeParser.ReadPayload(payload);

        var accountMappingState =
            await repositories.AccountMappings.FindStateAsync(projectId, cancellationToken);
        var hasAuthorizedPreparers =
            await repositories.AuthorizedPreparers.CountAsync(projectId, cancellationToken) > 0;
        var taxonomy = await repositories.AccountTaxonomy.ReadAsync(projectId, cancellationToken);

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
            taxonomy);
        var ruleContext = new FilterRuleContext(
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
                previewRows = preview.PreviewRows.Select(r => new
                {
                    documentNumber = r.DocumentNumber,
                    lineItem = r.LineItem,
                    postDate = r.PostDate,
                    accountCode = r.AccountCode,
                    accountName = r.AccountName,
                    documentDescription = r.DocumentDescription,
                    amount = (decimal)r.AmountScaled / scale,
                    drCr = r.DrCr
                }).ToArray()
            }
        };
    }
}

/// <summary>
/// filter.commit：保存情境定義（replace-all、≤10、名稱不可重複、逐情境重驗）。
/// 保存時以同一 AST set-based 落地命中，並回傳與本批 SavedUtc 相同的 revision。
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

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var (projectId, repositories) = session.RequireActive();
        await CompletenessEligibilitySupport.RequireCurrentAsync(
            repositories.RuleRuns,
            projectId,
            cancellationToken);

        var document = await projectStore.FindAsync(projectId, cancellationToken)
            ?? throw new JetActionException(JetErrorCodes.ProjectNotFound, $"找不到專案 '{projectId}'。");

        var glMapping = await repositories.MappingStates.FindAsync(projectId, DatasetKind.Gl, cancellationToken)
            ?? throw new JetActionException(
                JetErrorCodes.NoTargetData,
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
                $"最多儲存 {MaxScenarios} 個篩選情境。");
        }

        var populationScope = FilterPopulationScopeParser.ReadPayload(payload);

        var accountMappingState =
            await repositories.AccountMappings.FindStateAsync(projectId, cancellationToken);
        var hasAuthorizedPreparers =
            await repositories.AuthorizedPreparers.CountAsync(projectId, cancellationToken) > 0;
        var taxonomy = await repositories.AccountTaxonomy.ReadAsync(projectId, cancellationToken);

        var savedUtc = DateTimeOffset.UtcNow;
        var revision = savedUtc.ToUniversalTime().ToString("O");
        var saved = new List<SavedFilterScenario>();
        var canonicalDocuments = new List<CanonicalFilterDocument>();
        var seenNames = new HashSet<string>(StringComparer.Ordinal);
        var validationContext = FilterValidationContextFactory.Create(
            document,
            glMapping,
            accountMappingState,
            hasAuthorizedPreparers,
            populationScope,
            taxonomy);
        var ruleContext = new FilterRuleContext(
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

        foreach (var scenarioElement in scenariosElement.EnumerateArray())
        {
            // 留痕替補唯一收斂點：legacy KCT 來源豁免名稱/動機必填，但
            // config_filter_scenario.name/.rationale NOT NULL。先取得一基 position，讓同批空名
            // 得到穩定且不同的替補；去重與持久化都用替補後的有效名稱。
            var position = saved.Count + 1;
            var canonicalDocument = FilterScenarioPayloadParser.ParseDocument(
                scenarioElement,
                document.MoneyScale,
                position);
            canonicalDocuments.Add(canonicalDocument);
            _ = JetAuditProgram.Plan(new FilterRequest(
                Action,
                projectId,
                canonicalDocuments,
                ruleContext,
                validationContext));

            var spec = canonicalDocument.Spec;
            var (persistName, persistRationale) =
                FilterScenarioSources.ResolvePersistable(spec, position);

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
                RuleLogicVersions.StampFilterDefinition(rawDocument.RootElement),
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
        var facts = await repositories.FilterFacts.ExecuteAsync(
            plan,
            cancellationToken);
        var result = JetAuditProgram.Finalize(plan, facts);

        if (result.MaterializedScenarioCount > 0)
        {
            await MappingCommitShared.AdvanceStepAsync(
                projectStore,
                document,
                WorkflowMilestones.For(Action),
                CancellationToken.None, logger);
        }

        return new
        {
            ok = true,
            savedCount = saved.Count,
            scenarios = saved.Select(FilterScenarioSummaryRenderer.Render).ToArray(),
            resultRef = new
            {
                revision,
                generatedUtc = revision,
                logicVersion = RuleLogicVersions.Filter,
                populationScope = GlPopulationScopeValues.ToValue(populationScope)
            }
        };
    }
}

/// <summary>
/// Preview 與 commit 的單一 mapping-aware validation context 組裝點。
/// 欄位 availability 只由 Domain field catalog 的 semantic-field／mapping-slot 關係判定。
/// </summary>
internal static class FilterValidationContextFactory
{
    internal static FilterValidationContext Create(
        ProjectDocument document,
        CommittedMapping glMapping,
        AccountMappingState? accountMappingState,
        bool hasAuthorizedPreparers,
        GlPopulationScope populationScope,
        AccountTaxonomySnapshot? taxonomy = null) =>
        new(
            HasLastPeriodStart: document.LastAccountingPeriodDate is not null,
            HasAccountMapping: accountMappingState is not null,
            HasAuthorizedPreparers: hasAuthorizedPreparers,
            PopulationScope: populationScope,
            HasAnyAccountCategory: accountMappingState?.HasAnyCategory ?? false,
            HasRevenueCategory: accountMappingState?.HasRevenue ?? false,
            HasCounterpartCategory: accountMappingState?.HasCounterpart ?? false)
        {
            HasManualFlag = JetFieldCatalog.HasMappedGlSemanticField(
                glMapping.Mapping,
                JetFieldCatalog.GlManual),
            HasCreatedBy = JetFieldCatalog.HasMappedGlSemanticField(
                glMapping.Mapping,
                JetFieldCatalog.GlCreateBy),
            HasApprovedBy = JetFieldCatalog.HasMappedGlSemanticField(
                glMapping.Mapping,
                JetFieldCatalog.GlApproveBy),
            HasDescription = JetFieldCatalog.HasMappedGlSemanticField(
                glMapping.Mapping,
                JetFieldCatalog.GlDescription),
            AvailableGlFields = JetFieldCatalog.GlFields
                .Where(field => JetFieldCatalog.HasMappedGlSemanticField(glMapping.Mapping, field.SemanticIdentity)
                    || field.SemanticIdentity == JetFieldCatalog.GlDocDate
                    && glMapping.GlOptions?.ApprovalDateMode == ApprovalDateModeNames.SameAsPostDate)
                .Select(static field => field.SemanticIdentity).ToArray(),
            TaxonomyCategoryIds = taxonomy is null
                ? AccountTaxonomyCatalog.BuiltInSnapshot.Categories
                    .Select(static category => category.CategoryId)
                    .ToArray()
                : taxonomy.Categories.Select(static category => category.CategoryId).ToArray(),
            RdeFields = glMapping.GlOptions?.RdeFields ?? [],
            MoneyScale = document.MoneyScale,
            DateParseOptions = document.DateParseOptions
        };
}
