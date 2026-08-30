namespace JET.Domain;

/// <summary>
/// 會讓既有審計結果失效的上游 mutation。這些名稱描述真正依賴，不描述
/// repository 或 provider 實作。
/// </summary>
internal enum AuditMutation
{
    GlImport,
    GlProjection,
    TbImport,
    TbProjection,
    Calendar,
    AccountMapping,
    AuthorizedPreparer,
    SchemaV7Migration,
    AccountTaxonomy
}

/// <summary>
/// 上游 mutation 對衍生審計資料的精確影響。Filter scenario definition／revision
/// 與 GL control total 是明列的保留例外，避免呼叫端以方便為由擴大清除。
/// </summary>
internal sealed record AuditDependencyImpact(
    bool InvalidateValidation,
    bool InvalidatePrescreen,
    bool InvalidateFilterHits,
    bool InvalidateFilterScenarioDefinitions,
    bool ClearGlControlTotal);

/// <summary>
/// Validation／prescreen／filter 失效矩陣的唯一 Domain 政策來源。
/// Infrastructure 只能消費結果，不能自行重建另一份 scope switch。
/// </summary>
internal static class AuditDependencyPolicy
{
    internal static AuditDependencyImpact For(AuditMutation mutation) => mutation switch
    {
        AuditMutation.GlImport or AuditMutation.GlProjection =>
            Impact(validation: true, prescreen: true, filterHits: true),
        AuditMutation.TbImport or AuditMutation.TbProjection =>
            Impact(validation: true, prescreen: false, filterHits: false),
        AuditMutation.Calendar or AuditMutation.AccountMapping or AuditMutation.AuthorizedPreparer =>
            Impact(validation: false, prescreen: true, filterHits: true),
        AuditMutation.SchemaV7Migration =>
            Impact(validation: true, prescreen: true, filterHits: true),
        AuditMutation.AccountTaxonomy =>
            Impact(validation: false, prescreen: true, filterHits: true),
        _ => throw new ArgumentOutOfRangeException(nameof(mutation), mutation, null)
    };

    private static AuditDependencyImpact Impact(
        bool validation,
        bool prescreen,
        bool filterHits) =>
        new(
            validation,
            prescreen,
            filterHits,
            InvalidateFilterScenarioDefinitions: false,
            ClearGlControlTotal: false);
}
