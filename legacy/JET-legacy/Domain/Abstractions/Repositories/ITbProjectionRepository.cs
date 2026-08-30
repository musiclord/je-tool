namespace JET.Domain.Abstractions.Repositories
{
    public interface ITbProjectionRepository
    {
        Task<ProjectionResult> ProjectLatestBatchAsync(
            string projectId,
            IReadOnlyDictionary<string, string> mapping,
            CancellationToken cancellationToken);
    }
}
