using JET.Domain;

namespace JET.AuditCore;

/// <summary>
/// 資料驗證報告 V_Report 6 讀借貸不平傳票全部有效分錄的介面，不綁特定資料庫，一個查詢讀完全部 entry_id。
/// 讀出的分錄與順序，和 <see cref="IUnbalancedVoucherPageRepository"/> 逐頁讀完相同：依 entry_id 升冪。
/// 哪些傳票不平只算一次；逐頁讀的每一頁都會重新彙總全部傳票的借貸。
/// </summary>
internal interface IUnbalancedVoucherEntryStreamRepository
{
    IAsyncEnumerable<long> StreamEntryIdsAsync(string projectId, CancellationToken cancellationToken);
}
