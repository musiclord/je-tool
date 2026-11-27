using JET.Domain;

namespace JET.AuditCore;

/// <summary>
/// 資料驗證報告 V_Report 6 讀借貸不平傳票全部有效分錄的介面，不綁特定資料庫，一個查詢讀完全部 entry_id，依 entry_id 升冪。
/// 哪些傳票不平只算一次。
/// </summary>
internal interface IUnbalancedVoucherEntryStreamRepository
{
    IAsyncEnumerable<long> StreamEntryIdsAsync(string projectId, CancellationToken cancellationToken);
}
