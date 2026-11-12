namespace JET.Infrastructure;

/// <summary>
/// <see cref="ILocalProjectDatabase"/> 的「批量列寫入」介面，用來加快大量匯入。
/// 用於熱路徑（匯入 staging、GL/TB 落地投影）；目前只有 SQLite 的實作
/// （<c>SqliteBulkRowWriter</c>），包裝參數化 INSERT。呼叫端只用
/// <see cref="AppendAsync"/> 迴圈＋一次 <see cref="CompleteAsync"/>。
/// </summary>
/// <remarks>
/// 契約：
/// <list type="bullet">
/// <item><see cref="AppendAsync"/> 的 <c>values</c> 依建立時給定的欄位清單順序對位；缺欄／auto-id 由實作補齊。</item>
/// <item><see cref="AppendAsync"/> 完成時已消費該次 <c>values</c>；呼叫端可在 await 後重用同一陣列。</item>
/// <item><see cref="CompleteAsync"/> 之後，寫入的列在「同一顯式交易內」的後續命令可見。
///   呼叫端須在讀取 staging 的 reader 迴圈結束後才呼叫，且在 commit 之前。</item>
/// <item>值型別：string、long、int、bool、null／<see cref="System.DBNull"/>。</item>
/// </list>
/// </remarks>
public interface IBulkRowWriter : IAsyncDisposable
{
    /// <summary>
    /// 寫入一列（值依建立時的欄位清單順序）；完成時已消費 <paramref name="values"/>，
    /// 呼叫端可在 await 後重用同一陣列。
    /// </summary>
    Task AppendAsync(object?[] values, CancellationToken cancellationToken);

    /// <summary>收斂。之後同交易內可見；須在 reader 關閉後、commit 前呼叫。</summary>
    Task CompleteAsync(CancellationToken cancellationToken);
}
