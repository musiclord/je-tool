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
    [InlineData("duckdb")]
    public async Task FreshSchema_IsV7AndContainsMinimumInventory(string provider)
    {
        using var env = new LocalEnv(provider);

        await env.Database.EnsureCreatedAsync(env.ProjectId, CancellationToken.None);

        // 裁定 2026-10-13-15 把訊息分三級，訊息紀錄的等級限制在第 12 版加上 error。第一次失敗收據 20261013-095718626-0edd56e525f6453997c4d1952148ce33。
        // 裁定 2026-10-21-04 把日期不符分成三種原因，本機 schema 再升到第 13 版。第一次失敗收據 20261021-080930829-9ee6cdc9021744a0bf40903ea77f833a。
        // 裁定 2026-10-22-15 讓每個來源可指定欄名在第幾列，import_batch_source 加 header_row 欄，本機 schema 升到第 14 版。第一次失敗收據 20261023-194649480-a2d7d7e4982c4c169b10b9a8a3f12d5b。
        // 裁定 2026-10-30-23：升版程式拿掉，新資料庫直接建成現行結構，版本推進到第 15 版。第一次失敗收據 20261030-065928748-be3c29975faa4b92b43ff12519708767。
        // 統一改名改了表名、欄名與 CHECK 值，版本推進到第 16 版，改名前建立的案件才會看到舊版案件的訊息。
        // 第一次失敗收據 20261030-135241888-3c45f8ad7a044419beee5d439bf6db8a。
        // 條件組結果改成依條件組編號儲存，filter_scenario 加 scenario_id、新增 filter_hit_condition，版本推進到第 17 版。
        // 第一次失敗收據 20261031-015438711-e019d04ff2b64ad08bc34c916334bd83。
        // 驗證摘要裡同一傳票號碼出現在不同總帳日期的鍵名改用 voucher（裁定 2026-11-01-01），版本推進到第 18 版。第一次失敗收據 20261101-062343383-804cee29d9d9428f9e5f502ea4e7ae94。
        // 資料缺漏輔助檢視的驗證摘要鍵名改用 blank 字根，版本推進到第 19 版。第一次失敗：Public 收據 20261103-030546323-460502e13dea45028e06acd80f8ce95b。
        // 欄位資訊多記每一欄的來歷與對應的 JET 欄位（裁定 2026-11-03-11），field_info 加 field_origin 與 jet_field，版本推進到第 20 版。第一次失敗：Focused 收據 20261103-070053462-34d240fe519d4190a85d1a4368c1a80a。
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

    [Fact]
    public void SqlServerColdCopy_IsNotAProjectDatabaseContract()
    {
        Assert.Equal(ProjectDocument.SqlServerDatabaseProvider, "sqlServer");
        Assert.NotEqual(DuckDbProjectDatabase.DatabaseFileName, ProjectDocument.SqlServerDatabaseProvider);
    }

    private sealed class LocalEnv : IDisposable
    {
        private readonly TempProjectRoot _root = new();
        private readonly string _provider;

        public LocalEnv(string provider)
        {
            _provider = provider;
            Folder = new JetProjectFolder(_root.Path);
            ProjectId = Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(Folder.GetProjectDirectory(ProjectId));
            Database = _provider == "duckdb"
                ? new DuckDbProjectDatabase(Folder)
                : new SqliteProjectDatabase(Folder);
        }

        public JetProjectFolder Folder { get; }
        public string ProjectId { get; }
        public ILocalProjectDatabase Database { get; }

        public Task<long> TableCountAsync(string table) => _provider == "sqlite"
            ? ScalarAsync($"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='{table}';")
            : ScalarAsync($"SELECT COUNT(*) FROM information_schema.tables WHERE table_name='{table}';");

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

        public Task<long> IndexCountAsync(string table) => _provider == "sqlite"
            ? ScalarAsync($"SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND tbl_name='{table}' AND name LIKE 'ix_%';")
            : ScalarAsync($"SELECT COUNT(*) FROM duckdb_indexes() WHERE table_name='{table}' AND index_name LIKE 'ix_%';");

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
