namespace JET.Domain;

/// <summary>條件在情境裡的位置（第幾組、第幾條），供傳票明細說明「本列符合哪些條件」與錯誤定位。</summary>
public sealed record FilterConditionPosition(int Group, int Rule);

/// <summary>命中傳票摘要；<paramref name="VoucherTotalScaled"/> 是該傳票期間內全部分錄的借方總額（scaled 整數），供「傳票總額」排序。</summary>
public sealed record FilterVoucherSummary(string DocumentNumber, string? PostDate, long HitRowCount, long TotalRowCount, long VoucherTotalScaled = 0);

/// <summary>
/// 命中傳票展開後的一列分錄。命中列以外的列是同傳票的參考列；PrimaryConditions 是決定命中的條件，
/// EvidenceConditions 是 sameVoucher 群組裡由本列提供佐證的條件，VoucherConditions 是傳票層條件（absent 模式）。
/// </summary>
public sealed record FilterVoucherDetail(long EntryId, string DocumentNumber, string? LineItem,
    string? PostDate, string? ApprovalDate, string? AccountCode, string? AccountName,
    string? Description, long AmountScaled, string DrCr, bool IsHit)
{
    public IReadOnlyList<FilterConditionPosition> PrimaryConditions { get; init; } = [];
    public IReadOnlyList<FilterConditionPosition> EvidenceConditions { get; init; } = [];
    public IReadOnlyList<FilterConditionPosition> VoucherConditions { get; init; } = [];
}
public sealed record FilterVoucherPage(IReadOnlyList<FilterVoucherSummary> Vouchers,
    IReadOnlyList<FilterVoucherDetail> Details, string? NextKey);

public interface IFilterVoucherRepository
{
    Task<string> ReadRevisionAsync(string projectId, CancellationToken ct);
    Task<FilterVoucherPage> ReadAsync(string projectId, FilterScenarioSpec scenario, FilterRuleContext context,
        string? documentNumber, PageRequest request, CancellationToken ct);
}
