using System.Text.RegularExpressions;
using JET.Domain;
using JET.Infrastructure;

namespace JET.Tests.Infrastructure;

/// <summary>
/// Filter predicate provider tests share one fixture shape: a fresh project database,
/// provider-neutral seed SQL, and the production preview repository for that provider.
/// SQL Server tests remain behind <see cref="SqlServerTheoryAttribute"/> at the caller.
/// </summary>
internal sealed class FilterPredicateProviderFixture : IAsyncDisposable
{
    private readonly TempProjectRoot? _root;
    private readonly TempSqlServerProject? _sqlServer;

    private FilterPredicateProviderFixture(
        string projectId,
        IFilterRunRepository repository,
        TempProjectRoot? root = null,
        TempSqlServerProject? sqlServer = null)
    {
        ProjectId = projectId;
        Repository = repository;
        _root = root;
        _sqlServer = sqlServer;
    }

    public string ProjectId { get; }

    public IFilterRunRepository Repository { get; }
    public IFilterVoucherRepository? Vouchers { get; private init; }

    public static async Task<FilterPredicateProviderFixture> CreateLocalAsync(
        string provider,
        string fixtureSql)
    {
        var root = new TempProjectRoot();
        try
        {
            var folder = new JetProjectFolder(root.Path);
            ILocalProjectDatabase database = provider switch
            {
                "sqlite" => new SqliteProjectDatabase(folder),
                "duckdb" => new DuckDbProjectDatabase(folder),
                _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unknown local provider.")
            };
            var projectId = Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(folder.GetProjectDirectory(projectId));
            await database.EnsureCreatedAsync(projectId, CancellationToken.None);

            await using (var connection = database.CreateConnection(projectId))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = fixtureSql;
                await command.ExecuteNonQueryAsync();
            }

            return new FilterPredicateProviderFixture(
                projectId,
                new LocalFilterRunRepository(database),
                root: root) { Vouchers = new LocalFilterVoucherRepository(database) };
        }
        catch
        {
            root.Dispose();
            throw;
        }
    }

    public static async Task<FilterPredicateProviderFixture> CreateSqlServerAsync(string fixtureSql)
    {
        var sqlServer = await TempSqlServerProject.TryCreateAsync()
            ?? throw new InvalidOperationException(
                "SqlServerTheory reported an available SQL Server, but no test project could be created.");
        try
        {
            // Fixture SQL is shared with SQLite/DuckDB. T-SQL needs Unicode literals for Chinese
            // fixture text and project-schema qualification for every target table.
            var transformed = Regex.Replace(
                    fixtureSql,
                    "'[^']*'",
                    match => "N" + match.Value)
                .Replace("INSERT INTO ", "INSERT INTO {s}.", StringComparison.Ordinal)
                .Replace("UPDATE target_gl_entry", "UPDATE {s}.target_gl_entry", StringComparison.Ordinal)
                // INSERT..SELECT 形式的 fixture（如 typed RDE 值以 source_row_number 對應 entry）
                // 也要 schema 限定子查詢中的 target_gl_entry。
                .Replace("FROM target_gl_entry", "FROM {s}.target_gl_entry", StringComparison.Ordinal);

            await using (var connection = sqlServer.Database.CreateConnection(sqlServer.ProjectId))
            {
                await connection.OpenAsync();
                await using var command = sqlServer.Database.CreateCommand(
                    connection,
                    sqlServer.ProjectId,
                    transformed);
                await command.ExecuteNonQueryAsync();
            }

            return new FilterPredicateProviderFixture(
                sqlServer.ProjectId,
                new SqlServerFilterRunRepository(sqlServer.Database),
                sqlServer: sqlServer);
        }
        catch
        {
            await sqlServer.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_sqlServer is not null)
        {
            await _sqlServer.DisposeAsync();
        }

        _root?.Dispose();
    }
}
