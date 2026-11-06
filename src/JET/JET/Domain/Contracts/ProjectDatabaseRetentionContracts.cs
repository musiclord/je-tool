namespace JET.Domain;

/// <summary>
/// 在一次操作期間保持案件資料庫開啟。DuckDB 每次重新開啟資料庫要幾十毫秒，同一個操作裡的
/// 每個 repository 呼叫若都各自開關，時間會疊加；先持有一條連線，之後的連線就沿用同一個資料庫實體。
/// 操作結束時釋放，兩次操作之間檔案照舊解除鎖定。
/// </summary>
/// <remarks>
/// 只有 DuckDB 實作有作用。SQLite 已用連線池，SQL Server 是遠端服務，兩者不需要持有。
/// 介面放在 Domain，讓 Bridge 與 Application 都能使用而不依賴 Infrastructure。
/// </remarks>
public interface IProjectDatabaseRetention
{
    /// <summary>
    /// 開始持有該案件的資料庫。不適用、資料庫檔不存在或開啟失敗時回 null，不往外丟例外，
    /// 讓之後的 repository 照原本的方式報錯。回傳物件 Dispose 時釋放，重複 Dispose 不出錯。
    /// </summary>
    IDisposable? TryRetain(string projectId);
}

/// <summary>
/// 案件資料庫在作業結束與離開案件時的收尾。SQLite 先把資料寫進記錄檔，再併回資料庫檔；記錄檔用過不會自己縮小，
/// 一百萬筆的匯入會留下幾百 MB 的記錄檔，而且記錄檔還有資料沒併回時，只複製資料庫檔會拿到不完整的副本。
/// 所以每項改資料的作業結束後併回並清空記錄檔，離開案件與結束 JET 時再關閉這個案件的連線（第 22 步盤點的第 6 項）。
/// </summary>
/// <remarks>
/// 只有 SQLite 實作有作用。DuckDB 每項作業結束時關閉資料庫，關閉時就會併回；SQL Server 是遠端服務。
/// 收尾失敗不改變作業結果：已提交的資料在記錄檔裡照樣完整，下一次併回或關閉時再處理。
/// </remarks>
public interface IProjectDatabaseCheckpoint
{
    /// <summary>
    /// 一項改資料的作業結束後呼叫，成功、失敗或取消都呼叫：把記錄檔裡已提交的資料併回資料庫檔，再把記錄檔清空。
    /// 資料庫檔不存在時什麼都不做；有其他連線正在讀寫時只併回能併的部分，不等待。
    /// </summary>
    Task CheckpointAsync(string projectId, CancellationToken cancellationToken);

    /// <summary>
    /// 離開案件、開案失敗與結束 JET 時呼叫：併回並清空記錄檔，再關閉本程式對這個案件資料庫的所有連線。
    /// 正在執行的查詢用完連線後才關閉。資料庫檔不存在時不建立新檔。
    /// </summary>
    Task CloseAsync(string projectId, CancellationToken cancellationToken);
}
