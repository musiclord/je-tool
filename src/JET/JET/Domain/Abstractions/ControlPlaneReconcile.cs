namespace JET.Domain;

/// <summary>
/// 幽靈登記（<c>dbo.project_registry</c> 有列、單庫卻無對應 <c>prj_%</c> schema）的一筆對帳結果。
/// 建議清 registry 列;帶 schemaName 供人工核對。
/// </summary>
public sealed record GhostRegistration(string ProjectId, string SchemaName);

/// <summary>
/// 單庫控制面三方對帳報告（<c>dev.db.reconcile</c>，控制面第四輪 §5）。三個漂移類別皆<b>只列建議、不自動清理</b>。
/// </summary>
public sealed record ControlPlaneReconcileReport(
    // 孤兒 schema:sys.schemas 有 prj_%、registry 無對應列（舊資料殘留,建議 DROP）。
    IReadOnlyList<string> OrphanSchemas,
    // 幽靈登記:registry 有列、無對應 schema（建議清 registry 列）。
    IReadOnlyList<GhostRegistration> GhostRegistrations,
    // 殭屍資料夾:本機 projects/ 有 sqlServer project.json、對應 schema 卻不存在（建議清資料夾）——回專案 id。
    IReadOnlyList<string> ZombieFolders);

/// <summary>
/// 單庫控制面三方對帳（sys.schemas ↔ registry ↔ 本機 projects 資料夾）的埠（<c>dev.db.reconcile</c>）。
/// <b>天生只屬 sqlServer 控制面</b>、Debug-only。實作在 Infrastructure（需 schema 衍生與單庫查詢）,回報跨三方的漂移。
/// </summary>
public interface IControlPlaneReconciler
{
    Task<ControlPlaneReconcileReport> ReconcileAsync(CancellationToken cancellationToken);
}

/// <summary>
/// provider 解析快取（projectId → databaseProvider）的失效埠。供 <c>dev.db.reconcile</c> 對帳時一併全清——
/// 解「app 執行中外部刪除 projects 資料夾後,快取殘留仍劫持同名重建路由」的技術債。
/// 實作為 Infrastructure 的 <c>ProjectProviderResolver</c>;Application 只依此埠、不觸 Infrastructure。
/// </summary>
public interface IProviderResolutionCache
{
    /// <summary>清空整個 provider 解析快取（下次解析各專案時重讀 project.json）。</summary>
    void InvalidateAll();
}
