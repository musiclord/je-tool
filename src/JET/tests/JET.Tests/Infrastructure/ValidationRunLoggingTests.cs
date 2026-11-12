using System.Data.Common;
using JET.AuditCore;
using JET.Domain;
using JET.Infrastructure;
using Microsoft.Extensions.Logging;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// Validation repo 診斷日誌（SQL、參數值、rows_affected、duration；transaction 共享 id）。
/// oracle：規格——TB mapping 存在時，ExecuteAsync 在單一交易內跑 stats / completeness（count + reader）/
/// unbalanced count / unbalanced detail / INF 抽樣 / null count / source-quality count / null detail /
/// 存入筆數與合計核對的控制總數 / amount distribution / 同號多日期計數，共 12 條 SQL；TB mapping 不存在時只略過
/// 逐科目比對的兩條，保留其餘 10 條。INF 為 INSERT，rows_affected 反映抽出列數。
/// 斷言鎖 SQL 內容、rows_affected、共享 id、綁定期間值。
/// 母體以固定 3 列直接播種（值可手算）。
/// </summary>
public sealed class ValidationRunLoggingTests
{
    // v2 INF PRF 是 SQLite 方言渲染後的 SQL，所以 command-sequence digest 跟著實際執行的 SQL 文字走；SQL 文字改變時 digest 要依新 SQL 更新。
    // 驗證規則與 INF 只讀 GL 的分錄；原始統計、存入筆數與合計核對，以及總帳日期空白的檢查不受這個範圍限制。
    // 空白紀錄述詞的去空白走 SQLite 方言的 Trim（字元集合和 .NET 相同）。
    // 第 1 條命令的日期不符筆數是三種日期原因的合計，原因清單由 GlExcludedPopulation.ExcludedByPeriodSqlPredicate 產生並嵌入 SQL。
    // 核准日空白照舊版也算成不在期間；這個情境有配對核准日，所以沒有出現核准日不可用時的 `(1 = 0)`。
    // 第 10 條命令的空值明細在旗標後面多讀傳票核准日 `, approval_date`。
    // 第 12 條命令是傳票號碼跨日重用的檢查，「不是空白」的片段走 AuditCore 的 SqlBlankText。
    private const string SqliteSnapshotDigest = "0767C4965E9B63A077A4A2FE1CBA080C4933B3240C49BBD2CAB1CEBB35E8E122";

    // 只填 NOT NULL + 少數欄，entry_id 為自增。
    private static string SeedThreeGlRows(string schemaPrefix = "") =>
        $"""
        INSERT INTO {schemaPrefix}gl
            (batch_id, source_row_number, voucher_number, line_item, gl_date, account_number,
             is_excluded, amount_scaled, debit_amount_scaled, credit_amount_scaled, dr_cr)
        VALUES
            ('b1', 1, 'D1', '1', '2025-03-05', '1101', 0, 100, 100, 0, 'DEBIT'),
            ('b1', 2, 'D1', '2', '2025-03-05', '4101', 0, -100, 0, 100, 'CREDIT'),
            ('b1', 3, 'D2', '1', '2025-06-07', '1101', 0, 5, 5, 0, 'DEBIT');
        """;

    private static ValidationPlan Plan(string projectId, bool hasTbMapping = true) =>
        JetAuditProgram.Plan(new ValidationRequest(
            ProjectId: projectId,
            HasGlMapping: true,
            HasTbMapping: hasTbMapping,
            PeriodStart: "2025-01-01",
            PeriodEnd: "2025-12-31",
            MoneyScale: 10_000,
            SampleSeed: 7,
            ResultId: "validation-log-run",
            GeneratedUtc: DateTimeOffset.UnixEpoch,
            SampleSize: 5));

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

    private static async Task SeedAsync(DbConnection connection, string schemaPrefix = "")
    {
        await using var command = connection.CreateCommand();
        command.CommandText = SeedThreeGlRows(schemaPrefix);
        await command.ExecuteNonQueryAsync();
    }

