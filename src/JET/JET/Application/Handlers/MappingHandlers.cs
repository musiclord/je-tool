using System.Globalization;
using System.Text.Json;
using JET.AuditCore;
using JET.Domain;
using Microsoft.Extensions.Logging;

namespace JET.Application;

/// <summary>
/// 從 JET 報告讀回 GL/TB mapping 草稿。唯讀：不保存 mapping、不投影 target、不推進流程。
/// 相容性以目前兩個 import batch 的 columns 和 commit 共用 MappingValidator 權威驗證。
/// </summary>
public sealed class MappingRestoreDraftHandler(
    IMappingMetadataReader metadataReader,
    MappingRestoreDraftAuthorizationStore restoreAuthorizations,
    ProjectSession session) : IApplicationActionHandler
{
    public string Action => "mapping.restoreDraft";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var (projectId, repositories) = session.RequireActive();
        var filePath = PayloadReader.GetRequiredString(payload, "filePath");
        var metadata = await metadataReader.ReadAsync(filePath, cancellationToken);

        var glBatch = await repositories.Imports.GetLatestBatchAsync(
            projectId, DatasetKind.Gl, cancellationToken)
            ?? throw new JetActionException(
                JetErrorCodes.NoImportBatch,
                "目前案件尚未匯入 GL，不能驗證還原草稿的來源欄位。");
        var tbBatch = await repositories.Imports.GetLatestBatchAsync(
            projectId, DatasetKind.Tb, cancellationToken)
            ?? throw new JetActionException(
                JetErrorCodes.NoImportBatch,
                "目前案件尚未匯入 TB，不能驗證還原草稿的來源欄位。");

        if (!GlAmountModeNames.TryParse(metadata.Gl.AmountMode, out var glMode)
            || !TbChangeModeNames.TryParse(metadata.Tb.ChangeMode, out var tbMode))
        {
            throw new JetActionException(
                JetErrorCodes.MappingMetadataInvalid,
                "報告裡的欄位配對使用了目前版本不支援的金額計算方式，無法還原。請回第三步欄位配對直接重新選擇。");
        }

        var glValidation = MappingValidator.ValidateGl(
            new GlMappingSpec(metadata.Gl.Mapping, glMode), glBatch.Columns.ToList());
        var tbValidation = MappingValidator.ValidateTb(
            new TbMappingSpec(metadata.Tb.Mapping, tbMode), tbBatch.Columns.ToList());

        var missing = glValidation.MissingRequiredKeys.Select(key => $"GL.{key}")
            .Concat(tbValidation.MissingRequiredKeys.Select(key => $"TB.{key}"))
            .ToArray();
        if (missing.Length > 0)
        {
            if (glValidation.MissingRequiredKeys.Any(key => key is "dcDebitCode" or "dcCreditCode"))
            {
                throw new JetActionException(
                    JetErrorCodes.MappingMetadataInvalid,
                    "報告裡的欄位配對未完整記錄借方代碼與貸方代碼，無法還原。請回第三步手動指定兩個代碼後再確認配對。");
            }
            throw new JetActionException(
                JetErrorCodes.MappingMetadataInvalid,
                $"報告裡的欄位配對缺少必要欄位：{string.Join("、", missing)}，無法還原。請回第三步欄位配對直接重新選擇。");
        }

        var unknownColumns = glValidation.UnknownColumns.Select(column => $"GL:{column}")
            .Concat(tbValidation.UnknownColumns.Select(column => $"TB:{column}"))
            .ToArray();
        if (unknownColumns.Length > 0)
        {
            throw new JetActionException(
                JetErrorCodes.MappingColumnNotFound,
                $"報告裡的欄位配對選到目前匯入資料中沒有的欄位：{string.Join("、", unknownColumns)}。請回第三步欄位配對重新選擇。");
        }

        GlMappingOptions glOptions;
        try
        {
            glOptions = GlMappingOptionsRules.NormalizeAndValidate(
                metadata.Gl.Mapping,
                glBatch.Columns,
                metadata.Gl.ToOptions());
        }
        catch (ArgumentException exception)
        {
            throw new JetActionException(
                JetErrorCodes.MappingMetadataInvalid,
                "報告裡的欄位配對進階設定與目前匯入的資料不一致，無法還原。請回第三步欄位配對直接重新選擇。",
                innerException: exception);
        }

        // restoreDraft 是唯一可把既有 backend stable ID 帶回一個已失效 mapping 的可信入口。
        // 授權綁目前 project/batch 與完整 canonical definition；不接受 caller 自行 mint ID。
        restoreAuthorizations.Authorize(projectId, glBatch.BatchId, glOptions.RdeFields);

        return new
        {
            formatVersion = MappingMetadataFormat.CurrentVersion,
            gl = new
            {
                mapping = metadata.Gl.Mapping,
                amountMode = metadata.Gl.AmountMode,
                approvalDateMode = glOptions.ApprovalDateMode,
                postingStatusPolicy = glOptions.PostingStatusPolicy,
                manualAutoPolicy = glOptions.ManualAutoPolicy,
                rdeFields = glOptions.RdeFields
            },
            tb = new { mapping = metadata.Tb.Mapping, changeMode = metadata.Tb.ChangeMode }
        };
    }
}

