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
                root: root) { Vouchers = new LocalFilterVoucherRepository(database) };
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
