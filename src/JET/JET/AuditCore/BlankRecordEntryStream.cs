using JET.Domain;

namespace JET.AuditCore;

/// <summary>
/// 資料驗證報告讀一類空值或期外日期紀錄全部明細的介面，不綁特定資料庫，一個查詢讀完全部分錄的 entry_id。
/// 讀出的分錄與順序，和畫面用的 <see cref="IBlankRecordsPageRepository"/> 沒有指定排序與搜尋時逐頁讀完相同：
/// 只看納入測試的分錄，依 entry_id 升冪。<c>periodStart</c> 與 <c>periodEnd</c> 只有「核准日不在期間」這一類用到。
/// </summary>
internal interface IBlankRecordEntryStreamRepository
{
    IAsyncEnumerable<long> StreamEntryIdsAsync(
        string projectId,
        BlankRecordCategory category,
        string periodStart,
        string periodEnd,
        CancellationToken cancellationToken);
}
