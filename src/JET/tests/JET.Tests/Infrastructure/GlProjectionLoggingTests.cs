using JET.Domain;
using JET.Infrastructure;
using Microsoft.Extensions.Logging;
using Xunit;

// 改走正式批次匯入與明示投影參數；保留原始合成資料及固定答案。
namespace JET.Tests.Infrastructure;

/// <summary>
/// GL 投影診斷日誌（TDD #1 SQL；#2 per-row 不逐筆記；transaction 共享 id；projection.milestone）。
/// oracle：規格——ProjectStagingToTargetAsync 只逐筆記一次性的 clear DELETE + staging SELECT
/// + lineItem 未對應時的逐傳票編號 UPDATE；逐列 INSERT（SQLite）/ SqlBulkCopy（SqlServer）不產生
/// sql.executed → 事件數為常數 3（與母體列數無關）；投影結束記一筆 projection.milestone。
/// 斷言鎖 SQL 內容、共享 id、phase、列數。SqlServer 側 LocalDB 閘控。
/// </summary>
public sealed class GlProjectionLoggingTests
{
    private static GlMappingSpec DualSpec() => new(
        new Dictionary<string, string>
        {
            [GlMappingKeys.VoucherNumber] = "doc",
            [GlMappingKeys.GlDate] = "date",
            [GlMappingKeys.AccountNumber] = "acc",
            [GlMappingKeys.AccountName] = "name",
            [GlMappingKeys.Description] = "desc",
            [GlMappingKeys.DebitAmount] = "debit",
            [GlMappingKeys.CreditAmount] = "credit"
        },
        GlAmountMode.DualAmount);

    private static StagingRow Row(int number, string doc, string? debit, string? credit)
    {
        var values = new Dictionary<string, string>
        {
            ["doc"] = doc, ["date"] = "2024-01-01", ["acc"] = "1101", ["name"] = "現金", ["desc"] = "test"
        };
        if (debit is not null) values["debit"] = debit;
        if (credit is not null) values["credit"] = credit;
        return new StagingRow(number, values);
    }

    private static async IAsyncEnumerable<StagingRow> ToAsync(IEnumerable<StagingRow> rows)
    {
        foreach (var row in rows)
        {
            yield return row;
        }

        await Task.CompletedTask;
    }

    private static ImportSourceDescriptor Source() => new(@"C:\gl.xlsx", "gl.xlsx", null, null, null);

    private static IReadOnlyList<string> Columns => ["doc", "date", "acc", "name", "desc", "debit", "credit"];

    private static IReadOnlyList<StagingRow> ThreeRows =>
        [Row(2, "D1", "100", null), Row(3, "D1", null, "100"), Row(4, "D2", "5", null)];

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
    private static void AssertGlProjectionLog(
        RingBufferLoggerProvider diagnostic, string expectedProvider, int expectedRows, string schemaPrefix = "")
    {
        var entries = diagnostic.Snapshot();
        var sql = entries.Where(e => e.EventName == "sql.executed").ToList();

        // 一次性 SQL：clear DELETE + staging SELECT + lineItem 未對應時的逐傳票編號 UPDATE；
        // 逐列 INSERT / bulk 不記事件（不隨母體列數成長，故 DualSpec 下事件數為常數 3）。
        Assert.Equal(3, sql.Count);
        Assert.All(sql, e => Assert.Equal(expectedProvider, e.Fields["provider"]?.ToString()));
        Assert.Contains(sql, e => e.Fields["sql"]!.ToString()!.Contains($"DELETE FROM {schemaPrefix}gl"));
        Assert.Contains(sql, e => e.Fields["sql"]!.ToString()!.Contains($"{schemaPrefix}pbc_gl"));
        Assert.Contains(sql, e => e.Fields["sql"]!.ToString()!.Contains("line_item"));

        // transaction：begin + commit + 其間 SQL 共享同一 transaction_id
        var begin = entries.Single(e => e.EventName == "tx.begin");
        var commit = entries.Single(e => e.EventName == "tx.commit");
        Assert.NotNull(begin.TransactionId);
        Assert.Equal(begin.TransactionId, commit.TransactionId);
        Assert.Contains(sql, e => e.TransactionId == begin.TransactionId);

        // 投影 milestone：phase 與處理列數
        var milestone = entries.Single(e => e.EventName == "projection.milestone");
        Assert.Equal("gl-projection", milestone.Fields["phase"]?.ToString());
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
        // 只有建案會建立案件資料庫，讀寫前的檢查不再順便建表（裁定 2026-11-05-72），所以照建案的做法先建表。第一次失敗收據 20261105-155959508-7b95566f0d5a426aa5145c6a92f63fc1。
        await db.EnsureCreatedAsync(projectId, CancellationToken.None);

        // 以未接 logger 的 import repo 播種 staging（不污染診斷緩衝）
        var batch = (await new LocalImportRepository(db).ReplaceBatchAsync(
            projectId, DatasetKind.Gl, [new ImportSourceInput(Source(), Columns, ToAsync(ThreeRows))], CancellationToken.None)).Batch;

        var (diagnostic, factory) = NewDiagnostic();
        using (factory)
        {
            var glRepo = new LocalGlRepository(db, factory.CreateLogger<LocalGlRepository>());
            var result = await glRepo.ProjectStagingToTargetAsync(
                projectId, batch.BatchId, DualSpec(), 10_000, DateParseOptions.Default, periodStart: DateOnly.MinValue, periodEnd: DateOnly.MaxValue,
                postingStatusMapped: false, postingStatusPolicy: null, committedUtc: DateTimeOffset.UnixEpoch,
                CancellationToken.None);
            Assert.Equal(3, result.ProjectedRowCount);
        }

        AssertGlProjectionLog(diagnostic, "sqlite", expectedRows: 3);
    }
}
