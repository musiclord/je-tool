namespace JET.Domain;

/// <summary>
/// 幽靈登記（<c>dbo.project_registry</c> 有列、單庫卻無對應 <c>prj_%</c> schema）的一筆漂移檢查結果。
/// 建議清 registry 列;帶 schemaName 供人工核對。
/// </summary>
public sealed record GhostRegistration(string ProjectId, string SchemaName);

/// <summary>
/// 單庫資料庫漂移檢查的報告（<c>dev.db.reconcile</c>，只供開發診斷）。三個漂移類別皆<b>只列建議、不自動清理</b>。
/// </summary>
public sealed record ControlPlaneReconcileReport(
    // 孤兒 schema:sys.schemas 有 prj_%、registry 無對應列（舊資料殘留,建議 DROP）。
    IReadOnlyList<string> OrphanSchemas,
    // 幽靈登記:registry 有列、無對應 schema（建議清 registry 列）。
    IReadOnlyList<GhostRegistration> GhostRegistrations,
    // 殭屍資料夾:本機 projects/ 有 sqlServer project.json、對應 schema 卻不存在（建議清資料夾）——回專案 id。
    IReadOnlyList<string> ZombieFolders);

/// <summary>
/// 單庫資料庫漂移檢查的埠（<c>dev.db.reconcile</c>），比對 sys.schemas、專案登錄與本機 projects 資料夾。
/// <b>只屬 sqlServer</b>、Debug-only。實作在 Infrastructure（需 schema 衍生與單庫查詢），回報三處之間的漂移。
/// </summary>
public interface IControlPlaneReconciler
{
    Task<ControlPlaneReconcileReport> ReconcileAsync(CancellationToken cancellationToken);
}
