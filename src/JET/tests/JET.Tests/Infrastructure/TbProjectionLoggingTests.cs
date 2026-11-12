using JET.Domain;
using JET.Infrastructure;
using Microsoft.Extensions.Logging;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// TB 投影診斷日誌（SQL；per-row 不逐筆記；transaction 共享 id；projection.milestone）。
/// oracle：規格——只逐筆記一次性 clear DELETE + staging SELECT；逐列 INSERT 不記 → 事件數常數 2；
/// 投影結束記一筆 projection.milestone phase=tb-projection。斷言鎖 SQL 內容、共享 id、phase、列數。
/// </summary>
public sealed class TbProjectionLoggingTests
{
    private static TbMappingSpec Spec() => new(
        new Dictionary<string, string>
        {
            [TbMappingKeys.AccountNumber] = "acc",
            [TbMappingKeys.AccountName] = "name",
            [TbMappingKeys.DebitAmount] = "dr",
            [TbMappingKeys.CreditAmount] = "cr"
        },
        TbChangeMode.DebitCredit);

    private static async IAsyncEnumerable<StagingRow> ToAsync(IEnumerable<StagingRow> rows)
    {
        foreach (var row in rows)
        {
            yield return row;
        }

        await Task.CompletedTask;
    }

    private static IReadOnlyList<StagingRow> TwoRows =>
    [
        new(2, new Dictionary<string, string> { ["acc"] = "110201", ["name"] = "現金", ["dr"] = "100", ["cr"] = "100" }),
        new(3, new Dictionary<string, string> { ["acc"] = "110202", ["name"] = "銀行", ["dr"] = "200", ["cr"] = "150" })
    ];

    private static ImportSourceDescriptor Source() => new(@"C:\tb.xlsx", "tb.xlsx", null, null, null);

    private static IReadOnlyList<string> Columns => ["acc", "name", "dr", "cr"];

    private static (RingBufferLoggerProvider Diagnostic, ILoggerFactory Factory) NewDiagnostic()
    {
        var diagnostic = new RingBufferLoggerProvider(5000);
        var factory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Trace);
            builder.AddProvider(diagnostic);
        });
        return (diagnostic, factory);
    }

    private static void AssertTbProjectionLog(
        RingBufferLoggerProvider diagnostic, string expectedProvider, int expectedRows, string schemaPrefix = "")
    {
        var entries = diagnostic.Snapshot();
        var sql = entries.Where(e => e.EventName == "sql.executed").ToList();

        // 一次性 SQL 僅 clear DELETE + staging SELECT；逐列 INSERT 不記事件（不隨母體列數成長）
        Assert.Equal(2, sql.Count);
        Assert.All(sql, e => Assert.Equal(expectedProvider, e.Fields["provider"]?.ToString()));
        Assert.Contains(sql, e => e.Fields["sql"]!.ToString()!.Contains($"DELETE FROM {schemaPrefix}tb"));
        Assert.Contains(sql, e => e.Fields["sql"]!.ToString()!.Contains($"{schemaPrefix}pbc_tb"));

        var begin = entries.Single(e => e.EventName == "tx.begin");
        var commit = entries.Single(e => e.EventName == "tx.commit");
        Assert.NotNull(begin.TransactionId);
        Assert.Equal(begin.TransactionId, commit.TransactionId);
        Assert.Contains(sql, e => e.TransactionId == begin.TransactionId);

        var milestone = entries.Single(e => e.EventName == "projection.milestone");
        Assert.Equal("tb-projection", milestone.Fields["phase"]?.ToString());
        Assert.Equal(expectedRows, Convert.ToInt32(milestone.Fields["rows_processed"]));
        Assert.True(Convert.ToDouble(milestone.Fields["throughput"]) >= 0);
    }

    [Fact]
    public async Task Projection_Sqlite_LogsOneShotSqlAndMilestone_NotPerRow()
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        var db = new SqliteProjectDatabase(folder);
        var projectId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(projectId));
        // 只有建案會建立案件資料庫，讀寫前的檢查不順便建表，所以照建案的做法先建表。
        await db.EnsureCreatedAsync(projectId, CancellationToken.None);

        var batch = (await new LocalImportRepository(db).ReplaceBatchAsync(
            projectId, DatasetKind.Tb, [new ImportSourceInput(Source(), Columns, ToAsync(TwoRows))], CancellationToken.None)).Batch;

        var (diagnostic, factory) = NewDiagnostic();
        using (factory)
        {
            var tbRepo = new LocalTbRepository(db, factory.CreateLogger<LocalTbRepository>());
            var result = await tbRepo.ProjectStagingToTargetAsync(
                projectId, batch.BatchId, Spec(), 10_000, committedUtc: DateTimeOffset.UnixEpoch, CancellationToken.None);
            Assert.Equal(2, result.ProjectedRowCount);
        }

        AssertTbProjectionLog(diagnostic, "sqlite", expectedRows: 2);
    }
}
