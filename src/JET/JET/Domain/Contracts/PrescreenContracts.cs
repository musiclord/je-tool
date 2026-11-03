namespace JET.Domain;

public sealed record PreparerSummaryRow(
    string CreatedBy,
    long EntryCount,
    long DebitTotalScaled,
    long CreditTotalScaled,
    long ManualCount);

public sealed record AccountUsageRow(
    string AccountNumber,
    string? AccountName,
    long EntryCount,
    long DebitTotalScaled,
    long CreditTotalScaled);

public sealed record PrescreenRunResult(
    long PrepPeriodApprovalCount,
    long SuspiciousKeywordsCount,
    long UnexpectedAccountPairCount,
    long TrailingZerosCount,
    int ZerosThreshold,
    IReadOnlyList<PreparerSummaryRow> Preparers,
    long DistinctAccountCount,
    IReadOnlyList<AccountUsageRow> Accounts,
    long WeekendPostingCount,
    long? WeekendApprovalCount,
    long HolidayPostingCount,
    long? HolidayApprovalCount,
    long BlankDescriptionCount,
    long NonAuthorizedPreparerCount);
