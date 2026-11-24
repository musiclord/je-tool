using System.Text.Json;
using JET.AuditCore;
using JET.Domain;
using Microsoft.Extensions.Logging;

namespace JET.Application;

/// <summary>
/// filter.preview：預覽一個還沒儲存的情境。先把前端送來的條件樹（AST）解析成規格，交給 Domain 驗證，
/// 再用參數化的集合式 SQL 算出符合的傳票數、分錄數與前幾筆分錄。不儲存任何東西，previewRows 最多 50 筆。
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
        var calendarLists = await FilterCalendarLists.ReadAsync(repositories.Calendar, projectId, cancellationToken);
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
            categorySnapshot,
            calendarLists);
        var ruleContext = new FilterConditionContext(
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
                    amount = MoneyScaling.FromScaled(r.AmountScaled, scale),
                    drCr = r.DrCr
                }).ToArray()
            }
        };
    }
}

/// <summary>
/// filter.commit：把已儲存的情境整批換成送來的這一批。最多 10 個，名稱不可重複，每個情境都重新驗證。
/// 儲存時用同一份條件樹，以集合式 SQL 算出命中的分錄並寫進 filter_hit，回應帶這一批的版本（見 FilterScenarioRevision）。
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
        var calendarLists = await FilterCalendarLists.ReadAsync(repositories.Calendar, projectId, cancellationToken);
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
            categorySnapshot,
            calendarLists);
        var ruleContext = new FilterConditionContext(
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

        var metadataContext = FilterScenarioMetadataContext.Create(document, glMapping, categorySnapshot);
        foreach (var scenarioElement in scenariosElement.EnumerateArray())
        {
            // 名稱或動機留白時依條件補上自動文字，和第五步畫面帶入的文字相同；
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

        // 情境定義與命中的 entry_id 由資料庫實作在同一個交易裡整批寫入；
        // 交易提交成功後才會推進步驟。
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
/// 第五步驗證要知道的兩份日曆清單有沒有上傳。沒有清單時，只看清單的條件停用並寫原因。
/// 第五步的六個驗證入口都從這裡讀，不各自查。
/// </summary>
internal readonly record struct FilterCalendarLists(bool HasHolidays, bool HasMakeupDays)
{
    internal static async Task<FilterCalendarLists> ReadAsync(
        ICalendarStore calendar,
        string projectId,
        CancellationToken cancellationToken) =>
        new(
            await calendar.CountAsync(projectId, CalendarDayType.Holiday, cancellationToken) > 0,
            await calendar.CountAsync(projectId, CalendarDayType.Makeup, cancellationToken) > 0);
}

/// <summary>
/// 預覽與儲存情境都從這裡建立驗證條件時要用的案件狀況（FilterValidationContext），內容依已確認的 GL 配對而定，不在別處另外組一份。
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
        AccountCategorySnapshot? categorySnapshot = null,
        FilterCalendarLists? calendarLists = null) =>
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
            NeverBlankGlFields = JetFieldCatalog.NeverBlankGlFields(glMapping.Mapping, glMapping.GlOptions),
            // 正式的六個入口都帶入案件有沒有國定假日清單與補班日清單；null 只供沒有案件資料的獨立測試。
            HasHolidayCalendar = calendarLists?.HasHolidays,
            HasMakeupCalendar = calendarLists?.HasMakeupDays,
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
