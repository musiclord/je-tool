namespace JET.Domain;

/// <summary>Pre-screening R6 的完整科目使用彙總；基數是 distinct accounts，不是 GL 明細母體。</summary>
public sealed record AccountUsageExportRow(
    string AccountCode,
    string? AccountName,
    long EntryCount,
    long DebitTotalScaled,
    long CreditTotalScaled);

public interface IAccountUsageExportRepository
{
    Task<IReadOnlyList<AccountUsageExportRow>> FetchAllAsync(
        string projectId,
        string periodStart,
        string periodEnd,
        CancellationToken cancellationToken);
}
