namespace JET.Domain.Abstractions.Repositories
{
    public interface IPreScreenRepository
    {
        Task<PreScreenRunResult> RunAsync(string projectId, CancellationToken cancellationToken);

        Task<PreScreenPageResult> QueryPageAsync(string projectId, string kind, long? cursor, int pageSize, CancellationToken cancellationToken);
    }

    public sealed record PreScreenRunResult(
        string RunId,
        int R1,
        int R2,
        int R3,
        int R4,
        int R4ZerosThreshold,
        IReadOnlyList<PreScreenCreatorSummary> R5Summary,
        int R6,
        int R7,
        int R8,
        int A2,
        int A3,
        int A4,
        int DescNullCount);

    public sealed record PreScreenCreatorSummary(string Creator, int Count);

    public sealed record PreScreenPageResult(IReadOnlyList<PreScreenDetailRow> Rows, long? NextCursor);

    public sealed record PreScreenDetailRow(long RowNo, string RunId, string BatchId, string? DocNum, string? LineId, string? AccNum, string Reason);
}
