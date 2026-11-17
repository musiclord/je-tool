using JET.Domain;

namespace JET.AuditCore;

/// <summary>
/// 條件篩選報告「#Criteria Select n」讀整張傳票分錄的介面，不綁特定資料庫，一個情境用一個查詢從頭讀到尾。
/// 讀出的分錄與順序，和畫面用的 <see cref="IHighRiskSummaryRowPageRepository"/> 只指定這個情境、
/// 沒有指定排序時逐頁讀完相同：命中傳票的全部有效分錄，依 entry_id 升冪，不含傳票號碼空白的分錄。
/// 命中傳票只算一次，不像逐頁讀那樣每一頁都重算一次。<c>IsHit</c> 是這筆分錄本身是否命中這個情境。
/// </summary>
internal interface IHighRiskSummaryRowStreamRepository
{
    IAsyncEnumerable<(long EntryId, bool IsHit)> StreamAsync(
        string projectId,
        GlPopulationContext context,
        int scenarioPosition,
        CancellationToken cancellationToken);
}
