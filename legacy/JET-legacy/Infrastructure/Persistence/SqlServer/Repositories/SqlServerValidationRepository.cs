using JET.Domain.Abstractions.Repositories;
using JET.Infrastructure.Configuration;

namespace JET.Infrastructure.Persistence.SqlServer.Repositories
{
    public sealed class SqlServerValidationRepository : IValidationRepository
    {
        public SqlServerValidationRepository(DatabaseOptions databaseOptions)
        {
            _ = databaseOptions;
        }

        public Task<ValidationRunResult> RunAsync(string projectId, CancellationToken cancellationToken)
        {
            throw new NotImplementedException("SqlServer validation SQL pushdown is scheduled for a later round (plan.md §6). ");
        }

        public Task<ValidationDetailsPageResult> QueryDetailsPageAsync(string projectId, string kind, long? cursor, int pageSize, CancellationToken cancellationToken)
        {
            throw new NotImplementedException("SqlServer validation detail paging is scheduled for a later round (plan.md §6). ");
        }
    }
}
