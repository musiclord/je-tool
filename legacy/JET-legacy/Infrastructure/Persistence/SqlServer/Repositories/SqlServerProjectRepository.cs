using JET.Domain.Abstractions.Repositories;
using JET.Domain.Entities;
using JET.Infrastructure.Configuration;

namespace JET.Infrastructure.Persistence.SqlServer.Repositories
{
    /// <summary>
    /// Scaffold-only SQL Server implementation of <see cref="IProjectRepository"/>.
    /// Mirrors <c>SqlServerSchemaInitializer</c>'s "available-but-unconfigured" stance:
    /// the dispatcher path stays uniform (no provider branching at call site), but no
    /// rows are persisted until the SQL Server provider is wired in a later round.
    /// Returns a generated GUID so the calling handler can complete without diverging.
    /// </summary>
    public sealed class SqlServerProjectRepository : IProjectRepository
    {
        public SqlServerProjectRepository(DatabaseOptions databaseOptions)
        {
            _ = databaseOptions;
        }

        public Task<string> CreateAsync(ProjectInfo project, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(project);
            return Task.FromResult(Guid.NewGuid().ToString("D"));
        }

        public Task<ProjectStateSnapshot?> LoadAsync(string projectId, CancellationToken cancellationToken)
        {
            return Task.FromResult<ProjectStateSnapshot?>(null);
        }
    }
}
