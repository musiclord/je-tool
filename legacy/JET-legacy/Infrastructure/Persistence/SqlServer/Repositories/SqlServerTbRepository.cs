using JET.Domain.Abstractions.Repositories;
using JET.Domain.Entities;
using JET.Infrastructure.Configuration;

namespace JET.Infrastructure.Persistence.SqlServer.Repositories
{
    /// <summary>
    /// SQL Server scaffold for <see cref="ITbRepository"/>. Concrete implementation
    /// (likely <c>SqlBulkCopy</c> backed) is deferred to a later round per
    /// plan.md §6 candidates list.
    /// </summary>
    public sealed class SqlServerTbRepository : ITbRepository
    {
        public SqlServerTbRepository(DatabaseOptions databaseOptions)
        {
            _ = databaseOptions;
        }

        public Task<BulkImportResult> BulkInsertStagingAsync(
            string projectId,
            string fileName,
            IAsyncEnumerable<GlRawRow> rows,
            string mode,
            CancellationToken cancellationToken)
        {
            throw new NotImplementedException("SqlServer TB ingest is scheduled for a later round (plan.md §6).");
        }
    }
}
