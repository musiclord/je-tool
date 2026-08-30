using System.Text.Json;
using JET.Domain;

namespace JET.Application;

/// <summary>第一個明示動作：刷新來源失效狀態並回傳後端權威清理預覽，不碰報告檔。</summary>
public sealed class ReportCleanupPreviewHandler(
    IRuleRunStore runStore,
    IResultStaleStateStore resultStaleStateStore,
    IFilterScenarioStore scenarioStore,
    IReportArtifactStore artifactStore,
    ProjectSession session) : IApplicationActionHandler
{
    public string Action => "report.cleanupPreview";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        ReportArtifactCleanupSupport.RequireEmptyObject(payload, Action);
        var projectId = session.RequireProjectId();
        var catalog = await ReportArtifactCleanupSupport.RefreshCatalogAsync(
            projectId,
            runStore,
            resultStaleStateStore,
            scenarioStore,
            artifactStore,
            cancellationToken);
        var candidates = ReportArtifactRetentionPolicy.Plan(catalog.Artifacts);

        return ReportArtifactCleanupSupport.PreviewWire(catalog.Revision, candidates);
    }
}

/// <summary>
/// 第二個明示動作：payload 只接受 preview revision；重新刷新與計畫後，交由 store 在同一把
/// cross-process lock 下再比對 revision 並完成 durable cleanup。
/// </summary>
public sealed class ReportCleanupConfirmHandler(
    IRuleRunStore runStore,
    IResultStaleStateStore resultStaleStateStore,
    IFilterScenarioStore scenarioStore,
    IReportArtifactStore artifactStore,
    CurrentPrincipal principal,
    ProjectSession session) : IApplicationActionHandler
{
    public string Action => "report.cleanupConfirm";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var expectedRevision = ReportArtifactCleanupSupport.RequireCatalogRevisionOnly(payload);
        var projectId = session.RequireProjectId();
        var catalog = await ReportArtifactCleanupSupport.RefreshCatalogAsync(
            projectId,
            runStore,
            resultStaleStateStore,
            scenarioStore,
            artifactStore,
            cancellationToken);

        if (!string.Equals(catalog.Revision, expectedRevision, StringComparison.Ordinal))
        {
            throw ReportArtifactCleanupSupport.CatalogChanged();
        }

        var candidates = ReportArtifactRetentionPolicy.Plan(catalog.Artifacts);
        if (candidates.Count == 0)
        {
            throw new JetActionException(
                JetErrorCodes.InvalidPayload,
                "目前沒有可清理的舊報告版本，請重新檢查清理預覽。");
        }

        var result = await artifactStore.CleanupAsync(
            projectId,
            expectedRevision,
            candidates,
            principal.Name,
            cancellationToken);

        return new
        {
            ok = true,
            deletedCount = result.DeletedCount,
            deletedBytes = result.DeletedBytes,
            auditId = result.AuditId,
            catalogRevision = result.Catalog.Revision,
            reportArtifacts = result.Catalog.Artifacts
                .Select(ReportExportSupport.ArtifactWire)
                .ToArray()
        };
    }
}

internal static class ReportArtifactCleanupSupport
{
    public static async Task<ReportArtifactCatalog> RefreshCatalogAsync(
        string projectId,
        IRuleRunStore runStore,
        IResultStaleStateStore resultStaleStateStore,
        IFilterScenarioStore scenarioStore,
        IReportArtifactStore artifactStore,
        CancellationToken cancellationToken)
    {
        var latestValidate = await runStore.FindLatestAsync(
            projectId,
            RuleRunKinds.Validate,
            cancellationToken);
        var latestPrescreen = await runStore.FindLatestAsync(
            projectId,
            RuleRunKinds.Prescreen,
            cancellationToken);
        latestValidate = RuleLogicVersions.IsCurrent(latestValidate) ? latestValidate : null;
        latestPrescreen = RuleLogicVersions.IsCurrent(latestPrescreen) ? latestPrescreen : null;
        var staleState = await resultStaleStateStore.ReadAsync(projectId, cancellationToken);

        var scenarios = await scenarioStore.ListAsync(projectId, cancellationToken);
        CurrentFilterRevision? currentFilterRevision = null;
        if (scenarios.Count > 0)
        {
            try
            {
                currentFilterRevision = FilterPopulationScopeParser.RequireCurrentRevision(scenarios);
            }
            catch (JetActionException exception) when (exception.Code == JetErrorCodes.StaleResult)
            {
                // 舊版或不一致的情境不能維持任何 scenario-bound artifact 為有效。
            }
        }

        var scenarioPositions = scenarios.Select(item => item.Position).ToArray();
        return await RefreshCatalogAsync(
            projectId,
            latestValidate,
            latestPrescreen,
            staleState.Filter,
            currentFilterRevision?.Revision,
            scenarioPositions,
            artifactStore,
            cancellationToken);
    }

