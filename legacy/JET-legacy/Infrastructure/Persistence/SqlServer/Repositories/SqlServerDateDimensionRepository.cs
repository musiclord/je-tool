using JET.Domain.Abstractions.Repositories;
using JET.Infrastructure.Configuration;

namespace JET.Infrastructure.Persistence.SqlServer.Repositories
{
    /// <summary>
    /// Scaffold-only SQL Server implementation of <see cref="IDateDimensionRepository"/>.
    /// Mirrors <see cref="SqlServerProjectRepository"/>'s "available-but-unconfigured"
    /// stance so the dispatcher path stays uniform across providers; concrete DDL +
    /// persistence are reserved for the SQL Server provider round.
    /// </summary>
    public sealed class SqlServerDateDimensionRepository : IDateDimensionRepository
    {
        public SqlServerDateDimensionRepository(DatabaseOptions databaseOptions)
        {
            _ = databaseOptions;
        }

        public Task<string> ReplaceCalendarInputAsync(string projectId, string kind, IReadOnlyList<string> dates, CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
            ArgumentException.ThrowIfNullOrWhiteSpace(kind);
            ArgumentNullException.ThrowIfNull(dates);
            return Task.FromResult(Guid.NewGuid().ToString("D"));
        }
    }
}
