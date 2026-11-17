using JET.Domain;

namespace JET.AuditCore;

/// <summary>
/// 資料可靠性抽樣清單與底稿 step2 讀一次驗證全部樣本的介面，不綁特定資料庫，一個查詢從頭讀到尾。
/// 讀出的樣本與順序，和畫面用的 <see cref="IInfSamplePageRepository"/> 沒有指定排序與搜尋時逐頁讀完相同：
/// 只讀 <c>resultId</c> 那一次驗證抽出、而且納入測試的樣本，依 entry_id 升冪。
/// </summary>
internal interface IInfSampleStreamRepository
{
    IAsyncEnumerable<InfSampleRow> StreamAsync(string projectId, string resultId, CancellationToken cancellationToken);
}
