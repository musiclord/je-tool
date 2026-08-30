using Dapper;
using Microsoft.Data.Sqlite;
using JET.Domain.Abstractions;
using JET.Domain.Enums;
using JET.Infrastructure.Configuration;

namespace JET.Infrastructure.Persistence.Sqlite
{
    public sealed class SqliteAppStateStore : IAppStateStore
    {
        private readonly string _connectionString;

        public SqliteAppStateStore(DatabaseOptions databaseOptions)
        {
            _connectionString = databaseOptions.SqliteConnectionString;
        }

        public async Task<DatabaseStatus> GetStatusAsync(CancellationToken cancellationToken)
        {
            var builder = new SqliteConnectionStringBuilder(_connectionString);
            var dataSource = builder.DataSource;

            if (!string.IsNullOrWhiteSpace(dataSource))
            {
                var directory = Path.GetDirectoryName(dataSource);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }
            }

            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            const string createTableSql = """
CREATE TABLE IF NOT EXISTS AppState
(
    Key TEXT NOT NULL PRIMARY KEY,
    Value TEXT NOT NULL,
    UpdatedUtc TEXT NOT NULL
);
""";

            const string upsertSql = """
INSERT INTO AppState(Key, Value, UpdatedUtc)
VALUES(@Key, @Value, @UpdatedUtc)
ON CONFLICT(Key)
DO UPDATE SET
    Value = excluded.Value,
    UpdatedUtc = excluded.UpdatedUtc;
""";

            await connection.ExecuteAsync(new CommandDefinition(createTableSql, cancellationToken: cancellationToken));

            var now = DateTimeOffset.UtcNow;
            await connection.ExecuteAsync(new CommandDefinition(
                upsertSql,
                new
                {
                    Key = "last_bootstrap_utc",
                    Value = now.ToString("O"),
                    UpdatedUtc = now.ToString("O")
                },
                cancellationToken: cancellationToken));

            return new DatabaseStatus(
                DatabaseProvider.Sqlite,
                true,
                dataSource,
                "Active provider");
        }
    }
}
