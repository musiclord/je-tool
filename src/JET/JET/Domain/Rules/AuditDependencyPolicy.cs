namespace JET.Domain;

/// <summary>
/// 會讓既有審計結果失效的上游 mutation。這些名稱描述資料修改事件，不描述
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
    PreparationDate,
    SchemaV7Migration,
    AccountTaxonomy
}

/// <summary>
/// 上游 mutation 對衍生審計資料的精確影響。使用者 2026-10-07 裁定上游修改清除下游：
/// 會讓篩選命中過期的修改，同時清掉全部已存篩選情境定義，不保留需要逐一修正的舊情境。
/// GL control total 仍是明列的保留例外，避免呼叫端以方便為由擴大清除。
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
        AuditMutation.GlImport or AuditMutation.GlProjection or AuditMutation.TbImport or AuditMutation.TbProjection =>
            Impact(validation: true, prescreen: true, filterHits: true),
        AuditMutation.Calendar or AuditMutation.AccountMapping or AuditMutation.AuthorizedPreparer or AuditMutation.PreparationDate =>
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
            // 情境定義與命中同進退；總帳與試算表使用同一條資料修改規則。
            InvalidateFilterScenarioDefinitions: filterHits,
            ClearGlControlTotal: false);
}
