using JET.Domain;

namespace JET.AuditCore;

/// <summary>
/// 資料驗證報告讀來源資料品質全部明細的介面，不綁特定資料庫，一個查詢從頭讀到尾。
/// 讀出的分錄與順序，和畫面用的 <see cref="ISourceQualityPageRepository"/> 沒有指定排序與搜尋時逐頁讀完相同：
/// 總帳日期空白的分錄，連未納入測試的一起看，依 entry_id 升冪。
/// </summary>
internal interface ISourceQualityFindingStreamRepository
{
    IAsyncEnumerable<SourceQualityFindingRow> StreamAsync(string projectId, CancellationToken cancellationToken);
}
