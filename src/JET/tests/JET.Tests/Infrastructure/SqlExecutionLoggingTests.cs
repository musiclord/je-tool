using JET.Domain;
using JET.Infrastructure;
using Microsoft.Extensions.Logging;
using Xunit;

// 第 9 批中低 14：改走正式批次匯入與明示投影參數；保留原始合成資料及固定答案。
namespace JET.Tests.Infrastructure;

/// <summary>
/// SQL 與 transaction 診斷日誌（TDD #1 SQL 完整命令/參數/rows_affected、#2 transaction 三事件共享 id）。
/// 對真 SQLite temp DB + ring buffer logger 跑匯入,斷言 ring buffer 內的 sql.executed / tx.* 結構化欄位。
/// </summary>
public sealed class SqlExecutionLoggingTests
{
    private static ImportSourceDescriptor Src() => new("/x.csv", "x.csv", null, null, null);

    private static StagingRow Row(int number, params (string Key, string Value)[] cells) =>
        new(number, cells.ToDictionary(c => c.Key, c => c.Value, StringComparer.Ordinal));

    private static async IAsyncEnumerable<StagingRow> Stream(IReadOnlyList<StagingRow> rows)
    {
        foreach (var row in rows)
        {
            yield return row;
        }

        await Task.CompletedTask;
    }

    private sealed class Env : IDisposable
    {
        private readonly ILoggerFactory _factory;
        private readonly TempProjectRoot _root = new();

        public Env()
        {
            _factory = LoggerFactory.Create(builder =>
            {
                builder.SetMinimumLevel(LogLevel.Trace);
                builder.AddProvider(Diagnostic);
            });
            var folder = new JetProjectFolder(_root.Path);
            var database = new SqliteProjectDatabase(folder);
            Directory.CreateDirectory(folder.GetProjectDirectory(ProjectId));
            Repo = new LocalImportRepository(database, _factory.CreateLogger<LocalImportRepository>());
        }

        public RingBufferLoggerProvider Diagnostic { get; } = new(5000);

        public LocalImportRepository Repo { get; }

        public string ProjectId { get; } = Guid.NewGuid().ToString("N");

        public void Dispose()
        {
            _factory.Dispose();
            _root.Dispose();
        }
    }

    [Fact]
    public async Task ImportReplace_LogsSql_WithCommandParamsAndRowsAffected()
    {
        using var env = new Env();

        await env.Repo.ReplaceBatchAsync(env.ProjectId, DatasetKind.Gl, [new ImportSourceInput(Src(), new[] { "科目", "金額" },
            Stream([Row(2, ("科目", "1001"), ("金額", "100"))]))], CancellationToken.None);

        var sql = env.Diagnostic.Snapshot().Where(e => e.EventName == "sql.executed").ToList();
        Assert.NotEmpty(sql);
        Assert.All(sql, e => Assert.Equal("sqlite", e.Fields["provider"]?.ToString()));

        // staging 逐列 INSERT 改為 milestone-only（與 SqlServer 的 SqlBulkCopy 路徑事件等價）→ 不再逐筆記事件
        Assert.DoesNotContain(sql, e => e.Fields["sql"]!.ToString()!.Contains("INSERT INTO staging_gl_raw_row"));

        // 一次性語句仍逐筆記錄：INSERT import_batch 帶 @columnsJson 參數值（欄名）、rows_affected=1
        var batchInsert = sql.First(e => e.Fields["parameters"]!.ToString()!.Contains("@columnsJson"));
        var parameters = batchInsert.Fields["parameters"]!.ToString()!;
        Assert.Contains("@batchId", parameters);          // 參數名稱
        Assert.Contains("科目", parameters);               // 參數值（columns_json 內含欄名）
        Assert.Equal(1, Convert.ToInt32(batchInsert.Fields["rows_affected"]));
    }

    [Fact]
    public async Task Transaction_Commit_BeginAndCommitShareTransactionId()
    {
        using var env = new Env();

        await env.Repo.ReplaceBatchAsync(env.ProjectId, DatasetKind.Gl, [new ImportSourceInput(Src(), new[] { "科目", "金額" },
            Stream([Row(2, ("科目", "1001"), ("金額", "100"))]))], CancellationToken.None);

        var entries = env.Diagnostic.Snapshot();
        var begin = entries.Single(e => e.EventName == "tx.begin");
        var commit = entries.Single(e => e.EventName == "tx.commit");

        Assert.NotNull(begin.TransactionId);
        Assert.Equal(begin.TransactionId, commit.TransactionId);
        // 其間的 SQL 也共享同一 transaction_id（BeginScope 跨 SQL）
        Assert.Contains(entries, e => e.EventName == "sql.executed" && e.TransactionId == begin.TransactionId);
    }

