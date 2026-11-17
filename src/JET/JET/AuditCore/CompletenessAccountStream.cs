using JET.Domain;

namespace JET.AuditCore;

/// <summary>
/// 正式輸出讀完整性全科目表的介面，不綁特定資料庫，一個查詢從頭讀到尾，各科目的 GL 與 TB 彙總只算一次。
/// 讀出的科目與順序，和畫面用的 <see cref="ICompletenessAccountPageRepository"/> 沒有指定排序與搜尋時逐頁讀完相同：
/// 空白科目在最前，同一個科目試算表那一列在總帳那一列之前，其餘依科目編號。
/// 資料驗證報告的 V_Report 5 與底稿 Step 1 用它，不用逐頁讀；逐頁讀的每一頁都會重算全部科目的彙總。
/// </summary>
internal interface ICompletenessAccountStreamRepository
{
    IAsyncEnumerable<CompletenessDiffAccount> StreamAsync(string projectId, CancellationToken cancellationToken);
}

/// <summary>
/// 正式輸出讀完整性差異表的介面，只列有差異的科目，其餘和 <see cref="ICompletenessAccountStreamRepository"/> 相同。
/// 讀出的科目與順序，和畫面用的 <see cref="ICompletenessDiffPageRepository"/> 沒有指定排序與搜尋時逐頁讀完相同。
/// 資料驗證報告的差異說明頁與底稿 Step 1-3 用它。
/// </summary>
internal interface ICompletenessDiffStreamRepository
{
    IAsyncEnumerable<CompletenessDiffAccount> StreamAsync(string projectId, CancellationToken cancellationToken);
}
