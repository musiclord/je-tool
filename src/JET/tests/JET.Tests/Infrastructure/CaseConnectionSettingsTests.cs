using System.Data.Common;
using JET.Domain;
using JET.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 匯入與確認 GL 配對時投影用的連線設定，只留在那一條連線上。
/// 案件連線進連線池，SQLite 的連線設定跟著連線走，放回連線池時不會重設；
/// 設定要是留給下一個拿到同一條連線的動作，那個動作的記憶體用量與寫入磁碟的時機，就取決於剛好拿到哪一條連線。
/// 連線池先給最近放回的那條連線，所以每個測試都在動作結束後立刻開一條案件連線，讀回設定，
/// 和一般案件連線應有的設定比對：SQLite 給新連線的設定，只有快取換成一般案件連線每次開啟時設的 256 MiB。
/// </summary>
public sealed class CaseConnectionSettingsTests
{
    private static readonly IReadOnlyList<string> Columns = ["doc", "date", "acc", "debit", "credit"];

    [Fact]
    public async Task ReplaceImport_LeavesNoImportSettingsOnTheNextConnection()
    {
        using var root = new TempProjectRoot();
        var (database, projectId) = await CreateProjectAsync(root);
        var import = new LocalImportRepository(database);

        await ReplaceGlAsync(import, projectId);

        Assert.Equal(await ReadExpectedCaseConnectionSettingsAsync(database, projectId), await ReadNextConnectionSettingsAsync(database, projectId));
    }

    [Fact]
    public async Task AppendImport_LeavesNoImportSettingsOnTheNextConnection()
    {
        using var root = new TempProjectRoot();
        var (database, projectId) = await CreateProjectAsync(root);
        var import = new LocalImportRepository(database);
        await ReplaceGlAsync(import, projectId);

        await import.AppendToBatchAsync(
            projectId,
            DatasetKind.Gl,
            [Source("append.csv", Rows(("D2", "200", "0"), ("D2", "0", "200")))],
            CancellationToken.None);

        Assert.Equal(await ReadExpectedCaseConnectionSettingsAsync(database, projectId), await ReadNextConnectionSettingsAsync(database, projectId));
    }

    [Fact]
    public async Task GlProjection_LeavesNoSettingsOnTheNextConnection()
    {
        using var root = new TempProjectRoot();
        var (database, projectId) = await CreateProjectAsync(root);
        var batchId = await ReplaceGlAsync(new LocalImportRepository(database), projectId);

        var result = await new LocalGlRepository(database).ProjectStagingToTargetAsync(
            projectId,
            batchId,
            Spec(),
            10_000,
            DateParseOptions.Default,
            periodStart: DateOnly.MinValue,
            periodEnd: DateOnly.MaxValue,
            postingStatusMapped: false,
            postingStatusPolicy: null,
            committedUtc: DateTimeOffset.UnixEpoch,
            CancellationToken.None);

        Assert.Equal(2, result.ProjectedRowCount);
        Assert.Equal(await ReadExpectedCaseConnectionSettingsAsync(database, projectId), await ReadNextConnectionSettingsAsync(database, projectId));
    }

    [Fact]
    public async Task ImportConnection_KeepsTheImportSettings()
    {
        using var root = new TempProjectRoot();
        var (database, projectId) = await CreateProjectAsync(root);

        await using var connection = await database.OpenImportConnectionAsync(projectId, CancellationToken.None);

        // synchronous=NORMAL 是 1，temp_store=MEMORY 是 2，快取 64 MiB 寫成 -65536（單位是 KiB，負數表示用大小計）。
        Assert.Equal(new ConnectionSettings(Synchronous: 1, TempStore: 2, CacheSize: -65536), await ReadSettingsAsync(connection));
    }

    [Fact]
    public async Task GlProjectionConnection_WritesTempFilesWithSixteenMebibyteCache()
    {
        using var root = new TempProjectRoot();
        var (database, projectId) = await CreateProjectAsync(root);
        var fresh = await ReadFreshSettingsAsync(database, projectId);

        await using var connection = await database.OpenGlProjectionConnectionAsync(projectId, CancellationToken.None);

        // temp_store=FILE 是 1；快取 16 MiB 寫成 -16384。synchronous 不改，和從沒用過的連線相同。
        Assert.Equal(fresh with { TempStore = 1, CacheSize = -16384 }, await ReadSettingsAsync(connection));
    }

    [Fact]
    public async Task CaseConnection_OpensWithTwoHundredFiftySixMebibyteCache()
    {
        using var root = new TempProjectRoot();
        var (database, projectId) = await CreateProjectAsync(root);

        Assert.Equal(await ReadExpectedCaseConnectionSettingsAsync(database, projectId), await ReadNextConnectionSettingsAsync(database, projectId));
    }

