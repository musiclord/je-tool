using JET.AuditCore;
using JET.Domain;
using JET.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// <see cref="JetSchemaCatalog"/> 登錄的資料表必須和 SQLite 實際建出的資料表完全一致。
///
/// 機制:以 <see cref="SqliteProjectDatabase.EnsureCreatedAsync(string, CancellationToken)"/> 在 temp 目錄建一個全新案件 DB
/// (跑正式的 SchemaSql),再查 <c>sqlite_master</c> 取得全部使用者表(排除 <c>sqlite_%</c> 內部表,
/// 與 dev 檢視 inspector 同一過濾),雙向比對:
///   (a) 每張實體表都已登錄 → 日後加表卻忘了登錄(漏登錄)時測試會失敗(負向保護)。
///   (b) 登錄表沒有幽靈條目 → 登錄了一張其實不存在的表也會失敗。
///
/// 資料表的對外名稱寫在目錄裡，實體表名不改；這個測試確保目錄登錄的內容和實際建出的資料表一致。
/// 注意 <c>sqlite_sequence</c> 由 AUTOINCREMENT 自動建立,屬 SQLite 內部簿記表,以 <c>sqlite_%</c>
/// 過濾排除——故不需(也不應)登錄。
/// </summary>
public sealed class SchemaCatalogDriftTests
{
    private sealed class FreshProjectDb : IDisposable
    {
        private readonly TempProjectRoot _root = new();

        public FreshProjectDb()
        {
            var folder = new JetProjectFolder(_root.Path);
            Database = new SqliteProjectDatabase(folder);
            ProjectId = Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(folder.GetProjectDirectory(ProjectId));

            // 全新庫直接建到目前 schema 版本(實體表以 SchemaSql 的清單為準)。
            Database.EnsureCreatedAsync(ProjectId, CancellationToken.None).GetAwaiter().GetResult();
        }

        public SqliteProjectDatabase Database { get; }
        public string ProjectId { get; }

        /// <summary>查 sqlite_master 取全部使用者表(排除 SQLite 內部 sqlite_% 表)。</summary>
        public async Task<HashSet<string>> ReadUserTablesAsync()
        {
            var tables = new HashSet<string>(StringComparer.Ordinal);

            await using var connection = Database.CreateConnection(ProjectId);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT name FROM sqlite_master
                WHERE type = 'table' AND name NOT LIKE 'sqlite_%'
                ORDER BY name;
                """;

            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                tables.Add(reader.GetString(0));
            }

            return tables;
        }

        public void Dispose() => _root.Dispose();
    }

    /// <summary>每一張實體表都必須已登錄，漏登錄時測試失敗。</summary>
    [Fact]
    public async Task EveryPhysicalTable_IsRegisteredInCatalog()
    {
        using var db = new FreshProjectDb();
        var physicalTables = await db.ReadUserTablesAsync();

        // 先確認真的建出了資料表，避免查到空集合時誤判通過。
        Assert.NotEmpty(physicalTables);

        var registered = JetSchemaCatalog.All
            .Select(e => e.PhysicalName)
            .ToHashSet(StringComparer.Ordinal);

        var missing = physicalTables.Except(registered).ToList();
        Assert.True(
            missing.Count == 0,
            $"以下實體表存在於 SQLite schema 卻未登錄於 JetSchemaCatalog:{string.Join(", ", missing)}");
    }

    /// <summary>每筆登錄都要對應一張真實存在的表，登錄了不存在的表時測試失敗。</summary>
    [Fact]
    public async Task EveryCatalogEntry_MapsToARealTable()
    {
        using var db = new FreshProjectDb();
        var physicalTables = await db.ReadUserTablesAsync();

        var registered = JetSchemaCatalog.All
            .Select(e => e.PhysicalName)
            .ToHashSet(StringComparer.Ordinal);

        var phantom = registered.Except(physicalTables).ToList();
        Assert.True(
            phantom.Count == 0,
            $"以下表登錄於 JetSchemaCatalog 卻不存在於 SQLite schema:{string.Join(", ", phantom)}");
    }

    /// <summary>雙向相等:登錄集合 ≡ 實體表集合(漏登錄與幽靈條目都被同一斷言鎖死)。</summary>
    [Fact]
    public async Task Catalog_PhysicalNames_EqualSqliteUserTables()
    {
        using var db = new FreshProjectDb();
        var physicalTables = await db.ReadUserTablesAsync();

        var registered = JetSchemaCatalog.All
            .Select(e => e.PhysicalName)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(physicalTables, registered);
    }
}
