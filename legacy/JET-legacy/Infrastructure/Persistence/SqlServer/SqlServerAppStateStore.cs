using JET.Domain.Abstractions;
using JET.Domain.Enums;
using JET.Infrastructure.Configuration;

namespace JET.Infrastructure.Persistence.SqlServer
{
    public sealed class SqlServerAppStateStore : IAppStateStore
    {
        private readonly string _connectionString;

        public SqlServerAppStateStore(DatabaseOptions databaseOptions)
        {
            _connectionString = databaseOptions.SqlServerConnectionString;
        }

        public Task<DatabaseStatus> GetStatusAsync(CancellationToken cancellationToken)
        {
            var status = new DatabaseStatus(
                DatabaseProvider.SqlServer,
                false,
                string.IsNullOrWhiteSpace(_connectionString) ? "(not configured)" : _connectionString,
                "Scaffold only - SQL Server provider is reserved but not implemented yet.");

            return Task.FromResult(status);
        }
    }
}
