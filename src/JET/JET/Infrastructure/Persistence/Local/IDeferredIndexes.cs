namespace JET.Infrastructure;

/// <summary>
/// 大量逐列寫入一張表之前先刪掉、寫完再建回的索引，由 <see cref="ILocalProjectDatabase.DeferIndexesAsync"/> 建立。
/// 用在確認 GL 配對時寫 gl。刪除與建回都在建立時給的同一個交易裡，交易回復時刪掉的索引跟著回復；
/// 建回後索引的名稱、定義文字與彼此的先後都和刪除前相同。
/// </summary>
public interface IDeferredIndexes
{
    /// <summary>依刪除前的先後建回索引；要在逐列寫完之後、commit 之前呼叫。</summary>
    Task RebuildAsync(CancellationToken cancellationToken);
}