public sealed class MappingCommitGlHandler : IApplicationActionHandler
{
    private readonly MappingRestoreDraftAuthorizationStore restoreAuthorizations;
    private readonly IProjectStore projectStore;
    private readonly ProjectSession session;
    private readonly IJetEventPublisher eventPublisher;
    private readonly ILogger? logger;

    internal MappingCommitGlHandler(
        MappingRestoreDraftAuthorizationStore restoreAuthorizations,
        IProjectStore projectStore,
        ProjectSession session,
        IJetEventPublisher eventPublisher, ILogger? logger = null)
    {
        this.restoreAuthorizations = restoreAuthorizations;
        this.projectStore = projectStore;
        this.session = session;
        this.eventPublisher = eventPublisher;
        this.logger = logger;
    }

    public string Action => "mapping.commit.gl";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var (projectId, repositories) = session.RequireActive();

        var mapping = PayloadReader.GetStringMap(payload, "mapping");
        var amountModeName = PayloadReader.GetRequiredString(payload, "amountMode");
        var amountMode = JetAuditProgram.ParseGlAmountMode(amountModeName);
        var postingStatusPolicy = MappingV2CommitPayloadPrerequisite.ReadPostingStatusPolicy(payload);

        var batch = await repositories.Imports.GetLatestBatchAsync(projectId, DatasetKind.Gl, cancellationToken)
            ?? throw new JetActionException(
                JetErrorCodes.NoImportBatch,
                "尚未匯入 GL 資料，請先在第二步匯入總帳。");
        var existingMapping = await repositories.MappingStates.FindAsync(
            projectId,
            DatasetKind.Gl,
            cancellationToken);
        var recordsRecommit = existingMapping is not null
            || await repositories.ProjectAuditLog.RequiresMappingRecommitAuditAsync(projectId, "gl", cancellationToken);
        // 重新匯入會刪掉已確認的配對，但畫面的草稿仍帶著上次確認時發出的攸關資料元素欄位身分。
        // 還沒重新確認時沿用那一份，草稿才送得出去，已儲存情境裡指到這些欄位的條件也還對得上。
        var reusableMapping = existingMapping
            ?? await repositories.MappingStates.FindPreviousAsync(projectId, DatasetKind.Gl, cancellationToken);
        var existingRdeIds = reusableMapping?.GlOptions?.RdeFields
            .Select(static field => field.FieldId)
            .ToHashSet(StringComparer.Ordinal)
            ?? [];
        var permittedRdeIds = existingRdeIds.ToHashSet(StringComparer.Ordinal);
        permittedRdeIds.UnionWith(
            restoreAuthorizations.GetAuthorizedFieldIds(projectId, batch.BatchId));
        var projectionOptions = MappingV2CommitPayloadPrerequisite.ReadProjectionOptions(
            payload,
            mapping,
            postingStatusPolicy,
            permittedRdeIds);
        var document = await projectStore.FindAsync(projectId, cancellationToken)
            ?? throw new JetActionException(JetErrorCodes.ProjectNotFound, $"找不到專案 '{projectId}'。");

