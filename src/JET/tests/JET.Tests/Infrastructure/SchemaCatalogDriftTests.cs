using JET.AuditCore;
using JET.Domain;
using JET.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 漂移守門:中央三層表名登錄(<see cref="JetSchemaCatalog"/>)必須與真 SQLite／DuckDB schema 完全對齊。
///
/// 機制:以 <see cref="SqliteProjectDatabase.EnsureCreatedAsync"/> 在 temp 目錄建一個全新專案 DB
/// (跑權威 SchemaSql),再查 <c>sqlite_master</c> 取得全部使用者表(排除 <c>sqlite_%</c> 內部表,
/// 與 dev 檢視 inspector 同一過濾),雙向比對:
///   (a) 每張實體表都已登錄 → 日後加表卻忘了登錄(漏登錄)即紅(負向保護)。
///   (b) 登錄表沒有幽靈條目 → 登錄了一張其實不存在的表也紅。
///
/// 這是 metadata 加法模型(不實體改名)的安全網:目錄是事實來源,本測試確保它不與真實 schema 漂移。
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

            // 全新庫直接建到目前 schema 版本(SchemaSql 即實體表的權威清單)。
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

    /// <summary>每一張實體表都必須已登錄(漏登錄 → 紅)。</summary>
    [Fact]
    public async Task EveryPhysicalTable_IsRegisteredInCatalog()
    {
        using var db = new FreshProjectDb();
        var physicalTables = await db.ReadUserTablesAsync();

        // sanity:確有建出表(防「查到空集合假綠」)。
        Assert.NotEmpty(physicalTables);

        var registered = JetSchemaCatalog.All
            .Select(e => e.PhysicalName)
            .ToHashSet(StringComparer.Ordinal);

        var missing = physicalTables.Except(registered).ToList();
        Assert.True(
            missing.Count == 0,
            $"以下實體表存在於 SQLite schema 卻未登錄於 JetSchemaCatalog:{string.Join(", ", missing)}");
    }

    /// <summary>登錄表不得有幽靈條目:每筆登錄都對應一張真實存在的表(登錄了不存在的表 → 紅)。</summary>
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

    [Fact]
    public async Task Catalog_PhysicalNames_EqualDuckDbMainSchemaTables()
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        var projectId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(projectId));
        var database = new DuckDbProjectDatabase(folder);
        await database.EnsureCreatedAsync(projectId, CancellationToken.None);

        var physicalTables = new HashSet<string>(StringComparer.Ordinal);
        await using (var connection = database.CreateConnection(projectId))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT table_name FROM information_schema.tables "
                + "WHERE table_schema='main' ORDER BY table_name;";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                physicalTables.Add(reader.GetString(0));
            }
        }

        Assert.NotEmpty(physicalTables);
        var registered = JetSchemaCatalog.All
            .Select(entry => entry.PhysicalName)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Equal(physicalTables, registered);
    }
}
