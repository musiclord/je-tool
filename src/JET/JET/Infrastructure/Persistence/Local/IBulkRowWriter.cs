namespace JET.Infrastructure;

/// <summary>
/// 本地引擎家族（<see cref="ILocalProjectDatabase"/>）的「批量列寫入」縫（spec §7 效能修法）。
/// 熱路徑（匯入 staging、GL/TB 落地投影）原以「單一顯式交易內逐列參數化 INSERT」寫入，對
/// OLAP 引擎（DuckDB）是最慢形；此縫讓各引擎自適配：SQLite 包裝現行參數化 INSERT（行為凍結），
/// DuckDB 走原生 Appender（批量 flush，快一數量級）。業務邏輯與共用 SQL 文本零改動——呼叫端只把
/// 「逐列 ExecuteNonQuery 迴圈」換成 <see cref="AppendAsync"/> 迴圈＋一次 <see cref="CompleteAsync"/>。
/// </summary>
/// <remarks>
/// 契約（兩引擎一致）：
/// <list type="bullet">
/// <item><see cref="AppendAsync"/> 的 <c>values</c> 依建立時給定的欄位清單順序對位；缺欄／auto-id 由實作補齊。</item>
/// <item><see cref="AppendAsync"/> 完成時已消費該次 <c>values</c>；呼叫端可在 await 後重用同一陣列。</item>
/// <item><see cref="CompleteAsync"/> 之後，寫入的列在「同一顯式交易內」的後續命令可見（DuckDB Appender flush
///   於 Close；本機探針實證同交易可見）。呼叫端須在讀取 staging 的 reader 迴圈結束後才呼叫，且在 commit 之前。</item>
/// <item>值型別：string、long、int、bool、null／<see cref="System.DBNull"/>；DuckDB 臂以目標欄型別強制轉換
///   （int↔long、null→AppendNullValue），落庫值與參數化路徑一致。</item>
/// </list>
/// </remarks>
public interface IBulkRowWriter : IAsyncDisposable
{
    /// <summary>
    /// 寫入一列（值依建立時的欄位清單順序）；完成時已消費 <paramref name="values"/>，
    /// 呼叫端可在 await 後重用同一陣列。DuckDB 臂緩衝進 data chunk、達門檻自動 flush。
    /// </summary>
    Task AppendAsync(object?[] values, CancellationToken cancellationToken);

    /// <summary>收斂（DuckDB：Close appender flush 剩餘 chunk 進交易）。之後同交易內可見；須在 reader 關閉後、commit 前呼叫。</summary>
    Task CompleteAsync(CancellationToken cancellationToken);
}
