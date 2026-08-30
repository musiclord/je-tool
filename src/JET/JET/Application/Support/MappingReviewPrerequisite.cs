using System.Text.Json;
using JET.Domain;

namespace JET.Application;

/// <summary>
/// 舊案 mapping review 的 Application 共用關卡。舊 GL／TB mapping 只能透過
/// project.load／import／mapping repair actions 修復；任何使用 schema-v7 新邏輯的 action
/// 必須在進入 facts、result materialization 或 artifact staging 前先通過此關卡。
/// </summary>
internal static class MappingReviewPrerequisite
{
    private const string DataPreviewAction = "query.dataPreview";

    private static readonly HashSet<string> GuardedActions = new(StringComparer.Ordinal)
    {
        "validate.run",
        "prescreen.run",
        "filter.preview",
        "filter.commit",
        "query.completenessDiffPage",
        "query.docBalancePage",
        "query.nullRecordsPage",
        "query.sourceQualityPage",
        "query.filterHitsPage",
        "query.prescreenPage",
        "query.infSamplePage",
        "query.tagMatrixScenarios",
        "query.tagMatrixVoucherPage",
        "query.tagMatrixRowPage",
        "export.workpaperStream",
        "export.validationArtifacts",
        "export.prescreenReport",
        "export.criteriaSelectionReport",
        "export.accountMappingTemplate"
    };

    internal static void EnsureSatisfied(bool mappingReviewRequired)
    {
        if (mappingReviewRequired)
        {
            throw new JetActionException(
                JetErrorCodes.MappingReviewRequired,
                "目前案件的欄位配對必須先由使用者確認並重新提交。");
        }
    }

    internal static async Task EnsureSatisfiedAsync(
        string projectId,
        IMappingStateStore mappingStore,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentNullException.ThrowIfNull(mappingStore);

        // 順序讀取以保留 provider store 的單呼叫契約；此處只讀 metadata，
        // 不觸及任何 GL／TB facts、result 或 artifact。
        var glMapping = await mappingStore.FindAsync(
            projectId,
            DatasetKind.Gl,
            cancellationToken);
        var tbMapping = await mappingStore.FindAsync(
            projectId,
            DatasetKind.Tb,
            cancellationToken);

        EnsureSatisfied(
            glMapping is { FormatVersion: < MappingMetadataFormat.CurrentVersion }
            || tbMapping is { FormatVersion: < MappingMetadataFormat.CurrentVersion });
    }

    /// <summary>
    /// 在 composition boundary 統一裝飾已啟用的 production actions。未列入的 repair／
    /// control-plane actions 原樣返回，不被 mapping review 擋下。
    /// </summary>
    internal static IApplicationActionHandler DecorateProductionAction(
        IApplicationActionHandler handler,
        IMappingStateStore mappingStore,
        ProjectSession session)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(mappingStore);
        ArgumentNullException.ThrowIfNull(session);

        return GuardedActions.Contains(handler.Action)
               || string.Equals(handler.Action, DataPreviewAction, StringComparison.Ordinal)
            ? new MappingReviewGuardedActionHandler(handler, mappingStore, session)
            : handler;
    }

    private static bool RequiresReviewCheck(string action, JsonElement payload)
    {
        if (!string.Equals(action, DataPreviewAction, StringComparison.Ordinal))
        {
            return true;
        }

        // staging／reference-data previews 是 mapping repair 必要路徑；只擋使用新投影
        // is_effective／exclusion 語意的兩種 GL preview。無效 payload 交回原 handler
        // 保留 invalid_payload 錯誤優先序。
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("dataset", out var datasetElement)
            || datasetElement.ValueKind != JsonValueKind.String
            || !DataPreviewDatasetNames.TryParse(datasetElement.GetString(), out var dataset))
        {
            return false;
        }

        return dataset is DataPreviewDataset.GlEntries
            or DataPreviewDataset.GlExcludedEntries;
    }

    private sealed class MappingReviewGuardedActionHandler(
        IApplicationActionHandler inner,
        IMappingStateStore mappingStore,
        ProjectSession session) : IApplicationActionHandler
    {
        public string Action => inner.Action;

        public async Task<object?> HandleAsync(
            JsonElement payload,
            CancellationToken cancellationToken)
        {
            if (RequiresReviewCheck(Action, payload))
            {
                await EnsureSatisfiedAsync(
                    session.RequireProjectId(),
                    mappingStore,
                    cancellationToken);
            }

            return await inner.HandleAsync(payload, cancellationToken);
        }
    }
}
