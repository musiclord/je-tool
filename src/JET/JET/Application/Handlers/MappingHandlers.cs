using System.Globalization;
using System.Text.Json;
using JET.AuditCore;
using JET.Domain;
using Microsoft.Extensions.Logging;

namespace JET.Application;

/// <summary>
/// 從 JET 報告讀回 GL/TB mapping 草稿。唯讀：不保存 mapping、不投影 target、不推進流程。
/// 相容性以目前的 import batch columns 和 commit 共用 MappingValidator 權威驗證。
/// 報告裡選到的來源欄在目前資料找不到時，不整份拒絕：對得上的照樣帶入，找不到的連同綁在它上面的設定拿掉，
/// 列在 unmatchedFields 請審計員重選；沒有匯入 TB，或報告匯出時 TB 還沒確認配對，只帶入 GL，tb 回 null（裁定 2026-11-05-03）。
/// 報告本身缺必要欄位或用了不支援的金額計算方式，仍整份拒絕。
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
                "目前案件尚未匯入 PBC-GL，不能驗證還原草稿的來源欄位。");
        // 沒有匯入 TB，或報告匯出時 TB 還沒確認配對，都只帶入 GL。
        var tbBatch = metadata.Tb is null
            ? null
            : await repositories.Imports.GetLatestBatchAsync(projectId, DatasetKind.Tb, cancellationToken);
        var tbMetadata = tbBatch is null ? null : metadata.Tb;

        var tbMode = default(TbChangeMode);
        if (!GlAmountModeNames.TryParse(metadata.Gl.AmountMode, out var glMode)
            || (tbMetadata is not null && !TbChangeModeNames.TryParse(tbMetadata.ChangeMode, out tbMode)))
        {
            throw new JetActionException(
                JetErrorCodes.MappingMetadataInvalid,
                "報告裡的欄位配對使用了目前版本不支援的金額計算方式，無法還原。請回第三步欄位配對直接重新選擇。");
        }

        var glValidation = MappingValidator.ValidateGl(
            new GlMappingSpec(metadata.Gl.Mapping, glMode), glBatch.Columns.ToList());
        var tbValidation = tbBatch is null || tbMetadata is null
            ? null
            : MappingValidator.ValidateTb(new TbMappingSpec(tbMetadata.Mapping, tbMode), tbBatch.Columns.ToList());

        // 必要欄位看的是報告本身有沒有選；選了但在目前資料找不到，屬於下面要重選的欄位，不在這裡擋。
        var missing = glValidation.MissingRequiredKeys.Select(key => $"GL.{key}")
            .Concat((tbValidation?.MissingRequiredKeys ?? []).Select(key => $"TB.{key}"))
            .ToArray();
        if (missing.Length > 0)
        {
            if (glValidation.MissingRequiredKeys.Any(key => key is "drCrDebitCode" or "drCrCreditCode"))
            {
                throw new JetActionException(
                    JetErrorCodes.MappingMetadataInvalid,
                    "報告裡的欄位配對沒有借貸代碼，無法還原。請回第三步至少指定一側代碼後再確認配對。");
            }
            throw new JetActionException(
                JetErrorCodes.MappingMetadataInvalid,
                $"報告裡的欄位配對缺少必要欄位：{string.Join("、", missing)}，無法還原。請回第三步欄位配對直接重新選擇。");
        }

        var unmatched = new List<object>();
        var glMapping = KeepMatchedColumns(
            metadata.Gl.Mapping, glBatch.Columns, "gl", JetFieldCatalog.GlMappingSlots, unmatched);
        var draftOptions = FitOptions(metadata.Gl.ToOptions(), glMapping, glBatch.Columns, unmatched);
        var tbMapping = tbBatch is null || tbMetadata is null
            ? null
            : KeepMatchedColumns(tbMetadata.Mapping, tbBatch.Columns, "tb", JetFieldCatalog.TbMappingSlots, unmatched);

        GlMappingOptions glOptions;
        try
        {
            glOptions = GlMappingOptionsRules.NormalizeAndValidate(
                glMapping,
                glBatch.Columns,
                draftOptions);
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
                mapping = glMapping,
                amountMode = metadata.Gl.AmountMode,
                approvalDateMode = glOptions.ApprovalDateMode,
                postingStatusPolicy = glOptions.PostingStatusPolicy,
                manualAutoPolicy = glOptions.ManualAutoPolicy,
                rdeFields = glOptions.RdeFields,
                fillDownFields = glOptions.FillDownFields
            },
            tb = tbMapping is null || tbMetadata is null ? null : new { mapping = tbMapping, changeMode = tbMetadata.ChangeMode },
            unmatchedFields = unmatched
        };
    }

    /// <summary>
    /// 只留下來源欄在目前資料找得到的配對；借方與貸方代碼是字面值，照樣帶入。找不到的寫進 unmatched：
    /// dataset、配對位置的鍵與名稱，以及報告裡選的來源欄。
    /// </summary>
    private static Dictionary<string, string> KeepMatchedColumns(
        IReadOnlyDictionary<string, string> mapping,
        IReadOnlyList<string> columns,
        string dataset,
        IReadOnlyList<JetMappingSlot> slots,
        List<object> unmatched)
    {
        var available = columns.ToHashSet(StringComparer.Ordinal);
        var kept = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var slot in slots)
        {
            if (!mapping.TryGetValue(slot.Key, out var column) || string.IsNullOrWhiteSpace(column))
            {
                continue;
            }

            if (slot.IsLiteral || available.Contains(column))
            {
                kept[slot.Key] = column;
            }
            else
            {
                unmatched.Add(new { dataset, key = slot.Key, label = slot.Label, sourceColumn = column });
            }
        }

        return kept;
    }

    /// <summary>
    /// 拿掉綁在找不到的來源欄上的設定：核准日的來源欄不見了改成沒有核准日，過帳狀態的來源欄不見了就不帶過帳政策，
    /// 往下填補只留還有來源欄的欄位；攸關資料元素的來源欄不見了，這個欄位寫進 unmatched。
    /// </summary>
    private static GlMappingOptions FitOptions(
        GlMappingOptions options,
        IReadOnlyDictionary<string, string> mapping,
        IReadOnlyList<string> columns,
        List<object> unmatched)
    {
        var available = columns.ToHashSet(StringComparer.Ordinal);
        var rdeFields = new List<GlRdeFieldMetadata>(options.RdeFields.Count);
        foreach (var field in options.RdeFields)
        {
            if (available.Contains(field.SourceColumn))
            {
                rdeFields.Add(field);
            }
            else
            {
                unmatched.Add(new { dataset = "gl", key = field.FieldId, label = field.Label, sourceColumn = field.SourceColumn });
            }
        }

        var approvalDateMode = options.ApprovalDateMode == ApprovalDateModeNames.Mapped
                               && !mapping.ContainsKey(JetFieldCatalog.GlApprovalDate)
            ? ApprovalDateModeNames.Unmapped
            : options.ApprovalDateMode;
        return options with
        {
            ApprovalDateMode = approvalDateMode,
            PostingStatusPolicy = mapping.ContainsKey(JetFieldCatalog.GlPostingStatus) ? options.PostingStatusPolicy : null,
            RdeFields = rdeFields,
            FillDownFields = options.FillDownFields.Where(mapping.ContainsKey).ToArray()
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

    /// <summary>配對確認後，案件至少推進到第三步。</summary>
    internal const int WorkflowStep = 3;

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var (projectId, repositories) = session.RequireActive();

        var mapping = PayloadReader.GetStringMap(payload, "mapping");
        var amountModeName = PayloadReader.GetRequiredString(payload, "amountMode");
        var amountMode = JetAuditProgram.ParseGlAmountMode(amountModeName);
        var postingStatusPolicy = MappingV2CommitPayloadPrerequisite.ReadPostingStatusPolicy(payload);

        SupportOperationStage.Enter("state_read");
        var batch = await repositories.Imports.GetLatestBatchAsync(projectId, DatasetKind.Gl, cancellationToken)
            ?? throw new JetActionException(
                JetErrorCodes.NoImportBatch,
                "尚未匯入 PBC-GL，請先在第二步匯入總帳。");
        var existingMapping = await repositories.MappingStates.FindAsync(
            projectId,
            DatasetKind.Gl,
            cancellationToken);
        // 已有確認的配對就要記重新配對；沒有時讀紀錄表判斷，本機案件讀不到時是 null（無法判斷）。
        var recordsRecommit = existingMapping is not null
            ? true
            : await repositories.ProjectOperationLog.TryRequiresMappingRecommitAuditAsync(projectId, "gl", cancellationToken);
        // 重新匯入會刪掉已確認的配對，但畫面的草稿仍帶著上次確認時發出的攸關資料元素欄位身分。
        // 還沒重新確認時沿用那一份，草稿才送得出去。已儲存情境會隨重新匯入一併清除（裁定 2026-10-07-03）。
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
        SupportOperationStage.Enter("metadata_read");
        var document = await projectStore.FindAsync(projectId, cancellationToken)
            ?? throw new JetActionException(JetErrorCodes.ProjectNotFound, ProjectFolderMessages.ActiveProjectMissing);

        SupportOperationStage.Enter("plan");
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

        SupportOperationStage.Enter("projection");
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

        SupportOperationStage.Enter("after_commit");
        await MappingCommitShared.AdvanceStepAsync(
            projectStore,
            document,
            WorkflowStep,
            CancellationToken.None, logger);

        var operationLogWarning = await CommittedOperationLog.AppendMappingRecommitAsync(
            repositories.ProjectOperationLog, projectId, recordsRecommit, "gl", result.Projection.ProjectedRowCount, logger);

        var mutationState = await WorkflowResultStateSupport.AfterMutationAsync(projectId, repositories.Results, repositories.ResultStaleStates,
            repositories.FilterScenarios, repositories.ReportStore, plan.Effects, logger);
        // 非阻斷提醒（如必填欄整欄空白，疑似配錯欄）；前端提交成功後一併顯示。多數情況為空陣列。
        var warnings = CommittedOperationLog.WithWarning(result.Projection.Warnings, operationLogWarning);
        // 第五步依這份清單停用用到未配對欄位的入口；和驗證器、第四步讀同一份欄位目錄的答案。
        var availableGlFields = JetFieldCatalog.AvailableGlFields(result.Spec.Mapping, result.Spec.Options);
        AuthorizedPreparerState? authorizedPreparerState;
        try
        {
            authorizedPreparerState = await repositories.AuthorizedPreparers.FindStateAsync(projectId, CancellationToken.None);
        }
        catch (Exception error)
        {
            // 變更已提交，讀不到授權清單比對狀態時和讀不回結果狀態相同處理：回報成功、附提醒，
            // 回應不帶這個欄位，前端保留畫面上的狀態（裁定 2026-10-19-19）。支援日誌記下錯誤類型。
            if (logger is not null) WorkflowResultStateSupport.StateReadFailed(logger, "authorized_preparer_state", error.GetType().Name);
            var unavailable = WorkflowResultStateSupport.StateUnavailable(mutationState.InvalidatedResults);
            return new
            {
                ok = true,
                mapping = result.Spec.Mapping,
                amountMode = GlAmountModeNames.ToWireName(result.Spec.AmountMode),
                postingStatusPolicy = plan.GlExcludedPopulationPlan.PostingStatusPolicy,
                approvalDateMode = result.Spec.Options.ApprovalDateMode,
                manualAutoPolicy = result.Spec.Options.ManualAutoPolicy,
                rdeFields = result.Spec.Options.RdeFields,
                fillDownFields = result.Spec.Options.FillDownFields,
                availableGlFields,
                batchId = batch.BatchId,
                projectedRowCount = result.Projection.ProjectedRowCount,
                fillDownCount = result.Projection.FillDownCellCount,
                warnings,
                invalidatedResults = unavailable.InvalidatedResults,
                staleState = unavailable.StaleState,
                reports = unavailable.Reports,
                reportWarning = unavailable.ReportWarning
            };
        }

        return new
        {
            ok = true,
            mapping = result.Spec.Mapping,
            amountMode = GlAmountModeNames.ToWireName(result.Spec.AmountMode),
            postingStatusPolicy = plan.GlExcludedPopulationPlan.PostingStatusPolicy,
            approvalDateMode = result.Spec.Options.ApprovalDateMode,
            manualAutoPolicy = result.Spec.Options.ManualAutoPolicy,
            rdeFields = result.Spec.Options.RdeFields,
            // 往下填補的欄位照欄位目錄順序回傳；fillDownCount 是這次投影把空白補成上一列值的格數。
            fillDownFields = result.Spec.Options.FillDownFields,
            availableGlFields,
            batchId = batch.BatchId,
            projectedRowCount = result.Projection.ProjectedRowCount,
            fillDownCount = result.Projection.FillDownCellCount,
            warnings,
            authorizedPreparerState,
            invalidatedResults = mutationState.InvalidatedResults,
            staleState = mutationState.StaleState,
            reports = mutationState.Reports,
            reportWarning = mutationState.ReportWarning
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

    /// <summary>配對確認後，案件至少推進到第三步。</summary>
    internal const int WorkflowStep = 3;

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var (projectId, repositories) = session.RequireActive();

        var mapping = PayloadReader.GetStringMap(payload, "mapping");
        var changeModeName = PayloadReader.GetRequiredString(payload, "changeMode");
        var changeMode = JetAuditProgram.ParseTbChangeMode(changeModeName);

        SupportOperationStage.Enter("state_read");
        var batch = await repositories.Imports.GetLatestBatchAsync(projectId, DatasetKind.Tb, cancellationToken)
            ?? throw new JetActionException(
                JetErrorCodes.NoImportBatch,
                "尚未匯入 PBC-TB，請先在第二步匯入試算表。");
        var existingMapping = await repositories.MappingStates.FindAsync(
            projectId,
            DatasetKind.Tb,
            cancellationToken);
        // 判斷方式同 GL：本機案件讀不到紀錄表時是 null（無法判斷）。
        var recordsRecommit = existingMapping is not null
            ? true
            : await repositories.ProjectOperationLog.TryRequiresMappingRecommitAuditAsync(projectId, "tb", cancellationToken);

        SupportOperationStage.Enter("metadata_read");
        var document = await projectStore.FindAsync(projectId, cancellationToken)
            ?? throw new JetActionException(JetErrorCodes.ProjectNotFound, ProjectFolderMessages.ActiveProjectMissing);

        SupportOperationStage.Enter("plan");
        var plan = JetAuditProgram.Plan(
            new TbMappingRequest(
                projectId,
                batch.BatchId,
                mapping,
                changeMode,
                batch.Columns,
                document.MoneyScale,
                DateTimeOffset.UtcNow));

        SupportOperationStage.Enter("projection");
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

        SupportOperationStage.Enter("after_commit");
        await MappingCommitShared.AdvanceStepAsync(
            projectStore,
            document,
            WorkflowStep,
            CancellationToken.None, logger);

        var operationLogWarning = await CommittedOperationLog.AppendMappingRecommitAsync(
            repositories.ProjectOperationLog, projectId, recordsRecommit, "tb", result.Projection.ProjectedRowCount, logger);

        var mutationState = await WorkflowResultStateSupport.AfterMutationAsync(projectId, repositories.Results, repositories.ResultStaleStates,
            repositories.FilterScenarios, repositories.ReportStore, plan.Effects, logger);
        return new
        {
            ok = true,
            mapping = result.Spec.Mapping,
            changeMode = TbChangeModeNames.ToWireName(result.Spec.ChangeMode),
            batchId = batch.BatchId,
            projectedRowCount = result.Projection.ProjectedRowCount,
            warnings = CommittedOperationLog.WithWarning(result.Projection.Warnings, operationLogWarning),
            invalidatedResults = mutationState.InvalidatedResults,
            staleState = mutationState.StaleState,
            reports = mutationState.Reports,
            reportWarning = mutationState.ReportWarning
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
            // 先前只寫一般警告，支援日誌收不到；改成支援日誌事件，記下例外型別與位置（2026-10-21）。
            if (logger is not null) SupportStepDiagnostics.StepFailed(logger, "workflow.milestone", exception);
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
                    // 只能沿用目前案件既有 definition，或目前匯入批次已驗證的 restoreDraft definition；新欄位應省略 fieldId。
                    throw JetPayloadRejection.Create(JetErrorCodes.InvalidPayload, "rde_field_id_not_authorized");
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
                throw Invalid("approval_date_mode_not_string");
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
                throw Invalid("manual_auto_policy_missing_value_lists");
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
                throw Invalid("rde_fields_not_array");
            }

            var fields = new List<GlRdeFieldMetadata>();
            foreach (var item in rdeElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    throw Invalid("rde_field_not_object");
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
                        // 只能沿用目前案件既有的後端 stable ID；新欄位應省略 fieldId。
                        throw Invalid("rde_field_id_not_existing");
                    }
                }
                else
                {
                    throw Invalid("rde_field_id_invalid");
                }

                fields.Add(new GlRdeFieldMetadata(fieldId, sourceColumn, label, valueType));
            }

            rdeFields = fields;
        }

        // 往下填補的欄位：只收形狀，哪些欄位能填由 Domain 的規則裁定。
        IReadOnlyList<string> fillDownFields = [];
        if (payload.TryGetProperty("fillDownFields", out var fillDownElement))
        {
            fillDownFields = ReadStringArray(fillDownElement, "fillDownFields");
        }

        return new GlMappingOptions(
            approvalDateMode,
            postingStatusPolicy,
            manualPolicy,
            rdeFields)
        {
            FillDownFields = fillDownFields
        };
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
            throw Invalid("posting_status_column_blank");
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
            throw Invalid("posting_status_policy_shape");
        }

        var acceptedValues = new List<string>();
        foreach (var item in acceptedElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                throw Invalid("posting_status_accepted_values_not_strings");
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
            throw Invalid($"string_array_expected:{path}");
        }

        var result = new List<string>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                throw Invalid($"string_array_expected:{path}");
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
            throw Invalid($"string_expected:{path}");
        }

        return element.GetString() ?? string.Empty;
    }

    // 畫面照正常操作送不出這些資料，出現時只會是程式送錯資料或存檔損毀，審計員在畫面上無從修正；所以畫面只顯示非預期錯誤那一句，檢查代號記進支援日誌供查原因。
    // path 是程式寫死的 payload 位置，不是使用者資料。
    private static JetActionException Invalid(string check) =>
        JetPayloadRejection.Create(JetErrorCodes.InvalidPayload, check);
}