        var plan = JetAuditProgram.Plan(
            new GlMappingRequest(
                projectId,
                batch.BatchId,
                mapping,
                amountMode,
                batch.Columns,
                document.MoneyScale,
                document.DateParseOptions,
                DateOnly.ParseExact(document.PeriodStart, "yyyy-MM-dd", CultureInfo.InvariantCulture),
                DateOnly.ParseExact(document.PeriodEnd, "yyyy-MM-dd", CultureInfo.InvariantCulture),
                postingStatusPolicy,
                DateTimeOffset.UtcNow)
            {
                ProjectionOptions = projectionOptions
            });
        restoreAuthorizations.EnsureMatches(
            projectId,
            batch.BatchId,
            payload,
            plan.Spec.Options.RdeFields,
            existingRdeIds);

        var projection = await Task.Run(
            () => repositories.MappingFacts.ExecuteAsync(
                plan,
                cancellationToken,
                progress => eventPublisher.Publish("mapping.progress", new
                {
                    kind = "gl",
                    rowsProcessed = progress.RowsProcessed,
                    totalRows = batch.RowCount
                })),
            cancellationToken);

        var result = JetAuditProgram.Finalize(plan, projection);
        restoreAuthorizations.Clear(projectId, batch.BatchId);

        await MappingCommitShared.AdvanceStepAsync(
            projectStore,
            document,
            WorkflowMilestones.For(Action),
            CancellationToken.None, logger);

        if (recordsRecommit)
        {
            await repositories.ProjectAuditLog.AppendAsync(
                projectId,
                ProjectAuditEvent.Create(
                    ProjectAuditOperations.MappingRecommit,
                    ProjectAuditTargetTypes.Mapping,
                    "gl",
                    result.Projection.ProjectedRowCount,
                    replacedCount: 1),
                CancellationToken.None);
        }

        var mutationState = await WorkflowResultStateSupport.AfterMutationAsync(projectId, repositories.RuleRuns, repositories.ResultStaleStates,
            repositories.FilterScenarios, repositories.ReportArtifactStore, plan.Effects);
        return new
        {
            ok = true,
            mapping = result.Spec.Mapping,
            amountMode = GlAmountModeNames.ToWireName(result.Spec.AmountMode),
            postingStatusPolicy = plan.EffectivePopulation.PostingStatusPolicy,
            approvalDateMode = result.Spec.Options.ApprovalDateMode,
            manualAutoPolicy = result.Spec.Options.ManualAutoPolicy,
            rdeFields = result.Spec.Options.RdeFields,
            batchId = batch.BatchId,
            projectedRowCount = result.Projection.ProjectedRowCount,
            // 非阻斷提醒（如必填欄整欄空白，疑似配錯欄）；前端提交成功後一併顯示。多數情況為空陣列。
            warnings = result.Projection.Warnings,
            authorizedPreparerState = await repositories.AuthorizedPreparers.FindStateAsync(projectId, CancellationToken.None),
            invalidatedResults = mutationState.InvalidatedResults,
            staleState = mutationState.StaleState,
            reportArtifacts = mutationState.ReportArtifacts,
            reportArtifactWarning = mutationState.ReportArtifactWarning
        };
    }
}

public sealed class MappingCommitTbHandler : IApplicationActionHandler
{
    private readonly IProjectStore projectStore;
    private readonly ProjectSession session;
    private readonly IJetEventPublisher eventPublisher;
    private readonly ILogger? logger;

    internal MappingCommitTbHandler(
        IProjectStore projectStore,
        ProjectSession session,
        IJetEventPublisher eventPublisher, ILogger? logger = null)
    {
        this.projectStore = projectStore;
        this.session = session;
        this.eventPublisher = eventPublisher;
        this.logger = logger;
    }

    public string Action => "mapping.commit.tb";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var (projectId, repositories) = session.RequireActive();

        var mapping = PayloadReader.GetStringMap(payload, "mapping");
        var changeModeName = PayloadReader.GetRequiredString(payload, "changeMode");
        var changeMode = JetAuditProgram.ParseTbChangeMode(changeModeName);

        var batch = await repositories.Imports.GetLatestBatchAsync(projectId, DatasetKind.Tb, cancellationToken)
            ?? throw new JetActionException(
                JetErrorCodes.NoImportBatch,
                "尚未匯入 TB 資料，請先在第二步匯入試算表。");
        var existingMapping = await repositories.MappingStates.FindAsync(
            projectId,
            DatasetKind.Tb,
            cancellationToken);
        var recordsRecommit = existingMapping is not null
            || await repositories.ProjectAuditLog.RequiresMappingRecommitAuditAsync(projectId, "tb", cancellationToken);

