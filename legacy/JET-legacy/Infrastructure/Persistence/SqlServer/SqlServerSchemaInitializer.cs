using JET.Domain.Abstractions.Persistence;
using JET.Infrastructure.Configuration;

namespace JET.Infrastructure.Persistence.SqlServer
{
    /// <summary>
    /// Scaffold-only SQL Server schema initializer. Reports the provider as unavailable
    /// when no connection string is configured, mirroring <c>SqlServerAppStateStore</c>.
    /// Concrete DDL is reserved for the round that wires <c>SqlBulkCopy</c> persistence.
    /// </summary>
    public sealed class SqlServerSchemaInitializer : ISchemaInitializer
    {
        private readonly string _connectionString;

        public SqlServerSchemaInitializer(DatabaseOptions databaseOptions)
        {
            _connectionString = databaseOptions.SqlServerConnectionString;
        }

        public Task<SchemaInitializationResult> EnsureAsync(CancellationToken cancellationToken)
        {
            var available = !string.IsNullOrWhiteSpace(_connectionString);
            var mode = available
                ? "SQL Server provider scaffold (DDL reserved)."
                : "SQL Server provider not configured.";
            return Task.FromResult(new SchemaInitializationResult(false, mode));
        }
    }
}
