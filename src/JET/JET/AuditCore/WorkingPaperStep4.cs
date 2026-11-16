using JET.Domain;

namespace JET.AuditCore;

/// <summary>
/// 工作底稿 step4 匯出專用的一列傳票。傳票摘要和所選情境的標記由同一句 SQL 產生，
/// 正式匯出就不必重複呼叫畫面用的分頁查詢、一再掃描母體。
/// </summary>
internal sealed record WorkingPaperStep4VoucherRow(
    VoucherTagRow Voucher,
    IReadOnlyList<int> MatchedPositions);

/// <summary>
/// 工作底稿 step4 的傳票讀取介面，不綁特定資料庫，一次查詢從頭讀到尾。
/// 畫面用的 <c>query.highRiskSummaryVoucherPage</c> 維持原來的分頁方式；
/// 正式匯出只透過這個介面，依排序順序一張一張往前讀傳票，不回頭。
/// </summary>
internal interface IWorkingPaperStep4StreamRepository
{
    IAsyncEnumerable<WorkingPaperStep4VoucherRow> StreamAsync(
        string projectId,
        GlPopulationContext context,
        IReadOnlyList<int> scenarioPositions,
        CancellationToken cancellationToken);
}