        var document = await projectStore.FindAsync(projectId, cancellationToken)
            ?? throw new JetActionException(JetErrorCodes.ProjectNotFound, $"找不到專案 '{projectId}'。");

        var plan = JetAuditProgram.Plan(
            new TbMappingRequest(
                projectId,
                batch.BatchId,
                mapping,
                changeMode,
                batch.Columns,
                document.MoneyScale,
                DateTimeOffset.UtcNow));

        var projection = await Task.Run(
            () => repositories.MappingFacts.ExecuteAsync(
                plan,
                cancellationToken,
                progress => eventPublisher.Publish("mapping.progress", new
                {
                    kind = "tb",
                    rowsProcessed = progress.RowsProcessed,
                    totalRows = batch.RowCount
                })),
            cancellationToken);

        var result = JetAuditProgram.Finalize(plan, projection);

        await MappingCommitShared.AdvanceStepAsync(
            projectStore,
            document,
            WorkflowMilestones.For(Action),
            CancellationToken.None, logger);

        if (recordsRecommit)
        {
            await repositories.ProjectAuditLog.AppendAsync(
                projectId,
                ProjectAuditEvent.Create(
                    ProjectAuditOperations.MappingRecommit,
                    ProjectAuditTargetTypes.Mapping,
                    "tb",
                    result.Projection.ProjectedRowCount,
                    replacedCount: 1),
                CancellationToken.None);
        }

        var mutationState = await WorkflowResultStateSupport.AfterMutationAsync(projectId, repositories.RuleRuns, repositories.ResultStaleStates,
            repositories.FilterScenarios, repositories.ReportArtifactStore, plan.Effects);
        return new
        {
            ok = true,
            mapping = result.Spec.Mapping,
            changeMode = TbChangeModeNames.ToWireName(result.Spec.ChangeMode),
            batchId = batch.BatchId,
            projectedRowCount = result.Projection.ProjectedRowCount,
            warnings = result.Projection.Warnings,
            invalidatedResults = mutationState.InvalidatedResults,
            staleState = mutationState.StaleState,
            reportArtifacts = mutationState.ReportArtifacts,
            reportArtifactWarning = mutationState.ReportArtifactWarning
        };
    }
}

internal static class MappingCommitShared
{
    public static async Task AdvanceStepAsync(
        IProjectStore projectStore,
        JET.Domain.ProjectDocument document,
        int milestone,
        CancellationToken cancellationToken,
        ILogger? logger = null)
    {
        await AfterCommitAsync(async () =>
        {
            var nextStep = Math.Max(document.CurrentStep, milestone);
            if (nextStep != document.CurrentStep)
                await projectStore.SaveAsync(document with { CurrentStep = nextStep }, cancellationToken);
        }, logger);
    }

    internal static async Task AfterCommitAsync(Func<Task> updateProgress, ILogger? logger)
    {
        try { await updateProgress(); }
        catch (Exception exception)
        {
            // Only a navigation milestone failed after data commit. Do not repeat the completed operation or expose paths.
            logger?.LogWarning("workflow.milestone: 已完成資料儲存，但案件步驟未更新。錯誤類型：{exceptionType}", exception.GetType().Name);
        }
    }
}

/// <summary>
/// mapping.restoreDraft 驗證成功後，暫存可由下一次同 project/import-batch commit 沿用的
/// backend stable IDs。授權綁完整 canonical definition；它不是持久 draft，也不改 mapping state。
/// </summary>
public sealed class MappingRestoreDraftAuthorizationStore
{
    private readonly object sync = new();
    private readonly Dictionary<string, Grant> grants = new(StringComparer.Ordinal);

    public void Authorize(
        string projectId,
        string batchId,
        IReadOnlyList<GlRdeFieldMetadata> fields)
    {
        lock (sync)
        {
            grants[projectId] = new Grant(
                batchId,
                fields.ToDictionary(static field => field.FieldId, StringComparer.Ordinal));
        }
    }

