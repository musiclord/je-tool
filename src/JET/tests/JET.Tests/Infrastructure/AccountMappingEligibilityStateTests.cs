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

internal sealed class AccountMappingStateFixture : IAsyncDisposable
{
    private readonly TempProjectRoot? _root;

    private AccountMappingStateFixture(
        string projectId, IAccountMappingStore store,
        TempProjectRoot? root = null)
    {
        ProjectId = projectId;
        Store = store;
        _root = root;
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
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unknown provider.")
        };
        var projectId = Guid.NewGuid().ToString("N");
        await database.EnsureCreatedAsync(projectId, CancellationToken.None);
        await SeedAsync(database.CreateConnection(projectId), string.Empty, imported, categories);
        return new AccountMappingStateFixture(projectId, new LocalAccountMappingRepository(database), root: root);
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
            // 科目配對一律以 category_id 連到分類表（2026-10-02 起刪除依分類名稱對應的退路），
            // 測試資料依分類名稱補上對應的內建分類身分。
            var targets = categoryList.Select((category, index) =>
                $"('batch', {index + 1}, 'A{index + 1:000}', 'Account {index + 1}', '{category}', " +
                $"'{AccountCategoryBuiltIns.All.Single(item => item.Label == category).CategoryId}')");
            var targetSql = categoryList.Length == 0
                ? string.Empty
                : $"INSERT INTO {prefix}account_mapping " +
                  "(batch_id, source_row_number, account_number, account_name, standardized_category, category_id) VALUES " +
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

    public ValueTask DisposeAsync()
    {
        _root?.Dispose();
        return ValueTask.CompletedTask;
    }
}
