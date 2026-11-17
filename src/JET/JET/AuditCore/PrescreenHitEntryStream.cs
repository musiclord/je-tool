using JET.Domain;

namespace JET.AuditCore;

/// <summary>
/// 預篩選報告讀一條規則全部明細的介面，不綁特定資料庫，一個查詢讀完全部命中分錄的 entry_id。
/// 讀出的分錄與順序，和畫面用的 <see cref="IPrescreenPageRepository"/> 沒有指定排序時逐頁讀完相同：依 entry_id 升冪。
/// 列出整張命中傳票的規則（未預期借貸組合）用它，命中傳票只算一次；逐頁讀的每一頁都會重算全部命中傳票。
/// </summary>
internal interface IPrescreenHitEntryStreamRepository
{
    IAsyncEnumerable<long> StreamEntryIdsAsync(
        string projectId,
        string ruleKey,
        FilterConditionContext context,
        CancellationToken cancellationToken);
}
