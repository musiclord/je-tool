using JET.Domain;
using JET.Infrastructure;
using Microsoft.Extensions.Logging;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// SqlServer import 診斷日誌（TDD #1 SQL/參數值/rows_affected；#2 SqlBulkCopy 不逐列；transaction；milestone）。
/// 與 <see cref="SqlExecutionLoggingTests"/>（SQLite import）對照——事件類型/順序一致（CP4 以序列差分證等價）。
/// gated：無 LocalDB 即靜默跳過（mystery-guest 豁免）。
/// </summary>
public sealed class SqlServerImportLoggingTests
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

    [SqlServerFact]
    public async Task Replace_LogsOneShotSql_TransactionAndMilestones_NotPerRow()
    {
        await using var temp = await TempSqlServerProject.TryCreateAsync();
        if (temp is null)
        {
            return; // 無 LocalDB → 跳過
        }

        var diagnostic = new RingBufferLoggerProvider(5000);
        using var factory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Trace);
            builder.AddProvider(diagnostic);
        });
        var repo = new SqlServerImportRepository(temp.Database, factory.CreateLogger<SqlServerImportRepository>());

        await repo.ReplaceBatchAsync(temp.ProjectId, DatasetKind.Gl, Src(), new[] { "科目", "金額" },
            Stream([Row(2, ("科目", "1001"), ("金額", "100")), Row(3, ("科目", "1002"), ("金額", "200"))]),
            CancellationToken.None);

        var entries = diagnostic.Snapshot();
        var sql = entries.Where(e => e.EventName == "sql.executed").ToList();
        Assert.NotEmpty(sql);
        Assert.All(sql, e => Assert.Equal("sqlServer", e.Fields["provider"]?.ToString()));

        // SqlBulkCopy 不逐列記事件
        Assert.DoesNotContain(sql, e => e.Fields["sql"]!.ToString()!.Contains("INSERT INTO staging_gl_raw_row"));

        // 一次性語句逐筆記錄：INSERT import_batch 帶 @columnsJson 參數值（欄名）、rows_affected=1
        var batchInsert = sql.First(e => e.Fields["parameters"]!.ToString()!.Contains("@columnsJson"));
        Assert.Contains("科目", batchInsert.Fields["parameters"]!.ToString()!);
        Assert.Equal(1, Convert.ToInt32(batchInsert.Fields["rows_affected"]));

        // transaction：begin + commit 共享同一 id；其間 SQL 亦帶同 id
        var begin = entries.Single(e => e.EventName == "tx.begin");
        var commit = entries.Single(e => e.EventName == "tx.commit");
        Assert.NotNull(begin.TransactionId);
        Assert.Equal(begin.TransactionId, commit.TransactionId);
        Assert.Contains(sql, e => e.TransactionId == begin.TransactionId);

        // staging 階段 + replace 總結兩筆 import.milestone（與 SQLite import 等價）
        var milestones = entries.Where(e => e.EventName == "import.milestone").ToList();
        Assert.Contains(milestones, e => e.Fields["phase"]?.ToString() == "staging");
        var replace = milestones.Single(e => e.Fields["phase"]?.ToString() == "replace");
        Assert.Equal(2, Convert.ToInt32(replace.Fields["rows_processed"]));
    }
}
