using System.Text.RegularExpressions;
using JET.Domain;
using JET.Infrastructure;

namespace JET.Tests.Infrastructure;

/// <summary>
/// Filter predicate provider tests share one fixture shape: a fresh project database,
/// provider-neutral seed SQL, and the production preview repository for that provider.
/// </summary>
internal sealed class FilterPredicateProviderFixture : IAsyncDisposable
{
    private readonly TempProjectRoot? _root;

    private FilterPredicateProviderFixture(
        string projectId,
        IFilterHitRepository repository,
        TempProjectRoot? root = null)
    {
        ProjectId = projectId;
        Repository = repository;
        _root = root;
    }

    public string ProjectId { get; }

    public IFilterHitRepository Repository { get; }
    public IFilterVoucherRepository? Vouchers { get; private init; }
    private ILocalProjectDatabase? Database { get; init; }

    /// <summary>直接對這個合成案件資料庫執行查詢，回傳第一欄的文字；連線和產品用的是同一種。</summary>
    public async Task<string[]> QueryTextsAsync(string sql)
    {
        await using var connection = Database!.CreateConnection(ProjectId);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var values = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) values.Add(reader.GetString(0));
        return values.ToArray();
    }

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
                new LocalFilterHitRepository(database),
                root: root) { Vouchers = new LocalFilterVoucherRepository(database), Database = database };
        }
        catch
        {
            root.Dispose();
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        _root?.Dispose();
        return ValueTask.CompletedTask;
    }
}
