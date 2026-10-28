namespace JET.Domain;

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
    long NonAuthorizedPreparerCount,
    long LowFrequencyPreparerCount,
    long LowFrequencyAccountCount)
{
    // 科目數與上面的命中分錄列數分開。相容呼叫端未提供時保留 null，不冒充零個科目。
    public long? LowFrequencyDistinctAccountCount { get; init; }
}
