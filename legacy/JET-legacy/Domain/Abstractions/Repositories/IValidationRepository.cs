namespace JET.Domain.Abstractions.Repositories
{
    public interface IValidationRepository
    {
        Task<ValidationRunResult> RunAsync(string projectId, CancellationToken cancellationToken);

        Task<ValidationDetailsPageResult> QueryDetailsPageAsync(
            string projectId,
            string kind,
            long? cursor,
            int pageSize,
            CancellationToken cancellationToken);
    }

    public sealed record ValidationRunResult(
        string RunId,
        ValidationStats Stats,
        ValidationSummary Summary,
        int V1,
        int V2,
        int V3,
        int V4,
        IReadOnlyList<ValidationDiffAccount> DiffAccounts);

    public sealed record ValidationStats(
        int Total,
        int Docs,
        decimal TotalDebit,
        decimal TotalCredit,
        decimal Net);

    public sealed record ValidationSummary(
        int CompletenessDiffAccounts,
        int OutOfBalanceDocuments,
        int InfSampleSize,
        int NullRecordCount);

    public sealed record ValidationDiffAccount(
        string Acc,
        decimal TbAmt,
        decimal GlAmt,
        decimal Diff);

    public sealed record ValidationDetailsPageResult(
        IReadOnlyList<ValidationDetailRow> Rows,
        long? NextCursor);

    public sealed record ValidationDetailRow(
        long RowNo,
        string RunId,
        string BatchId,
        string? DocNum,
        string? LineId,
        string? AccNum,
        string Reason);
}