    private static void AssertValidationLog(
        RingBufferLoggerProvider diagnostic, string expectedProvider, string schemaPrefix = "")
    {
        var entries = diagnostic.Snapshot();
        var sql = entries.Where(e => e.EventName == "sql.executed").ToList();

        // The voucher reuse check adds exactly one set-based query; blank voucher numbers are excluded from balance.
        Assert.Equal(12, sql.Count);
        Assert.All(sql, e => Assert.Equal(expectedProvider, e.Fields["provider"]?.ToString()));
        Assert.All(sql, e => Assert.True(Convert.ToInt64(e.Fields["duration_ms"]) >= 0));

        // INF 抽樣為 INSERT：rows_affected 反映抽出列數（母體 3 列、SampleSize 5 → 3）
        var insert = sql.Single(e => e.Fields["sql"]!.ToString()!.Contains($"INSERT INTO {schemaPrefix}inf_sample"));
        Assert.Equal(3, Convert.ToInt32(insert.Fields["rows_affected"]));

        // null 記錄 SELECT 綁定查核期間 → parameters 含 2025-01-01（使用者值參數綁定）
        Assert.Contains(sql, e => e.Fields["parameters"]!.ToString()!.Contains("2025-01-01"));

        // 金額級距為單一參數化、只讀 GL、set-based GROUP BY CASE。
        var amountDistribution = sql.Single(e =>
            e.Fields["sql"]!.ToString()!.Contains("@amount1k", StringComparison.Ordinal));
        var amountDistributionSql = amountDistribution.Fields["sql"]!.ToString()!;
        Assert.Contains("GROUP BY", amountDistributionSql, StringComparison.Ordinal);
        Assert.Contains("amount_scaled", amountDistributionSql, StringComparison.Ordinal);
        Assert.Contains("@amount10M", amountDistributionSql, StringComparison.Ordinal);
        Assert.Contains("is_excluded = 0", amountDistributionSql, StringComparison.Ordinal);
        Assert.DoesNotContain("@periodStart", amountDistributionSql, StringComparison.Ordinal);
        Assert.DoesNotContain("2025-01-01", amountDistribution.Fields["parameters"]!.ToString(), StringComparison.Ordinal);

        // sourceQuality 是第 9 個 site：不收進 blankRecords，也不套「只看納入測試的分錄」的述詞。
        var sourceQuality = sql[8];
        var sourceQualitySql = sourceQuality.Fields["sql"]!.ToString()!;
        Assert.Contains("gl_date IS NULL", sourceQualitySql, StringComparison.Ordinal);
        Assert.DoesNotContain("is_excluded", sourceQualitySql, StringComparison.Ordinal);
        Assert.Equal(string.Empty, sourceQuality.Fields["parameters"]?.ToString());
        Assert.Equal(-1, Convert.ToInt32(sourceQuality.Fields["rows_affected"]));

        var dateReuse = sql[11];
        Assert.Contains("DISTINCT gl_date", dateReuse.Fields["sql"]!.ToString());
        Assert.Contains("voucher_number IS NOT NULL", dateReuse.Fields["sql"]!.ToString());
        Assert.Equal(string.Empty, dateReuse.Fields["parameters"]?.ToString());

        // transaction：begin + commit 共享 id；所有 SQL 全在同一交易內
        var begin = entries.Single(e => e.EventName == "tx.begin");
        var commit = entries.Single(e => e.EventName == "tx.commit");
        Assert.NotNull(begin.TransactionId);
        Assert.Equal(begin.TransactionId, commit.TransactionId);
        Assert.All(sql, e => Assert.Equal(begin.TransactionId, e.TransactionId));

        var expectedDigest = expectedProvider switch
        {
            "sqlite" => SqliteSnapshotDigest,
            _ => throw new InvalidOperationException(
                $"未登錄 provider '{expectedProvider}' 的 validation SQL snapshot。")
        };
        SqlExecutionSnapshot.AssertDigest(entries, expectedDigest, schemaPrefix);
    }

    [Fact]
    public async Task Run_Sqlite_LogsTwelveSqlSites_WithTransactionAndInsertRowsAffected()
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        var db = new SqliteProjectDatabase(folder);
        var projectId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(projectId));
        await db.EnsureCreatedAsync(projectId, CancellationToken.None);

        await using (var seed = db.CreateConnection(projectId))
        {
            await seed.OpenAsync();
            await SeedAsync(seed);
        }

        var (diagnostic, factory) = NewDiagnostic();
        using (factory)
        {
            var repo = new LocalValidationRunRepository(db, factory.CreateLogger<LocalValidationRunRepository>());
            await ValidationExecutionTestData.ExecuteForFactsAsync(repo, Plan(projectId), CancellationToken.None);
        }

        AssertValidationLog(diagnostic, "sqlite");
    }

    [Fact]
    public async Task Run_Sqlite_WithoutTbMapping_SkipsOnlyAccountComparisonQueries()
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        var db = new SqliteProjectDatabase(folder);
        var projectId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(projectId));
        await db.EnsureCreatedAsync(projectId, CancellationToken.None);

        await using (var seed = db.CreateConnection(projectId))
        {
            await seed.OpenAsync();
            await SeedAsync(seed);
        }

        var (diagnostic, factory) = NewDiagnostic();
        using (factory)
        {
            var repo = new LocalValidationRunRepository(db, factory.CreateLogger<LocalValidationRunRepository>());
            var result = await ValidationExecutionTestData.ExecuteForFactsAsync(repo, Plan(projectId, hasTbMapping: false), CancellationToken.None);

            Assert.Equal(3, result.InfSampleCount);
        }

        var entries = diagnostic.Snapshot();
        var sql = entries.Where(entry => entry.EventName == "sql.executed").ToList();

        Assert.Equal(10, sql.Count);
        Assert.DoesNotContain(sql, entry =>
            entry.Fields["sql"]?.ToString()?.Contains("tb", StringComparison.Ordinal) == true);
        Assert.Contains(sql, entry =>
            entry.Fields["sql"]?.ToString()?.Contains("inf_sample", StringComparison.Ordinal) == true);
        Assert.Contains(sql, entry =>
            entry.Fields["sql"]?.ToString()?.Contains("gl_control_total", StringComparison.Ordinal) == true);
        Assert.Contains(sql, entry =>
            entry.Fields["sql"]?.ToString()?.Contains("gl_date IS NULL", StringComparison.Ordinal) == true);

        var begin = entries.Single(entry => entry.EventName == "tx.begin");
        var commit = entries.Single(entry => entry.EventName == "tx.commit");
        Assert.Equal(begin.TransactionId, commit.TransactionId);
        Assert.All(sql, entry => Assert.Equal(begin.TransactionId, entry.TransactionId));
    }
}
