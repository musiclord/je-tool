using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JET.AuditCore;
using JET.Domain;

namespace JET.Application;

public sealed class QueryFilterVoucherPageHandler(FilterVoucherQueryService service) : IApplicationActionHandler
{
    public string Action => "query.filterVoucherPage";
    public Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken) => service.ReadAsync(payload, false, cancellationToken);
}

public sealed class QueryFilterVoucherRowsPageHandler(FilterVoucherQueryService service) : IApplicationActionHandler
{
    public string Action => "query.filterVoucherRowsPage";
    public Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken) => service.ReadAsync(payload, true, cancellationToken);
}

/// <summary>
/// 命中傳票分頁（傳票摘要與展開分錄）。草稿與已存情境用同一個編譯器；每一頁綁定情境、資料版本與查詢版本，
/// 上游變更後游標失效。只有命中傳票，沒有待判定（2026-09-07 裁定）。
/// </summary>
public sealed class FilterVoucherQueryService(IFilterVoucherRepository repository, IFilterScenarioStore scenarioStore,
    IMappingStateStore mappingStore, IAccountMappingStore accountMappingStore,
    IAuthorizedPreparerStore authorizedPreparerStore, IAccountTaxonomyStore taxonomyStore,
    IProjectStore projectStore, ProjectSession session, IResultPageRdeValuesPort rdeValuesPort)
{
    public async Task<object?> ReadAsync(JsonElement payload, bool detail, CancellationToken ct)
    {
        var projectId = session.RequireProjectId();
        var revision = await repository.ReadRevisionAsync(projectId, ct);
        var project = await projectStore.FindAsync(projectId, ct)
            ?? throw new JetActionException(JetErrorCodes.ProjectNotFound, "案件已離開，請重新載入。");
        await MappingReviewPrerequisite.EnsureSatisfiedAsync(projectId, mappingStore, ct);
        var mapping = await mappingStore.FindAsync(projectId, DatasetKind.Gl, ct)
            ?? throw new JetActionException(JetErrorCodes.NoTargetData, "請先完成 GL 欄位配對，再預覽篩選結果。");
        var position = PayloadReader.GetOptionalInt(payload, "scenarioPosition");
        var hasDraft = payload.TryGetProperty("scenario", out var scenarioJson);
        if (hasDraft == position.HasValue)
            throw new JetActionException(JetErrorCodes.InvalidPayload, "請提供草稿或已保存情境的位置，兩者擇一。");
        string? savedRevision = null;
        if (position is int selected)
        {
            var saved = await scenarioStore.ListAsync(projectId, ct);
            var current = FilterPopulationScopeParser.RequireCurrentRevision(saved);
            savedRevision = current.Revision;
            if (PayloadReader.GetOptionalString(payload, "scenarioRevision") != savedRevision || !current.Positions.Contains(selected))
                throw Stale();
            using var document = JsonDocument.Parse(saved.Single(item => item.Position == selected).DefinitionJson);
            scenarioJson = document.RootElement.Clone();
        }
        var spec = FilterScenarioPayloadParser.Parse(scenarioJson, project.MoneyScale);
        var accountMapping = await accountMappingStore.FindStateAsync(projectId, ct);
        var taxonomy = await taxonomyStore.ReadAsync(projectId, ct);
        var hasPreparers = await authorizedPreparerStore.CountAsync(projectId, ct) > 0;
        var scope = FilterPopulationScopeParser.ReadPayload(payload);
        var validation = FilterValidationContextFactory.Create(project, mapping, accountMapping, hasPreparers, scope, taxonomy);
        var errors = FilterScenarioValidator.Validate(spec, validation, forSave: false);
        if (errors.Count > 0) throw FilterScenarioErrorDetails.InvalidScenario(errors);
        var context = new FilterRuleContext(project.MoneyScale, project.LastAccountingPeriodDate,
            project.PeriodStart, project.PeriodEnd, project.NonWorkingDays, scope) { RdeFields = mapping.GlOptions?.RdeFields ?? [] };
        var queryRevision = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            projectId, revision, savedRevision, definition = scenarioJson, context,
            mapping.SourceBatchId, mapping.CommittedUtc
        }))));
        var requiredRevision = PayloadReader.GetOptionalString(payload, "queryRevision");
        if (requiredRevision is not null && requiredRevision != queryRevision) throw Stale();
        var documentNumber = detail ? PayloadReader.GetOptionalString(payload, "documentNumber") : null;
        if (detail && string.IsNullOrEmpty(documentNumber))
            throw new JetActionException(JetErrorCodes.InvalidPayload, "請先選擇要展開的傳票。");
        if (detail && requiredRevision is null) throw Stale();
        string? key = null;
        var cursor = PayloadReader.GetOptionalString(payload, "cursor");
        if (cursor is not null)
        {
            if (cursor.Length > 16_384 || !PageCursor.TryDecode(cursor, out var decoded)) throw Stale();
            try
            {
                var parsed = JsonSerializer.Deserialize<VoucherCursor>(decoded);
                if (parsed is null || parsed.QueryRevision != queryRevision || parsed.DocumentNumber != documentNumber) throw Stale();
                key = parsed.Key;
            }
            catch (JsonException) { throw Stale(); }
        }
        // 傳票摘要頁可排序、可依傳票號碼查看；展開分錄頁固定依分錄順序，不吃 sort 與 search。
        var catalog = ResultPageSorting.FilterVoucher;
        var pageRequest = new PageRequest(key, PayloadReader.GetOptionalInt(payload, "pageSize") ?? PageRequest.DefaultPageSize,
            detail ? null : PageRequestReader.ReadSort(payload, catalog), detail ? null : PageRequestReader.ReadSearch(payload, catalog));
        var page = await repository.ReadAsync(projectId, spec, context, documentNumber, pageRequest, ct);
        var columnPlan = ResultPageColumnRegistry.ForFilter(new SavedFilterScenario(1, spec.Name, spec.Rationale,
            scenarioJson.GetRawText(), DateTimeOffset.MinValue), mapping, project.MoneyScale);
        var entryIds = page.Details.Select(row => row.EntryId).ToArray();
        var customValues = ResultPageCustomValueRenderer.Render(entryIds,
            await rdeValuesPort.ReadAsync(projectId, entryIds, ct), columnPlan, project.MoneyScale);
        if (session.CurrentProjectId != projectId || await repository.ReadRevisionAsync(projectId, ct) != revision) throw Stale();
        if (savedRevision is not null)
        {
            var current = FilterPopulationScopeParser.RequireCurrentRevision(await scenarioStore.ListAsync(projectId, ct));
            if (current.Revision != savedRevision) throw Stale();
        }
        // 整個情境只有傳票量詞或不存在分類等傳票層條件時，列說明用「傳票條件成立」。
        var voucherConditionOnly = spec.Groups.Count > 0 && spec.Groups.All(group =>
            group.Rules.All(rule => rule.IsVoucherCondition));
        var categoryLabels = taxonomy.Categories.ToDictionary(item => item.CategoryId, item => item.Label);
        var fieldLabels = context.RdeFields.ToDictionary(item => item.FieldId, item => item.Label);
        string Condition(FilterConditionPosition position) => $"第 {position.Group} 組條件 {position.Rule}："
            + FilterConditionRenderer.Render(JsonSerializer.SerializeToElement(new { groups = new[] { new { rules = new[] {
                scenarioJson.GetProperty("groups")[position.Group - 1].GetProperty("rules")[position.Rule - 1] } } } }), categoryLabels, fieldLabels);
        string Describe(FilterVoucherDetail row)
        {
            var descriptions = new List<string>();
            if (row.IsHit) descriptions.Add(voucherConditionOnly ? "傳票條件成立" : "命中分錄");
            if (row.PrimaryConditions.Count > 0) descriptions.Add("符合主要條件：" + string.Join("；", row.PrimaryConditions.Select(Condition)));
            if (row.EvidenceConditions.Count > 0) descriptions.Add("提供佐證：" + string.Join("；", row.EvidenceConditions.Select(Condition)));
            if (row.VoucherConditions.Count > 0) descriptions.Add("傳票條件成立：" + string.Join("；", row.VoucherConditions.Select(Condition)));
            return descriptions.Count == 0 ? "同傳票的參考分錄" : string.Join("；", descriptions);
        }
        var rows = detail
            ? page.Details.Select(row => (object)new
            {
                documentNumber = row.DocumentNumber, lineItem = row.LineItem, postDate = row.PostDate,
                approvalDate = row.ApprovalDate, accountCode = row.AccountCode, accountName = row.AccountName,
                description = row.Description, amount = (decimal)row.AmountScaled / project.MoneyScale, drCr = row.DrCr,
                isHit = row.IsHit,
                primaryConditions = row.PrimaryConditions, evidenceConditions = row.EvidenceConditions,
                voucherConditions = row.VoucherConditions,
                customValues = customValues[row.EntryId],
                matchDescription = Describe(row)
            }).ToArray()
            : page.Vouchers.Select(row => (object)new { documentNumber = row.DocumentNumber, postDate = row.PostDate,
                hitRowCount = row.HitRowCount, totalRowCount = row.TotalRowCount,
                voucherTotal = (decimal)row.VoucherTotalScaled / project.MoneyScale }).ToArray();
        return new
        {
            rows, queryRevision, scenarioRevision = savedRevision,
            columns = columnPlan.Columns.Select(column => new { key = column.Key, label = column.Label,
                valueType = column.ValueType, isCustom = column.IsCustom }).ToArray(),
            conditionText = FilterConditionRenderer.Render(scenarioJson, categoryLabels, fieldLabels),
            nextCursor = page.NextKey is null ? null : PageCursor.Encode(JsonSerializer.Serialize(new VoucherCursor(queryRevision, documentNumber, page.NextKey)))
        };
    }

    private sealed record VoucherCursor(string QueryRevision, string? DocumentNumber, string Key);
    private static JetActionException Stale() => new(JetErrorCodes.StaleResult, "資料或條件已改變，請重新預覽再查看傳票。");
}
