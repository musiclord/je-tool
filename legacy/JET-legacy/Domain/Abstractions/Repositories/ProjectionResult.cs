namespace JET.Domain.Abstractions.Repositories
{
    public sealed record ProjectionResult(
        string? BatchId,
        int ProjectedRowCount);
}
