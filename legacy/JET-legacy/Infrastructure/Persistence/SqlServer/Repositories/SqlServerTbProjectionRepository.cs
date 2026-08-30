using JET.Domain.Abstractions.Repositories;
using JET.Infrastructure.Configuration;

namespace JET.Infrastructure.Persistence.SqlServer.Repositories
{
    public sealed class SqlServerTbProjectionRepository : ITbProjectionRepository
    {
        public SqlServerTbProjectionRepository(DatabaseOptions databaseOptions)
        {
            _ = databaseOptions;
        }

        public Task<ProjectionResult> ProjectLatestBatchAsync(
            string projectId,
            IReadOnlyDictionary<string, string> mapping,
            CancellationToken cancellationToken)
        {
            throw new NotImplementedException("SqlServer TB projection is scheduled for a later round (plan.md §6). ");
        }
    }
}
