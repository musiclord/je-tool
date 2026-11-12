using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JET.AuditCore;
using JET.Domain;

namespace JET.Application;

public sealed class QueryFilterVoucherPageHandler(ProjectSession session) : IApplicationActionHandler
{
    public string Action => "query.filterVoucherPage";

    public Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var (projectId, repositories) = session.RequireActive();
        return repositories.FilterVoucherQueryService.ReadAsync(projectId, payload, false, cancellationToken);
    }
}

public sealed class QueryFilterVoucherRowsPageHandler(ProjectSession session) : IApplicationActionHandler
{
    public string Action => "query.filterVoucherRowsPage";

    public Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var (projectId, repositories) = session.RequireActive();
        return repositories.FilterVoucherQueryService.ReadAsync(projectId, payload, true, cancellationToken);
    }
}

/// <summary>
/// 命中傳票分頁（傳票摘要與展開分錄）。草稿用同一個編譯器即時判斷條件；已儲存的情境讀儲存時存下的命中與
/// 符合第幾組第幾條，不再判斷條件。每一頁綁定情境、資料版本與查詢版本，上游變更後游標失效；
/// 已儲存情境的頁面只綁這個情境自己的版本，存或移除別的情境不會讓它過期。只有命中傳票，沒有待判定。
/// 案件編號由 handler 從作用中案件快照傳入。
/// </summary>
public sealed class FilterVoucherQueryService(IFilterVoucherRepository repository, IFilterScenarioStore scenarioStore,
    IMappingStateStore mappingStore, IAccountMappingStore accountMappingStore,
    IAuthorizedPreparerStore authorizedPreparerStore, IAccountCategoryStore categoryStore, ICalendarStore calendarStore,
    IProjectStore projectStore, ProjectSession session, IResultPageRdeValuesPort rdeValuesPort,
    FilterHitMaterializeService? materializeService = null, IHighRiskSummaryScenariosRepository? scenarioCounts = null)
{
    public async Task<object?> ReadAsync(string projectId, JsonElement payload, bool detail, CancellationToken ct)
    {
        var revision = await repository.ReadRevisionAsync(projectId, ct);
        var project = await projectStore.FindAsync(projectId, ct)
            ?? throw new JetActionException(JetErrorCodes.ProjectNotFound, "尚未開啟案件。");
        var mapping = await mappingStore.FindAsync(projectId, DatasetKind.Gl, ct)
            ?? throw new JetActionException(JetErrorCodes.GlMappingNotConfirmed, "尚未確認 GL 欄位配對，請先到第三步按「確認配對」。");
        var position = PayloadReader.GetOptionalInt(payload, "scenarioPosition");
        var hasDraft = payload.TryGetProperty("scenario", out var scenarioJson);
        if (hasDraft == position.HasValue)
            throw new JetActionException(JetErrorCodes.InvalidPayload, "請提供草稿或已儲存情境的位置，兩者擇一。");
        SavedFilterScenario? savedScenario = null;
        string? savedRevision = null;
        if (position is int selected)
        {
            var saved = await scenarioStore.ListAsync(projectId, ct);
            _ = FilterPopulationScopeParser.RequireCurrentRevision(saved);
            // 結果標成過期時（例如開案時整批改用新規則），和高風險條件彙總一樣先用目前規則重算全部情境，再讀存好的結果。
            if (materializeService is not null
                && await materializeService.MaterializeForConcurrentQueryAsync(projectId, ct) is { } refreshed)
            {
                saved = refreshed.Scenarios;
            }
            savedScenario = saved.SingleOrDefault(item => item.Position == selected);
            if (savedScenario is null
                || !FilterScenarioRevision.Covers(PayloadReader.GetOptionalString(payload, "scenarioRevision"), selected, savedScenario))
                throw Stale();
            savedRevision = FilterScenarioRevision.OfScenario(savedScenario);
            using var document = JsonDocument.Parse(savedScenario.DefinitionJson);
            scenarioJson = document.RootElement.Clone();
        }
        var spec = FilterScenarioPayloadParser.Parse(scenarioJson, project.MoneyScale);
        var accountMapping = await accountMappingStore.FindStateAsync(projectId, ct);
        var categorySnapshot = await categoryStore.ReadAsync(projectId, ct);
        var hasPreparers = await authorizedPreparerStore.CountAsync(projectId, ct) > 0;
        var calendarLists = await FilterCalendarLists.ReadAsync(calendarStore, projectId, ct);
        var scope = FilterPopulationScopeParser.ReadPayload(payload);
        var validation = FilterValidationContextFactory.Create(project, mapping, accountMapping, hasPreparers, scope, categorySnapshot, calendarLists);
        var errors = FilterScenarioValidator.Validate(spec, validation, forSave: false);
        if (errors.Count > 0) throw FilterScenarioErrorDetails.InvalidScenario(errors);
        var context = new FilterConditionContext(project.MoneyScale, project.LastAccountingPeriodDate,
            project.PeriodStart, project.PeriodEnd, project.NonWorkingDays, scope)
        {
            RdeFields = mapping.GlOptions?.RdeFields ?? [],
            DateParseOptions = project.DateParseOptions,
            ZerosThreshold = TrailingZeroThreshold.FromCommittedGl(mapping, project.MoneyScale)
        };
        var queryRevision = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            projectId, revision, savedRevision, definition = scenarioJson, context,
            mapping.SourceBatchId, mapping.CommittedUtc
        }))));
        var requiredRevision = PayloadReader.GetOptionalString(payload, "queryRevision");
        if (requiredRevision is not null && requiredRevision != queryRevision) throw Stale();
        var voucherNumber = detail ? PayloadReader.GetOptionalString(payload, "voucherNumber") : null;
        if (detail && string.IsNullOrEmpty(voucherNumber))
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
                if (parsed is null || parsed.QueryRevision != queryRevision || parsed.VoucherNumber != voucherNumber) throw Stale();
                key = parsed.Key;
            }
            catch (JsonException exception) { throw Stale(exception); }
        }
        // 傳票摘要頁可排序、可依傳票號碼查看；展開分錄頁固定依分錄順序，不吃 sort 與 search。
        var catalog = ResultPageSorting.FilterVoucher;
        var pageRequest = new PageRequest(key, Math.Clamp(PayloadReader.GetOptionalInt(payload, "pageSize") ?? 200, 1, 200),
            detail ? null : PageRequestReader.ReadSort(payload, catalog), detail ? null : PageRequestReader.ReadSearch(payload, catalog), IncludeTotalCount: key is null);
        // 只用「未預期借貸組合」一個條件時，Criteria Selection 摘要與工作底稿都以整張傳票計算；
        // 這裡的符合分錄數與展開分錄用同一個判斷，畫面才不會和報告不同。
        var wholeVoucher = JetAuditProgram.UsesLegacyWholeVoucherRows(spec);
        var page = savedScenario is null
            ? await repository.ReadAsync(projectId, spec, context, voucherNumber, pageRequest, wholeVoucher, ct)
            : await repository.ReadSavedAsync(projectId, savedScenario.ScenarioId, spec, context, voucherNumber, pageRequest, wholeVoucher, ct);
        // 已儲存情境打開時的兩個數字讀存好的結果，和高風險條件彙總同一個口徑；畫面不必再即時預覽一次。
        (long VoucherCount, long SummaryRowCount)? savedCounts = null;
        if (savedScenario is not null && scenarioCounts is not null && !detail && cursor is null)
        {
            var counts = (await scenarioCounts.GetCountsAsync(projectId, ct)).GetValueOrDefault(savedScenario.Position);
            var summaryRowCount = wholeVoucher
                ? (await scenarioCounts.GetWholeVoucherRowCountsAsync(projectId, [savedScenario.Position], ct))
                    .GetValueOrDefault(savedScenario.Position)
                : counts.RowHitCount;
            savedCounts = (counts.VoucherHitCount, summaryRowCount);
        }
        var columnPlan = ResultPageColumnRegistry.ForFilter(new SavedFilterScenario(1, spec.Name, spec.Rationale,
            scenarioJson.GetRawText(), DateTimeOffset.MinValue), mapping, project.MoneyScale);
        var entryIds = page.Details.Select(row => row.EntryId).ToArray();
        var customValues = ResultPageCustomValueRenderer.Render(entryIds,
            await rdeValuesPort.ReadAsync(projectId, entryIds, ct), columnPlan, project.MoneyScale);
        if (session.CurrentProjectId != projectId || await repository.ReadRevisionAsync(projectId, ct) != revision) throw Stale();
        if (savedScenario is not null)
        {
            var current = (await scenarioStore.ListAsync(projectId, ct)).SingleOrDefault(item => item.Position == savedScenario.Position);
            if (current is null || FilterScenarioRevision.OfScenario(current) != savedRevision) throw Stale();
        }
        // 整個情境只有整張傳票條件或不存在分類等傳票層條件時，列說明用「整張傳票條件成立」。
        var voucherConditionOnly = spec.Groups.Count > 0 && spec.Groups.All(group =>
            group.Conditions.All(rule => rule.IsVoucherCondition));
        var categoryLabels = categorySnapshot.Categories.ToDictionary(item => item.CategoryId, item => item.Label);
        var fieldLabels = context.RdeFields.ToDictionary(item => item.FieldId, item => item.Label);
        string Condition(FilterConditionPosition position)
        {
            if (position.Group < 1 || position.Group > spec.Groups.Count
                || position.Rule < 1 || position.Rule > spec.Groups[position.Group - 1].Conditions.Count) throw Stale();
            return $"第 {position.Group} 組條件 {position.Rule}："
            + FilterConditionRenderer.Render(JsonSerializer.SerializeToElement(new { groups = new[] { new { conditions = new[] {
                scenarioJson.GetProperty("groups")[position.Group - 1].GetProperty("conditions")[position.Rule - 1] } } } }), categoryLabels, fieldLabels, categorySnapshot.Categories, project.LastAccountingPeriodDate, project.PeriodStart, project.PeriodEnd,
                zerosThreshold: context.ZerosThreshold);
        }
        string Describe(FilterVoucherDetail row)
        {
            var descriptions = new List<string>();
            if (row.IsHit) descriptions.Add(voucherConditionOnly ? "整張傳票條件成立" : "符合條件的分錄");
            if (row.PrimaryConditions.Count > 0) descriptions.Add("符合主要條件：" + string.Join("；", row.PrimaryConditions.Select(Condition)));
            if (row.VoucherConditions.Count > 0) descriptions.Add("整張傳票條件成立：" + string.Join("；", row.VoucherConditions.Select(Condition)));
            if (descriptions.Count > 0) return string.Join("；", descriptions);
            return wholeVoucher ? "同一張傳票的其他分錄，整張傳票列入結果" : "同傳票的參考分錄";
        }
        var rows = detail
            ? page.Details.Select(row => (object)new
            {
                voucherNumber = row.VoucherNumber, lineItem = row.LineItem, glDate = row.GlDate,
                approvalDate = row.ApprovalDate, accountNumber = row.AccountNumber, accountName = row.AccountName,
                description = row.Description, amount = (decimal)row.AmountScaled / project.MoneyScale, drCr = row.DrCr,
                isHit = row.IsHit || wholeVoucher,
                primaryConditions = row.PrimaryConditions,
                voucherConditions = row.VoucherConditions,
                customValues = customValues[row.EntryId],
                matchDescription = Describe(row)
            }).ToArray()
            : page.Vouchers.Select(row => (object)new { voucherNumber = row.VoucherNumber, glDate = row.GlDate,
                hitRowCount = row.HitRowCount, totalRowCount = row.TotalRowCount,
                voucherTotal = (decimal)row.VoucherTotalScaled / project.MoneyScale }).ToArray();
        var columns = columnPlan.Columns.Select(column => new { key = column.Key, label = column.Label,
            valueType = column.ValueType, isCustom = column.IsCustom }).ToArray();
        var conditionText = FilterConditionRenderer.Render(scenarioJson, categoryLabels, fieldLabels, categorySnapshot.Categories, project.LastAccountingPeriodDate, project.PeriodStart, project.PeriodEnd,
            zerosThreshold: context.ZerosThreshold);
        var nextCursor = page.NextKey is null ? null : PageCursor.Encode(JsonSerializer.Serialize(new VoucherCursor(queryRevision, voucherNumber, page.NextKey)));
        if (savedCounts is null)
        {
            return new
            {
                rows, totalCount = page.TotalCount, queryRevision, scenarioRevision = savedRevision,
                columns, conditionText, nextCursor
            };
        }

        // 已儲存情境的第一頁另外帶存好的筆數，畫面打開情境時直接顯示，不再即時預覽。
        return new
        {
            rows, totalCount = page.TotalCount, queryRevision, scenarioRevision = savedRevision,
            columns, conditionText, nextCursor,
            voucherCount = savedCounts.Value.VoucherCount,
            summaryRowCount = savedCounts.Value.SummaryRowCount,
            countsWholeVoucherRows = wholeVoucher
        };
    }

    private sealed record VoucherCursor(string QueryRevision, string? VoucherNumber, string Key);
    private static JetActionException Stale(Exception? innerException = null) =>
        new(JetErrorCodes.StaleResult, "資料或條件已改變，請重新預覽再查看傳票。", innerException: innerException);
}
