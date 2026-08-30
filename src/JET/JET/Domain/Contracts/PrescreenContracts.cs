namespace JET.Domain;

/// <summary>
/// prescreen.run 的執行輸入。前置條件由 handler 判定後以旗標傳入：
/// LastPeriodStart=null 或 HasApprovalDate=false → 期末後核准跳過；
/// HasApprovalDate=false → 週末/假日核准計數回 null；HasCreatedBy=false → 編製者彙總跳過；
/// HasHolidays=false → 假日規則跳過；RunUnexpectedAccountPair=false →
/// 未預期借貸組合跳過（科目配對未匯入或缺 Revenue/對方分類）；
/// HasAuthorizedPreparers=false → 非授權編製人員跳過（授權清單未匯入）。
/// 連續零尾數門檻固定取 TrailingZeroThreshold.DefaultZerosThreshold。此 public input
/// 只供既有 repository caller 相容；production typed lifecycle 的 prerequisites、
/// N/A 與 outward nullable shape 由 AuditCore 決定。
/// </summary>
public sealed record PrescreenRunInput(
    string? LastPeriodStart,
    string PeriodStart,
    string PeriodEnd,
    bool HasApprovalDate,
    bool HasCreatedBy,
    bool HasHolidays,
    bool RunUnexpectedAccountPair,
    bool HasAuthorizedPreparers,
    int MoneyScale,
    IReadOnlyList<int>? NonWorkingDays = null);

public sealed record CreatorSummaryRow(
    string CreatedBy,
    long EntryCount,
    long DebitTotalScaled,
    long CreditTotalScaled,
    long ManualCount);

public sealed record AccountUsageRow(
    string AccountCode,
    string? AccountName,
    long EntryCount,
    long DebitTotalScaled,
    long CreditTotalScaled);

public sealed record PrescreenRunResult(
    long PostPeriodApprovalCount,
    long SuspiciousKeywordsCount,
    long UnexpectedAccountPairCount,
    long TrailingZerosCount,
    int ZerosThreshold,
    IReadOnlyList<CreatorSummaryRow> Creators,
    long DistinctAccountCount,
    IReadOnlyList<AccountUsageRow> Accounts,
    long WeekendPostingCount,
    long? WeekendApprovalCount,
    long HolidayPostingCount,
    long? HolidayApprovalCount,
    long BlankDescriptionCount,
    long BackdatedPostingCount,
    long NonAuthorizedPreparerCount,
    long LowFrequencyPreparerCount,
    long LowFrequencyAccountCount);

public interface IPrescreenRunRepository
{
    Task<PrescreenRunResult> RunAsync(
        string projectId,
        PrescreenRunInput input,
        CancellationToken cancellationToken);
}