    public IReadOnlySet<string> GetAuthorizedFieldIds(string projectId, string batchId)
    {
        lock (sync)
        {
            return grants.TryGetValue(projectId, out var grant)
                   && string.Equals(grant.BatchId, batchId, StringComparison.Ordinal)
                ? grant.Fields.Keys.ToHashSet(StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);
        }
    }

    public void EnsureMatches(
        string projectId,
        string batchId,
        JsonElement payload,
        IReadOnlyList<GlRdeFieldMetadata> fields,
        IReadOnlySet<string> existingFieldIds)
    {
        lock (sync)
        {
            grants.TryGetValue(projectId, out var grant);
            var canonicalFields = fields.ToDictionary(static field => field.FieldId, StringComparer.Ordinal);
            if (!payload.TryGetProperty("rdeFields", out var rdeElement)
                || rdeElement.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            foreach (var item in rdeElement.EnumerateArray())
            {
                if (!item.TryGetProperty("fieldId", out var fieldIdElement)
                    || fieldIdElement.ValueKind != JsonValueKind.String)
                {
                    // 省略／null 是本次 commit 由 backend 產生的新 ID，不需 restore grant。
                    continue;
                }

                var fieldId = fieldIdElement.GetString()!;
                if (existingFieldIds.Contains(fieldId))
                {
                    continue;
                }

                if (grant is null
                    || !string.Equals(grant.BatchId, batchId, StringComparison.Ordinal)
                    || !grant.Fields.TryGetValue(fieldId, out var authorized)
                    || !canonicalFields.TryGetValue(fieldId, out var field)
                    || authorized != field)
                {
                    throw new JetActionException(
                        JetErrorCodes.InvalidPayload,
                        "rdeFields[].fieldId 只能沿用目前案件既有 definition，或目前匯入批次已驗證的 restoreDraft definition；新欄位請省略 fieldId。");
                }
            }
        }
    }

    public void Clear(string projectId, string batchId)
    {
        lock (sync)
        {
            if (grants.TryGetValue(projectId, out var grant)
                && string.Equals(grant.BatchId, batchId, StringComparison.Ordinal))
            {
                grants.Remove(projectId);
            }
        }
    }

    private sealed record Grant(
        string BatchId,
        IReadOnlyDictionary<string, GlRdeFieldMetadata> Fields);
}

/// <summary>
/// mapping v2 projection options 的 wire parser。正準化與跨欄驗證仍由 AuditCore/Domain 執行。
/// </summary>
internal static class MappingV2CommitPayloadPrerequisite
{
    internal static GlMappingOptions ReadProjectionOptions(
        JsonElement payload,
        IReadOnlyDictionary<string, string> mapping,
        GlPostingStatusPolicy? postingStatusPolicy,
        IReadOnlySet<string> existingRdeIds)
    {
        var defaults = GlMappingOptions.NormalizeLegacy(mapping);
        var approvalDateMode = defaults.ApprovalDateMode;
        if (payload.TryGetProperty("approvalDateMode", out var approvalElement))
        {
            if (approvalElement.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(approvalElement.GetString()))
            {
                throw Invalid("approvalDateMode 必須是非空白字串。");
            }

            approvalDateMode = approvalElement.GetString()!;
        }

        var manualPolicy = defaults.ManualAutoPolicy;
        if (payload.TryGetProperty("manualAutoPolicy", out var manualElement))
        {
            if (manualElement.ValueKind != JsonValueKind.Object
                || !manualElement.TryGetProperty("manualValues", out var manualValues)
                || !manualElement.TryGetProperty("automaticValues", out var automaticValues))
            {
                throw Invalid(
                    "manualAutoPolicy 必須包含 manualValues 與 automaticValues 字串陣列。");
            }

            manualPolicy = new GlManualAutoPolicy(
                ReadStringArray(manualValues, "manualAutoPolicy.manualValues"),
                ReadStringArray(automaticValues, "manualAutoPolicy.automaticValues"))
            {
                UnlistedValueKind = manualElement.TryGetProperty("unlistedValueKind", out var unlisted)
                    ? RequireString(manualElement, "unlistedValueKind", "manualAutoPolicy.unlistedValueKind") : null,
                BlankValueKind = manualElement.TryGetProperty("blankValueKind", out var blank)
                    ? RequireString(manualElement, "blankValueKind", "manualAutoPolicy.blankValueKind") : null
            };
        }

        IReadOnlyList<GlRdeFieldMetadata> rdeFields = [];
        if (payload.TryGetProperty("rdeFields", out var rdeElement))
        {
            if (rdeElement.ValueKind != JsonValueKind.Array)
            {
                throw Invalid("rdeFields 必須是 array。");
            }

            var fields = new List<GlRdeFieldMetadata>();
            foreach (var item in rdeElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    throw Invalid("rdeFields 的每個項目都必須是 object。");
                }

                var sourceColumn = RequireString(item, "sourceColumn", "rdeFields[].sourceColumn");
                var label = RequireString(item, "label", "rdeFields[].label");
                var valueType = RequireString(item, "valueType", "rdeFields[].valueType");
                string fieldId;
                if (!item.TryGetProperty("fieldId", out var fieldIdElement)
                    || fieldIdElement.ValueKind == JsonValueKind.Null)
                {
                    fieldId = "rde." + Guid.NewGuid().ToString("N");
                }
                else if (fieldIdElement.ValueKind == JsonValueKind.String
                         && !string.IsNullOrWhiteSpace(fieldIdElement.GetString()))
                {
                    fieldId = fieldIdElement.GetString()!;
                    if (!existingRdeIds.Contains(fieldId))
                    {
                        throw Invalid(
                            "rdeFields[].fieldId 只能沿用目前案件既有的後端 stable ID；新欄位請省略 fieldId。");
                    }
                }
                else
                {
                    throw Invalid("rdeFields[].fieldId 必須省略、為 null，或是非空白字串。");
                }

                fields.Add(new GlRdeFieldMetadata(fieldId, sourceColumn, label, valueType));
            }

            rdeFields = fields;
        }

