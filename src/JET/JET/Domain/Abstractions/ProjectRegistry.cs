namespace JET.Domain;

/// <summary>
/// 線上（sqlServer）專案的登記簿條目：ProjectDocument（自 registry 的 project_json 反序列化）
/// 搭配 registry 欄位的時間戳。<see cref="CreatedUtc"/> 與 <see cref="LastOpenedUtc"/> 取自
/// registry 資料表欄位（非 doc 內欄位）——serverOnly 條目的清單排序權威即這兩欄
/// （last_opened 只有存在 registry 才能跨機器更新）。
/// </summary>
public sealed record RegisteredProject(
    ProjectDocument Document,
    DateTimeOffset CreatedUtc,
    DateTimeOffset? LastOpenedUtc);

/// <summary>
/// 線上專案登記簿（<c>dbo.project_registry</c> ＋ 存取名單 <c>dbo.project_access</c>）的埠。
/// <b>此埠天生只屬 sqlServer——不在依資料庫種類選定的資料庫組裡</b>：sqlServer 專案的存在性與 metadata 權威
/// 在單庫 <c>JET</c> 的登記簿，sqlite 專案完全不觸及本埠（其資料夾即權威、可攜）。
/// 連線失敗一律由 Application 端 catch 降級（清單 action 絕不整體失敗）——本埠不吞錯、據實拋出。
/// </summary>
public interface IProjectRegistry
{
    /// <summary>
    /// 登記一個 sqlServer 專案：於同一交易內 INSERT registry 一列（project_json 原樣入庫、
    /// created_by 由伺服器端 <c>SUSER_SNAME()</c> 取值）並自動授權建立者 <paramref name="principal"/>
    /// （INSERT access 一列）。重複 project_id → PK 衝突，整筆交易回滾（不留半套）。
    /// </summary>
    Task RegisterAsync(ProjectDocument document, string principal, CancellationToken cancellationToken);

    /// <summary>
    /// 以目前的完整 ProjectDocument 原子更新既有 registry 列的 project_json。
    /// 供 sqlServer 案件在 project 文件狀態改變後維持跨機 serverOnly 物化快照；
    /// 登記不存在時必須 fail-loud，不得悄悄新增未授權案件。
    /// </summary>
    Task UpdateDocumentAsync(ProjectDocument document, CancellationToken cancellationToken);

    /// <summary>登記簿是否已有此 project_id（不分 principal）。供建案撞名預檢與載入的登記狀態判定。</summary>
    Task<bool> ExistsAsync(string projectId, CancellationToken cancellationToken);

    /// <summary>當前 principal 被授權可見的所有 sqlServer 專案（registry ⋈ access）。</summary>
    Task<IReadOnlyList<RegisteredProject>> ListVisibleAsync(string principal, CancellationToken cancellationToken);

    /// <summary>當前 principal 可見且 project_id 相符的單一登記（無或未授權時回 null）。</summary>
    Task<RegisteredProject?> FindVisibleAsync(string projectId, string principal, CancellationToken cancellationToken);

    /// <summary>戳記 last_opened_utc（載入成功後 best-effort；失敗由呼叫端吞掉，不影響載入）。</summary>
    Task TouchLastOpenedAsync(string projectId, CancellationToken cancellationToken);

    /// <summary>移除登記：於同一交易內 DELETE access 與 registry 兩表對應列（刪案時先清、清單即時消失）。</summary>
    Task UnregisterAsync(string projectId, CancellationToken cancellationToken);
}
