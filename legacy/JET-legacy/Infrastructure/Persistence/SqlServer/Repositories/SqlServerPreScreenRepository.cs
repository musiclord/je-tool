using JET.Domain.Abstractions.Repositories;
using JET.Infrastructure.Configuration;

namespace JET.Infrastructure.Persistence.SqlServer.Repositories
{
    public sealed class SqlServerPreScreenRepository : IPreScreenRepository
    {
        public SqlServerPreScreenRepository(DatabaseOptions databaseOptions)
        {
            _ = databaseOptions;
        }

        public Task<PreScreenRunResult> RunAsync(string projectId, CancellationToken cancellationToken)
        {
            throw new NotImplementedException("SqlServer prescreen SQL pushdown is scheduled for a later round (plan.md §6). ");
        }

        public Task<PreScreenPageResult> QueryPageAsync(string projectId, string kind, long? cursor, int pageSize, CancellationToken cancellationToken)
        {
            throw new NotImplementedException("SqlServer prescreen paging is scheduled for a later round (plan.md §6). ");
        }
    }
}