        return new GlMappingOptions(
            approvalDateMode,
            postingStatusPolicy,
            manualPolicy,
            rdeFields);
    }

    internal static GlPostingStatusPolicy? ReadPostingStatusPolicy(JsonElement payload)
    {
        if (payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty("mapping", out var rawMapping)
            && rawMapping.ValueKind == JsonValueKind.Object
            && rawMapping.TryGetProperty(GlMappingKeys.PostingStatus, out var rawPostingStatus)
            && (rawPostingStatus.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(rawPostingStatus.GetString())))
        {
            throw new JetActionException(
                JetErrorCodes.InvalidPayload,
                "mapping.postingStatus 必須是非空白來源欄名。");
        }

        if (!payload.TryGetProperty("postingStatusPolicy", out var policyElement)
            || policyElement.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (policyElement.ValueKind != JsonValueKind.Object
            || !policyElement.TryGetProperty("acceptedValues", out var acceptedElement)
            || acceptedElement.ValueKind != JsonValueKind.Array
            || !policyElement.TryGetProperty("includeBlank", out var includeBlankElement)
            || includeBlankElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new JetActionException(
                JetErrorCodes.InvalidPayload,
                "postingStatusPolicy 必須包含 acceptedValues 字串陣列與 includeBlank 布林值。");
        }

        var acceptedValues = new List<string>();
        foreach (var item in acceptedElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                throw new JetActionException(
                    JetErrorCodes.InvalidPayload,
                    "postingStatusPolicy.acceptedValues 必須是字串陣列。");
            }

            acceptedValues.Add(item.GetString() ?? string.Empty);
        }

        return new GlPostingStatusPolicy(
            acceptedValues,
            includeBlankElement.GetBoolean());
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement element, string path)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            throw Invalid($"{path} 必須是字串陣列。");
        }

        var result = new List<string>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                throw Invalid($"{path} 必須是字串陣列。");
            }

            result.Add(item.GetString() ?? string.Empty);
        }

        return result;
    }

    private static string RequireString(JsonElement item, string property, string path)
    {
        if (!item.TryGetProperty(property, out var element)
            || element.ValueKind != JsonValueKind.String)
        {
            throw Invalid($"{path} 必須是字串。");
        }

        return element.GetString() ?? string.Empty;
    }

    private static JetActionException Invalid(string message) =>
        new(JetErrorCodes.InvalidPayload, message);
}
