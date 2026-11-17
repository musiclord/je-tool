using JET.Domain;

namespace JET.AuditCore;

/// <summary>
/// 底稿 step1-1 讀借貸不平傳票明細的介面，不綁特定資料庫，一個查詢從頭讀到尾。
/// 不平的傳票依傳票號碼與總帳日期彙總後依序交出；判定哪些傳票不平只看傳票號碼，
/// 和畫面用的 <see cref="IVoucherBalancePageRepository"/> 相同。
/// </summary>
internal interface IUnbalancedVoucherDateRowStreamRepository
{
    IAsyncEnumerable<UnbalancedVoucherDateRow> StreamVoucherDateRowsAsync(
        string projectId, CancellationToken cancellationToken);
}
