namespace JET.Domain;

/// <summary>
/// 跨專案系統設定的埠（<c>dbo.app_config</c>，控制面第四輪 §3）。<b>天生只屬 sqlServer 單庫控制面</b>——
/// 存放不繫任一專案的全域參數（本輪先備地基,消費者第六輪抵達:專案租約鎖的心跳／逾時參數存此）。
/// value 一律以 JSON 字串進出（呼叫端自負序列化）；不存在的 key 回 null。UPSERT 語意（同 key 覆寫,不重複）。
/// SQLite／DuckDB 專案不涉本埠（本地檔式模型無跨專案系統設定）。
/// </summary>
public interface IAppConfigStore
{
    /// <summary>取 key 的 value_json；不存在回 null。</summary>
    Task<string?> GetAsync(string key, CancellationToken cancellationToken);

    /// <summary>寫入或覆寫 key 的 value_json（UPSERT）；updated_by 由伺服器端 SUSER_SNAME() 取值。</summary>
    Task SetAsync(string key, string valueJson, CancellationToken cancellationToken);
}
