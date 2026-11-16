namespace JET.Domain;

/// <summary>
/// 依一頁分錄的 entry id 讀取已儲存的 RDE 值。實作必須先用 <see cref="ResultPageRdeValueBatch"/> 檢查這批 id，
/// 再用參數化的集合式 SQL 一次讀完，不能逐列查詢。
/// 回傳的只有有值的格子；沒有值的格子，由 Application 組回應時依要求的欄位補 null。
/// </summary>
public interface IResultPageRdeValuesPort
{
    Task<IReadOnlyList<ResultPageRdeValue>> ReadAsync(
        string projectId,
        IReadOnlyList<long> entryIds,
        CancellationToken cancellationToken);
}