    [Fact]
    public async Task CaseConnection_ResetsTheCacheEachTimeItOpens()
    {
        using var root = new TempProjectRoot();
        var (database, projectId) = await CreateProjectAsync(root);
        await using (var connection = database.CreateConnection(projectId))
        {
            await connection.OpenAsync();
            await using var shrink = connection.CreateCommand();
            shrink.CommandText = "PRAGMA cache_size=-2000;";
            await shrink.ExecuteNonQueryAsync();
        }

        // 連線池先給剛放回、快取被改成約 2 MiB 的那一條；開啟時要重新設回 256 MiB。
        Assert.Equal(-262144, (await ReadNextConnectionSettingsAsync(database, projectId)).CacheSize);
    }

    [Fact]
    public async Task CaseDatabase_SetsOneSqliteMemoryLimitForTheWholeProcess()
    {
        using var root = new TempProjectRoot();
        await CreateProjectAsync(root);

        // 上限是整個程序共用一個值：一條不經過 JET、從沒用過的記憶體資料庫連線也讀到 1 GiB。
        // 只設盡量不超過的上限；會讓配置直接失敗的硬上限維持 0，表示沒有設。
        await using var unrelated = new SqliteConnection("Data Source=:memory:;Pooling=False");
        await unrelated.OpenAsync();
        Assert.Equal(1L << 30, await ReadPragmaAsync(unrelated, "soft_heap_limit"));
        Assert.Equal(0L, await ReadPragmaAsync(unrelated, "hard_heap_limit"));
    }

    private sealed record ConnectionSettings(long Synchronous, long TempStore, long CacheSize);

    private static async Task<(ILocalProjectDatabase Database, string ProjectId)> CreateProjectAsync(TempProjectRoot root)
    {
        var folder = new JetProjectFolder(root.Path);
        ILocalProjectDatabase database = new SqliteProjectDatabase(folder);
        var projectId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(projectId));
        await database.EnsureCreatedAsync(projectId, CancellationToken.None);
        return (database, projectId);
    }

    private static async Task<string> ReplaceGlAsync(IImportRepository import, string projectId)
    {
        var result = await import.ReplaceBatchAsync(
            projectId,
            DatasetKind.Gl,
            [Source("fixture.csv", Rows(("D1", "100", "0"), ("D1", "0", "100")))],
            CancellationToken.None);
        return result.Batch.BatchId;
    }

    private static ImportSourceInput Source(string fileName, IAsyncEnumerable<StagingRow> rows) =>
        new(new ImportSourceDescriptor(fileName, fileName, null, null, null), Columns, rows);

    private static async IAsyncEnumerable<StagingRow> Rows(params (string Doc, string Debit, string Credit)[] rows)
    {
        var sourceRow = 2;
        foreach (var (doc, debit, credit) in rows)
        {
            yield return new StagingRow(
                sourceRow++,
                new Dictionary<string, string>
                {
                    ["doc"] = doc,
                    ["date"] = "2025-01-01",
                    ["acc"] = "1101",
                    ["debit"] = debit,
                    ["credit"] = credit
                });
        }

        await Task.CompletedTask;
    }

    private static GlMappingSpec Spec()
    {
        var mapping = new Dictionary<string, string>
        {
            [GlMappingKeys.VoucherNumber] = "doc",
            [GlMappingKeys.GlDate] = "date",
            [GlMappingKeys.AccountNumber] = "acc",
            [GlMappingKeys.DebitAmount] = "debit",
            [GlMappingKeys.CreditAmount] = "credit"
        };
        return new GlMappingSpec(mapping, GlAmountMode.DualAmount)
        {
            Options = GlMappingOptions.NormalizeLegacy(mapping)
        };
    }

    /// <summary>用一般方式開案件連線，連線池給的是最近放回的那一條。</summary>
    private static async Task<ConnectionSettings> ReadNextConnectionSettingsAsync(ILocalProjectDatabase database, string projectId)
    {
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync();
        return await ReadSettingsAsync(connection);
    }

    /// <summary>
    /// 一般案件連線開好時應有的設定：SQLite 給新連線的設定，只有快取換成 256 MiB。
    /// 快取的單位是 KiB，負數表示用大小計，256 MiB 寫成 -262144。
    /// </summary>
    private static async Task<ConnectionSettings> ReadExpectedCaseConnectionSettingsAsync(ILocalProjectDatabase database, string projectId) =>
        await ReadFreshSettingsAsync(database, projectId) with { CacheSize = -262144 };

    /// <summary>開一條不進連線池的新連線，讀到的是 SQLite 給新連線的設定。</summary>
    private static async Task<ConnectionSettings> ReadFreshSettingsAsync(ILocalProjectDatabase database, string projectId)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database.GetDatabasePath(projectId),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        await connection.OpenAsync();
        return await ReadSettingsAsync(connection);
    }

    private static async Task<ConnectionSettings> ReadSettingsAsync(DbConnection connection) =>
        new(
            await ReadPragmaAsync(connection, "synchronous"),
            await ReadPragmaAsync(connection, "temp_store"),
            await ReadPragmaAsync(connection, "cache_size"));

    private static async Task<long> ReadPragmaAsync(DbConnection connection, string name)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA {name};";
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
}
