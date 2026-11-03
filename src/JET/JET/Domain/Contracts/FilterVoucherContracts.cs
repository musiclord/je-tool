namespace JET.Domain;

/// <summary>條件在情境裡的位置（第幾組、第幾條），供傳票明細說明「本列符合哪些條件」與錯誤定位。</summary>
public sealed record FilterConditionPosition(int Group, int Rule);

/// <summary>命中傳票摘要；<paramref name="VoucherTotalScaled"/> 是該傳票期間內全部分錄的借方總額（scaled 整數），供「傳票總額」排序。</summary>
public sealed record FilterVoucherSummary(string VoucherNumber, string? GlDate, long HitRowCount, long TotalRowCount, long VoucherTotalScaled = 0);

/// <summary>
/// 命中傳票展開後的一列分錄。命中列以外的列是同傳票的參考列；PrimaryConditions 是決定命中的條件，
/// VoucherConditions 是整張傳票條件。
/// </summary>
public sealed record FilterVoucherDetail(long EntryId, string VoucherNumber, string? LineItem,
    string? GlDate, string? ApprovalDate, string? AccountNumber, string? AccountName,
    string? Description, long AmountScaled, string DrCr, bool IsHit)
{
    public IReadOnlyList<FilterConditionPosition> PrimaryConditions { get; init; } = [];
    public IReadOnlyList<FilterConditionPosition> VoucherConditions { get; init; } = [];
}
public sealed record FilterVoucherPage(IReadOnlyList<FilterVoucherSummary> Vouchers,
    IReadOnlyList<FilterVoucherDetail> Details, string? NextKey, long? TotalCount = null);

public interface IFilterVoucherRepository
{
    Task<string> ReadRevisionAsync(string projectId, CancellationToken ct);

    /// <param name="countWholeVoucherRows">
    /// 為真時，傳票摘要的符合分錄數改用整張傳票的分錄數（排序與搜尋同一個值），口徑與 Criteria Selection 摘要相同。
    /// </param>
    Task<FilterVoucherPage> ReadAsync(string projectId, FilterScenarioSpec scenario, FilterConditionContext context,
        string? voucherNumber, PageRequest request, bool countWholeVoucherRows, CancellationToken ct);

    /// <summary>
    /// 已儲存情境的同一份傳票頁，讀儲存時存下的命中與符合第幾組第幾條，不再判斷條件；傳票的分錄數、總額與日期仍讀 GL。
    /// <paramref name="scenario"/> 只用來分辨每條是主要條件還是整張傳票條件。
    /// </summary>
    Task<FilterVoucherPage> ReadSavedAsync(string projectId, int scenarioId, FilterScenarioSpec scenario,
        FilterConditionContext context, string? voucherNumber, PageRequest request, bool countWholeVoucherRows, CancellationToken ct);
}
