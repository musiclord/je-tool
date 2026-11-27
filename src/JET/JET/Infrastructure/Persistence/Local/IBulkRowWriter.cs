namespace JET.Infrastructure;

/// <summary>
/// 把很多列寫進同一張資料表的寫入器，由 <see cref="ILocalProjectDatabase"/> 建立，用來加快大量匯入。
/// 用在列數最多的兩處：匯入時寫匯入暫存表，以及確認配對時寫 GL 與 TB。實作是 SQLite 的
/// <c>SqliteBulkRowWriter</c>，裡面是一句重複執行的參數化 INSERT。呼叫端每一列呼叫一次
/// <see cref="AppendAsync"/>，全部寫完再呼叫一次 <see cref="CompleteAsync"/>。
/// </summary>
/// <remarks>
/// 使用規則：
/// <list type="bullet">
/// <item><see cref="AppendAsync"/> 的 <c>values</c> 要照建立寫入器時給的欄位清單順序排；清單沒列的欄位與自動編號由實作補上。</item>
/// <item><see cref="AppendAsync"/> 回來時已經把那一次的 <c>values</c> 用完，呼叫端可以在 await 之後重用同一個陣列。</item>
/// <item>呼叫 <see cref="CompleteAsync"/> 之後，同一個交易裡接下來的命令讀得到寫入的列。
///   呼叫端要等讀匯入暫存表的 reader 迴圈結束後才呼叫，而且要在 commit 之前。</item>
/// <item>值的型別：string、long、int、bool、null 或 <see cref="System.DBNull"/>。</item>
/// </list>
/// </remarks>
public interface IBulkRowWriter : IAsyncDisposable
{
    /// <summary>
    /// 寫入一列，值照建立寫入器時給的欄位清單順序排。回來時已經把 <paramref name="values"/> 用完，
    /// 呼叫端可以在 await 之後重用同一個陣列。
    /// </summary>
    Task AppendAsync(object?[] values, CancellationToken cancellationToken);

    /// <summary>結束寫入。之後同一個交易裡的命令讀得到寫入的列；要在 reader 關閉之後、commit 之前呼叫。</summary>
    Task CompleteAsync(CancellationToken cancellationToken);
}
