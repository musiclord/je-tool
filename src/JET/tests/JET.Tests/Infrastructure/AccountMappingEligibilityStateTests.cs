using System.Data.Common;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

public sealed class LocalAccountMappingEligibilityStateTests
{
    [Theory]
    [InlineData("sqlite", false, "", false, false, false)]
    [InlineData("sqlite", true, "", false, false, false)]
    [InlineData("sqlite", true, "Others", true, false, false)]
    [InlineData("sqlite", true, "Revenue", true, true, false)]
    [InlineData("sqlite", true, "Cash", true, false, true)]
    [InlineData("sqlite", true, "Revenue,Cash", true, true, true)]
    [InlineData("duckdb", false, "", false, false, false)]
    [InlineData("duckdb", true, "", false, false, false)]
    [InlineData("duckdb", true, "Others", true, false, false)]
    [InlineData("duckdb", true, "Revenue", true, true, false)]
    [InlineData("duckdb", true, "Cash", true, false, true)]
    [InlineData("duckdb", true, "Revenue,Cash", true, true, true)]
    public async Task FindState_ComputesEligibilityFromTargetContent(
        string provider, bool imported, string categories,
        bool hasAny, bool hasRevenue, bool hasCounterpart)
    {
        await using var fixture = await AccountMappingStateFixture.CreateLocalAsync(provider, imported, categories);

        await AssertStateAsync(fixture.Store, fixture.ProjectId, imported, hasAny, hasRevenue, hasCounterpart);
    }

    internal static async Task AssertStateAsync(
        IAccountMappingStore store, string projectId, bool imported,
        bool hasAny, bool hasRevenue, bool hasCounterpart)
    {
        var state = await store.FindStateAsync(projectId, CancellationToken.None);
        if (!imported)
        {
            Assert.Null(state);
            return;
        }

        Assert.NotNull(state);
        Assert.Equal(hasAny, state.HasAnyCategory);
        Assert.Equal(hasRevenue, state.HasRevenue);
        Assert.Equal(hasCounterpart, state.HasCounterpart);
    }
}

public sealed class SqlServerAccountMappingEligibilityStateTests
{
    [SqlServerTheory]
    [InlineData(false, "", false, false, false)]
    [InlineData(true, "", false, false, false)]
    [InlineData(true, "Others", true, false, false)]
    [InlineData(true, "Revenue", true, true, false)]
    [InlineData(true, "Cash", true, false, true)]
    [InlineData(true, "Revenue,Cash", true, true, true)]
    public async Task FindState_ComputesEligibilityFromTargetContent(
        bool imported, string categories, bool hasAny, bool hasRevenue, bool hasCounterpart)
    {
        await using var fixture = await AccountMappingStateFixture.CreateSqlServerAsync(imported, categories);

        await LocalAccountMappingEligibilityStateTests.AssertStateAsync(
            fixture.Store, fixture.ProjectId, imported, hasAny, hasRevenue, hasCounterpart);
    }
}

internal sealed class AccountMappingStateFixture : IAsyncDisposable
{
    private readonly TempProjectRoot? _root;
    private readonly TempSqlServerProject? _sql;

    private AccountMappingStateFixture(
        string projectId, IAccountMappingStore store,
        TempProjectRoot? root = null, TempSqlServerProject? sql = null)
    {
        ProjectId = projectId;
        Store = store;
        _root = root;
        _sql = sql;
    }

    public string ProjectId { get; }
    public IAccountMappingStore Store { get; }

    public static async Task<AccountMappingStateFixture> CreateLocalAsync(
        string provider, bool imported, string categories)
    {
        var root = new TempProjectRoot();
        var database = provider switch
        {
            "sqlite" => (ILocalProjectDatabase)new SqliteProjectDatabase(new JetProjectFolder(root.Path)),
            "duckdb" => new DuckDbProjectDatabase(new JetProjectFolder(root.Path)),
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unknown provider.")
        };
        var projectId = Guid.NewGuid().ToString("N");
        await database.EnsureCreatedAsync(projectId, CancellationToken.None);
        await SeedAsync(database.CreateConnection(projectId), string.Empty, imported, categories);
        return new AccountMappingStateFixture(projectId, new LocalAccountMappingRepository(database), root: root);
    }

    public static async Task<AccountMappingStateFixture> CreateSqlServerAsync(bool imported, string categories)
    {
        var sql = Assert.IsType<TempSqlServerProject>(await TempSqlServerProject.TryCreateAsync());
        await SeedAsync(sql.Database.CreateConnection(sql.ProjectId),
            SqlServerProjectSchema.QualifierFor(sql.ProjectId), imported, categories);
        return new AccountMappingStateFixture(
            sql.ProjectId, new SqlServerAccountMappingRepository(sql.Database), sql: sql);
    }

    private static async Task SeedAsync(
        DbConnection connection, string prefix, bool imported, string categories)
    {
        await using (connection)
        {
            await connection.OpenAsync();
            if (!imported)
            {
                return;
            }

            var categoryList = categories.Split(',', StringSplitOptions.RemoveEmptyEntries);
            var targets = categoryList.Select((category, index) =>
                $"('batch', {index + 1}, 'A{index + 1:000}', 'Account {index + 1}', '{category}')");
            var targetSql = categoryList.Length == 0
                ? string.Empty
                : $"INSERT INTO {prefix}target_account_mapping " +
                  "(batch_id, source_row_number, account_code, account_name, standardized_category) VALUES " +
                  string.Join(",", targets) + ";";

            await using var command = connection.CreateCommand();
            command.CommandText =
                $"INSERT INTO {prefix}import_batch " +
                "(batch_id, dataset_kind, source_file_path, source_file_name, imported_utc, row_count, columns_json) " +
                $"VALUES ('batch', 'account_mapping', 'fixture.csv', 'fixture.csv', '2026-07-13T00:00:00+00:00', {Math.Max(1, categoryList.Length)}, '[]');" +
                targetSql;
            await command.ExecuteNonQueryAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_sql is not null)
        {
            await _sql.DisposeAsync();
        }
        _root?.Dispose();
    }
}
