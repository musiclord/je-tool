namespace JET.Domain;

/// <summary>
/// 依單一 keyset page 的 entry ids 讀取已保存 RDE values。實作必須先套
/// <see cref="ResultPageRdeValueBatch"/>，以參數化 set-based SQL 一次讀取，不得逐列查詢。
/// 回傳只包含 present cells；missing cell 由 Application renderer 依 requested columns 補 null。
/// </summary>
public interface IResultPageRdeValuesPort
{
    Task<IReadOnlyList<ResultPageRdeValue>> ReadAsync(
        string projectId,
        IReadOnlyList<long> entryIds,
        CancellationToken cancellationToken);
}
