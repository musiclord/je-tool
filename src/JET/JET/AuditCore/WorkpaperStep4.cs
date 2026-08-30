using JET.Domain;

namespace JET.AuditCore;

/// <summary>
/// Finalized WorkingPaper step4 的 export-only 傳票列。傳票摘要與所選情境標記
/// 由同一個資料庫 statement 產生，避免 production writer 重用公開分頁查詢反覆掃描母體。
/// </summary>
internal sealed record WorkpaperStep4VoucherRow(
    VoucherTagRow Voucher,
    IReadOnlyList<int> MatchedPositions);

/// <summary>
/// Finalized WorkingPaper step4 的 provider-neutral 單次串流 port。
/// 公開 query.tagMatrixVoucherPage 保留既有 keyset contract；production writer
/// 只透過本 port 取得 ordered forward-only voucher stream。
/// </summary>
internal interface IWorkpaperStep4StreamRepository
{
    IAsyncEnumerable<WorkpaperStep4VoucherRow> StreamAsync(
        string projectId,
        GlPopulationContext context,
        IReadOnlyList<int> scenarioPositions,
        CancellationToken cancellationToken);
}