    [Fact]
    public async Task Transaction_Rollback_BeginAndRollbackShareTransactionId()
    {
        using var env = new Env();

        // 空來源 → rollback
        await Assert.ThrowsAsync<JetActionException>(() => env.Repo.ReplaceBatchAsync(
            env.ProjectId, DatasetKind.Gl, [new ImportSourceInput(Src(), new[] { "科目", "金額" }, Stream([]))], CancellationToken.None));

        var entries = env.Diagnostic.Snapshot();
        var begin = entries.Single(e => e.EventName == "tx.begin");
        var rollback = entries.Single(e => e.EventName == "tx.rollback");

        Assert.NotNull(begin.TransactionId);
        Assert.Equal(begin.TransactionId, rollback.TransactionId);
    }

    [Fact]
    public async Task ImportReplace_LogsMilestone_WithPhaseRowsElapsedThroughput()
    {
        using var env = new Env();

        await env.Repo.ReplaceBatchAsync(env.ProjectId, DatasetKind.Gl, [new ImportSourceInput(Src(), new[] { "科目", "金額" },
            Stream([Row(2, ("科目", "1001"), ("金額", "100")), Row(3, ("科目", "1002"), ("金額", "200"))]))],
            CancellationToken.None);

        var milestones = env.Diagnostic.Snapshot().Where(e => e.EventName == "import.milestone").ToList();
        // staging 階段 + replace 總結兩筆 milestone（與 SqlServer SqlBulkCopy 路徑事件等價）
        Assert.Contains(milestones, e => e.Fields["phase"]?.ToString() == "staging");
        var replace = milestones.Single(e => e.Fields["phase"]?.ToString() == "replace");
        Assert.Equal(2, Convert.ToInt32(replace.Fields["rows_processed"]));     // 處理列數
        Assert.True(Convert.ToInt64(replace.Fields["elapsed_ms"]) >= 0);
        Assert.True(Convert.ToDouble(replace.Fields["throughput"]) >= 0);       // 列/秒
    }

    /// <summary>
    /// TDD #2（metamorphic）：staging 逐列 INSERT 不逐筆記事件——單來源的 sql.executed 固定為 6
    /// （cleanup、batch、source、columns、counts，以及交易內讀回來源中繼資料），與母體列數無關。
    /// 故百萬列亦遠 &lt; 100 筆事件，不會灌爆 ring buffer。
    /// </summary>
    [Theory]
    [InlineData(20)]
    [InlineData(200)]
    public async Task ImportReplace_StagingEvents_DoNotScaleWithRowCount(int rowCount)
    {
        using var env = new Env();

        var rows = Enumerable.Range(2, rowCount)
            .Select(i => Row(i, ("科目", i.ToString()), ("金額", "100")))
            .ToList();
        await env.Repo.ReplaceBatchAsync(env.ProjectId, DatasetKind.Gl, [new ImportSourceInput(Src(), new[] { "科目", "金額" },
            Stream(rows))], CancellationToken.None);

        // 第 9 批中低 14：首敗 20261004-105344715-0a3f9684995a4a20bcdcb2fb954649f6。
        // 已移除的單來源入口自行組來源資訊；正式 batch 入口在 commit 前只查一次 import_batch_source。
        var sql = env.Diagnostic.Snapshot().Where(e => e.EventName == "sql.executed").ToList();
        Assert.Equal(6, sql.Count); // 20 與 200 列皆為 6，不因 staging 列數增加。
        const string expectedSourceQuery = """
            SELECT source_no, source_file_name, sheet_name, encoding, delimiter, row_count, imported_utc
            FROM import_batch_source
            WHERE batch_id = @batchId
            ORDER BY source_no;
            """;
        var sourceQuery = Assert.Single(sql, entry => entry.Fields["sql"]?.ToString()
            ?.StartsWith("SELECT source_no,", StringComparison.Ordinal) == true);
        Assert.Equal(expectedSourceQuery.ReplaceLineEndings("\n"), sourceQuery.Fields["sql"]!.ToString()!.ReplaceLineEndings("\n"));
        Assert.Equal(sourceQuery, sql[^1]);
    }
}
