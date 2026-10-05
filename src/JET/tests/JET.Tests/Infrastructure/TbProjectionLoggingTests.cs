using JET.Domain;
using JET.Infrastructure;
using Microsoft.Extensions.Logging;
using Xunit;

// 第 9 批中低 14：改走正式批次匯入與明示投影參數；保留原始合成資料及固定答案。
// 第 9 批中低 9：TB 提交時間固定使用 DateTimeOffset.UnixEpoch。
namespace JET.Tests.Infrastructure;

/// <summary>
/// TB 投影診斷日誌（TDD #1 SQL；#2 per-row 不逐筆記；transaction 共享 id；projection.milestone）。
/// oracle：規格——只逐筆記一次性 clear DELETE + staging SELECT；逐列 INSERT 不記 → 事件數常數 2；
/// 投影結束記一筆 projection.milestone phase=tb-projection。斷言鎖 SQL 內容、共享 id、phase、列數。
/// SqlServer 側以 LocalDB 閘控、無則跳過。
/// </summary>
public sealed class TbProjectionLoggingTests
{
    private static TbMappingSpec Spec() => new(
        new Dictionary<string, string>
        {
            [TbMappingKeys.AccNum] = "acc",
            [TbMappingKeys.AccName] = "name",
            [TbMappingKeys.DebitAmt] = "dr",
            [TbMappingKeys.CreditAmt] = "cr"
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

    // schemaPrefix：SQL Server 端的專案表在 logged SQL 中以 [schema]. 限定（production 同），SQLite 端為 ""。
    private static void AssertTbProjectionLog(
        RingBufferLoggerProvider diagnostic, string expectedProvider, int expectedRows, string schemaPrefix = "")
    {
        var entries = diagnostic.Snapshot();
        var sql = entries.Where(e => e.EventName == "sql.executed").ToList();

        // 一次性 SQL 僅 clear DELETE + staging SELECT；逐列 INSERT 不記事件（不隨母體列數成長）
        Assert.Equal(2, sql.Count);
        Assert.All(sql, e => Assert.Equal(expectedProvider, e.Fields["provider"]?.ToString()));
        Assert.Contains(sql, e => e.Fields["sql"]!.ToString()!.Contains($"DELETE FROM {schemaPrefix}target_tb_balance"));
        Assert.Contains(sql, e => e.Fields["sql"]!.ToString()!.Contains($"{schemaPrefix}staging_tb_raw_row"));

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

    [SqlServerFact]
    public async Task Projection_SqlServer_LogsOneShotSqlAndMilestone_WhenLocalDbAvailable()
    {
        await using var temp = await TempSqlServerProject.TryCreateAsync();
        if (temp is null)
        {
            return; // 無 LocalDB → 跳過（mystery-guest 豁免）
        }

        var batch = (await new SqlServerImportRepository(temp.Database).ReplaceBatchAsync(
            temp.ProjectId, DatasetKind.Tb, [new ImportSourceInput(Source(), Columns, ToAsync(TwoRows))], CancellationToken.None)).Batch;

        var (diagnostic, factory) = NewDiagnostic();
        using (factory)
        {
            var tbRepo = new SqlServerTbRepository(temp.Database, factory.CreateLogger<SqlServerTbRepository>());
            var result = await tbRepo.ProjectStagingToTargetAsync(
                temp.ProjectId, batch.BatchId, Spec(), 10_000, committedUtc: DateTimeOffset.UnixEpoch, CancellationToken.None);
            Assert.Equal(2, result.ProjectedRowCount);
        }

        AssertTbProjectionLog(diagnostic, "sqlServer", expectedRows: 2,
            SqlServerProjectSchema.QualifierFor(temp.ProjectId));
    }
}