    /// <summary>
    /// 已由 caller 載入並驗證 current sources 時的惰性刷新入口。stale 判定仍只委派
    /// <see cref="ReportExportSupport.IsSourceStale"/>，不建立第二份 run／revision 規則。
    /// </summary>
    public static async Task<ReportArtifactCatalog> RefreshCatalogAsync(
        string projectId,
        RuleRunRecord? latestValidate,
        RuleRunRecord? latestPrescreen,
        string? currentFilterRevision,
        IReadOnlyCollection<int> scenarioPositions,
        IReportArtifactStore artifactStore,
        CancellationToken cancellationToken)
        => await RefreshCatalogAsync(
            projectId,
            latestValidate,
            latestPrescreen,
            filterStale: false,
            currentFilterRevision,
            scenarioPositions,
            artifactStore,
            cancellationToken);

    public static async Task<ReportArtifactCatalog> RefreshCatalogAsync(
        string projectId,
        RuleRunRecord? latestValidate,
        RuleRunRecord? latestPrescreen,
        bool filterStale,
        string? currentFilterRevision,
        IReadOnlyCollection<int> scenarioPositions,
        IReportArtifactStore artifactStore,
        CancellationToken cancellationToken)
    {
        await artifactStore.MarkStaleAsync(
            projectId,
            artifact => ReportExportSupport.IsSourceStale(
                artifact,
                latestValidate,
                latestPrescreen,
                filterStale,
                currentFilterRevision,
                scenarioPositions),
            cancellationToken);

        return await artifactStore.ReadCatalogAsync(projectId, cancellationToken);
    }

    public static object PreviewWire(
        string catalogRevision,
        IReadOnlyList<ReportArtifactCleanupCandidate> candidates)
    {
        long candidateBytes;
        try
        {
            candidateBytes = candidates.Aggregate(
                0L,
                (total, candidate) => checked(total + candidate.Artifact.Bytes));
        }
        catch (OverflowException)
        {
            throw new JetActionException(
                JetErrorCodes.ArtifactCleanupFailed,
                "報告清理候選的檔案大小索引無效，無法安全預覽。");
        }

        return new
        {
            catalogRevision,
            candidateCount = candidates.Count,
            candidateBytes,
            candidates = candidates.Select(candidate => new
            {
                artifactId = candidate.Artifact.ArtifactId,
                kind = ReportArtifactKindValues.ToValue(candidate.Artifact.Kind),
                fileName = candidate.Artifact.RelativeFileName,
                generatedUtc = candidate.Artifact.GeneratedUtc,
                bytes = candidate.Artifact.Bytes,
                reason = ReportArtifactCleanupReasonValues.ToValue(candidate.Reason)
            }).ToArray()
        };
    }

    public static void RequireEmptyObject(JsonElement payload, string action)
    {
        if (payload.ValueKind != JsonValueKind.Object || payload.EnumerateObject().Any())
        {
            throw new JetActionException(
                JetErrorCodes.InvalidPayload,
                $"{action} 的 payload 必須是空物件。");
        }
    }

    public static string RequireCatalogRevisionOnly(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
        {
            throw InvalidConfirmPayload();
        }

        var properties = payload.EnumerateObject().ToArray();
        if (properties.Length != 1
            || !string.Equals(properties[0].Name, "catalogRevision", StringComparison.Ordinal)
            || properties[0].Value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(properties[0].Value.GetString()))
        {
            throw InvalidConfirmPayload();
        }

        return properties[0].Value.GetString()!.Trim();
    }

    public static JetActionException CatalogChanged() => new(
        JetErrorCodes.ArtifactCatalogChanged,
        "報告版本清單已改變，沒有刪除任何檔案；請重新檢查清理預覽。");

    private static JetActionException InvalidConfirmPayload() => new(
        JetErrorCodes.InvalidPayload,
        "report.cleanupConfirm 的 payload 只接受必填字串 'catalogRevision'。");
}
