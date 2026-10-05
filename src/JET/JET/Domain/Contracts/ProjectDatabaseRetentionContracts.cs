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
