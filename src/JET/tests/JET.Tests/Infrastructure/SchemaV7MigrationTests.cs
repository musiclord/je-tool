using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 新建的本機案件資料庫要有現行結構的內容：版本、科目分類與結果失效狀態的初始資料，以及攸關資料元素的索引。
/// </summary>
public sealed class SchemaV7MigrationTests
{
    [Theory]
    [InlineData("sqlite")]
    public async Task FreshSchema_IsV7AndContainsMinimumInventory(string provider)
    {
        using var env = new LocalEnv(provider);

        await env.Database.EnsureCreatedAsync(env.ProjectId, CancellationToken.None);

        // 新資料庫直接建成現行結構，現行版本是第 20 版。
        Assert.Equal("20", await env.TextAsync(
            "SELECT value FROM schema_info WHERE key = 'schema_version';"));
        Assert.Equal(4, await env.V7TableCountAsync());
        Assert.Equal(
            AccountCategoryBuiltIns.All.ToArray(),
            (await env.CategoryAsync()).ToArray());
        Assert.Equal(1, await env.ScalarAsync("SELECT MAX(revision) FROM account_category;"));
        Assert.Equal(1, await env.ScalarAsync(
            "SELECT COUNT(*) FROM result_stale_state "
            + "WHERE singleton=1 AND validation_stale=0 AND prescreen_stale=0 AND filter_stale=0;"));
        Assert.Equal(3, await env.IndexCountAsync("gl_rde_value"));
    }

    private sealed class LocalEnv : IDisposable
    {
        private readonly TempProjectRoot _root = new();

        public LocalEnv(string provider)
        {
            ArgumentOutOfRangeException.ThrowIfNotEqual(provider, "sqlite");
            Folder = new JetProjectFolder(_root.Path);
            ProjectId = Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(Folder.GetProjectDirectory(ProjectId));
            Database = new SqliteProjectDatabase(Folder);
        }

        public JetProjectFolder Folder { get; }
        public string ProjectId { get; }
        public ILocalProjectDatabase Database { get; }

        public Task<long> TableCountAsync(string table) =>
            ScalarAsync($"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='{table}';");

        public async Task<long> V7TableCountAsync()
        {
            long count = 0;
            foreach (var table in new[]
                     {
                         "gl_rde_field",
                         "gl_rde_value",
                         "account_category",
                         "result_stale_state"
                     })
            {
                count += await TableCountAsync(table);
            }

            return count;
        }

        public Task<long> IndexCountAsync(string table) =>
            ScalarAsync($"SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND tbl_name='{table}' AND name LIKE 'ix_%';");

        public async Task<long> ScalarAsync(string sql)
        {
            await using var connection = Database.CreateConnection(ProjectId);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt64(await command.ExecuteScalarAsync());
        }

        public async Task<string> TextAsync(string sql)
        {
            await using var connection = Database.CreateConnection(ProjectId);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            return (string)(await command.ExecuteScalarAsync())!;
        }

        public async Task<IReadOnlyList<AccountCategory>> CategoryAsync()
        {
            await using var connection = Database.CreateConnection(ProjectId);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT category_id, label, ordinal, semantic_role, is_builtin "
                + "FROM account_category ORDER BY ordinal, category_id;";
            await using var reader = await command.ExecuteReaderAsync();
            var categories = new List<AccountCategory>();
            while (await reader.ReadAsync())
            {
                categories.Add(new AccountCategory(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetInt32(2),
                    reader.GetString(3),
                    Convert.ToBoolean(reader.GetValue(4))));
            }

            return categories;
        }

        public void Dispose() => _root.Dispose();
    }
}
