namespace JET.Domain.Abstractions.Repositories
{
    public interface IGlProjectionRepository
    {
        Task<ProjectionResult> ProjectLatestBatchAsync(
            string projectId,
            IReadOnlyDictionary<string, string> mapping,
            CancellationToken cancellationToken);
    }
}
